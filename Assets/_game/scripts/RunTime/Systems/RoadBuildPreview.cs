using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadBuildPreview — 런타임 도로 건설 프리뷰 (색 마커) · URP 대응
    //
    //  설계:
    //    - 입력 컨트롤러(RoadBuildController)가 PreviewCell 버퍼에 "어느 셀을
    //      어떤 유효성으로 보여줄지" 채운다.
    //    - 이 시스템은 그 버퍼를 매 프레임 관리 캐시(_cells)에 복사하고,
    //      URP의 RenderPipelineManager.endCameraRendering 콜백에서 GL로 그린다.
    //    - URP에서는 PresentationSystemGroup의 GL 직접 호출이 카메라 렌더 밖이라
    //      화면에 안 나온다. 반드시 endCameraRendering 콜백 안에서 그려야 한다.
    //
    //    - 모양(비트마스크) 결정은 프리뷰에서 하지 않는다. 마커는 위치/유효성만.
    //      실제 모양은 확정 후 RoadSystem이 "실제 연결 상태"로만 파생.
    // ══════════════════════════════════════════════════════════════════════════

    public enum PreviewKind : byte
    {
        Pending  = 0,   // 확정 대기 (이미 뗀 구간)
        Dragging = 1,   // 현재 드래그 중
    }

    /// <summary>프리뷰 마커 한 칸.</summary>
    [InternalBufferCapacity(64)]
    public struct PreviewCell : IBufferElementData
    {
        public int2        Cell;
        public bool        Valid;   // true=초록(가능), false=빨강(불가)
        public PreviewKind Kind;
    }

    /// <summary>
    /// 프리뷰 버퍼를 담는 싱글톤.
    /// RoadBuildController가 이 엔티티의 PreviewCell 버퍼를 매 프레임 다시 채운다.
    /// </summary>
    public struct RoadBuildPreviewState : IComponentData
    {
        public bool Active;
        public byte RoadSize;  // 도로 한 변 셀 수 (1이상), 프리뷰 쿼드 크기 결정
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  렌더 시스템 (관리형 — URP 콜백 + GL)
    // ──────────────────────────────────────────────────────────────────────────
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class RoadBuildPreviewRenderSystem : SystemBase
    {
        Material _mat;

        // 색
        static readonly Color ValidPending     = new Color(0.20f, 0.85f, 0.30f, 0.45f);
        static readonly Color InvalidPending   = new Color(0.90f, 0.20f, 0.20f, 0.45f);
        static readonly Color ValidDragging    = new Color(0.30f, 0.95f, 0.45f, 0.65f);
        static readonly Color InvalidDragging  = new Color(1.00f, 0.30f, 0.30f, 0.65f);

        // 콜백이 그릴 데이터의 스냅샷 (메인스레드 캐시)
        struct DrawCell { public float3 Center; public Color Color; }
        readonly List<DrawCell> _cells = new(128);
        float _cellSize;
        bool  _hasData;

        protected override void OnCreate()
        {
            var shader = Shader.Find("Hidden/Internal-Colored");
            _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _mat.SetInt("_Cull",   (int)CullMode.Off);
            _mat.SetInt("_ZWrite", 0);
            _mat.SetInt("_ZTest",  (int)CompareFunction.Always); // 항상 위에 그림

            RequireForUpdate<RoadBuildPreviewState>();
            RequireForUpdate<GridSettings>();

            // URP: 카메라 렌더가 끝나는 시점에 GL로 덧그린다.
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        protected override void OnDestroy()
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            if (_mat != null) Object.DestroyImmediate(_mat);
        }

        float _halfExtent;  // = RoadSize * cellSize * 0.5f

        // 메인스레드: 버퍼 → 그리기 캐시로 스냅샷.
        protected override void OnUpdate()
        {
            _hasData = false;
            _cells.Clear();

            var state = SystemAPI.GetSingleton<RoadBuildPreviewState>();
            if (!state.Active) return;

            var settings = SystemAPI.GetSingleton<GridSettings>();
            _cellSize = settings.CellSize;
            if (_cellSize <= 0f) return;

            int roadSize = math.max(1, state.RoadSize);
            _halfExtent = roadSize * _cellSize * 0.5f;

            var previewEntity = SystemAPI.GetSingletonEntity<RoadBuildPreviewState>();
            if (!EntityManager.HasBuffer<PreviewCell>(previewEntity)) return;

            var buf = EntityManager.GetBuffer<PreviewCell>(previewEntity);
            if (buf.Length == 0) return;

            for (int i = 0; i < buf.Length; i++)
            {
                var pc = buf[i];
                Color c = pc.Kind == PreviewKind.Dragging
                    ? (pc.Valid ? ValidDragging : InvalidDragging)
                    : (pc.Valid ? ValidPending  : InvalidPending);

                // Cell은 footprint 원점(좌하단). 쿼드 중심 = 원점 + halfExtent
                float cx = pc.Cell.x * _cellSize + _halfExtent;
                float cz = pc.Cell.y * _cellSize + _halfExtent;
                _cells.Add(new DrawCell { Center = new float3(cx, 0.05f, cz), Color = c });
            }
            _hasData = _cells.Count > 0;
        }

        // URP 콜백: 카메라 렌더 직후 GL 즉시모드로 그림.
        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!_hasData || _mat == null) return;

            // 게임/씬 카메라만 (프리뷰·리플렉션 카메라 제외)
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView)
                return;

            float h = _halfExtent;

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
