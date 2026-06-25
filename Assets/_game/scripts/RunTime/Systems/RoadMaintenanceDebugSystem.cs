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
    //  RoadMaintenanceDebugSystem — 디버그: 모든 팀의 도로 관리 coverage 시각화
    // ──────────────────────────────────────────────────────────────────────────
    //  StampLayers의 각 슬롯에서 Kind==RoadMaintenance 도장이 찍힌 도로셀을 소유자별
    //  색으로 그린다. **플레이어 본인은 제외(AI/적 전용)** — 본인 coverage는 "관리소 배치"
    //  오버레이가 이미 보여주고, 같은 셀에 겹쳐 그리면 색이 혼란스럽다(배치 청색과도 충돌).
    //  런타임 도장이 실제로 찍히는지 눈으로 확인하는 용도(Phase 5 검증). 기본 OFF, F6로 토글.
    //
    //  URP에서 PresentationSystemGroup의 GL 직접 호출은 카메라 렌더 밖이라 안 보이므로,
    //  RoadBuildPreviewRenderSystem과 동일하게 endCameraRendering 콜백에서 그린다.
    //  에디터/개발빌드 전용(#if) — 릴리스엔 포함 안 됨.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class RoadMaintenanceDebugSystem : SystemBase
    {
        Material _mat;
        bool     _enabled;

        struct DrawCell { public float3 Center; public Color Color; }
        readonly List<DrawCell> _cells = new(256);
        float _cellSize, _half;
        bool  _hasData;

        // 소유자(LocalId 0~7)별 색.
        //   "관리소 배치" 오버레이 색(초록 CoverageExisting / 금색 DepotExisting / 청색 Coverage)
        //   과 겹치지 않게 골랐다 — 둘이 동시에 보여도 구분되도록.
        static readonly Color[] OwnerColors =
        {
            new Color(0.95f, 0.25f, 0.25f, 1f), // 0 빨강
            new Color(0.90f, 0.30f, 0.85f, 1f), // 1 자홍
            new Color(0.65f, 0.35f, 0.95f, 1f), // 2 보라
            new Color(1.00f, 0.45f, 0.10f, 1f), // 3 주황
            new Color(1.00f, 0.40f, 0.65f, 1f), // 4 분홍
            new Color(0.55f, 0.40f, 0.25f, 1f), // 5 갈색
            new Color(0.55f, 0.55f, 0.60f, 1f), // 6 슬레이트
            new Color(0.60f, 0.10f, 0.15f, 1f), // 7 진홍
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

            RequireForUpdate<StampLayers>();
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
            // F6 토글.
            var kb = Keyboard.current;
            if (kb != null && kb.f6Key.wasPressedThisFrame)
            {
                _enabled = !_enabled;
                Debug.Log($"[RoadMaintenanceDebug] coverage 표시 {(_enabled ? "ON" : "OFF")} (F6)");
            }

            _hasData = false;
            _cells.Clear();
            if (!_enabled) return;

            var settings = SystemAPI.GetSingleton<GridSettings>();
            _cellSize = settings.CellSize;
            if (_cellSize <= 0f) return;
            _half = _cellSize * 0.5f;

            var stamp = SystemAPI.GetSingleton<StampLayers>();
            bool hasTerrain = SystemAPI.TryGetSingleton<GridLayers>(out var layers)
                              && layers.TerrainLayer.IsCreated;

            // 플레이어 본인 슬롯 제외(AI/적 전용). 본인 coverage는 배치 오버레이가 보여줌.
            int playerSlot = SystemAPI.TryGetSingleton<Game.Unit.UserPlayer>(out var up) ? up.LocalID : -1;

            for (int p = 0; p < StampLayers.MaxPlayers; p++)
            {
                if (p == playerSlot) continue;
                var map = stamp[p];
                if (!map.IsCreated) continue;

                Color col = OwnerColors[p % OwnerColors.Length];
                col.a = 0.5f;

                var seen = new NativeHashSet<int2>(256, Allocator.Temp);
                var kv   = map.GetKeyValueArrays(Allocator.Temp);
                for (int i = 0; i < kv.Length; i++)
                {
                    if (kv.Values[i].Kind != StampKind.RoadMaintenance) continue;
                    int2 cell = kv.Keys[i];
                    if (!seen.Add(cell)) continue;   // 같은 셀 다중 도장 → 1회만

                    float h = 0f;
                    if (hasTerrain && layers.TerrainLayer.TryGetValue(cell, out var tc))
                        h = tc.Height * _cellSize;

                    _cells.Add(new DrawCell
                    {
                        Center = new float3(cell.x * _cellSize + _half, h + 0.06f, cell.y * _cellSize + _half),
                        Color  = col,
                    });
                }
                kv.Dispose();
                seen.Dispose();
            }

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
#endif
