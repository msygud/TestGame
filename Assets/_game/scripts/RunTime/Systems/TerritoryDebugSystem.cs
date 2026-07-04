#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritoryDebugSystem — 영역 시각화 (에디터/개발빌드 전용, F7 토글, 기본 OFF)
    // ──────────────────────────────────────────────────────────────────────────
    //  GridLayers.TerritoryLayer의 팀 영역 셀을 소유팀 색으로 반투명 채움.
    //  영역 계산이 실제로 도는지 눈으로 확인하는 프로토타입 오버레이.
    //  (경합지(-2) 강조 — 띠/사선 — 는 TerritoryOutlineRenderSystem이 담당.)
    //
    //  렌더 경로: 동적 Mesh + Graphics.DrawMesh (renderQueue=Transparent).
    //    GL 즉시모드/endCameraRendering은 Unity 6 URP에서 Overlay UI '위'로 그려진다
    //    (UI가 파이프라인 내부 패스로 먼저 그려지고 콜백이 그 뒤). 투명 큐 메시는
    //    투명 패스(=UI 패스보다 앞)에서 그려져 UI가 위에 온다. ZTest=LessEqual이라
    //    유닛/건물이 앞이면 가린다. (TerritoryOutlineRenderSystem과 동일 패턴.)
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class TerritoryDebugSystem : SystemBase
    {
        Material _mat;
        Mesh     _mesh;
        bool     _enabled;          // F7로 토글
        uint     _builtVersion = uint.MaxValue;   // 메시를 구축한 TerritoryVersion(캐시 키)
        bool     _builtEnabled;

        // 메시 빌드 버퍼(재사용).
        readonly List<Vector3> _v = new(1024);
        readonly List<Color>   _c = new(1024);
        readonly List<int>     _i = new(1536);

        // 소유자(LocalId)별 색 (배치 오버레이 색과 겹치지 않게).
        static readonly Color[] OwnerColors =
        {
            new Color(0.20f, 0.55f, 1.00f, 1f), // 0 파랑
            new Color(0.95f, 0.30f, 0.30f, 1f), // 1 빨강
            new Color(0.30f, 0.85f, 0.45f, 1f), // 2 초록
            new Color(0.95f, 0.85f, 0.25f, 1f), // 3 노랑
            new Color(0.80f, 0.40f, 0.90f, 1f), // 4 보라
            new Color(0.30f, 0.85f, 0.90f, 1f), // 5 청록
            new Color(1.00f, 0.55f, 0.15f, 1f), // 6 주황
            new Color(0.85f, 0.85f, 0.85f, 1f), // 7 흰
        };

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

            _mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();

            RequireForUpdate<GridLayers>();
            RequireForUpdate<GridSettings>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)  Object.DestroyImmediate(_mat);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
        }

        protected override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f7Key.wasPressedThisFrame) _enabled = !_enabled;

            // 버전 게이트 — TerritoryLayer가 안 바뀌었으면 캐시 메시만 제출(재구축 스킵).
            uint ver = SystemAPI.TryGetSingleton<TerritoryVersion>(out var tv) ? tv.Value : 0;
            if (ver == _builtVersion && _enabled == _builtEnabled)
            {
                if (_enabled && _mesh.vertexCount > 0)
                    Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0);
                return;
            }
            _builtVersion = ver; _builtEnabled = _enabled;

            _v.Clear(); _c.Clear(); _i.Clear();
            if (!_enabled) { _mesh.Clear(); return; }

            var settings = SystemAPI.GetSingleton<GridSettings>();
            float cs = settings.CellSize;
            if (cs <= 0f) { _mesh.Clear(); return; }

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) { _mesh.Clear(); return; }
            bool hasTerrain = layers.TerrainLayer.IsCreated;

            foreach (var kv in layers.TerritoryLayer)
            {
                int owner = kv.Value;
                if ((uint)owner >= OwnerColors.Length) continue;   // 경합지(-2)/중립 제외

                Color c = OwnerColors[owner];
                c.a = 0.35f;

                float x0 = kv.Key.x * cs, x1 = x0 + cs;
                float z0 = kv.Key.y * cs, z1 = z0 + cs;
                float h  = (hasTerrain && layers.TerrainLayer.TryGetValue(kv.Key, out var tc))
                    ? tc.Height * cs : 0f;
                h += 0.04f;

                int b = _v.Count;
                _v.Add(new Vector3(x0, h, z0));
                _v.Add(new Vector3(x0, h, z1));
                _v.Add(new Vector3(x1, h, z1));
                _v.Add(new Vector3(x1, h, z0));
                _c.Add(c); _c.Add(c); _c.Add(c); _c.Add(c);
                _i.Add(b); _i.Add(b + 1); _i.Add(b + 2);
                _i.Add(b); _i.Add(b + 2); _i.Add(b + 3);
            }

            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetIndices(_i, MeshTopology.Triangles, 0, false);
                Graphics.DrawMesh(_mesh, Matrix4x4.identity, _mat, 0);
            }
        }
    }
}
#endif
