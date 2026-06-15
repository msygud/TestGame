using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

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

        [Tooltip("도로 크기 출처. 지정 시 Registry.DefaultSize를 사용. 비우면 Size 필드 직접 사용.")]
        public RoadPrefabRegistry Registry;

        [Tooltip("Registry가 없을 때 사용할 도로 크기 (한 변 셀 수). 1 = 1×1.")]
        [Range(1, 8)]
        public int Size = 1;

        [Header("상태 (읽기용)")]
        [SerializeField] bool _modeActive;
        [SerializeField] int  _pendingCellCount;
        [SerializeField] int  _segmentCount;
        [Tooltip("UserPlayer→FactionConfig로 해소된 플레이어 슬롯/팩션 (런타임 자동).")]
        [SerializeField] int  _playerSlot   = -1;
        [SerializeField] int  _playerFaction = -1;

        public bool IsModeActive => _modeActive;
        public int  SegmentCount => _segmentCount;

        // ── 내부 상태 ──────────────────────────────────────────────
        // 각 segment = 그 드래그가 만든 셀 목록 (순서 보존, 중복 제거).
        readonly List<List<int2>> _segments = new();

        bool       _dragging;
        int2       _dragStart;
        List<int2> _currentDrag = new();   // 현재 드래그 중 셀 (확정 전)

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

            foreach (var seg in _segments)
            {
                foreach (var cell in seg)
                {
                    if (!placed.Add(cell)) continue;          // 이미 발행
                    if (hasLayers && !IsCellPlaceable(cell, layers)) continue; // 불가 셀 스킵

                    var e = _em.CreateEntity();
                    _em.AddComponentData(e, new PlaceRoadCommand
                    {
                        Cell         = cell,
                        OwnerLocalId = slot,
                        LaneCount    = LaneCount,
                        FactionId    = faction,
                        Size         = (byte)(Registry != null ? Registry.DefaultSize : Size),
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
            if (!TryGetHoverCell(out int2 cell)) return;

            if (Input.GetMouseButtonDown(0))
            {
                _dragging = true;
                _dragStart = cell;
                _currentDrag = BuildPath(_dragStart, cell);
            }
            else if (_dragging && Input.GetMouseButton(0))
            {
                _currentDrag = BuildPath(_dragStart, cell);
            }
            else if (_dragging && Input.GetMouseButtonUp(0))
            {
                _dragging = false;
                var path = BuildPath(_dragStart, cell);
                if (path.Count > 0)
                {
                    _segments.Add(path);   // 한 드래그 = 한 segment
                }
                _currentDrag.Clear();
            }
        }

        // ───────────────────────────────────────────────────────────
        //  경로 계산 — 자동 축 고정 + L자
        //
        //  start→end 를 격자 직선으로 보정한다.
        //    - 가로 이동량 ≥ 세로 이동량 → 가로 우세.
        //    - 한 축만 있으면 직선.
        //    - 두 축 다 있으면 L자: "가로 먼저, 그다음 세로" (사용자 선택 옵션 1).
        //  대각선·계단식 없음. 살짝 어긋나도 항상 직선/L자.
        // ───────────────────────────────────────────────────────────
        static List<int2> BuildPath(int2 start, int2 end)
        {
            var path = new List<int2>();

            int dx = end.x - start.x;
            int dz = end.y - start.y;
            int adx = math.abs(dx);
            int adz = math.abs(dz);

            int sx = (int)math.sign(dx);
            int sz = (int)math.sign(dz);

            // 코너 지점: 가로 먼저 진행 → (end.x, start.z) 에서 꺾어 세로로.
            int2 corner = new int2(end.x, start.y);

            // 1) 가로 구간: start.x → end.x (start.z 고정)
            int x = start.x;
            path.Add(new int2(x, start.y));
            while (x != end.x)
            {
                x += sx;
                path.Add(new int2(x, start.y));
            }

            // 2) 세로 구간: corner.z → end.z (end.x 고정), 코너 중복 제외
            int z = start.y;
            while (z != end.y)
            {
                z += sz;
                path.Add(new int2(end.x, z));
            }

            return path;
        }

        // ───────────────────────────────────────────────────────────
        //  프리뷰 버퍼 갱신
        // ───────────────────────────────────────────────────────────
        void RebuildPreviewBuffer()
        {
            if (!_ecsReady) return;
            var buf = _em.GetBuffer<PreviewCell>(_previewEntity);
            buf.Clear();

            var layers = GetLayers(out bool hasLayers);

            // 확정 대기 segment들
            int cellCount = 0;
            foreach (var seg in _segments)
            {
                foreach (var cell in seg)
                {
                    bool valid = !hasLayers || IsCellPlaceable(cell, layers);
                    buf.Add(new PreviewCell { Cell = cell, Valid = valid, Kind = PreviewKind.Pending });
                    cellCount++;
                }
            }

            // 현재 드래그 중 segment
            if (_dragging)
            {
                foreach (var cell in _currentDrag)
                {
                    bool valid = !hasLayers || IsCellPlaceable(cell, layers);
                    buf.Add(new PreviewCell { Cell = cell, Valid = valid, Kind = PreviewKind.Dragging });
                }
            }

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
        //  유효성 — 점유 여부 (도로 끼리 겹침은 허용: 모양만 갱신)
        // ───────────────────────────────────────────────────────────
        bool IsCellPlaceable(int2 cell, GridLayers layers)
        {
            // 이미 도로면 "가능"으로 본다(겹쳐 그어도 모양만 갱신, RoadSystem에서 처리).
            if (layers.RoadLayer.ContainsKey(cell)) return true;

            // 그 외 점유(건물/유닛/지형)면 불가.
            if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty)
                return false;

            // 맵 범위 밖이면 불가 (TerrainLayer에 셀이 없으면 범위 밖으로 간주).
            if (layers.TerrainLayer.IsCreated && !layers.TerrainLayer.ContainsKey(cell))
                return false;

            return true;
        }

        // ───────────────────────────────────────────────────────────
        //  레이캐스트 → 셀
        // ───────────────────────────────────────────────────────────
        bool TryGetHoverCell(out int2 cell)
        {
            cell = default;
            if (BuildCamera == null) return false;
            if (!SystemAPI_TryGetCellSize(out float cs) || cs <= 0f) return false;

            Ray ray = BuildCamera.ScreenPointToRay(Input.mousePosition);

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
            _em.SetComponentData(_previewEntity, new RoadBuildPreviewState { Active = active });
        }

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
