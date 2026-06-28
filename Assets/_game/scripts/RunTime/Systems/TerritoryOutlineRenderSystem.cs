using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritoryOutlineRenderSystem — 영역 경계 아웃라인 상시 렌더 (소유팀 색)
    // ──────────────────────────────────────────────────────────────────────────
    //  GridLayers.TerritoryLayer를 읽어, '소유자가 다른(또는 영역 아님) 이웃과
    //  맞닿은 변'만 소유팀 색으로 GL 라인 렌더 → 각 팀 영역의 외곽선이 그려진다.
    //  (셀마다 4변 다 그리지 않고 경계 변만 → 깔끔 + 라인 수 절감.)
    //  RoadBuildPreviewRenderSystem과 동일한 URP endCameraRendering + GL 패턴.
    //  상시 ON(디버그 토글 아님) — 영역 시각화는 정식 기능.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class TerritoryOutlineRenderSystem : SystemBase
    {
        Material _mat;

        struct Seg { public float3 A, B; public Color C; }
        readonly List<Seg> _segs = new(512);
        bool _hasData;

        // 소유자(LocalId)별 색 (TerritoryDebugSystem과 동일 팔레트).
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

        // 4-이웃 오프셋.
        static readonly int2[] NB = { new int2(0, 1), new int2(1, 0), new int2(0, -1), new int2(-1, 0) };

        protected override void OnUpdate()
        {
            _hasData = false;
            _segs.Clear();

            var settings = SystemAPI.GetSingleton<GridSettings>();
            float cs = settings.CellSize;
            if (cs <= 0f) return;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;
            bool hasTerrain = layers.TerrainLayer.IsCreated;

            foreach (var kv in layers.TerritoryLayer)
            {
                int2 cell  = kv.Key;
                int  owner = kv.Value;
                if ((uint)owner >= OwnerColors.Length) continue;

                Color col = OwnerColors[owner];
                float h = (hasTerrain && layers.TerrainLayer.TryGetValue(cell, out var tc))
                    ? tc.Height * cs : 0f;
                h += 0.06f;

                float x0 = cell.x * cs, x1 = x0 + cs;
                float z0 = cell.y * cs, z1 = z0 + cs;

                for (int d = 0; d < 4; d++)
                {
                    // 같은 소유자 이웃과 맞닿은 변은 내부 → 스킵. 다르면 경계 변 → 그림.
                    if (layers.TerritoryLayer.TryGetValue(cell + NB[d], out int no) && no == owner)
                        continue;

                    float3 a, b;
                    switch (d)
                    {
                        case 0: a = new float3(x0, h, z1); b = new float3(x1, h, z1); break; // N
                        case 1: a = new float3(x1, h, z0); b = new float3(x1, h, z1); break; // E
                        case 2: a = new float3(x0, h, z0); b = new float3(x1, h, z0); break; // S
                        default:a = new float3(x0, h, z0); b = new float3(x0, h, z1); break; // W
                    }
                    _segs.Add(new Seg { A = a, B = b, C = col });
                }
            }
            _hasData = _segs.Count > 0;
        }

        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_hasData || _mat == null) return;
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;

            _mat.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            for (int i = 0; i < _segs.Count; i++)
            {
                var s = _segs[i];
                GL.Color(s.C);
                GL.Vertex3(s.A.x, s.A.y, s.A.z);
                GL.Vertex3(s.B.x, s.B.y, s.B.z);
            }
            GL.End();
            GL.PopMatrix();
        }
    }
}
