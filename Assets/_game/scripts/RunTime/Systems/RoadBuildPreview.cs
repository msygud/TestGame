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
        Disconnected     = 8,  // 내 기존 도로망에 연결 안 됨 — 건설 안 됨(연속성)
        Coverage         = 9,  // 새로 배치할 관리시설의 도달 범위 (청색, 비차단)
        CoverageExisting = 10, // 기존 관리시설들의 도달 범위 = 현재 관리되는 도로(연결성, 초록, 비차단)
        DepotExisting    = 11, // 기존 관리시설 위치 마커 (금색, 비차단)
        Entrance         = 12, // 건물 입구가 향하는 도로셀 — 사각 테두리로 표시 (비차단)
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
            || s == PreviewStatus.ResourceBlocked
            || s == PreviewStatus.Disconnected;

        /// <summary>대상 오브젝트(점유물/철거 예정물)에 외곽선 강조를 줄지.</summary>
        public static bool ShowOutline(PreviewStatus s)
            => s == PreviewStatus.Occupied || s == PreviewStatus.WillClear
            || s == PreviewStatus.Entrance;

        /// <summary>유저에게 보일 사유 텍스트 (HUD 라벨용).</summary>
        public static string ToText(PreviewStatus s) => s switch
        {
            PreviewStatus.Valid          => "Buildable",
            PreviewStatus.Occupied       => "Blocked: occupied",
            PreviewStatus.OutOfBounds    => "Blocked: out of bounds",
            PreviewStatus.HeightMismatch => "Blocked: height mismatch",
            PreviewStatus.ParallelWarn   => "Warning: parallel road — not connected",
            PreviewStatus.OwnerWarn      => "Warning: other player's road — not connected",
            PreviewStatus.ResourceBlocked => "Blocked: resource",
            PreviewStatus.WillClear      => "Clears object, then builds",
            PreviewStatus.Disconnected   => "Not built: disconnected from road network",
            PreviewStatus.Coverage       => "Coverage",
            PreviewStatus.CoverageExisting => "Existing coverage",
            PreviewStatus.DepotExisting  => "Existing depot",
            PreviewStatus.Entrance       => "Entrance",
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
        public byte RoadSize;   // 도로 한 변 셀 수 (1이상), 프리뷰 쿼드 크기 결정
        public int2 Center;     // 페이딩 그리드 중심(현재 호버/드래그 셀). 컨트롤러가 매 프레임 set.
        public bool HasCenter;  // Center 유효 여부(호버 없음 시 false → 그리드 안 그림)
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  렌더 시스템 (관리형 — 동적 Mesh + Graphics.DrawMesh)
    // ──────────────────────────────────────────────────────────────────────────
    //  렌더 경로: GL 즉시모드/endCameraRendering은 Unity 6 URP에서 Overlay UI '위'로
    //  그려진다(UI가 파이프라인 내부 패스로 먼저, 콜백이 그 뒤). 그래서 동적 Mesh를
    //  renderQueue=Transparent로 DrawMesh → 투명 패스(=UI 패스보다 앞)에서 그려져
    //  Overlay UI가 위에 온다. 빌드 툴 특성상 마커는 항상 보여야 하므로 ZTest=Always
    //  유지(유닛/건물에 안 가림). (TerritoryOutlineRenderSystem과 동일 이행, ZTest만 다름.)
    // ──────────────────────────────────────────────────────────────────────────
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class RoadBuildPreviewRenderSystem : SystemBase
    {
        Material _mat;
        Mesh     _lineMesh;   // 그리드 + 점유 외곽선 (MeshTopology.Lines)
        Mesh     _quadMesh;   // 채움 쿼드 (삼각형)

        // 메시 빌드 버퍼(재사용).
        readonly List<Vector3> _lv = new(2048);
        readonly List<Color>   _lc = new(2048);
        readonly List<int>     _li = new(2048);
        readonly List<Vector3> _qv = new(512);
        readonly List<Color>   _qc = new(512);
        readonly List<int>     _qi = new(768);

        const int GridRadius = 8;   // 프리뷰 중심에서 이 반경(셀)까지 그리드, 멀수록 옅게

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
            PreviewStatus.Disconnected   => new Color(0.45f, 0.45f, 0.50f, 1f), // 회색 (미연결)
            PreviewStatus.Coverage       => new Color(0.20f, 0.55f, 1.00f, 1f), // 청색 (새 관리 범위)
            PreviewStatus.CoverageExisting => new Color(0.30f, 0.80f, 0.45f, 1f), // 초록 (기존 관리 범위)
            PreviewStatus.DepotExisting  => new Color(1.00f, 0.80f, 0.20f, 1f), // 금색 (기존 관리소 위치)
            PreviewStatus.Entrance       => new Color(0.20f, 0.95f, 1.00f, 1f), // 밝은 청록 (입구 테두리)
            _                            => Color.white,
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
            _mat.SetInt("_ZTest",  (int)CompareFunction.Always); // 빌드 툴 — 항상 위에 그림
            _mat.renderQueue = (int)RenderQueue.Transparent;     // 투명 패스(UI 패스보다 앞) → UI 아래

            _lineMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _quadMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            _lineMesh.MarkDynamic();
            _quadMesh.MarkDynamic();

            RequireForUpdate<RoadBuildPreviewState>();
            RequireForUpdate<GridSettings>();
        }

        protected override void OnDestroy()
        {
            if (_mat != null)      Object.DestroyImmediate(_mat);
            if (_lineMesh != null) Object.DestroyImmediate(_lineMesh);
            if (_quadMesh != null) Object.DestroyImmediate(_quadMesh);
        }

        // 메인스레드: 버퍼 → 메시 빌드 → DrawMesh.
        protected override void OnUpdate()
        {
            _lv.Clear(); _lc.Clear(); _li.Clear();
            _qv.Clear(); _qc.Clear(); _qi.Clear();

            var state = SystemAPI.GetSingleton<RoadBuildPreviewState>();
            if (!state.Active) { Flush(); return; }

            var settings = SystemAPI.GetSingleton<GridSettings>();
            float cs = settings.CellSize;
            if (cs <= 0f) { Flush(); return; }

            int   roadSize   = math.max(1, state.RoadSize);
            float halfExtent = roadSize * cs * 0.5f;

            // 지형 높이 소스 (그리드·마커 Y).
            bool hasTerrain = SystemAPI.TryGetSingleton<GridLayers>(out var layers)
                              && layers.TerrainLayer.IsCreated;

            // ── 페이딩 그리드 (프리뷰 중심 ±GridRadius, 멀수록 옅게) ──────────
            if (state.HasCenter)
            {
                int2 ctr  = state.Center;
                float inv = 1f / GridRadius;
                for (int dz = -GridRadius; dz <= GridRadius; dz++)
                for (int dx = -GridRadius; dx <= GridRadius; dx++)
                {
                    float dist = math.sqrt(dx * dx + dz * dz);
                    if (dist > GridRadius) continue;
                    float a = (1f - dist * inv) * 0.35f;   // 멀수록 옅게(최대 0.35)
                    if (a <= 0.02f) continue;
                    int2 cc = ctr + new int2(dx, dz);
                    float gh = 0.04f;
                    if (hasTerrain && layers.TerrainLayer.TryGetValue(cc, out var tg))
                        gh += tg.Height * cs;
                    float x0 = cc.x * cs, x1 = x0 + cs;
                    float z0 = cc.y * cs, z1 = z0 + cs;
                    Color gc = new Color(1f, 1f, 1f, a);
                    AddLine(new Vector3(x0, gh, z0), new Vector3(x1, gh, z0), gc); // S변
                    AddLine(new Vector3(x0, gh, z0), new Vector3(x0, gh, z1), gc); // W변
                }
            }

            var previewEntity = SystemAPI.GetSingletonEntity<RoadBuildPreviewState>();
            if (EntityManager.HasBuffer<PreviewCell>(previewEntity))
            {
                var buf = EntityManager.GetBuffer<PreviewCell>(previewEntity);
                for (int i = 0; i < buf.Length; i++)
                {
                    var pc = buf[i];

                    Color c = StatusColor(pc.Status);
                    // 확정 대기는 살짝 옅게, 드래그 중은 진하게.
                    c.a = pc.Kind == PreviewKind.Dragging ? 0.65f : 0.45f;

                    // Cell은 footprint 원점(좌하단). 쿼드 중심 = 원점 + halfExtent
                    float cx = pc.Cell.x * cs + halfExtent;
                    float cz = pc.Cell.y * cs + halfExtent;
                    float height = 0f;
                    if (hasTerrain && layers.TerrainLayer.TryGetValue(pc.Cell, out var tc))
                        height = tc.Height * cs;
                    float y = height + 0.05f;

                    AddQuad(cx, y, cz, halfExtent, c);
                    if (PreviewStatusOps.ShowOutline(pc.Status))
                        AddOutline(cx, y + 0.02f, cz, halfExtent, new Color(1f, 1f, 1f, 0.95f));
                }
            }

            Flush();
        }

        // ── 메시 빌드 헬퍼 ──────────────────────────────────────────────────────
        void AddLine(Vector3 a, Vector3 b, Color c)
        {
            int i = _lv.Count;
            _lv.Add(a); _lv.Add(b);
            _lc.Add(c); _lc.Add(c);
            _li.Add(i); _li.Add(i + 1);
        }

        // 중심(cx,y,cz) 기준 한 변 2*h 쿼드(삼각형 2개).
        void AddQuad(float cx, float y, float cz, float h, Color c)
        {
            int i = _qv.Count;
            _qv.Add(new Vector3(cx - h, y, cz - h));
            _qv.Add(new Vector3(cx - h, y, cz + h));
            _qv.Add(new Vector3(cx + h, y, cz + h));
            _qv.Add(new Vector3(cx + h, y, cz - h));
            _qc.Add(c); _qc.Add(c); _qc.Add(c); _qc.Add(c);
            _qi.Add(i); _qi.Add(i + 1); _qi.Add(i + 2);
            _qi.Add(i); _qi.Add(i + 2); _qi.Add(i + 3);
        }

        // 사각 외곽선 4변(라인).
        void AddOutline(float cx, float y, float cz, float h, Color c)
        {
            var p0 = new Vector3(cx - h, y, cz - h);
            var p1 = new Vector3(cx - h, y, cz + h);
            var p2 = new Vector3(cx + h, y, cz + h);
            var p3 = new Vector3(cx + h, y, cz - h);
            AddLine(p0, p1, c); AddLine(p1, p2, c); AddLine(p2, p3, c); AddLine(p3, p0, c);
        }

        // 빌드된 버퍼를 메시에 올리고 DrawMesh로 큐잉(투명 패스 → UI 아래).
        void Flush()
        {
            _lineMesh.Clear();
            if (_lv.Count > 0)
            {
                _lineMesh.SetVertices(_lv);
                _lineMesh.SetColors(_lc);
                _lineMesh.SetIndices(_li, MeshTopology.Lines, 0, false);
                Graphics.DrawMesh(_lineMesh, Matrix4x4.identity, _mat, 0);
            }

            _quadMesh.Clear();
            if (_qv.Count > 0)
            {
                _quadMesh.SetVertices(_qv);
                _quadMesh.SetColors(_qc);
                _quadMesh.SetIndices(_qi, MeshTopology.Triangles, 0, false);
                Graphics.DrawMesh(_quadMesh, Matrix4x4.identity, _mat, 0);
            }
        }
    }
}
