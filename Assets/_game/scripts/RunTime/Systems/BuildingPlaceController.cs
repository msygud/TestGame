using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  BuildingPlaceController — 런타임 건물 배치 입력 컨트롤러 (MonoBehaviour)
    //
    //  RoadBuildController의 건물판. 책임:
    //    1. 배치 모드 진입/해제 (EnterMode(mainKey) / ExitMode)
    //    2. 마우스 호버 → footprint(meta.Size, 회전 반영) 프리뷰 마커
    //       (RoadBuildPreview 싱글톤·렌더 시스템 재사용 — 도로 빌드와 상호배타)
    //    3. R = 90° 회전 (0~3 step)
    //    4. 좌클릭 = PlaceBuildingRequest 발행 (RequireRoadAccess=false — 자유 배치)
    //
    //  설계 메모:
    //    - 프리뷰 유효성은 힌트일 뿐. 진짜 검증은 BuildingPlacementSystem이 한다.
    //    - 좌표 변환·ECS 접근·팩션 해소는 RoadBuildController와 동일 규약(복제).
    //      (추후 공용 베이스 추출 가능 — 지금은 테스트 우선.)
    //    - 테스트 도구: 물류/생산/영역 등을 손으로 건물 깔아 확인하는 용도.
    // ══════════════════════════════════════════════════════════════════════════
    public class BuildingPlaceController : MonoBehaviour
    {
        [Header("참조")]
        [Tooltip("레이캐스트에 쓸 카메라. 비우면 Camera.main.")]
        public Camera BuildCamera;

        [Tooltip("지면 레이캐스트용 레이어 마스크. 비우면 평면 y=0과 교차.")]
        public LayerMask GroundLayerMask = ~0;

        [Header("상태 (읽기용)")]
        [SerializeField] bool _modeActive;
        [SerializeField] int  _mainKey;
        [SerializeField] int  _variantKey;
        [SerializeField] int  _rotSteps;            // 0~3 (R로 회전)
        [SerializeField] int  _playerSlot    = -1;
        [SerializeField] int  _playerFaction = -1;

        public bool IsModeActive   => _modeActive;
        public int  SelectedMainKey => _mainKey;

        PreviewStatus _hoverStatus = PreviewStatus.Valid;
        bool          _hasHover;
        public string StatusText => _modeActive
            ? $"Building #{_mainKey} (R rotate: {_rotSteps * 90}°) — {PreviewStatusOps.ToText(_hoverStatus)}"
            : string.Empty;

        EntityManager _em;
        Entity        _previewEntity;
        bool          _ecsReady;

        Entity _ghost = Entity.Null;   // 프리뷰 고스트 건물(인스턴스). 호버 따라 이동.
        int    _ghostKey = -1;

        void Awake()
        {
            if (BuildCamera == null) BuildCamera = Camera.main;
        }

        void Update()
        {
            if (!_modeActive) return;
            if (!EnsureEcs()) return;

            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.rKey.wasPressedThisFrame || kb.rightBracketKey.wasPressedThisFrame)
                    _rotSteps = (_rotSteps + 1) & 3;          // R 또는 ] = 시계
                else if (kb.leftBracketKey.wasPressedThisFrame)
                    _rotSteps = (_rotSteps + 3) & 3;          // [ = 반시계
            }

            HandleClick();
            RebuildPreviewBuffer();
        }

        void OnDisable()
        {
            if (_modeActive) ExitMode();
        }

        // ── 공개 API (GameHUD에서 호출) ─────────────────────────────
        /// <summary>지정 MainKey 건물 배치 모드 진입.</summary>
        public void EnterMode(int mainKey)
        {
            if (!EnsureEcs()) return;
            _mainKey    = mainKey;
            _variantKey = 0;
            _rotSteps   = 0;
            _modeActive = true;
            SetPreviewActive(true);
            RebuildPreviewBuffer();
        }

        public void ExitMode()
        {
            _modeActive = false;
            if (_ecsReady)
            {
                SetPreviewActive(false);
                ClearPreviewBuffer();
            }
            DestroyGhost();
        }

        void DestroyGhost()
        {
            if (_ghost != Entity.Null && _em != default && _em.Exists(_ghost))
                _em.DestroyEntity(_ghost);
            _ghost = Entity.Null; _ghostKey = -1;
        }

        // 프리뷰 고스트 건물 — 실제 스폰과 같은 위치식(CellCenter+Offset)·회전으로 호버에 띄운다.
        //   인스턴스화 후 게임플레이 태그(거주/직장/서비스/정원/공급) 제거 → 시각 전용(영역·시민 영향 X).
        void UpdateGhost(int mainKey, in PrefabMeta meta, int2 origin, byte baseHeight)
        {
            // 프리팹 조회
            var pq = _em.CreateEntityQuery(typeof(PrefabLookup));
            if (pq.IsEmpty) { pq.Dispose(); DestroyGhost(); return; }
            var lookup = pq.GetSingleton<PrefabLookup>();
            pq.Dispose();
            Entity prefab = lookup.Get(mainKey, _variantKey);
            if (prefab == Entity.Null) { DestroyGhost(); return; }

            // 키 바뀌면 재생성
            if (_ghost == Entity.Null || !_em.Exists(_ghost) || _ghostKey != mainKey)
            {
                DestroyGhost();
                _ghost = _em.Instantiate(prefab);
                _ghostKey = mainKey;
                // 게임플레이 컴포넌트 제거 — 고스트는 시각 전용.
                RemoveIfHas<ResidenceBuilding>(_ghost);
                RemoveIfHas<WorkplaceBuilding>(_ghost);
                RemoveIfHas<ServiceBuilding>(_ghost);
                RemoveIfHas<BuildingOccupancy>(_ghost);
                RemoveIfHas<StampSupplier>(_ghost);
                RemoveIfHas<WarehouseTag>(_ghost);
            }

            float3 pos = default;
            var gq = _em.CreateEntityQuery(typeof(GridSettings));
            if (!gq.IsEmpty)
                pos = gq.GetSingleton<GridSettings>().CellCenter(origin.x, origin.y, meta.Size, baseHeight) + meta.Offset;
            gq.Dispose();

            quaternion rot = quaternion.RotateY(math.radians(_rotSteps * 90f));
            if (_em.HasComponent<LocalTransform>(_ghost))
                _em.SetComponentData(_ghost, LocalTransform.FromPositionRotationScale(pos, rot, 1f));
        }

        void RemoveIfHas<T>(Entity e) where T : unmanaged, IComponentData
        {
            if (_em.HasComponent<T>(e)) _em.RemoveComponent<T>(e);
        }

        // ── 배치 ────────────────────────────────────────────────────
        void HandleClick()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            // UI 위 클릭은 무시 (버튼 누름이 배치로 새지 않게).
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;
            if (!mouse.leftButton.wasPressedThisFrame) return;
            if (!TryGetHoverCell(out int2 cell)) return;

            if (!ResolvePlayerFaction(out int slot, out int faction))
            {
                Debug.LogWarning("[BuildingPlaceController] 플레이어 팩션 해소 실패 — "
                               + "UserPlayer/FactionConfig 싱글톤 확인.");
                return;
            }

            var e = _em.CreateEntity();
            _em.AddComponentData(e, new PlaceBuildingRequest
            {
                MainKey           = _mainKey,
                VariantKey        = _variantKey,
                Cell              = cell,
                RotationY         = _rotSteps * 90f,
                OwnerLocalId      = slot,
                FactionId         = faction,
                RequireRoadAccess = true,    // #2: 입구가 자기 도로에 닿아야 건설(프리뷰로 정렬).
            });
        }

        // ── 프리뷰 ──────────────────────────────────────────────────
        void RebuildPreviewBuffer()
        {
            if (!_ecsReady) return;

            var previewState = _em.GetComponentData<RoadBuildPreviewState>(_previewEntity);
            previewState.RoadSize  = 1;                 // 셀당 1×1 마커
            previewState.HasCenter = false;            // 호버 확정 전엔 그리드 off
            _em.SetComponentData(_previewEntity, previewState);

            var buf = _em.GetBuffer<PreviewCell>(_previewEntity);
            buf.Clear();
            _hasHover = false;
            _hoverStatus = PreviewStatus.Valid;

            if (!TryGetMeta(_mainKey, out var meta)) { DestroyGhost(); return; }
            if (!TryGetHoverCell(out int2 origin))   { DestroyGhost(); return; }
            _hasHover = true;

            // 그리드 중심 = 호버 origin
            previewState.Center    = origin;
            previewState.HasCenter = true;
            _em.SetComponentData(_previewEntity, previewState);

            var layers = GetLayers(out bool hasLayers);
            int ownerSlot = hasLayers && ResolvePlayerFaction(out int s, out _) ? s : -1;

            int2 eff = EntranceOps.RotateSize(meta.Size, _rotSteps);
            if (eff.x <= 0) eff.x = 1;
            if (eff.y <= 0) eff.y = 1;

            // footprint 전체 상태 = 가장 나쁜 셀 상태(하나라도 차단이면 전체 차단색).
            PreviewStatus worst = PreviewStatus.Valid;
            bool firstH = true; byte baseH = 0;
            for (int dx = 0; dx < eff.x; dx++)
            for (int dz = 0; dz < eff.y; dz++)
            {
                int2 c = origin + new int2(dx, dz);
                PreviewStatus st = hasLayers ? EvalCell(c, ownerSlot, layers, ref firstH, ref baseH)
                                             : PreviewStatus.Valid;
                if (Severity(st) > Severity(worst)) worst = st;
            }

            _hoverStatus = worst;
            for (int dx = 0; dx < eff.x; dx++)
            for (int dz = 0; dz < eff.y; dz++)
                buf.Add(new PreviewCell
                {
                    Cell   = origin + new int2(dx, dz),
                    Status = worst,
                    Kind   = PreviewKind.Dragging,
                });

            // 입구가 향하는 도로셀을 사각 테두리로 표시(입구 방향 안내). [/]로 회전.
            if (meta.HasEntrance && TryGetEntrance(_mainKey, out var ent))
            {
                int2 erc = EntranceOps.EntranceRoadCell(origin, meta.Size, in ent, _rotSteps);
                buf.Add(new PreviewCell { Cell = erc, Status = PreviewStatus.Entrance, Kind = PreviewKind.Dragging });
            }

            // 프리뷰 고스트 건물 (실제 위치/회전으로 호버에 띄움).
            byte gh = 0;
            if (hasLayers && layers.TerrainLayer.TryGetValue(origin, out var oc)) gh = oc.Height;
            UpdateGhost(_mainKey, in meta, origin, gh);
        }

        bool TryGetEntrance(int mainKey, out EntranceInfo ent)
        {
            ent = default;
            var q = _em.CreateEntityQuery(typeof(EntranceLookup));
            if (q.IsEmpty) { q.Dispose(); return false; }
            var look = q.GetSingleton<EntranceLookup>();
            q.Dispose();
            return look.TryGet(mainKey, out ent);
        }

        // 셀 상태(프리뷰 힌트). 진짜 검증은 BuildingPlacementSystem.
        PreviewStatus EvalCell(int2 cell, int ownerSlot, GridLayers layers,
                               ref bool firstH, ref byte baseH)
        {
            if (layers.TerrainLayer.IsCreated && !layers.TerrainLayer.ContainsKey(cell))
                return PreviewStatus.OutOfBounds;

            if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty
                && occ.Type != OccupantType.Environment)
                return PreviewStatus.Occupied;

            if (layers.ResourceLayer.IsCreated
                && layers.ResourceLayer.TryGetValue(cell, out var res) && res.Amount > 0)
                return PreviewStatus.ResourceBlocked;

            // 적 영역 → 배치 불가(빨강). (전용 enum 없어 Occupied로 표시.)
            if (TerritoryOps.InEnemyTerritory(in layers.TerritoryLayer, cell, ownerSlot))
                return PreviewStatus.Occupied;

            byte h = layers.TerrainLayer.IsCreated
                  && layers.TerrainLayer.TryGetValue(cell, out var tc) ? tc.Height : (byte)0;
            if (firstH) { baseH = h; firstH = false; }
            else if (h != baseH) return PreviewStatus.HeightMismatch;

            return PreviewStatus.Valid;
        }

        static int Severity(PreviewStatus s) => PreviewStatusOps.IsBlocking(s) ? 2
            : s == PreviewStatus.Valid ? 0 : 1;

        // ── ECS / 좌표 / 팩션 (RoadBuildController 규약 복제) ─────────
        bool TryGetMeta(int mainKey, out PrefabMeta meta)
        {
            meta = default;
            var q = _em.CreateEntityQuery(typeof(PrefabMetaLookup));
            if (q.IsEmpty) { q.Dispose(); return false; }
            var lookup = q.GetSingleton<PrefabMetaLookup>();
            q.Dispose();
            return lookup.TryGetMeta(mainKey, _variantKey, out meta);
        }

        bool TryGetHoverCell(out int2 cell)
        {
            cell = default;
            if (BuildCamera == null) BuildCamera = Camera.main;
            if (BuildCamera == null) return false;
            if (!TryGetCellSize(out float cs) || cs <= 0f) return false;

            var mouse = Mouse.current;
            if (mouse == null) return false;
            Ray ray = BuildCamera.ScreenPointToRay((Vector2)mouse.position.ReadValue());

            if (Physics.Raycast(ray, out var hit, 5000f, GroundLayerMask))
            { cell = WorldToCell(hit.point, cs); return true; }

            if (math.abs(ray.direction.y) > 1e-5f)
            {
                float t = -ray.origin.y / ray.direction.y;
                if (t > 0f) { cell = WorldToCell(ray.origin + ray.direction * t, cs); return true; }
            }
            return false;
        }

        static int2 WorldToCell(Vector3 world, float cs)
            => new int2((int)math.floor(world.x / cs), (int)math.floor(world.z / cs));

        bool EnsureEcs()
        {
            if (_ecsReady && _em != default && _em.Exists(_previewEntity)) return true;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return false;
            _em = world.EntityManager;

            var q = _em.CreateEntityQuery(typeof(RoadBuildPreviewState));
            if (q.IsEmpty)
            {
                _previewEntity = _em.CreateEntity(typeof(RoadBuildPreviewState));
                _em.AddBuffer<PreviewCell>(_previewEntity);
                _em.SetComponentData(_previewEntity, new RoadBuildPreviewState { Active = false, RoadSize = 1 });
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
            _em.SetComponentData(_previewEntity, new RoadBuildPreviewState { Active = active, RoadSize = 1 });
        }

        void ClearPreviewBuffer()
        {
            if (!_ecsReady) return;
            var buf = _em.GetBuffer<PreviewCell>(_previewEntity);
            buf.Clear();
        }

        GridLayers GetLayers(out bool ok)
        {
            ok = false;
            var q = _em.CreateEntityQuery(typeof(GridLayers));
            if (q.IsEmpty) { q.Dispose(); return default; }
            var layers = q.GetSingleton<GridLayers>();
            q.Dispose();
            ok = layers.TerrainLayer.IsCreated;
            return layers;
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

        bool ResolvePlayerFaction(out int slot, out int faction)
        {
            if (_playerSlot >= 0 && _playerFaction >= 0)
            { slot = _playerSlot; faction = _playerFaction; return true; }

            slot = -1; faction = -1;

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
            _playerSlot = slot; _playerFaction = faction;
            return true;
        }
    }
}
