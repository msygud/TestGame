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
    //  GridLayers.TerritoryLayer의 셀을 소유자별 색으로 GL 반투명 채움.
    //  영역 계산/capture가 실제로 도는지 눈으로 확인하는 프로토타입 오버레이.
    //  (RoadBuildPreviewRenderSystem GL 패턴 재사용 — URP endCameraRendering.)
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class TerritoryDebugSystem : SystemBase
    {
        Material _mat;
        bool     _enabled;          // F7로 토글

        struct DrawCell { public float3 Center; public Color Color; }
        readonly List<DrawCell> _cells = new(256);
        float _cellSize;
        bool  _hasData;

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
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.Always);

            RequireForUpdate<GridLayers>();
            RequireForUpdate<GridSettings>();

            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        protected override void OnDestroy()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            if (_mat != null) Object.DestroyImmediate(_mat);
        }

        protected override void OnUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f7Key.wasPressedThisFrame) _enabled = !_enabled;

            _hasData = false;
            _cells.Clear();
            if (!_enabled) return;

            var settings = SystemAPI.GetSingleton<GridSettings>();
            _cellSize = settings.CellSize;
            if (_cellSize <= 0f) return;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;
            bool hasTerrain = layers.TerrainLayer.IsCreated;

            float half = _cellSize * 0.5f;
            foreach (var kv in layers.TerritoryLayer)
            {
                int owner = kv.Value;
                if ((uint)owner >= OwnerColors.Length) continue;

                Color c = OwnerColors[owner];
                c.a = 0.35f;

                float cx = kv.Key.x * _cellSize + half;
                float cz = kv.Key.y * _cellSize + half;
                float h  = (hasTerrain && layers.TerrainLayer.TryGetValue(kv.Key, out var tc))
                    ? tc.Height * _cellSize : 0f;

                _cells.Add(new DrawCell { Center = new float3(cx, h + 0.04f, cz), Color = c });
            }
            _hasData = _cells.Count > 0;
        }

        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_hasData || _mat == null) return;
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;

            float h = _cellSize * 0.5f;
            _mat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.QUADS);
            for (int i = 0; i < _cells.Count; i++)
            {
                var d = _cells[i];
                GL.Color(d.Color);
                float x = d.Center.x, y = d.Center.y, z = d.Center.z;
                GL.Vertex3(x - h, y, z - h);
                GL.Vertex3(x - h, y, z + h);
                GL.Vertex3(x + h, y, z + h);
                GL.Vertex3(x + h, y, z - h);
            }
            GL.End();
            GL.PopMatrix();
        }
    }
}
#endif
