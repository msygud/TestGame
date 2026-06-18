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
    //      어떤 상태(PreviewStatus)로 보여줄지" 채운다.
    //    - 이 시스템은 그 버퍼를 매 프레임 관리 캐시(_cells)에 복사하고,
    //      URP의 RenderPipelineManager.endCameraRendering 콜백에서 GL로 그린다.
    //    - URP에서는 PresentationSystemGroup의 GL 직접 호출이 카메라 렌더 밖이라
    //      화면에 안 나온다. 반드시 endCameraRendering 콜백 안에서 그려야 한다.
    //
    //    - 모양(비트마스크) 결정은 프리뷰에서 하지 않는다. 마커는 위치/상태만.
    //      실제 모양은 확정 후 RoadSystem이 "실제 연결 상태"로만 파생.
    //
    //    - 셀 Y는 지형 높이(TerrainCell.Height)를 따라간다. 평지(0)뿐 아니라
    //      단차 지형 위에서도 마커가 지표면에 붙는다.
    // ══════════════════════════════════════════════════════════════════════════

    public enum PreviewKind : byte
    {
        Pending  = 0,   // 확정 대기 (이미 뗀 구간)
        Dragging = 1,   // 현재 드래그 중
    }

    /// <summary>
    /// 프리뷰 셀의 상태. 색·외곽선·건설 가능 여부를 결정.
    ///   Valid          — 건설 가능 (초록)
    ///   Occupied       — 건물/유닛/지형/타 플레이어 도로가 점유 → 건설 불가 (빨강 + 외곽선)
    ///   OutOfBounds    — 맵 범위 밖 → 건설 불가 (어두운 빨강)
    ///   HeightMismatch — 연결될 인접 도로와 단차 불일치 → 건설 불가 (주황)
    ///   ParallelWarn   — 평행 동축 도로라 연결 안 됨 (경고, 건설은 가능 · 노랑)
    ///   OwnerWarn      — 인접한 타 플레이어 도로와 연결 안 됨 (경고 · 자홍)
    /// </summary>
    public enum PreviewStatus : byte
    {
        Valid           = 0,
        Occupied        = 1,
        OutOfBounds     = 2,
        HeightMismatch  = 3,
        ParallelWarn    = 4,
        OwnerWarn       = 5,
        ResourceBlocked = 6,  // 채취 자원 위 — 건설 불가 (자원 보존)
        WillClear       = 7,  // 환경물(나무/바위) 위 — 건설 가능, 배치 시 철거됨
    }

    /// <summary>프리뷰 마커 한 칸.</summary>
    [InternalBufferCapacity(64)]
    public struct PreviewCell : IBufferElementData
    {
        public int2          Cell;
        public PreviewStatus Status;
        public PreviewKind   Kind;
    }

    public static class PreviewStatusOps
    {
        /// <summary>이 상태가 배치를 막는 상태인가 (건설 불가).</summary>
        public static bool IsBlocking(PreviewStatus s)
            => s == PreviewStatus.Occupied
            || s == PreviewStatus.OutOfBounds
            || s == PreviewStatus.HeightMismatch
            || s == PreviewStatus.ResourceBlocked;

        /// <summary>대상 오브젝트(점유물/철거 예정물)에 외곽선 강조를 줄지.</summary>
        public static bool ShowOutline(PreviewStatus s)
            => s == PreviewStatus.Occupied || s == PreviewStatus.WillClear;

        /// <summary>유저에게 보일 사유 텍스트 (HUD 라벨용).</summary>
        public static string ToText(PreviewStatus s) => s switch
        {
            PreviewStatus.Valid          => "건설 가능",
            PreviewStatus.Occupied       => "건설 불가: 오브젝트 점유",
            PreviewStatus.OutOfBounds    => "건설 불가: 맵 범위 밖",
            PreviewStatus.HeightMismatch => "건설 불가: 단차 불일치",
            PreviewStatus.ParallelWarn   => "경고: 평행 도로 — 연결 안 됨",
            PreviewStatus.OwnerWarn      => "경고: 타 플레이어 도로 — 연결 안 됨",
            PreviewStatus.ResourceBlocked => "건설 불가: 자원 점유",
            PreviewStatus.WillClear      => "환경물 철거 후 건설",
            _                            => string.Empty,
        };
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

        // 상태별 색 (RGBA). 알파는 Kind(Pending/Dragging)로 가감.
        static Color StatusColor(PreviewStatus s) => s switch
        {
            PreviewStatus.Valid          => new Color(0.25f, 0.90f, 0.40f, 1f),  // 초록
            PreviewStatus.Occupied       => new Color(0.95f, 0.20f, 0.20f, 1f),  // 빨강
            PreviewStatus.OutOfBounds    => new Color(0.55f, 0.10f, 0.10f, 1f),  // 어두운 빨강
            PreviewStatus.HeightMismatch => new Color(1.00f, 0.55f, 0.10f, 1f),  // 주황
            PreviewStatus.ParallelWarn   => new Color(0.95f, 0.90f, 0.20f, 1f),  // 노랑
            PreviewStatus.OwnerWarn      => new Color(0.85f, 0.30f, 0.90f, 1f),  // 자홍
            PreviewStatus.ResourceBlocked => new Color(0.10f, 0.70f, 0.85f, 1f), // 청록 (자원)
            PreviewStatus.WillClear      => new Color(0.55f, 0.40f, 0.20f, 1f), // 갈색 (철거 예정)
            _                            => Color.white,
        };

        // 콜백이 그릴 데이터의 스냅샷 (메인스레드 캐시)
        struct DrawCell { public float3 Center; public Color Color; public bool Outline; }
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

            // 지형 높이를 따라 마커 Y를 올린다 (단차 지형 대응).
            bool hasTerrain = SystemAPI.TryGetSingleton<GridLayers>(out var layers)
                              && layers.TerrainLayer.IsCreated;

            for (int i = 0; i < buf.Length; i++)
            {
                var pc = buf[i];

                Color c = StatusColor(pc.Status);
                // 확정 대기는 살짝 옅게, 드래그 중은 진하게.
                c.a = pc.Kind == PreviewKind.Dragging ? 0.65f : 0.45f;

                // Cell은 footprint 원점(좌하단). 쿼드 중심 = 원점 + halfExtent
                float cx = pc.Cell.x * _cellSize + _halfExtent;
                float cz = pc.Cell.y * _cellSize + _halfExtent;

                float height = 0f;
                if (hasTerrain && layers.TerrainLayer.TryGetValue(pc.Cell, out var tc))
                    height = tc.Height * _cellSize;

                _cells.Add(new DrawCell
                {
                    Center  = new float3(cx, height + 0.05f, cz),
                    Color   = c,
                    Outline = PreviewStatusOps.ShowOutline(pc.Status),
                });
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

            // 1) 채움 쿼드
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

            // 2) 점유 오브젝트 강조 외곽선 (대상 오브젝트가 무엇인지 식별 가능하게)
            GL.Begin(GL.LINES);
            for (int i = 0; i < _cells.Count; i++)
            {
                var d = _cells[i];
                if (!d.Outline) continue;
                GL.Color(new Color(1f, 1f, 1f, 0.95f));   // 흰 테두리로 대상 강조
                float x = d.Center.x, y = d.Center.y + 0.02f, z = d.Center.z;
                // 사각 외곽선 4변
                GL.Vertex3(x - h, y, z - h); GL.Vertex3(x - h, y, z + h);
                GL.Vertex3(x - h, y, z + h); GL.Vertex3(x + h, y, z + h);
                GL.Vertex3(x + h, y, z + h); GL.Vertex3(x + h, y, z - h);
                GL.Vertex3(x + h, y, z - h); GL.Vertex3(x - h, y, z - h);
            }
            GL.End();

            GL.PopMatrix();
        }
    }
}
