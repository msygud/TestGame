#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CoverageOverlaySystem — stamp 커버리지 시각화 (에디터/개발빌드, 2026-07-09)
    // ──────────────────────────────────────────────────────────────────────────
    //  "무엇이 어디까지 닿나"를 눈으로 확인 — 공급망 병목 진단(제분소가 창고 커버 밖?
    //  집이 식당 커버 밖?)의 도구. stamp[owner](도로셀→SupplierRef)를 그대로 그린다.
    //
    //  두 모드:
    //    ① 전역 커버리지 (F6 토글, 기본 OFF): 모든 도로셀을 그 셀 owner의 stamp로 색칠.
    //         창고 커버=초록 / 식당(Supplier) 커버=주황 / 둘 다=청록 / 없음=회색.
    //         → 회색 도로 = 풀·서비스 접속 끊긴 구역(굶는 생산자·소비자의 원인).
    //    ② 호버 커버 (상시): 커서 아래 건물이 공급자/창고면 그 건물이 stamp로 커버하는
    //         셀 전부를 노랑으로 강조. "이 창고/식당이 정확히 어디를 커버하나".
    //         + 커버 축소 진단(2026-07-11, "일부 창고 커버 확연 축소" 실측 추적):
    //           · 빨강 = 단절 프런티어 — 거리 여유가 남았는데(dist<MaxDist) 물리 인접 + 같은
    //             owner 도로인데 stamp가 못 건넌 셀(방향비트 불일치/일방 연결). 빨강 라인이
    //             보이면 축소 원인 = 도로 비트 단절(그 너머가 통째로 잘림).
    //           · 파랑 = 타-owner 도로 경계 — 인접 도로가 남의 것이라 BFS가 못 건넘(소유권은
    //             안 뒤집히므로(배치 거부·capture=철거) 파랑 다수 = 영토 전환 후 포켓 잔존 창고
    //             또는 국경 밀착 — 커버가 자기 도로 조각에 갇힌 상태).
    //           · 마젠타(전체) = 창고 MaxDist가 현행 상수(SpawnSystem.WarehouseStampMaxDist)와
    //             다름 — 스폰 시점 박제 반경이 낡음(구세이브 등). 노랑만+빨강/파랑 없음 = 순수 거리 한계.
    //
    //  렌더: 동적 Mesh + Graphics.DrawMesh(Transparent) — DemandHeatmap과 동일 패턴.
    //  스로틀: 전역 1초(토글 시), 호버 0.15초(피킹). 대량 셀 → indexFormat UInt32.
    //
    //  F키 맵: F5 지구 / F6 커버리지 / F7 영역 / F8 해치 / F9 자원 / F10 시민 / F11 건물 / F12 수요.
    //
    //  ※ 컨테이너 안전: stamp/RoadLayer를 메인스레드 RO로 읽음. 쓰기자(StampRebuild·
    //    RoadSystem)는 메인스레드라 Presentation 시점엔 완료. 병행 RO 잡(ServiceSearch 등)과는
    //    read-fence만 겹쳐 안전(다중 리더 허용).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class CoverageOverlaySystem : SystemBase
    {
        Material _mat;
        Mesh     _global, _hover;
        bool     _enabled;                 // F6
        float    _nextGlobalRT, _nextHoverRT;
        Entity   _hovered = Entity.Null;
        EntityQuery _bfQ;

        readonly List<Vector3> _v = new(4096);
        readonly List<Color>   _c = new(4096);
        readonly List<int>     _i = new(6144);

        static readonly Color CWarehouse = new(0.20f, 0.80f, 0.35f, 1f);  // 초록 — 창고 커버
        static readonly Color CSupplier  = new(1.00f, 0.55f, 0.15f, 1f);  // 주황 — 식당(Supplier) 커버
        static readonly Color CBoth      = new(0.25f, 0.80f, 0.85f, 1f);  // 청록 — 둘 다
        static readonly Color CNone      = new(0.55f, 0.55f, 0.55f, 1f);  // 회색 — 커버 없음
        static readonly Color CHover     = new(1.00f, 0.95f, 0.25f, 1f);  // 노랑 — 호버 건물 커버
        static readonly Color CHoverStale = new(1.00f, 0.35f, 0.90f, 1f); // 마젠타 — MaxDist가 현행 상수와 다름(낡은 반경)
        static readonly Color CHoverBreak = new(1.00f, 0.20f, 0.15f, 1f); // 빨강 — 단절 프런티어(비트 불일치로 못 건넌 인접 도로)
        static readonly Color CHoverForeign = new(0.25f, 0.50f, 1.00f, 1f); // 파랑 — 타-owner 도로 경계(포켓/국경 잘림)
        static readonly Color CAura      = new(0.62f, 0.36f, 0.95f, 1f);  // 보라 — 오라(치안 등) 커버 필드

        const float GlobalAlpha = 0.35f;
        const float HoverAlpha  = 0.70f;

        // ECS→UI 정적 미러(관례) — F6 전역 모드 상태. AuraLoadHud(치안 부하 라벨)가 읽는다.
        public static bool GlobalEnabled;

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetColor("_Color", Color.white);
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.LessEqual);
            _mat.renderQueue = (int)RenderQueue.Transparent;

            _global = NewMesh();
            _hover  = NewMesh();

            _bfQ = GetEntityQuery(ComponentType.ReadOnly<BuildingFootprint>());

            RequireForUpdate<StampLayers>();
            RequireForUpdate<GridLayers>();
            RequireForUpdate<GridSettings>();
        }

        static Mesh NewMesh()
        {
            var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            m.indexFormat = IndexFormat.UInt32;
            m.MarkDynamic();
            return m;
        }

        protected override void OnDestroy()
        {
            if (_mat != null)    Object.DestroyImmediate(_mat);
            if (_global != null) Object.DestroyImmediate(_global);
            if (_hover != null)  Object.DestroyImmediate(_hover);
        }

        protected override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f6Key.wasPressedThisFrame) _enabled = !_enabled;
            GlobalEnabled = _enabled;

            float cs = SystemAPI.GetSingleton<GridSettings>().CellSize;
            if (cs <= 0f) return;
            var stamp = SystemAPI.GetSingleton<StampLayers>();
            var road  = SystemAPI.GetSingleton<GridLayers>().RoadLayer;

            // ── ① 전역 (토글, 1초 스로틀 재구축) ──
            if (_enabled)
            {
                if (UnityEngine.Time.unscaledTime >= _nextGlobalRT)
                {
                    _nextGlobalRT = UnityEngine.Time.unscaledTime + 1.0f;
                    BuildGlobal(in stamp, in road, cs);
                }
                if (_global.vertexCount > 0)
                    Graphics.DrawMesh(_global, Matrix4x4.identity, _mat, 0);
            }

            // ── ② 호버 (상시, 0.15초 스로틀 피킹 + 갱신) ──
            if (UnityEngine.Time.unscaledTime >= _nextHoverRT)
            {
                _nextHoverRT = UnityEngine.Time.unscaledTime + 0.15f;
                _hovered = PickBuilding(cs);
                BuildHover(in stamp, in road, cs);
            }
            if (_hover.vertexCount > 0)
                Graphics.DrawMesh(_hover, Matrix4x4.identity, _mat, 0);
        }

        // 모든 도로셀 → 그 셀 owner의 stamp 커버 종류로 색칠. + 오라 필드(보라, 도로 아래층).
        void BuildGlobal(in StampLayers stamp, in NativeHashMap<int2, RoadCell> road, float cs)
        {
            _v.Clear(); _c.Clear(); _i.Clear();
            const float h = 0.05f;

            // 오라 커버 필드(커버형 욕구, 2026-07-12) — 도로 셀 색칠보다 낮은 층(0.042)에 전면 채움.
            //   도로가 아닌 셀(주거지 등)의 오라 커버가 그대로 보인다(치안 판정 = 집 원점 셀).
            if (SystemAPI.TryGetSingleton<AuraCoverage>(out var auraCov) && auraCov.Map.IsCreated)
            {
                var colA = CAura; colA.a = 0.22f;
                foreach (var kv in auraCov.Map)
                    AddCell(new int2(kv.Key.y, kv.Key.z), cs, 0.042f, colA);
            }

            foreach (var kv in road)
            {
                int owner = kv.Value.OwnerLocalId;
                Color col = CNone;
                if ((uint)owner < StampLayers.MaxPlayers)
                {
                    var map = stamp[owner];
                    bool wh = false, sup = false;
                    if (map.IsCreated && map.TryGetFirstValue(kv.Key, out var sr, out var it))
                    {
                        do
                        {
                            if (sr.Kind == StampKind.Warehouse)     wh  = true;
                            else if (sr.Kind == StampKind.Supplier) sup = true;
                        }
                        while (map.TryGetNextValue(out sr, ref it));
                    }
                    col = wh && sup ? CBoth : wh ? CWarehouse : sup ? CSupplier : CNone;
                }
                col.a = GlobalAlpha;
                AddCell(kv.Key, cs, h, col);
            }

            Upload(_global);
        }

        // 호버한 건물이 stamp로 커버하는 셀 전부 강조(공급자/창고 = stamp 소스).
        //   소비자(집·제분소 등, stamp 소스 아님)면 결과 없음 — 전역 오버레이가 그 셀 커버를 보여줌.
        //   + 커버 축소 진단(2026-07-11): 헤더 주석 ② 참조 — 빨강=비트 단절 / 마젠타=낡은 반경.
        void BuildHover(in StampLayers stamp, in NativeHashMap<int2, RoadCell> road, float cs)
        {
            _v.Clear(); _c.Clear(); _i.Clear();

            if (_hovered != Entity.Null && EntityManager.HasComponent<BuildingFootprint>(_hovered))
            {
                int owner = EntityManager.GetComponentData<BuildingFootprint>(_hovered).OwnerLocalId;
                if ((uint)owner < StampLayers.MaxPlayers)
                {
                    var map = stamp[owner];
                    if (map.IsCreated)
                    {
                        const float h = 0.09f;

                        // 시설의 stamp 반경 — 창고는 현행 상수와 비교(낡은 박제 반경 = 마젠타).
                        int maxDist = 0; bool stale = false;
                        if (EntityManager.HasComponent<WarehouseTag>(_hovered))
                        {
                            maxDist = EntityManager.GetComponentData<WarehouseTag>(_hovered).MaxDist;
                            stale   = maxDist != SpawnSystem.WarehouseStampMaxDist;
                        }
                        else if (EntityManager.HasComponent<StampSupplier>(_hovered))
                            maxDist = EntityManager.GetComponentData<StampSupplier>(_hovered).MaxDist;

                        var col = stale ? CHoverStale : CHover; col.a = HoverAlpha;

                        // 이 시설의 커버 셀(→BFS 거리) 수집 + 노랑/마젠타 채움.
                        var covered = new NativeHashMap<int2, int>(1024, Allocator.Temp);
                        var kva = map.GetKeyValueArrays(Allocator.Temp);
                        for (int i = 0; i < kva.Keys.Length; i++)
                            if (kva.Values[i].Supplier == _hovered)
                            {
                                covered.TryAdd(kva.Keys[i], kva.Values[i].Dist);
                                AddCell(kva.Keys[i], cs, h, col);
                            }
                        kva.Dispose();

                        // 단절 프런티어: 거리 여유가 남았는데(dist<maxDist) 물리 인접인데 stamp가
                        //   못 건넌 이웃 도로 — 같은 owner = 빨강(방향비트 불일치/일방 연결) /
                        //   타 owner = 파랑(소유 경계 — 포켓 잔존·국경 잘림). 빨강·파랑 없음 =
                        //   순수 거리 한계(정상 축소). maxDist<=0(무제한)은 전부 검사.
                        var breaks  = new NativeHashSet<int2>(64, Allocator.Temp);
                        var foreign = new NativeHashSet<int2>(64, Allocator.Temp);
                        foreach (var kv in covered)
                        {
                            if (maxDist > 0 && kv.Value >= maxDist) continue;   // 거리 한계 도달 셀은 정상
                            for (int d = 0; d < 4; d++)
                            {
                                int2 nb = kv.Key + Dir4(d);
                                if (covered.ContainsKey(nb)) continue;
                                if (!road.TryGetValue(nb, out var rc)) continue;
                                if (rc.OwnerLocalId == owner) breaks.Add(nb);
                                else                          foreign.Add(nb);
                            }
                        }
                        var colBreak = CHoverBreak; colBreak.a = HoverAlpha;
                        foreach (var b in breaks)
                            AddCell(b, cs, h + 0.01f, colBreak);
                        var colForeign = CHoverForeign; colForeign.a = HoverAlpha;
                        foreach (var f in foreign)
                            AddCell(f, cs, h + 0.01f, colForeign);
                        foreign.Dispose();
                        breaks.Dispose();
                        covered.Dispose();
                    }
                }

                // 오라 공급자 호버(2026-07-12): stamp 소스가 아니라 위 스캔에 안 잡힘 —
                //   오라 원(footprint-클램프 유클리드 제곱)을 직접 그림. 노랑(호버 관례).
                if (EntityManager.HasComponent<AuraSupplier>(_hovered))
                {
                    var aura = EntityManager.GetComponentData<AuraSupplier>(_hovered);
                    var afp  = EntityManager.GetComponentData<BuildingFootprint>(_hovered);
                    if (aura.Radius > 0)
                    {
                        int2 aeff = EntranceOps.RotateSize(afp.Size, afp.RotSteps);
                        int r2 = aura.Radius * aura.Radius;
                        var colA = CHover; colA.a = HoverAlpha;
                        for (int y = afp.Origin.y - aura.Radius; y <= afp.Origin.y + aeff.y - 1 + aura.Radius; y++)
                        for (int x = afp.Origin.x - aura.Radius; x <= afp.Origin.x + aeff.x - 1 + aura.Radius; x++)
                        {
                            int nx = math.clamp(x, afp.Origin.x, afp.Origin.x + aeff.x - 1);
                            int ny = math.clamp(y, afp.Origin.y, afp.Origin.y + aeff.y - 1);
                            int dx = x - nx, dy = y - ny;
                            if (dx * dx + dy * dy > r2) continue;
                            AddCell(new int2(x, y), cs, 0.09f, colA);
                        }
                    }
                }
            }

            Upload(_hover);
        }

        static int2 Dir4(int d) => d switch
        {
            0 => new int2(0, 1),
            1 => new int2(1, 0),
            2 => new int2(0, -1),
            _ => new int2(-1, 0),
        };

        void AddCell(int2 cell, float cs, float h, Color col)
        {
            float x0 = cell.x * cs, x1 = x0 + cs;
            float z0 = cell.y * cs, z1 = z0 + cs;
            int b = _v.Count;
            _v.Add(new Vector3(x0, h, z0));
            _v.Add(new Vector3(x0, h, z1));
            _v.Add(new Vector3(x1, h, z1));
            _v.Add(new Vector3(x1, h, z0));
            _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
            _i.Add(b); _i.Add(b + 1); _i.Add(b + 2);
            _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
        }

        void Upload(Mesh m)
        {
            m.Clear();
            if (_v.Count > 0)
            {
                m.SetVertices(_v);
                m.SetColors(_c);
                m.SetIndices(_i, MeshTopology.Triangles, 0, false);
            }
        }

        // 커서 아래 건물(footprint 포함). 없으면 Null. (BuildingInspectorHud 피킹과 동일.)
        Entity PickBuilding(float cs)
        {
            var mouse = Mouse.current;
            var cam   = Camera.main;
            if (mouse == null || cam == null) return Entity.Null;

            Ray ray = cam.ScreenPointToRay((Vector2)mouse.position.ReadValue());
            Vector3 p;
            if (Physics.Raycast(ray, out var hit, 5000f)) p = hit.point;
            else if (math.abs(ray.direction.y) > 1e-5f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t <= 0f) return Entity.Null;
                p = ray.origin + ray.direction * t;
            }
            else return Entity.Null;

            int2 cell = new int2((int)math.floor(p.x / cs), (int)math.floor(p.z / cs));

            var ents = _bfQ.ToEntityArray(Allocator.Temp);
            var fps  = _bfQ.ToComponentDataArray<BuildingFootprint>(Allocator.Temp);
            Entity found = Entity.Null;
            for (int i = 0; i < fps.Length; i++)
            {
                int2 eff = EntranceOps.RotateSize(fps[i].Size, fps[i].RotSteps);
                var o = fps[i].Origin;
                if (cell.x >= o.x && cell.x < o.x + eff.x &&
                    cell.y >= o.y && cell.y < o.y + eff.y)
                { found = ents[i]; break; }
            }
            ents.Dispose(); fps.Dispose();
            return found;
        }
    }
}
#endif
