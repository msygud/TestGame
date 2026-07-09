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
    //
    //  렌더: 동적 Mesh + Graphics.DrawMesh(Transparent) — DemandHeatmap과 동일 패턴.
    //  스로틀: 전역 1초(토글 시), 호버 0.15초(피킹). 대량 셀 → indexFormat UInt32.
    //
    //  F키 맵: F6 커버리지 / F7 영역 / F8 해치 / F9 자원 / F10 시민 / F11 건물 / F12 수요.
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

        const float GlobalAlpha = 0.35f;
        const float HoverAlpha  = 0.70f;

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
                BuildHover(in stamp, cs);
            }
            if (_hover.vertexCount > 0)
                Graphics.DrawMesh(_hover, Matrix4x4.identity, _mat, 0);
        }

        // 모든 도로셀 → 그 셀 owner의 stamp 커버 종류로 색칠.
        void BuildGlobal(in StampLayers stamp, in NativeHashMap<int2, RoadCell> road, float cs)
        {
            _v.Clear(); _c.Clear(); _i.Clear();
            const float h = 0.05f;

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
        void BuildHover(in StampLayers stamp, float cs)
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
                        var col = CHover; col.a = HoverAlpha;

                        var kva = map.GetKeyValueArrays(Allocator.Temp);
                        for (int i = 0; i < kva.Keys.Length; i++)
                            if (kva.Values[i].Supplier == _hovered)
                                AddCell(kva.Keys[i], cs, h, col);
                        kva.Dispose();
                    }
                }
            }

            Upload(_hover);
        }

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
