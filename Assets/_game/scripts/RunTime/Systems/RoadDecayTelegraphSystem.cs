using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadDecayTelegraphSystem — 미관리 도로 decay 진행도 시각화(압박감)
    // ──────────────────────────────────────────────────────────────────────────
    //  RoadDecayState.Unmaintained(셀 → 미관리 시작 게임시각)를 읽어, 각 미관리
    //  도로셀을 **진행도(노랑→빨강)** 로 칠한다. 진행도 = (now − since) / 유예.
    //  임박(진행도↑)이면 alpha를 펄스시켜 "곧 부서짐"을 예고 → 플레이어가 관리시설을
    //  잇기 전에 압박감을 느낀다. 전역 day 일괄이 아니라 셀별 연속 타이머라, 셀마다
    //  제 진행도로 칠해진다.
    //
    //  항상 ON(게임플레이 피드백). RoadBuildPreviewRenderSystem과 동일 GL+URP 패턴.
    //  ※ 프로토타입 오버레이 — 추후 도로 균열 셰이더/디칼/VFX로 대체 가능.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class RoadDecayTelegraphSystem : SystemBase
    {
        Material _mat;

        struct DrawCell { public float3 Center; public Color Color; }
        readonly List<DrawCell> _cells = new(256);
        float _cellSize, _half;
        bool  _hasData;

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.Always);

            RequireForUpdate<RoadDecayState>();
            RequireForUpdate<GameClock>();
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
            _hasData = false;
            _cells.Clear();

            var decay = SystemAPI.GetSingleton<RoadDecayState>();
            if (!decay.Unmaintained.IsCreated || decay.GraceDays <= 0) return;

            var clock = SystemAPI.GetSingleton<GameClock>();
            double grace = (double)decay.GraceDays * clock.SecondsPerDay;
            if (grace <= 0.0) return;
            double now = clock.TotalSeconds;

            var settings = SystemAPI.GetSingleton<GridSettings>();
            _cellSize = settings.CellSize;
            if (_cellSize <= 0f) return;
            _half = _cellSize * 0.5f;

            bool hasTerrain = SystemAPI.TryGetSingleton<GridLayers>(out var layers)
                              && layers.TerrainLayer.IsCreated;

            // 임박(진행도↑) 셀 깜빡임 — 긴박감. (SystemBase의 Time은 ECS TimeData라
            //   실시간 펄스는 UnityEngine.Time을 명시.)
            float pulse = 0.7f + 0.3f * math.sin(UnityEngine.Time.time * 6f);

            var kv = decay.Unmaintained.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < kv.Length; i++)
            {
                int2   cell  = kv.Keys[i];
                double since = kv.Values[i];
                float  prog  = math.saturate((float)((now - since) / grace));

                // 노랑(0) → 빨강(1).
                Color c = Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(1f, 0.1f, 0.1f), prog);
                float a = math.lerp(0.25f, 0.7f, prog);
                if (prog > 0.66f) a *= pulse;   // 임박이면 깜빡
                c.a = a;

                float h = 0f;
                if (hasTerrain && layers.TerrainLayer.TryGetValue(cell, out var tc))
                    h = tc.Height * _cellSize;

                _cells.Add(new DrawCell
                {
                    Center = new float3(cell.x * _cellSize + _half, h + 0.07f, cell.y * _cellSize + _half),
                    Color  = c,
                });
            }
            kv.Dispose();

            _hasData = _cells.Count > 0;
        }

        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_hasData || _mat == null) return;
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView) return;

            float h = _half;
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
