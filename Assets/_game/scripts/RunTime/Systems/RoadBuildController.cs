using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadBuildController — 런타임 도로 건설 입력 컨트롤러 (MonoBehaviour)
    //
    //  책임:
    //    1. 도로 건설 모드 진입/해제 (EnterBuildMode / ExitBuildMode)
    //    2. 마우스 드래그를 받아 셀 경로 계산 (자동 축 고정 + L자)
    //    3. 한 드래그(누름→뗌) = 1 segment. pending 목록에 누적.
    //    4. 프리뷰 버퍼 갱신 → RoadBuildPreviewRenderSystem이 색 마커로 그림.
    //    5. Undo() = 마지막 segment 통째 취소.
    //    6. Confirm() = pending 전체를 PlaceRoadCommand로 발행. RoadSystem이
    //       비트마스크·모양·이웃 갱신을 자동 처리.
    //
    //  설계 메모:
    //    - 모양(비트마스크)은 여기서 절대 계산하지 않는다. 마커는 위치/유효성만.
    //      실제 모양은 확정 후 RoadSystem.ComputeDirections가 "실제 연결"로 파생.
    //    - 드래그 축은 "어느 칸을 채울지"만 결정. "각 칸의 모양"과 무관.
    //    - 좌표 변환은 GridSettings.CellSize 규약(셀 중심 = idx*cs + cs*0.5)을
    //      그대로 따른다. CellSize를 박지 않고 싱글톤에서 읽는다.
    //
    //  사용:
    //    씬에 빈 GameObject 하나 만들고 이 컴포넌트 부착.
    //    BuildCamera(없으면 Camera.main), GroundLayerMask 설정.
    //    FactionId는 UserPlayer→FactionConfig에서 런타임 자동 해소(설정 불필요).
    //    UI 버튼 OnClick에서 EnterBuildMode / Undo / Confirm / ExitBuildMode 연결.
    // ══════════════════════════════════════════════════════════════════════════
    public class RoadBuildController : MonoBehaviour
    {
        [Header("참조")]
        [Tooltip("레이캐스트에 쓸 카메라. 비우면 Camera.main.")]
        public Camera BuildCamera;

        [Tooltip("지면 레이캐스트용 레이어 마스크. 비우면 평면 y=0과 교차.")]
        public LayerMask GroundLayerMask = ~0;

        [Header("도로 설정")]
        [Tooltip("차선 수 (2 기본, 4 업그레이드).")]
        public byte LaneCount = 2;

        [Tooltip("도로 비주얼 프리팹 레지스트리. 크기 결정에는 사용하지 않음 (항상 1×1).")]
        public RoadPrefabRegistry Registry;

        [Header("상태 (읽기용)")]
        [SerializeField] bool _modeActive;
        [SerializeField] int  _pendingCellCount;
        [SerializeField] int  _segmentCount;
        [Tooltip("UserPlayer→FactionConfig로 해소된 플레이어 슬롯/팩션 (런타임 자동).")]
        [SerializeField] int  _playerSlot   = -1;
        [SerializeField] int  _playerFaction = -1;

        public bool IsModeActive => _modeActive;
        public int  SegmentCount => _segmentCount;

        // 현재 호버 셀의 상태 (HUD 사유 표시용). 호버 없으면 Valid.
        PreviewStatus _hoverStatus   = PreviewStatus.Valid;
        OccupantType  _hoverOccupant = OccupantType.None;  // 점유 종류 (Occupied일 때)
        public PreviewStatus HoverStatus => _hoverStatus;
        public bool          HasHover    => _hasHover;
        public string        HoverStatusText
        {
            get
            {
                if (!_hasHover) return string.Empty;
                // 점유 셀이면 무엇이 막는지(건물/유닛/지형) 같이 표시.
                if (_hoverStatus == PreviewStatus.Occupied
                    && _hoverOccupant != OccupantType.None)
                    return $"건설 불가: {OccupantText(_hoverOccupant)} 점유";
                return PreviewStatusOps.ToText(_hoverStatus);
            }
        }

        static string OccupantText(OccupantType t) => t switch
        {
            OccupantType.Road        => "도로",
            OccupantType.Building     => "건물",
            OccupantType.Environment => "환경물",
            _                        => "오브젝트",
        };

        // ── 내부 상태 ──────────────────────────────────────────────
        // 각 segment = 그 드래그가 만든 셀 목록 (순서 보존, 중복 제거).
        readonly List<List<int2>> _segments = new();

        bool       _dragging;
        int2       _dragStart;
        List<int2> _currentDrag = new();   // 현재 드래그 중 셀 (확정 전)
        readonly List<int2> _hoverList = new(1);  // 호버 단일 셀 평가용 (재사용)
        bool       _hasHover;
        int2       _hoverCell;

        EntityManager _em;
        Entity        _previewEntity;
        bool          _ecsReady;

        // ───────────────────────────────────────────────────────────
        //  Unity 수명주기
        // ───────────────────────────────────────────────────────────
        void Awake()
        {
            if (BuildCamera == null) BuildCamera = Camera.main;
        }

        void Update()
        {
            if (!_modeActive) return;
            if (!EnsureEcs()) return;

            HandleDragInput();
            RebuildPreviewBuffer();
        }

        void OnDisable()
        {
            // 모드 켜진 채 비활성화되면 프리뷰 정리
            if (_modeActive) ExitBuildMode();
        }

        // ───────────────────────────────────────────────────────────
        //  공개 API — UI 버튼에서 호출
        // ───────────────────────────────────────────────────────────

        /// <summary>도로 건설 모드 진입.</summary>
        public void EnterBuildMode()
        {
            if (!EnsureEcs()) return;
            _modeActive = true;
            _segments.Clear();
            _currentDrag.Clear();
            _dragging = false;
            SetPreviewActive(true);
            RebuildPreviewBuffer();
        }

        /// <summary>도로 건설 모드 해제. 미확정 pending은 폐기.</summary>
        public void ExitBuildMode()
        {
            _modeActive = false;
            _dragging = false;
            _segments.Clear();
            _currentDrag.Clear();
            if (_ecsReady)
            {
                SetPreviewActive(false);
                ClearPreviewBuffer();
            }
            _pendingCellCount = 0;
            _segmentCount = 0;
        }

        /// <summary>마지막 segment 통째 취소. 드래그 중이면 현재 드래그를 취소.</summary>
        public void Undo()
        {
            if (!_modeActive) return;

            if (_dragging)
            {
                // 드래그 도중 Undo = 현재 드래그 무효화
                _dragging = false;
                _currentDrag.Clear();
            }
            else if (_segments.Count > 0)
            {
                _segments.RemoveAt(_segments.Count - 1);
            }
            RebuildPreviewBuffer();
        }

        /// <summary>pending 전체를 실제 도로로 확정. 유효한 셀만 명령 발행.</summary>
        public void Confirm()
        {
            if (!_modeActive || !EnsureEcs()) return;

            // 플레이어 슬롯/팩션 해소 (UserPlayer → FactionConfig). 실패 시 중단.
            if (!ResolvePlayerFaction(out int slot, out int faction))
            {
                Debug.LogWarning("[RoadBuildController] 플레이어 팩션 해소 실패 — "
                               + "UserPlayer/FactionConfig 싱글톤을 확인하세요.");
                return;
            }

            // 모든 segment의 셀을 합치고 중복 제거.
            // 모양은 RoadSystem이 알아서 계산하므로 순서는 무관하지만,
            // 한 셀당 한 번만 명령 발행되도록 집합으로 정리.
            var placed = new HashSet<int2>(new Int2Comparer());
            var layers = GetLayers(out bool hasLayers);
            byte roadSize = 1;

            foreach (var seg in _segments)
            {
                if (seg.Count == 0) continue;

                // 중간에 건물/자원/단차가 하나라도 있으면 이 구간 전체를 건설하지 않음.
                if (hasLayers &&
                    PreviewStatusOps.IsBlocking(SegmentBlockStatus(seg, slot, layers)))
                    continue;

                RoadPlacedAxis segAxis = SegmentAxis(seg);

                foreach (var cell in seg)
                {
                    if (!placed.Add(cell)) continue;

                    RoadPlacedAxis axis = segAxis;

                    var e = _em.CreateEntity();
                    _em.AddComponentData(e, new PlaceRoadCommand
                    {
                        Cell         = cell,
                        OwnerLocalId = slot,
                        LaneCount    = LaneCount,
                        FactionId    = faction,
                        Size         = roadSize,
                        Axis         = axis,
                    });
                }
            }

            // 확정 후 pending 비움. 모드는 유지(계속 더 그릴 수 있게).
            _segments.Clear();
            _currentDrag.Clear();
            _dragging = false;
            RebuildPreviewBuffer();
        }

        // ───────────────────────────────────────────────────────────
        //  드래그 입력
        // ───────────────────────────────────────────────────────────
        void HandleDragInput()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            // UI 위 포인터면 새 드래그를 시작하지 않는다.
            // (Undo/Confirm 버튼 클릭이 유령 드래그→세그먼트 추가로 새던 버그 방지.
            //  진행 중 드래그는 그대로 유지해 UI 위를 지나가도 끊기지 않게 함.)
            if (!_dragging && EventSystem.current != null
                && EventSystem.current.IsPointerOverGameObject())
            {
                _hasHover = false;
                return;
            }

            if (!TryGetHoverCell(out int2 cell))
            {
                _hasHover = false;
                return;
            }
            _hasHover  = true;
            _hoverCell = cell;

            int step = CurrentRoadSize();
            if (mouse.leftButton.wasPressedThisFrame)
            {
                _dragging  = true;
                _dragStart = cell;   // 시작점은 1셀 정밀도 그대로
                _currentDrag = BuildPath(_dragStart, cell, step);
            }
            else if (_dragging && mouse.leftButton.isPressed)
            {
                _currentDrag = BuildPath(_dragStart, cell, step);
            }
            else if (_dragging && mouse.leftButton.wasReleasedThisFrame)
            {
                _dragging = false;
                var path = BuildPath(_dragStart, cell, step);
                if (path.Count > 0)
                    _segments.Add(path);
                _currentDrag.Clear();
            }
        }

        // ───────────────────────────────────────────────────────────
        //  경로 계산 — 단일 축 직선
        //
        //  드래그 이동량이 큰 축 하나만 사용 (L자 없음).
        //  동점이면 수평(X) 우선.
        //  → 코너 셀(Axis=Any) 생성을 원천 차단.
        // ───────────────────────────────────────────────────────────
        static List<int2> BuildPath(int2 start, int2 end, int step)
        {
            var path = new List<int2>();

            int dx = end.x - start.x;
            int dz = end.y - start.y;

            // 더 많이 움직인 축 하나만 사용
            if (math.abs(dx) >= math.abs(dz))
            {
                // 수평(X) 직선
                int ex = start.x + (dx / step) * step;
                int sx = (int)math.sign(ex - start.x);
                int x  = start.x;
                path.Add(new int2(x, start.y));
                while (x != ex) { x += sx; path.Add(new int2(x, start.y)); }
            }
            else
            {
                // 수직(Z) 직선
                int ez = start.y + (dz / step) * step;
                int sz = (int)math.sign(ez - start.y);
                int z  = start.y;
                path.Add(new int2(start.x, z));
                while (z != ez) { z += sz; path.Add(new int2(start.x, z)); }
            }

            return path;
        }

        // 세그먼트(셀 목록)의 배치 축. 단일 축 직선이므로 양 끝으로 판정.
        // 셀 1개 이하면 축 미정 → Any.
        static RoadPlacedAxis SegmentAxis(List<int2> seg)
        {
            if (seg == null || seg.Count < 2) return RoadPlacedAxis.Any;
            return seg[seg.Count - 1].x != seg[0].x
                ? RoadPlacedAxis.EW : RoadPlacedAxis.NS;
        }

        // 세그먼트 전체 차단 평가.
        //   "중간에 건물/자원/단차가 있으면 그 구간 전체를 건설불가"
        //   - 건물(Occupied)/자원(ResourceBlocked)/범위밖(OutOfBounds): 즉시 반환
        //   - 단차(HeightMismatch): 세그먼트 내 모든 셀의 지형 높이가 같아야 함
        //     (도로는 경사를 가로지를 수 없음). 단차 너머 기존 도로와는 "연결만"
        //     안 될 뿐 배치는 허용하므로 여기서 막지 않는다.
        //   - 환경물은 차단 아님(배치 시 철거)
        //  차단 사유 없으면 Valid.
        PreviewStatus SegmentBlockStatus(List<int2> seg, int ownerSlot, GridLayers layers)
        {
            bool first = true;
            byte baseH = 0;

            foreach (var cell in seg)
            {
                if (layers.RoadLayer.TryGetValue(cell, out var hereRoad))
                {
                    if (ownerSlot >= 0 && hereRoad.OwnerLocalId != ownerSlot)
                        return PreviewStatus.Occupied;
                    if (CurrentRoadSize() > 1)
                        return PreviewStatus.Occupied;
                }
                else
                {
                    if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty
                        && occ.Type != OccupantType.Environment)
                        return PreviewStatus.Occupied;
                    if (layers.ResourceLayer.IsCreated
                        && layers.ResourceLayer.TryGetValue(cell, out var res) && res.Amount > 0)
                        return PreviewStatus.ResourceBlocked;
                    if (layers.TerrainLayer.IsCreated && !layers.TerrainLayer.ContainsKey(cell))
                        return PreviewStatus.OutOfBounds;
                }

                byte h = layers.TerrainLayer.IsCreated
                      && layers.TerrainLayer.TryGetValue(cell, out var tc) ? tc.Height : (byte)0;

                // 세그먼트 내부 단차만 차단 (도로는 경사를 가로지를 수 없음).
                // 단차 너머 기존 도로와는 "연결만" 안 됨(배치는 허용) → 여기서 막지 않는다.
                if (first) { baseH = h; first = false; }
                else if (h != baseH) return PreviewStatus.HeightMismatch;
            }
            return PreviewStatus.Valid;
        }

        // 세그먼트를 프리뷰 버퍼에 추가. 세그먼트 차단 사유가 있으면 전 셀을
        // 그 사유로 표시(전체 빨강), 아니면 셀별 상태(환경철거/경고/가능)로 표시.
        // 반환: 추가한 셀 수.
        int AddSegmentPreview(DynamicBuffer<PreviewCell> buf, List<int2> seg,
                              PreviewKind kind, int ownerSlot, GridLayers layers, bool hasLayers)
        {
            if (seg.Count == 0) return 0;

            PreviewStatus segBlock = hasLayers
                ? SegmentBlockStatus(seg, ownerSlot, layers) : PreviewStatus.Valid;
            bool blocked = PreviewStatusOps.IsBlocking(segBlock);
            RoadPlacedAxis axis = SegmentAxis(seg);

            foreach (var cell in seg)
            {
                var st = blocked ? segBlock
                       : (hasLayers ? EvaluateCell(cell, axis, ownerSlot, layers)
                                    : PreviewStatus.Valid);
                buf.Add(new PreviewCell { Cell = cell, Status = st, Kind = kind });
            }
            return seg.Count;
        }

        // ───────────────────────────────────────────────────────────
        //  프리뷰 버퍼 갱신
        // ───────────────────────────────────────────────────────────
        void RebuildPreviewBuffer()
        {
            if (!_ecsReady) return;

            // RoadSize를 매 프레임 동기화 (Registry.DefaultSize 변경 대응)
            var previewState = _em.GetComponentData<RoadBuildPreviewState>(_previewEntity);
            byte curSize = CurrentRoadSize();
            if (previewState.RoadSize != curSize)
            {
                previewState.RoadSize = curSize;
                _em.SetComponentData(_previewEntity, previewState);
            }

            var buf = _em.GetBuffer<PreviewCell>(_previewEntity);
            buf.Clear();

            var layers = GetLayers(out bool hasLayers);

            // 프리뷰용 소유자 슬롯 해소 (실패해도 점유/범위 검사는 계속).
            int ownerSlot = hasLayers && ResolvePlayerFaction(out int s, out _) ? s : -1;

            // 확정 대기 segment들
            int cellCount = 0;
            foreach (var seg in _segments)
                cellCount += AddSegmentPreview(buf, seg, PreviewKind.Pending, ownerSlot, layers, hasLayers);

            // 드래그 중이면 드래그 경로, 아니면 호버 셀 1개
            if (_dragging)
            {
                AddSegmentPreview(buf, _currentDrag, PreviewKind.Dragging, ownerSlot, layers, hasLayers);
            }
            else if (_hasHover)
            {
                _hoverList.Clear();
                _hoverList.Add(_hoverCell);
                AddSegmentPreview(buf, _hoverList, PreviewKind.Dragging, ownerSlot, layers, hasLayers);

                // HUD 사유 표시용 호버 상태/점유 종류
                if (hasLayers)
                {
                    var hb = SegmentBlockStatus(_hoverList, ownerSlot, layers);
                    _hoverStatus = PreviewStatusOps.IsBlocking(hb)
                        ? hb
                        : EvaluateCell(_hoverCell, RoadPlacedAxis.Any, ownerSlot, layers);
                }
                else _hoverStatus = PreviewStatus.Valid;

                _hoverOccupant = OccupantType.None;
                if (_hoverStatus == PreviewStatus.Occupied && hasLayers)
                {
                    if (layers.OccupancyLayer.TryGetValue(_hoverCell, out var occ) && !occ.IsEmpty)
                        _hoverOccupant = occ.Type;
                    else if (layers.RoadLayer.ContainsKey(_hoverCell))
                        _hoverOccupant = OccupantType.Road;
                }
            }

            if (!_hasHover) { _hoverStatus = PreviewStatus.Valid; _hoverOccupant = OccupantType.None; }

            _pendingCellCount = cellCount;
            _segmentCount = _segments.Count;
        }

        void ClearPreviewBuffer()
        {
            if (!_ecsReady) return;
            var buf = _em.GetBuffer<PreviewCell>(_previewEntity);
            buf.Clear();
        }

        // ───────────────────────────────────────────────────────────
        //  셀 상태 평가 — 점유/단차/연결 사유를 구분해 반환
        //
        //  비차단 셀의 셀별 상태(환경철거/경고/가능)만 판정한다.
        //  차단 사유(건물/자원/범위/세그먼트 단차)는 SegmentBlockStatus가 담당.
        //  단차 너머 이웃 도로는 "연결만" 안 함(여기선 막지도 경고하지도 않음).
        //
        //  axis  : 이 셀이 속한 드래그/세그먼트의 배치 축 (호버 단독이면 Any).
        //  ownerSlot : 플레이어 슬롯 (-1 = 미해소 → 소유자 검사 생략).
        // ───────────────────────────────────────────────────────────
        PreviewStatus EvaluateCell(int2 cell, RoadPlacedAxis axis, int ownerSlot,
                                   GridLayers layers)
        {
            // 기존 도로 위?
            if (layers.RoadLayer.TryGetValue(cell, out var hereRoad))
            {
                // 타 플레이어 도로 위에는 배치 불가.
                if (ownerSlot >= 0 && hereRoad.OwnerLocalId != ownerSlot)
                    return PreviewStatus.Occupied;
                // 멀티셀 도로는 기존 도로와 겹치면 끼워넣기가 되므로 불가.
                if (CurrentRoadSize() > 1)
                    return PreviewStatus.Occupied;
                // 같은 소유자 1×1: 재배치 허용 → 아래 연결 검사 계속.
            }
            bool willClear = false;   // 환경물 위 → 배치 시 철거됨(비차단, 프리뷰 강조)

            // 기존 도로 위가 아니어도 환경물이 함께 있을 수 있음(겹쳐 등록).
            if (layers.OccupancyLayer.TryGetValue(cell, out var hereOcc)
                && hereOcc.Type == OccupantType.Environment)
                willClear = true;

            if (!layers.RoadLayer.ContainsKey(cell))
            {
                // 점유(건물 등)면 불가. 환경(나무/바위)은 배치 시 치우므로 막지 않음.
                if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty
                    && occ.Type != OccupantType.Environment)
                    return PreviewStatus.Occupied;

                // 채취 자원 위면 불가 (자원 보존 — ResourceLayer가 단일 소스).
                if (layers.ResourceLayer.IsCreated
                    && layers.ResourceLayer.TryGetValue(cell, out var res) && res.Amount > 0)
                    return PreviewStatus.ResourceBlocked;

                // 맵 범위 밖이면 불가 (TerrainLayer에 셀이 없으면 범위 밖).
                if (layers.TerrainLayer.IsCreated && !layers.TerrainLayer.ContainsKey(cell))
                    return PreviewStatus.OutOfBounds;
            }

            // ── 연결 검사 (단차 / 평행 / 소유자) ──
            byte myHeight = layers.TerrainLayer.IsCreated
                         && layers.TerrainLayer.TryGetValue(cell, out var tc)
                ? tc.Height : (byte)0;

            bool ownerKnown  = ownerSlot >= 0;
            bool ownerWarn   = false;
            bool parallelWarn = false;

            for (int i = 0; i < 4; i++)
            {
                var n = cell + RoadDirOps.Offsets[i];
                if (!layers.RoadLayer.TryGetValue(n, out var nc)) continue;

                // 타 플레이어 도로 → 연결 안 됨 (경고). 단차 무관.
                if (ownerKnown && nc.OwnerLocalId != ownerSlot) { ownerWarn = true; continue; }

                // 단차 너머 도로 → 연결 안 됨(배치는 허용). 경고도 아님 — 그냥 안 이어짐.
                byte nh = layers.TerrainLayer.IsCreated
                       && layers.TerrainLayer.TryGetValue(n, out var nt)
                    ? nt.Height : (byte)0;
                if (nh != myHeight) continue;

                // 축 필터: 평행 동축이라 연결 안 되는가? (둘 다 같은 평행 축, Any 아님)
                bool isEW = RoadDirOps.Offsets[i].x != 0;
                bool myAllows = axis == RoadPlacedAxis.Any
                    || (axis == RoadPlacedAxis.EW && isEW)
                    || (axis == RoadPlacedAxis.NS && !isEW);
                bool nbAllows = nc.Axis == RoadPlacedAxis.Any
                    || (nc.Axis == RoadPlacedAxis.EW && isEW)
                    || (nc.Axis == RoadPlacedAxis.NS && !isEW);
                if (!(myAllows || nbAllows))
                    parallelWarn = true;
            }

            // 철거 예정(환경물)은 경고보다 우선 표시 — 어떤 오브젝트가 사라지는지
            // 외곽선으로 강조해 보여주기 위함. (단차 등 차단은 위에서 이미 반환됨)
            if (willClear)    return PreviewStatus.WillClear;
            if (parallelWarn) return PreviewStatus.ParallelWarn;
            if (ownerWarn)    return PreviewStatus.OwnerWarn;
            return PreviewStatus.Valid;
        }

        // ───────────────────────────────────────────────────────────
        //  레이캐스트 → 셀
        // ───────────────────────────────────────────────────────────
        bool TryGetHoverCell(out int2 cell)
        {
            cell = default;
            if (BuildCamera == null) return false;
            if (!SystemAPI_TryGetCellSize(out float cs) || cs <= 0f) return false;

            var mouse = Mouse.current;
            if (mouse == null) return false;
            Ray ray = BuildCamera.ScreenPointToRay(
                (Vector2)mouse.position.ReadValue());

            // 1) 콜라이더 우선
            if (Physics.Raycast(ray, out var hit, 5000f, GroundLayerMask))
            {
                cell = WorldToCell(hit.point, cs);
                return true;
            }

            // 2) 평면 y=0 교차 (콜라이더 없을 때)
            if (math.abs(ray.direction.y) > 1e-5f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t > 0f)
                {
                    Vector3 p = ray.origin + ray.direction * t;
                    cell = WorldToCell(p, cs);
                    return true;
                }
            }
            return false;
        }

        static int2 WorldToCell(Vector3 world, float cs)
        {
            // CellCenter 규약 역산: idx = floor(world / cs)
            int cx = (int)math.floor(world.x / cs);
            int cz = (int)math.floor(world.z / cs);
            return new int2(cx, cz);
        }

        // ───────────────────────────────────────────────────────────
        //  ECS 접근 헬퍼
        // ───────────────────────────────────────────────────────────
        bool EnsureEcs()
        {
            if (_ecsReady && _em != default && _em.Exists(_previewEntity)) return true;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            _em = world.EntityManager;

            // 프리뷰 싱글톤 확보(없으면 생성).
            var q = _em.CreateEntityQuery(typeof(RoadBuildPreviewState));
            if (q.IsEmpty)
            {
                _previewEntity = _em.CreateEntity(
                    typeof(RoadBuildPreviewState));
                _em.AddBuffer<PreviewCell>(_previewEntity);
                _em.SetComponentData(_previewEntity, new RoadBuildPreviewState { Active = false });
            }
            else
            {
                _previewEntity = q.GetSingletonEntity();
                if (!_em.HasBuffer<PreviewCell>(_previewEntity))
                    _em.AddBuffer<PreviewCell>(_previewEntity);
            }
            q.Dispose();

            _ecsReady = true;
            return true;
        }

        void SetPreviewActive(bool active)
        {
            if (!_ecsReady) return;
            _em.SetComponentData(_previewEntity, new RoadBuildPreviewState
            {
                Active   = active,
                RoadSize = CurrentRoadSize(),
            });
        }

        byte CurrentRoadSize() => 1;

        GridLayers GetLayers(out bool ok)
        {
            ok = false;
            var q = _em.CreateEntityQuery(typeof(GridLayers));
            if (q.IsEmpty) { q.Dispose(); return default; }
            var layers = q.GetSingleton<GridLayers>();
            q.Dispose();
            ok = layers.RoadLayer.IsCreated;
            return layers;
        }

        // ───────────────────────────────────────────────────────────
        //  플레이어 슬롯/팩션 해소
        //
        //  "어떤 플레이어가 어떤 팩션인가"라는 런타임 흔적을 활용:
        //    UserPlayer.LocalID            → 플레이어 슬롯
        //    FactionConfig.Slots[slot]     → 그 슬롯의 FactionId
        //
        //  호출자 책임 원칙: 유저 경로(이 컨트롤러)가 직접 해소해 명령에 담는다.
        //  한 번 해소되면 캐시(_playerSlot/_playerFaction)를 재사용.
        // ───────────────────────────────────────────────────────────
        bool ResolvePlayerFaction(out int slot, out int faction)
        {
            // 캐시 히트
            if (_playerSlot >= 0 && _playerFaction >= 0)
            {
                slot = _playerSlot;
                faction = _playerFaction;
                return true;
            }

            slot = -1;
            faction = -1;

            // UserPlayer 싱글톤 → 플레이어 슬롯
            var upq = _em.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Unit.UserPlayer>());
            if (upq.IsEmpty) { upq.Dispose(); return false; }
            var userPlayer = upq.GetSingleton<Game.Unit.UserPlayer>();
            upq.Dispose();
            slot = userPlayer.LocalID;
            if (slot < 0) return false;

            // FactionConfig.Slots[slot] → FactionId
            var fcq = _em.CreateEntityQuery(
                ComponentType.ReadOnly<FactionConfig>());
            if (fcq.IsEmpty) { fcq.Dispose(); return false; }
            var config = fcq.GetSingleton<FactionConfig>();
            fcq.Dispose();

            if (!config.Slots.IsCreated) return false;
            if (!config.Slots.TryGetValue(slot, out var fslot)) return false;
            if (fslot.FactionId < 0) return false;

            faction = fslot.FactionId;

            // 캐시
            _playerSlot = slot;
            _playerFaction = faction;
            return true;
        }

        bool SystemAPI_TryGetCellSize(out float cs)
        {
            cs = 0f;
            var q = _em.CreateEntityQuery(typeof(GridSettings));
            if (q.IsEmpty) { q.Dispose(); return false; }
            cs = q.GetSingleton<GridSettings>().CellSize;
            q.Dispose();
            return true;
        }

        // int2 HashSet 비교자
        struct Int2Comparer : IEqualityComparer<int2>
        {
            public bool Equals(int2 a, int2 b) => a.x == b.x && a.y == b.y;
            public int GetHashCode(int2 v) => (v.x * 397) ^ v.y;
        }
    }
}
