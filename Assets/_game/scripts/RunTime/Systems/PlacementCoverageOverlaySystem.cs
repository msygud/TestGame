using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  PlacementPreviewState — 배치 컨트롤러 → 커버 프리뷰 오버레이 브리지 싱글톤
    //  (BuildingPlaceController가 매 프레임 발행, 아래 시스템이 소비. ECS↔UI 관례.)
    // ══════════════════════════════════════════════════════════════════════════
    public struct PlacementPreviewState : IComponentData
    {
        public bool Active;
        public int  MainKey;
        public int2 Origin;     // 호버 footprint 원점(실셀)
        public byte RotSteps;   // 0~3
        public int  OwnerSlot;  // 배치 플레이어 LocalId(-1 = 미해소)
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PlacementCoverageOverlaySystem — 배치 시 위치 정보 제공 (2026-07-12, 합의 4번)
    // ──────────────────────────────────────────────────────────────────────────
    //  "무엇을 보여줄까"는 건물별 하드코딩이 아니라 **프리팹 엔티티의 능력 컴포넌트에서
    //  파생**한다(PrefabLookup이 준 프리팹의 같은 컴포넌트를 읽음 — CLAUDE.md "인스턴스 전
    //  접근" 원칙의 첫 UI 실사용):
    //
    //  ① 내보내는 커버(이 건물이 여기 서면 어디를 덮나):
    //     · AuraSupplier → 오라 원(footprint-클램프 유클리드 제곱) 보라 채움.
    //     · StampSupplier → 가상 입구 도로셀에서 도로 BFS(RoadCoverageOps.Flood) 주황.
    //     · WarehouseTag  → 같은 BFS 초록. (입구가 자기 도로에 안 닿으면 빈 결과 = 정직)
    //  ② 받는 커버 배지(이 자리에서 무엇이 닿나 — footprint 위 작은 사각 행):
    //     · 거주 능력(ResidenceBuilding) → [식당(주황) | 치안(보라)] 배지.
    //     · 물류 재고(Input/Output StockEntry) → [창고(초록)] 배지.
    //     · 켜짐 = 밝은 색 / 꺼짐 = 어두운 회색(검사했지만 안 닿음).
    //     색 팔레트는 F6 커버리지 오버레이와 동일(학습 비용 0).
    //
    //  렌더: 동적 Mesh + Graphics.DrawMesh(Transparent) — CoverageOverlay 패턴.
    //  재구축: 상태(키/원점/회전/owner) 변화 시 + 0.25s 스로틀(맵 자체는 저빈도 갱신).
    //  stamp/오라 front는 메인 RO 읽기(발행자와 메인-메인 순차 — CoverageOverlay 선례).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class PlacementCoverageOverlaySystem : SystemBase
    {
        Material _mat;
        Mesh     _mesh;

        bool _lastActive; int _lastKey = -1; int2 _lastOrigin; byte _lastRot; int _lastOwner;
        float _nextRT;

        readonly List<Vector3> _v = new(2048);
        readonly List<Color>   _c = new(2048);
        readonly List<int>     _i = new(3072);

        // F6 팔레트와 동일 계열.
        static readonly Color COutWarehouse = new(0.20f, 0.80f, 0.35f, 0.30f);  // 창고 stamp 범위
        static readonly Color COutSupply    = new(1.00f, 0.55f, 0.15f, 0.30f);  // 방문형 stamp 범위
        static readonly Color COutAura      = new(0.62f, 0.36f, 0.95f, 0.28f);  // 오라 범위
        static readonly Color CBadgeOff     = new(0.22f, 0.22f, 0.22f, 0.85f);  // 검사했지만 안 닿음

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.LessEqual);
            _mat.renderQueue = (int)RenderQueue.Transparent;

            _mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _mesh.indexFormat = IndexFormat.UInt32;
            _mesh.MarkDynamic();

            RequireForUpdate<GridSettings>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)  Object.DestroyImmediate(_mat);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
        }

        protected override void OnUpdate()
        {
            if (!SystemAPI.TryGetSingleton<PlacementPreviewState>(out var st) || !st.Active)
            {
                if (_lastActive) { _mesh.Clear(); _lastActive = false; }
                return;
            }

            bool changed = !_lastActive || st.MainKey != _lastKey
                           || !st.Origin.Equals(_lastOrigin) || st.RotSteps != _lastRot
                           || st.OwnerSlot != _lastOwner;
            if (changed || UnityEngine.Time.unscaledTime >= _nextRT)
            {
                _nextRT = UnityEngine.Time.unscaledTime + 0.25f;
                _lastActive = true; _lastKey = st.MainKey; _lastOrigin = st.Origin;
                _lastRot = st.RotSteps; _lastOwner = st.OwnerSlot;
                Build(in st);
            }

            if (_mesh.vertexCount > 0)
                Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0);
        }

        void Build(in PlacementPreviewState st)
        {
            _v.Clear(); _c.Clear(); _i.Clear();

            float cs = SystemAPI.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) { Upload(); return; }
            if (!SystemAPI.TryGetSingleton<PrefabLookup>(out var lookup)) { Upload(); return; }
            if (!SystemAPI.TryGetSingleton<PrefabMetaLookup>(out var metaLookup)
                || !metaLookup.TryGetMeta(st.MainKey, 0, out var meta)) { Upload(); return; }

            Entity prefab = lookup.Get(st.MainKey, 0);
            if (prefab == Entity.Null) { Upload(); return; }

            var em  = EntityManager;
            int2 eff = EntranceOps.RotateSize(meta.Size, st.RotSteps);
            if (eff.x <= 0) eff.x = 1;
            if (eff.y <= 0) eff.y = 1;

            bool hasEnt = SystemAPI.TryGetSingleton<EntranceLookup>(out var entLookup)
                          && entLookup.TryGet(st.MainKey, out var ent);
            int2 erc = hasEnt
                ? EntranceOps.EntranceRoadCell(st.Origin, meta.Size, in ent, st.RotSteps)
                : default;

            bool hasLayers = SystemAPI.TryGetSingleton<GridLayers>(out var layers)
                             && layers.RoadLayer.IsCreated;

            // ── ① 내보내는 커버 ────────────────────────────────────────────
            if (em.HasComponent<AuraSupplier>(prefab))
            {
                var aura = em.GetComponentData<AuraSupplier>(prefab);
                if (aura.Radius > 0)
                    FillAuraCircle(st.Origin, eff, aura.Radius, cs, 0.055f, COutAura);
            }
            if (hasEnt && hasLayers && st.OwnerSlot >= 0)
            {
                if (em.HasComponent<StampSupplier>(prefab))
                    FillRoadFlood(erc, st.OwnerSlot,
                        em.GetComponentData<StampSupplier>(prefab).MaxDist,
                        in layers.RoadLayer, cs, 0.06f, COutSupply);
                if (em.HasComponent<WarehouseTag>(prefab))
                    FillRoadFlood(erc, st.OwnerSlot,
                        em.GetComponentData<WarehouseTag>(prefab).MaxDist,
                        in layers.RoadLayer, cs, 0.06f, COutWarehouse);
            }

            // ── ② 받는 커버 배지(능력 파생) ───────────────────────────────
            //   프로브 셀 = 입구 도로셀(있으면). 오라는 footprint 원점(치안 판정과 동일 해상도).
            bool probeStamp = hasEnt && st.OwnerSlot >= 0
                              && SystemAPI.TryGetSingleton<StampLayers>(out var stamp)
                              && (uint)st.OwnerSlot < StampLayers.MaxPlayers;
            bool whCovered = false, hungerCovered = false;
            if (probeStamp)
            {
                var map = stamp[st.OwnerSlot];
                if (map.IsCreated && map.TryGetFirstValue(erc, out var sr, out var it))
                {
                    do
                    {
                        if (sr.Kind == StampKind.Warehouse) whCovered = true;
                        else if (sr.Kind == StampKind.Supplier
                                 && (sr.Relief & NeedType.Hunger) != NeedType.None) hungerCovered = true;
                    }
                    while (map.TryGetNextValue(out sr, ref it));
                }
            }
            bool auraCovered = st.OwnerSlot >= 0
                && SystemAPI.TryGetSingleton<AuraCoverage>(out var ac) && ac.Map.IsCreated
                && ac.Map.TryGetValue(new int3(st.OwnerSlot, st.Origin.x, st.Origin.y), out ulong bits)
                && (bits & (ulong)NeedType.HighCrime) != 0;

            int badge = 0;
            if (em.HasComponent<ResidenceBuilding>(prefab))
            {
                AddBadge(st.Origin, eff, badge++, cs, hungerCovered, COutSupply);
                AddBadge(st.Origin, eff, badge++, cs, auraCovered,   COutAura);
            }
            if (em.HasBuffer<StockEntry>(prefab))
            {
                // 물류 연결 필요 = Input/Output 칸 보유(완성품 로컬·Store만 있으면 무관).
                var stock = em.GetBuffer<StockEntry>(prefab);
                bool needsLogistics = false;
                for (int i = 0; i < stock.Length; i++)
                    if (stock[i].Role == StockRole.Input || stock[i].Role == StockRole.Output)
                    { needsLogistics = true; break; }
                if (needsLogistics)
                    AddBadge(st.Origin, eff, badge++, cs, whCovered, COutWarehouse);
            }

            Upload();
        }

        // 오라 원(footprint-클램프 유클리드 제곱) — AuraCoverageSystem.RebuildAuraJob과 동일 판정.
        void FillAuraCircle(int2 origin, int2 eff, int r, float cs, float h, Color col)
        {
            int r2 = r * r;
            for (int y = origin.y - r; y <= origin.y + eff.y - 1 + r; y++)
            for (int x = origin.x - r; x <= origin.x + eff.x - 1 + r; x++)
            {
                int nx = math.clamp(x, origin.x, origin.x + eff.x - 1);
                int ny = math.clamp(y, origin.y, origin.y + eff.y - 1);
                int dx = x - nx, dy = y - ny;
                if (dx * dx + dy * dy > r2) continue;
                AddCell(new int2(x, y), cs, h, col);
            }
        }

        // 가상 입구에서 도로 BFS — 실제 stamp와 같은 규칙(RoadCoverageOps 공용 fact).
        void FillRoadFlood(int2 start, int owner, int maxDist,
            in NativeHashMap<int2, RoadCell> roadLayer, float cs, float h, Color col)
        {
            var queue   = new NativeQueue<int2>(Allocator.Temp);
            var covered = new NativeHashMap<int2, int>(512, Allocator.Temp);
            RoadCoverageOps.Flood(start, owner, maxDist, in roadLayer, ref queue, ref covered);
            foreach (var kv in covered)
                AddCell(kv.Key, cs, h, col);
            covered.Dispose();
            queue.Dispose();
        }

        // 받는 커버 배지 — footprint 윗변 위 한 줄(순서 고정: 거주[식당|치안], 물류[창고]).
        void AddBadge(int2 origin, int2 eff, int index, float cs, bool covered, Color onCol)
        {
            float size = 0.6f;
            float x0 = (origin.x + 0.2f + index * 0.8f) * cs;
            float z0 = (origin.y + eff.y + 0.35f) * cs;
            var col = covered ? new Color(onCol.r, onCol.g, onCol.b, 0.95f) : CBadgeOff;
            AddRect(x0, z0, size * cs, size * cs, 0.12f, col);
        }

        void AddCell(int2 cell, float cs, float h, Color col)
            => AddRect(cell.x * cs, cell.y * cs, cs, cs, h, col);

        void AddRect(float x0, float z0, float w, float d, float y, Color col)
        {
            int b = _v.Count;
            _v.Add(new Vector3(x0, y, z0));
            _v.Add(new Vector3(x0, y, z0 + d));
            _v.Add(new Vector3(x0 + w, y, z0 + d));
            _v.Add(new Vector3(x0 + w, y, z0));
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _i.Add(b); _i.Add(b + 1); _i.Add(b + 2);
            _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
        }

        void Upload()
        {
            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetIndices(_i, MeshTopology.Triangles, 0, false);
            }
        }
    }
}
