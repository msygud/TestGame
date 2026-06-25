using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DepotPlaceController — 도로 관리시설(RoadMaintenanceDepot) 배치 컨트롤러
    //
    //  설계:
    //    · 관리소는 "도로 인프라"라 건물탭이 아니라 도로탭에서 배치한다(사용자 결정).
    //    · RoadBuildController와 같은 RoadBuildPreviewState/PreviewCell 싱글톤 버퍼를
    //      공유하되, 동시에 하나만 활성(GameHUD가 상호배타 보장). 렌더는 기존
    //      RoadBuildPreviewRenderSystem 그대로 재사용.
    //    · 배치 자체는 Phase 1 배선을 그대로 탄다: PlaceBuildingRequest(관리소 MainKey)
    //      → BuildingPlacementSystem.EmitSingle → SpawnSystem이 RoadMaintenanceDepot 부착.
    //
    //  배치-시점 커버리지(핵심 요구):
    //    · 호버한 자리에 관리소를 놓으면 입구가 향할 도로셀에서 자기 도로망을 따라
    //      MaintenanceMaxDist칸 이내 도로셀이 covered. RoadCoverageOps.Flood로 계산
    //      (= Phase 2 런타임 stamp BFS와 동일 규칙) → 청색 마커로 미리 보여준다.
    //    · 입구가 도로를 향하도록 회전(0~3)을 자동 선택 → 커버리지가 의미 있게 잡힘.
    //
    //  사용(에디터):
    //    · 빈 GameObject에 부착. BuildCamera(없으면 Camera.main), GroundLayerMask 설정.
    //    · DepotMainKey/VariantKey = 관리소 프리팹의 (MainKey, VariantKey).
    //    · GameHUD의 "관리소 배치" 토글이 EnterMode/ExitMode를 호출.
    // ══════════════════════════════════════════════════════════════════════════
    public class DepotPlaceController : MonoBehaviour
    {
        [Header("참조")]
        [Tooltip("레이캐스트에 쓸 카메라. 비우면 Camera.main.")]
        public Camera BuildCamera;

        [Tooltip("지면 레이캐스트용 레이어 마스크. 비우면 평면 y=0과 교차.")]
        public LayerMask GroundLayerMask = ~0;

        [Header("관리소 프리팹 키")]
        [Tooltip("배치할 관리시설 프리팹의 MainKey (Building 범위 1000~4999).")]
        public int DepotMainKey = 0;
        [Tooltip("배치할 관리시설 프리팹의 VariantKey (보통 0).")]
        public int DepotVariantKey = 0;

        [Header("테스트 (프리팹 정리 후 0으로 되돌릴 것)")]
        [Tooltip("테스트용 override(도로 칸 수). >0이면 (a) 프리뷰 커버리지를 이 반경으로, "
               + "(b) 배치 시 이 건물을 관리시설로 강제(RoadMaintenanceDepot 태그 부착 + MaxDist=이 값). "
               + "→ 프리팹 IsRoadMaintenance/MaxDist 미설정 상태에서도 풀 루프 테스트. "
               + "프리팹 RegistryItem에 값을 설정·베이크한 뒤엔 0으로 되돌리세요.")]
        public int MaintenanceMaxDistOverride = 6;

        [Header("상태 (읽기용)")]
        [SerializeField] bool _modeActive;
        [SerializeField] int  _playerSlot    = -1;
        [SerializeField] int  _playerFaction = -1;

        public bool IsModeActive => _modeActive;

        // HUD 사유/안내 표시용.
        string _statusText = string.Empty;
        public string StatusText => _statusText;

        // ── ECS 접근 ───────────────────────────────────────────────
        EntityManager _em;
        Entity        _previewEntity;
        bool          _ecsReady;

        // Flood 재사용 컨테이너(Persistent, OnDestroy에서 해제).
        NativeQueue<int2>        _floodQueue;
        NativeHashMap<int2, int> _floodCovered;
        bool                     _containersReady;

        // 기존 관리소 표시용 — coverage union + 위치 셀 (Persistent, 매 프레임 재계산).
        NativeHashMap<int2, byte> _existingCovered;
        NativeList<int2>          _existingDepotCells;
        int                       _existingDepotCount;

        // ───────────────────────────────────────────────────────────
        void Awake()
        {
            if (BuildCamera == null) BuildCamera = Camera.main;
        }

        void Update()
        {
            if (!_modeActive) return;
            if (!EnsureEcs()) return;
            BuildPreviewAndHandleInput();
        }

        void OnDisable()
        {
            if (_modeActive) ExitMode();
        }

        void OnDestroy()
        {
            if (_containersReady)
            {
                if (_floodQueue.IsCreated)        _floodQueue.Dispose();
                if (_floodCovered.IsCreated)      _floodCovered.Dispose();
                if (_existingCovered.IsCreated)   _existingCovered.Dispose();
                if (_existingDepotCells.IsCreated) _existingDepotCells.Dispose();
                _containersReady = false;
            }
        }

        // ───────────────────────────────────────────────────────────
        //  공개 API — GameHUD 토글에서 호출
        // ───────────────────────────────────────────────────────────
        public void EnterMode()
        {
            if (!EnsureEcs()) return;
            _modeActive = true;
            SetPreviewActive(true);
        }

        public void ExitMode()
        {
            _modeActive = false;
            if (_ecsReady)
            {
                SetPreviewActive(false);
                var buf = _em.GetBuffer<PreviewCell>(_previewEntity);
                buf.Clear();
            }
            _statusText = string.Empty;
        }

        // ───────────────────────────────────────────────────────────
        //  프리뷰 빌드 + 클릭 처리
        // ───────────────────────────────────────────────────────────
        void BuildPreviewAndHandleInput()
        {
            // 매 프레임 버퍼 초기화(이전 프레임 마커 제거). 채우기는 모든 쿼리/조회가
            //   끝난 뒤 fresh 핸들로 한다(중간 SetComponentData·쿼리 생성에 의한 버퍼
            //   핸들 무효화 회피).
            {
                var clr = _em.GetBuffer<PreviewCell>(_previewEntity);
                clr.Clear();
            }
            _statusText = string.Empty;

            // 프리뷰 쿼드 크기는 항상 1×1(관리소 footprint·커버리지 모두 셀 단위).
            var ps = _em.GetComponentData<RoadBuildPreviewState>(_previewEntity);
            if (ps.RoadSize != 1) { ps.RoadSize = 1; _em.SetComponentData(_previewEntity, ps); }

            // UI 위에서는 프리뷰/배치를 하지 않는다(버튼 클릭이 유령 배치로 새는 것 방지).
            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (overUI) return;

            if (!TryGetHoverCell(out int2 hover)) return;

            // ── 싱글톤/메타 조회 ──
            if (!TryGetSingleton(out PrefabMetaLookup metaLookup)) return;
            if (!metaLookup.TryGetMeta(DepotMainKey, DepotVariantKey, out var meta))
            {
                _statusText = $"관리소 프리팹 미설정 (MainKey={DepotMainKey})";
                return;
            }

            var layers = GetLayers(out bool hasLayers);
            if (!hasLayers) return;
            var roadLayer = layers.RoadLayer;

            TryGetSingleton(out EntranceLookup entranceLookup);
            bool hasCtl = TryGetSingleton(out CellTypeLookup cellTypeLookup);

            bool resolved = ResolvePlayerFaction(out int slot, out int faction);

            // entrance를 미리 default 초기화 — out var를 && 단축평가 뒤에 두면 컴파일러가
            //   '확정 할당'을 증명 못 함(CS0165). 사전 선언으로 항상 할당 보장.
            EntranceInfo entrance = default;
            bool hasEntrance = meta.HasEntrance
                               && entranceLookup.Table.IsCreated
                               && entranceLookup.TryGet(DepotMainKey, out entrance);

            // ── 입구가 도로를 향하는 회전 자동 선택 ──
            int rot = ChooseRotation(hover, meta, hasEntrance, entrance, slot,
                                     roadLayer, hasCtl, cellTypeLookup, layers);

            int2 effSize = EntranceOps.RotateSize(meta.Size, rot);

            // ── 기존 관리소 수집(쿼리 — buf 획득 전에 모두 끝낸다) ──
            //   다른 모든 관리소의 위치 + 그들의 도달 범위(연결성)를 같이 보여준다.
            GatherExistingDepots(slot, in roadLayer);

            // ── 새 depot 입구 도로셀 + 커버리지 사전 계산(쿼리 아님 — buf 전/후 무관) ──
            int2 entRoadCell = hasEntrance
                ? EntranceOps.EntranceRoadCell(hover, meta.Size, in entrance, rot)
                : default;
            bool entOnOwnerRoad = hasEntrance && slot >= 0
                && roadLayer.TryGetValue(entRoadCell, out var entRc) && entRc.OwnerLocalId == slot;

            bool hasNewCoverage = false;
            if (entOnOwnerRoad)
            {
                // 테스트 override(>0)면 프리팹 값 대신 사용 — 프리뷰·배치 동일 값 보장.
                int maxDist = MaintenanceMaxDistOverride > 0
                    ? MaintenanceMaxDistOverride : meta.MaintenanceMaxDist;
                RoadCoverageOps.Flood(entRoadCell, slot, maxDist,
                                      in roadLayer, ref _floodQueue, ref _floodCovered);
                hasNewCoverage = true;   // 결과는 _floodCovered에 보존(아래 ③에서 읽음)
            }

            // ── 버퍼 채우기(쿼리 끝난 뒤 fresh 핸들). 그리는 순서 = 위에 덮이는 순서 ──
            //   ① 기존 coverage(초록·옅게) → ② 새 footprint → ③ 새 coverage(청색)
            //   → ④ 기존 관리소 위치(금색·최상단, 항상 보이게).
            var buf = _em.GetBuffer<PreviewCell>(_previewEntity);

            // ① 기존 관리 범위(연결성)
            var exCov = _existingCovered.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < exCov.Length; i++)
                buf.Add(new PreviewCell
                {
                    Cell   = exCov[i],
                    Status = PreviewStatus.CoverageExisting,
                    Kind   = PreviewKind.Pending,    // 컨텍스트 = 옅게
                });
            exCov.Dispose();

            // ② 새 depot footprint
            bool footprintValid = EvaluateFootprintCells(
                buf, hover, effSize, meta.BuildableOn, slot, layers, hasCtl, cellTypeLookup);

            // ③ 새 depot 도달 범위(청색, 위)
            int newCoverageCount = 0;
            if (hasNewCoverage)
            {
                var nk = _floodCovered.GetKeyArray(Allocator.Temp);
                newCoverageCount = nk.Length;
                for (int i = 0; i < nk.Length; i++)
                    buf.Add(new PreviewCell
                    {
                        Cell   = nk[i],
                        Status = PreviewStatus.Coverage,
                        Kind   = PreviewKind.Dragging,
                    });
                nk.Dispose();
            }

            // ④ 기존 관리소 위치(금색, 최상단 — 항상 보이게)
            for (int i = 0; i < _existingDepotCells.Length; i++)
                buf.Add(new PreviewCell
                {
                    Cell   = _existingDepotCells[i],
                    Status = PreviewStatus.DepotExisting,
                    Kind   = PreviewKind.Dragging,
                });

            // ── 상태 텍스트 ──
            if (!footprintValid)
                _statusText = $"배치 불가 · 기존 관리소 {_existingDepotCount}개";
            else if (hasNewCoverage)
                _statusText = $"관리 범위 {newCoverageCount}칸 · 기존 관리소 {_existingDepotCount}개";
            else
                _statusText = hasEntrance
                    ? $"경고: 입구가 내 도로에 안 닿음 · 기존 {_existingDepotCount}개"
                    : "이 프리팹은 입구 정의가 없습니다";

            // ── 좌클릭 배치 ──
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                if (!resolved)
                    _statusText = "배치 실패: 플레이어 팩션 해소 실패";
                else if (!footprintValid)
                    _statusText = "배치 불가: 자리가 유효하지 않음";
                else
                    EmitPlace(hover, rot, slot, faction);
            }
        }

        // 내 기존 관리소(RoadMaintenanceDepot) 전부 수집 — 위치(footprint 셀) +
        //   각자의 coverage(입구 도로셀에서 MaxDist BFS) union. 결과는 _existingCovered/
        //   _existingDepotCells에. **쿼리를 쓰므로 PreviewCell 버퍼 획득 전에 호출**.
        void GatherExistingDepots(int slot, in NativeHashMap<int2, RoadCell> roadLayer)
        {
            _existingCovered.Clear();
            _existingDepotCells.Clear();
            _existingDepotCount = 0;
            if (slot < 0) return;

            var q = _em.CreateEntityQuery(
                ComponentType.ReadOnly<RoadMaintenanceDepot>(),
                ComponentType.ReadOnly<BuildingFootprint>(),
                ComponentType.ReadOnly<BuildingEntrance>());
            var ents = q.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < ents.Length; i++)
            {
                var depot = _em.GetComponentData<RoadMaintenanceDepot>(ents[i]);
                if (depot.OwnerLocalId != slot) continue;

                var fp = _em.GetComponentData<BuildingFootprint>(ents[i]);
                var be = _em.GetComponentData<BuildingEntrance>(ents[i]);
                _existingDepotCount++;

                // 위치 마커 — footprint 전체 셀(회전 반영).
                int2 esize = EntranceOps.RotateSize(fp.Size, fp.RotSteps);
                for (int dx = 0; dx < esize.x; dx++)
                for (int dz = 0; dz < esize.y; dz++)
                    _existingDepotCells.Add(fp.Origin + new int2(dx, dz));

                // coverage — 입구 도로셀에서 BFS, union에 누적.
                int2 start = EntranceOps.EntranceRoadCell(fp.Origin, fp.Size, in be.Entrance, fp.RotSteps);
                RoadCoverageOps.Flood(start, slot, depot.MaxDist,
                                      in roadLayer, ref _floodQueue, ref _floodCovered);
                var ck = _floodCovered.GetKeyArray(Allocator.Temp);
                for (int k = 0; k < ck.Length; k++)
                    _existingCovered.TryAdd(ck[k], 0);
                ck.Dispose();
            }

            ents.Dispose();
            q.Dispose();

            // 영구 면제(베이스 외곽 링) 도로도 decay-안전 → 같은 coverage 색(초록)으로 표시.
            //   depot coverage가 아니라 RoadDecayState.Exempt로 보호되지만, 플레이어 관점에선
            //   "decay 안 되는 도로"가 한눈에 보이도록 합친다. 내 소유 도로만 필터.
            if (TryGetSingleton(out RoadDecayState decay) && decay.Exempt.IsCreated)
            {
                foreach (var c in decay.Exempt)
                    if (roadLayer.TryGetValue(c, out var rc) && rc.OwnerLocalId == slot)
                        _existingCovered.TryAdd(c, 0);
            }
        }

        // 입구가 내 도로를 향하면서 footprint도 유효한 회전을 우선 선택.
        //   1순위: footprint 유효 + 입구가 내 도로. 2순위: footprint 유효. 폴백: 0.
        int ChooseRotation(int2 hover, PrefabMeta meta, bool hasEntrance, EntranceInfo entrance,
                            int slot, in NativeHashMap<int2, RoadCell> roadLayer,
                            bool hasCtl, CellTypeLookup ctl, GridLayers layers)
        {
            int firstFree = -1;
            for (int steps = 0; steps < 4; steps++)
            {
                int2 eff = EntranceOps.RotateSize(meta.Size, steps);
                bool free = FootprintAllValid(hover, eff, meta.BuildableOn, slot, layers, hasCtl, ctl);
                if (!free) continue;
                if (firstFree < 0) firstFree = steps;

                if (hasEntrance && slot >= 0)
                {
                    int2 rc = EntranceOps.EntranceRoadCell(hover, meta.Size, in entrance, steps);
                    if (roadLayer.TryGetValue(rc, out var cell) && cell.OwnerLocalId == slot)
                        return steps;   // 1순위 즉시 채택
                }
            }
            return firstFree >= 0 ? firstFree : 0;
        }

        // ───────────────────────────────────────────────────────────
        //  footprint 검증 (BuildingPlacement.ValidateCells와 동치)
        // ───────────────────────────────────────────────────────────

        // 셀별 상태를 버퍼에 채우고, 차단 셀이 하나도 없으면 true.
        bool EvaluateFootprintCells(
            DynamicBuffer<PreviewCell> buf, int2 origin, int2 size, TerrainMask buildableOn,
            int slot, GridLayers layers, bool hasCtl, CellTypeLookup ctl)
        {
            // 1차: 기준 높이 + 단차 여부.
            bool firstH = true; byte baseH = 0; bool heightBad = false;
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);
                if (!layers.TerrainLayer.TryGetValue(cell, out var t)) continue;
                if (firstH) { baseH = t.Height; firstH = false; }
                else if (t.Height != baseH) heightBad = true;
            }

            // 2차: 셀별 상태 마커.
            bool allValid = true;
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);
                var st = CellStatus(cell, buildableOn, layers, hasCtl, ctl, heightBad);
                if (PreviewStatusOps.IsBlocking(st)) allValid = false;
                buf.Add(new PreviewCell { Cell = cell, Status = st, Kind = PreviewKind.Dragging });
            }
            return allValid;
        }

        // 마커 없이 유효 여부만(회전 선택용).
        bool FootprintAllValid(int2 origin, int2 size, TerrainMask buildableOn,
                               int slot, GridLayers layers, bool hasCtl, CellTypeLookup ctl)
        {
            bool firstH = true; byte baseH = 0; bool heightBad = false;
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);
                if (!layers.TerrainLayer.TryGetValue(cell, out var t)) continue;
                if (firstH) { baseH = t.Height; firstH = false; }
                else if (t.Height != baseH) heightBad = true;
            }

            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);
                if (PreviewStatusOps.IsBlocking(
                        CellStatus(cell, buildableOn, layers, hasCtl, ctl, heightBad)))
                    return false;
            }
            return true;
        }

        // 한 셀의 배치 상태. 환경물은 비차단(배치 시 철거).
        //   지형타입 불일치(물 위 등)는 전용 enum이 없어 Occupied(빨강·차단)로 표기.
        PreviewStatus CellStatus(int2 cell, TerrainMask buildableOn, GridLayers layers,
                                 bool hasCtl, CellTypeLookup ctl, bool heightBad)
        {
            if (!layers.TerrainLayer.TryGetValue(cell, out var terrain))
                return PreviewStatus.OutOfBounds;

            if (heightBad)
                return PreviewStatus.HeightMismatch;

            if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty
                && occ.Type != OccupantType.Environment)
                return PreviewStatus.Occupied;

            if (layers.ResourceLayer.IsCreated
                && layers.ResourceLayer.TryGetValue(cell, out var res) && res.Amount > 0)
                return PreviewStatus.ResourceBlocked;

            if (hasCtl && ctl.TryGet(terrain.TypeId, out var typeInfo))
            {
                var mask = typeInfo.TerrainCategory == TerrainCategory.Water
                    ? TerrainMask.Water : TerrainMask.Land;
                if ((buildableOn & mask) == 0)
                    return PreviewStatus.Occupied;  // 지형타입 불일치 = 차단
            }

            if (layers.OccupancyLayer.TryGetValue(cell, out var occ2)
                && occ2.Type == OccupantType.Environment)
                return PreviewStatus.WillClear;     // 환경물 위 — 배치 시 철거(비차단)

            return PreviewStatus.Valid;
        }

        // ───────────────────────────────────────────────────────────
        //  배치 명령 발행
        // ───────────────────────────────────────────────────────────
        void EmitPlace(int2 cell, int rotSteps, int slot, int faction)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new PlaceBuildingRequest
            {
                MainKey           = DepotMainKey,
                VariantKey        = DepotVariantKey,
                Cell              = cell,
                RotationY         = EntranceOps.StepsToRotationY(rotSteps),
                OwnerLocalId      = slot,
                FactionId         = faction,
                RequireRoadAccess = false,   // 인간 배치 — 연결성은 정보로만, 선택은 본인 몫
                MaintenanceMaxDistOverride = MaintenanceMaxDistOverride, // 테스트용(>0이면 메타 대체)
            });
        }

        // ───────────────────────────────────────────────────────────
        //  레이캐스트 → 셀 (RoadBuildController와 동일 규약)
        // ───────────────────────────────────────────────────────────
        bool TryGetHoverCell(out int2 cell)
        {
            cell = default;
            if (BuildCamera == null) return false;
            if (!TryGetCellSize(out float cs) || cs <= 0f) return false;

            var mouse = Mouse.current;
            if (mouse == null) return false;
            Ray ray = BuildCamera.ScreenPointToRay((Vector2)mouse.position.ReadValue());

            if (Physics.Raycast(ray, out var hit, 5000f, GroundLayerMask))
            {
                cell = WorldToCell(hit.point, cs);
                return true;
            }
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
            => new int2((int)math.floor(world.x / cs), (int)math.floor(world.z / cs));

        // ───────────────────────────────────────────────────────────
        //  ECS 접근 헬퍼
        // ───────────────────────────────────────────────────────────
        bool EnsureEcs()
        {
            if (_ecsReady && _em != default && _em.Exists(_previewEntity))
            {
                EnsureContainers();
                return true;
            }

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            _em = world.EntityManager;

            var q = _em.CreateEntityQuery(typeof(RoadBuildPreviewState));
            if (q.IsEmpty)
            {
                _previewEntity = _em.CreateEntity(typeof(RoadBuildPreviewState));
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
            EnsureContainers();
            return true;
        }

        void EnsureContainers()
        {
            if (_containersReady) return;
            _floodQueue         = new NativeQueue<int2>(Allocator.Persistent);
            _floodCovered       = new NativeHashMap<int2, int>(1024, Allocator.Persistent);
            _existingCovered    = new NativeHashMap<int2, byte>(1024, Allocator.Persistent);
            _existingDepotCells = new NativeList<int2>(64, Allocator.Persistent);
            _containersReady = true;
        }

        void SetPreviewActive(bool active)
        {
            if (!_ecsReady) return;
            _em.SetComponentData(_previewEntity, new RoadBuildPreviewState
            {
                Active   = active,
                RoadSize = 1,
            });
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

        bool TryGetSingleton<T>(out T value) where T : unmanaged, IComponentData
        {
            value = default;
            var q = _em.CreateEntityQuery(typeof(T));
            if (q.IsEmpty) { q.Dispose(); return false; }
            value = q.GetSingleton<T>();
            q.Dispose();
            return true;
        }

        bool TryGetCellSize(out float cs)
        {
            cs = 0f;
            var q = _em.CreateEntityQuery(typeof(GridSettings));
            if (q.IsEmpty) { q.Dispose(); return false; }
            cs = q.GetSingleton<GridSettings>().CellSize;
            q.Dispose();
            return true;
        }

        // ───────────────────────────────────────────────────────────
        //  플레이어 슬롯/팩션 해소 (RoadBuildController와 동일)
        // ───────────────────────────────────────────────────────────
        bool ResolvePlayerFaction(out int slot, out int faction)
        {
            if (_playerSlot >= 0 && _playerFaction >= 0)
            {
                slot = _playerSlot;
                faction = _playerFaction;
                return true;
            }

            slot = -1;
            faction = -1;

            var upq = _em.CreateEntityQuery(ComponentType.ReadOnly<Game.Unit.UserPlayer>());
            if (upq.IsEmpty) { upq.Dispose(); return false; }
            var userPlayer = upq.GetSingleton<Game.Unit.UserPlayer>();
            upq.Dispose();
            slot = userPlayer.LocalID;
            if (slot < 0) return false;

            var fcq = _em.CreateEntityQuery(ComponentType.ReadOnly<FactionConfig>());
            if (fcq.IsEmpty) { fcq.Dispose(); return false; }
            var config = fcq.GetSingleton<FactionConfig>();
            fcq.Dispose();

            if (!config.Slots.IsCreated) return false;
            if (!config.Slots.TryGetValue(slot, out var fslot)) return false;
            if (fslot.FactionId < 0) return false;

            faction = fslot.FactionId;
            _playerSlot = slot;
            _playerFaction = faction;
            return true;
        }
    }
}
