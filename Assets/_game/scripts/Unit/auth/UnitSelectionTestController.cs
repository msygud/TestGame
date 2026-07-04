using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Unit
{
    public enum SelectionMarkerShape
    {
        Ring,
        CornerRectangle,
    }

    public sealed class UnitSelectionTestController : MonoBehaviour
    {
        [Header("Input")]
        public Camera SelectionCamera;
        public LayerMask GroundLayerMask = ~0;
        public float GroundPlaneY = 0f;
        public float DragThresholdPixels = 8f;
        public float WorldPickDistance = 2f;
        public float CombatTargetPickDistance = 2.5f;

        [Header("Control")]
        public bool RestrictControlToLocalId = true;
        [Range(0, 7)]
        public int ControllableLocalId = 0;

        [Header("Move Order")]
        public float MoveStopDistance = 0.25f;
        public float FormationSpacing = 1.4f;
        public float FormationPadding = 0.35f;
        public bool PreserveRelativeFormation = true;
        public bool UseSharedGroupPath = true;
        public bool AvoidOccupiedSlots = true;
        [Min(1)]
        public int MaxUnitsForSharedGroupPath = 128;
        [Min(1)]
        public int MaxUnitsForOccupiedSlotSearch = 128;
        public bool UseParallelLargeSelectionOrders = true;
        [Min(1)]
        public int MinUnitsForParallelSelectionOrders = 129;
        [Range(0, 8)]
        public int SlotSearchRings = 4;

        [Header("Selection Ring")]
        public SelectionMarkerShape MarkerShape = SelectionMarkerShape.CornerRectangle;
        public Material RingMaterial;
        public Color RingColor = new Color(0.2f, 0.9f, 1f, 0.9f);
        public float RingHeightOffset = 0.04f;
        [Range(0.03f, 0.5f)]
        public float RingWidthRatio = 0.14f;
        [Range(16, 128)]
        public int RingSegments = 64;
        [Range(0.05f, 0.5f)]
        public float CornerLengthRatio = 0.28f;
        [Range(0.01f, 0.2f)]
        public float CornerWidthRatio = 0.05f;

        [Header("Debug")]
        public bool ShowDebugHud = true;
        public bool LogSelectionEvents;
        public bool ShowUnitNames;
        public bool UnitNamesSelectedOnly = true;
        public bool ShowMovementDebug;
        public bool ShowCombatDebug;
        public bool DebugSelectedOnly = true;
        public bool CombatDebugSelectedOnly;
        public bool ShowObstacleDebugBoxes = true;
        public bool ShowObstacleDebugRadius;
        public Color DebugRadiusColor = new Color(1f, 0.85f, 0.15f, 0.85f);
        public Color DebugTargetLineColor = new Color(0.3f, 1f, 0.35f, 0.85f);
        public Color DebugPartialPathColor = new Color(1f, 0.55f, 0.15f, 0.9f);
        public Color DebugFailedPathColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        public Color DebugWaypointColor = new Color(0.2f, 0.65f, 1f, 0.85f);
        public Color DebugObstacleBoxColor = new Color(1f, 0.25f, 0.95f, 0.85f);
        public Color DebugObstacleRadiusColor = new Color(1f, 0.25f, 0.95f, 0.65f);
        public Color DebugCombatRangeColor = new Color(1f, 0.35f, 0.15f, 0.65f);
        public Color DebugCombatAttackLineColor = new Color(1f, 0.15f, 0.1f, 0.9f);
        public Color DebugCombatHealthColor = new Color(0.95f, 1f, 0.95f, 0.95f);
        public Color DebugCombatWeaponReadyColor = new Color(0.25f, 1f, 0.45f, 0.95f);
        public Color DebugCombatWeaponBlockedColor = new Color(1f, 0.55f, 0.15f, 0.95f);
        public Color DebugNameColor = new Color(0.65f, 0.95f, 1f, 0.95f);
        public Color DebugStatusTextColor = Color.white;
        [Range(16, 96)]
        public int DebugCircleSegments = 48;
        [Range(0.01f, 0.12f)]
        public float DebugLineWidth = 0.035f;

        EntityManager _entityManager;
        EntityQuery _unitQuery;
        EntityQuery _selectedQuery;
        EntityQuery _obstacleQuery;
        EntityQuery _combatTargetableQuery;
        EntityQuery _combatDebugQuery;
        EntityQuery _weaponQuery;
        EntityQuery _unitNameQuery;
        EntityQuery _selectedUnitNameQuery;

        bool _hasWorld;
        bool _isDragging;
        Vector2 _dragStart;
        Vector2 _dragEnd;

        Mesh _ringMesh;
        Mesh _cornerRectangleMesh;
        Material _runtimeRingMaterial;
        Material _debugLineMaterial;
        readonly Dictionary<Entity, GameObject> _rings = new();
        readonly Dictionary<Entity, GameObject> _debugRadiusObjects = new();
        readonly Dictionary<Entity, GameObject> _debugTargetLineObjects = new();
        readonly Dictionary<Entity, GameObject> _debugPathLineObjects = new();
        readonly Dictionary<Entity, GameObject> _debugStatusLabelObjects = new();
        readonly Dictionary<Entity, GameObject> _debugObstacleBoxObjects = new();
        readonly Dictionary<Entity, GameObject> _debugObstacleRadiusObjects = new();
        readonly Dictionary<Entity, GameObject> _debugCombatRangeObjects = new();
        readonly Dictionary<Entity, GameObject> _debugCombatAttackLineObjects = new();
        readonly Dictionary<Entity, GameObject> _debugCombatHealthLabelObjects = new();
        readonly Dictionary<Entity, GameObject> _debugCombatWeaponStateLabelObjects = new();
        readonly Dictionary<Entity, GameObject> _debugNameLabelObjects = new();

        // GC 방지 캐시(2026-07-05) — 디버그 라벨 문자열은 표시 값이 바뀔 때만 재조립.
        //   매 프레임 보간($")/FixedString.ToString()은 라벨 수 × 프레임만큼 관리형 쓰레기.
        readonly Dictionary<Entity, (UnitCommandKind Kind, string Text)> _nameTextCache = new();
        readonly Dictionary<Entity, (int2 Shown, string Text)>           _hpTextCache   = new();
        string _hudHeaderText = string.Empty;
        int    _hudUnitN = -1, _hudSelN = -1, _hudObsN = -1;
        readonly List<Entity> _removeBuffer = new();
        readonly List<Entity> _debugActiveBuffer = new();
        readonly List<Entity> _debugObstacleActiveBuffer = new();
        readonly List<Entity> _debugCombatActiveBuffer = new();
        readonly List<Entity> _debugCombatHealthActiveBuffer = new();
        readonly List<Entity> _debugNameActiveBuffer = new();
        readonly List<MoveSlotReservation> _moveSlotReservations = new();

        struct MoveSlotReservation
        {
            public float3 Position;
            public float Radius;
        }

        struct FormationSortEntry
        {
            public int Index;
            public float Lateral;
            public float Forward;
            public float Radius;
        }

        void Awake()
        {
            if (SelectionCamera == null)
                SelectionCamera = Camera.main;
        }

        void OnDisable()
        {
            ClearRingObjects();
            ClearMovementDebugObjects();
            ClearCombatDebugObjects();
            ClearUnitNameObjects();
        }

        void OnDestroy()
        {
            ClearRingObjects();
            ClearMovementDebugObjects();
            ClearCombatDebugObjects();
            ClearUnitNameObjects();

            if (_ringMesh != null)
                Destroy(_ringMesh);

            if (_cornerRectangleMesh != null)
                Destroy(_cornerRectangleMesh);

            if (_runtimeRingMaterial != null)
                Destroy(_runtimeRingMaterial);

            if (_debugLineMaterial != null)
                Destroy(_debugLineMaterial);
        }

        void Update()
        {
            if (!EnsureWorld())
                return;

            if (SelectionCamera == null)
                SelectionCamera = Camera.main;

            var mouse = Mouse.current;
            if (mouse == null || SelectionCamera == null)
            {
                UpdateSelectionRings();
                UpdateUnitNameVisuals();
                UpdateMovementDebugVisuals();
                UpdateCombatDebugVisuals();
                return;
            }

            Vector2 mousePosition = mouse.position.ReadValue();
            bool additive = IsAdditiveSelectionPressed();

            if (mouse.leftButton.wasPressedThisFrame)
            {
                _isDragging = true;
                _dragStart = mousePosition;
                _dragEnd = mousePosition;
            }

            if (_isDragging && mouse.leftButton.isPressed)
                _dragEnd = mousePosition;

            if (_isDragging && mouse.leftButton.wasReleasedThisFrame)
            {
                _isDragging = false;
                _dragEnd = mousePosition;

                if (Vector2.Distance(_dragStart, _dragEnd) >= DragThresholdPixels)
                    SelectUnitsInScreenRect(BuildScreenRect(_dragStart, _dragEnd), additive);
                else
                    SelectUnitAtScreenPoint(mousePosition, additive);
            }

            if (mouse.rightButton.wasPressedThisFrame)
            {
                bool attackMove = IsAttackMovePressed();
                Entity attackTarget = FindCombatTargetAtScreenPoint(mousePosition);
                if (attackTarget != Entity.Null)
                    IssueAttackOrders(attackTarget);
                else if (TryGetGroundPoint(mousePosition, out var target))
                    IssueMoveOrders(target, attackMove ? UnitCommandKind.AttackMove : UnitCommandKind.ForceMove);
            }

            UpdateSelectionRings();
            UpdateUnitNameVisuals();
            UpdateMovementDebugVisuals();
            UpdateCombatDebugVisuals();
        }

        void OnGUI()
        {
            if (ShowDebugHud && _hasWorld)
            {
                var oldColor = GUI.color;
                GUI.color = Color.white;
                // 헤더 문자열은 수가 바뀔 때만 재조립(OnGUI는 프레임당 2회+ 호출 — 매번 보간하면 GC 쓰레기).
                int un = _unitQuery.CalculateEntityCount();
                int sn = _selectedQuery.CalculateEntityCount();
                int on = _obstacleQuery.CalculateEntityCount();
                if (un != _hudUnitN || sn != _hudSelN || on != _hudObsN)
                {
                    _hudUnitN = un; _hudSelN = sn; _hudObsN = on;
                    _hudHeaderText = $"Units: {un}  Selected: {sn}  Obstacles: {on}";
                }
                GUI.Label(new Rect(12f, 12f, 360f, 48f), _hudHeaderText);
                ShowUnitNames = GUI.Toggle(
                    new Rect(12f, 58f, 180f, 22f),
                    ShowUnitNames,
                    "Unit Names");
                UnitNamesSelectedOnly = GUI.Toggle(
                    new Rect(12f, 82f, 180f, 22f),
                    UnitNamesSelectedOnly,
                    "Names Selected");
                ShowMovementDebug = GUI.Toggle(
                    new Rect(12f, 106f, 180f, 22f),
                    ShowMovementDebug,
                    "Move Debug");
                DebugSelectedOnly = GUI.Toggle(
                    new Rect(12f, 130f, 180f, 22f),
                    DebugSelectedOnly,
                    "Selected Only");
                ShowCombatDebug = GUI.Toggle(
                    new Rect(12f, 154f, 180f, 22f),
                    ShowCombatDebug,
                    "Combat Debug");
                CombatDebugSelectedOnly = GUI.Toggle(
                    new Rect(12f, 178f, 180f, 22f),
                    CombatDebugSelectedOnly,
                    "Combat Selected");
                ShowObstacleDebugBoxes = GUI.Toggle(
                    new Rect(12f, 202f, 180f, 22f),
                    ShowObstacleDebugBoxes,
                    "Obstacle Boxes");
                ShowObstacleDebugRadius = GUI.Toggle(
                    new Rect(12f, 226f, 180f, 22f),
                    ShowObstacleDebugRadius,
                    "Obstacle Radius");
                GUI.color = oldColor;
            }

            if (!_isDragging)
                return;

            var rect = BuildGuiRect(_dragStart, _dragEnd);
            var oldDragColor = GUI.color;

            GUI.color = new Color(0.2f, 0.75f, 1f, 0.18f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);

            GUI.color = new Color(0.2f, 0.9f, 1f, 0.75f);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMax, rect.width, 1f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMin, rect.yMin, 1f, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.xMax, rect.yMin, 1f, rect.height), Texture2D.whiteTexture);

            GUI.color = oldDragColor;
        }

        bool EnsureWorld()
        {
            if (_hasWorld)
                return true;

            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return false;

            _entityManager = world.EntityManager;
            _unitQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<SelectableUnit>(),
                ComponentType.ReadOnly<UnitFootprint>(),
                ComponentType.ReadOnly<UnitActivityState>(),
                ComponentType.ReadOnly<UnitSelectionRadius>(),
                ComponentType.ReadOnly<LocalTransform>());
            _selectedQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<SelectedUnit>(),
                ComponentType.ReadOnly<UnitFootprint>(),
                ComponentType.ReadOnly<UnitActivityState>(),
                ComponentType.ReadOnly<UnitSelectionRadius>(),
                ComponentType.ReadOnly<LocalTransform>());
            _obstacleQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ObstacleFootprint>(),
                ComponentType.ReadOnly<LocalTransform>());
            _combatTargetableQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<CombatTargetable>(),
                ComponentType.ReadOnly<LocalTransform>());
            _combatDebugQuery = _entityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<LocalTransform>(),
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<CombatTargetable>(),
                    ComponentType.ReadOnly<CombatHealth>(),
                    ComponentType.ReadOnly<CombatAttackTarget>(),
                    ComponentType.ReadOnly<CombatWeapon>(),
                },
            });
            _weaponQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<CombatWeaponOwner>(),
                ComponentType.ReadOnly<CombatWeaponEnabled>(),
                ComponentType.ReadOnly<CombatWeapon>(),
                ComponentType.ReadOnly<CombatWeaponReadyState>());
            _unitNameQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UnitDisplayName>(),
                ComponentType.ReadOnly<LocalTransform>());
            _selectedUnitNameQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<SelectedUnit>(),
                ComponentType.ReadOnly<UnitDisplayName>(),
                ComponentType.ReadOnly<LocalTransform>());

            _hasWorld = true;
            return true;
        }

        void SelectUnitAtScreenPoint(Vector2 screenPoint, bool additive)
        {
            Entity picked = FindUnitAtScreenPoint(screenPoint);
            if (picked == Entity.Null && TryGetGroundPoint(screenPoint, out var worldPoint))
                picked = FindUnitNearWorldPoint(worldPoint);

            if (!additive)
                ClearSelection();

            if (picked == Entity.Null)
            {
                if (LogSelectionEvents)
                    Debug.Log("[UnitSelectionTestController] No unit picked.");
                return;
            }

            if (additive)
            {
                SetSelected(picked, !IsSelected(picked));
            }
            else
            {
                SetSelected(picked, true);
            }

            if (LogSelectionEvents)
                Debug.Log($"[UnitSelectionTestController] Picked unit {picked}.");
        }

        Entity FindUnitAtScreenPoint(Vector2 screenPoint)
        {
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var radii = _unitQuery.ToComponentDataArray<UnitSelectionRadius>(Allocator.Temp);

            Entity best = Entity.Null;
            float bestDistanceSq = float.MaxValue;

            for (int i = 0; i < entities.Length; i++)
            {
                if (!IsControllableUnit(entities[i]))
                    continue;

                Vector3 screen = SelectionCamera.WorldToScreenPoint(ToVector3(transforms[i].Position));
                if (screen.z <= 0f)
                    continue;

                float2 delta = new float2(screen.x - screenPoint.x, screen.y - screenPoint.y);
                float distanceSq = math.lengthsq(delta);
                float pickRadius = radii[i].ScreenPixels;

                if (distanceSq <= pickRadius * pickRadius && distanceSq < bestDistanceSq)
                {
                    best = entities[i];
                    bestDistanceSq = distanceSq;
                }
            }

            entities.Dispose();
            transforms.Dispose();
            radii.Dispose();

            return best;
        }

        Entity FindUnitNearWorldPoint(float3 worldPoint)
        {
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var radii = _unitQuery.ToComponentDataArray<UnitSelectionRadius>(Allocator.Temp);
            var footprints = _unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);

            Entity best = Entity.Null;
            float bestDistanceSq = float.MaxValue;

            for (int i = 0; i < entities.Length; i++)
            {
                if (!IsControllableUnit(entities[i]))
                    continue;

                float3 delta = transforms[i].Position - worldPoint;
                delta.y = 0f;

                float pickDistance = math.max(
                    WorldPickDistance,
                    math.max(radii[i].WorldRadius, math.max(radii[i].RingRadius, footprints[i].Radius)));
                float distanceSq = math.lengthsq(delta);

                if (distanceSq <= pickDistance * pickDistance && distanceSq < bestDistanceSq)
                {
                    best = entities[i];
                    bestDistanceSq = distanceSq;
                }
            }

            entities.Dispose();
            transforms.Dispose();
            radii.Dispose();
            footprints.Dispose();

            return best;
        }

        Entity FindCombatTargetAtScreenPoint(Vector2 screenPoint)
        {
            Entity picked = FindCombatTargetByScreenRadius(screenPoint);
            if (picked != Entity.Null)
                return picked;

            return TryGetGroundPoint(screenPoint, out var worldPoint)
                ? FindCombatTargetNearWorldPoint(worldPoint)
                : Entity.Null;
        }

        Entity FindCombatTargetByScreenRadius(Vector2 screenPoint)
        {
            var entities = _combatTargetableQuery.ToEntityArray(Allocator.Temp);
            var transforms = _combatTargetableQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            Entity best = Entity.Null;
            float bestDistanceSq = float.MaxValue;

            for (int i = 0; i < entities.Length; i++)
            {
                Vector3 screen = SelectionCamera.WorldToScreenPoint(ToVector3(transforms[i].Position));
                if (screen.z <= 0f)
                    continue;

                float pickRadius = GetCombatTargetScreenPickRadius(entities[i]);
                float2 delta = new float2(screen.x - screenPoint.x, screen.y - screenPoint.y);
                float distanceSq = math.lengthsq(delta);

                if (distanceSq <= pickRadius * pickRadius && distanceSq < bestDistanceSq)
                {
                    best = entities[i];
                    bestDistanceSq = distanceSq;
                }
            }

            transforms.Dispose();
            entities.Dispose();
            return best;
        }

        Entity FindCombatTargetNearWorldPoint(float3 worldPoint)
        {
            var entities = _combatTargetableQuery.ToEntityArray(Allocator.Temp);
            var transforms = _combatTargetableQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            Entity best = Entity.Null;
            float bestDistanceSq = float.MaxValue;

            for (int i = 0; i < entities.Length; i++)
            {
                float radius = GetCombatTargetWorldPickRadius(entities[i]);
                float distanceSq = HorizontalDistanceSq(transforms[i].Position, worldPoint);
                if (distanceSq <= radius * radius && distanceSq < bestDistanceSq)
                {
                    best = entities[i];
                    bestDistanceSq = distanceSq;
                }
            }

            transforms.Dispose();
            entities.Dispose();
            return best;
        }

        float GetCombatTargetScreenPickRadius(Entity entity)
        {
            if (_entityManager.HasComponent<UnitSelectionRadius>(entity))
                return math.max(1f, _entityManager.GetComponentData<UnitSelectionRadius>(entity).ScreenPixels);

            return 28f;
        }

        float GetCombatTargetWorldPickRadius(Entity entity)
        {
            float radius = math.max(0.01f, CombatTargetPickDistance);

            if (_entityManager.HasComponent<UnitFootprint>(entity))
                radius = math.max(radius, math.max(0.01f, _entityManager.GetComponentData<UnitFootprint>(entity).Radius));

            if (_entityManager.HasComponent<ObstacleFootprint>(entity))
                radius = math.max(radius, GetEffectiveObstacleRadius(_entityManager.GetComponentData<ObstacleFootprint>(entity)));

            // 건물: transform은 footprint 중심이라 기본 반경으론 큰 건물 몸통을 클릭해도 못 잡힌다.
            //   footprint 반-대각선을 픽 반경으로 → 건물 위/가장자리 클릭으로 공격 지정 가능.
            if (_entityManager.HasComponent<CitySim.BuildingFootprint>(entity))
            {
                var  bf  = _entityManager.GetComponentData<CitySim.BuildingFootprint>(entity);
                int2 eff = CitySim.EntranceOps.RotateSize(bf.Size, bf.RotSteps);
                radius   = math.max(radius, 0.5f * GetCellSize() * math.length((float2)eff));
            }

            return radius;
        }

        // GridSettings.CellSize (없으면 1). 픽킹은 우클릭 시에만 호출되므로 매번 조회해도 무방.
        float GetCellSize()
        {
            var q = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<CitySim.GridSettings>());
            float cs = q.IsEmpty ? 1f : q.GetSingleton<CitySim.GridSettings>().CellSize;
            q.Dispose();
            return cs;
        }

        void SelectUnitsInScreenRect(Rect rect, bool additive)
        {
            if (!additive)
                ClearSelection();

            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (!IsControllableUnit(entities[i]))
                    continue;

                Vector3 screen = SelectionCamera.WorldToScreenPoint(ToVector3(transforms[i].Position));
                if (screen.z > 0f && rect.Contains(new Vector2(screen.x, screen.y)))
                    SetSelected(entities[i], true);
            }

            entities.Dispose();
            transforms.Dispose();

            if (LogSelectionEvents)
                Debug.Log($"[UnitSelectionTestController] Drag selected. Selected={_selectedQuery.CalculateEntityCount()}.");
        }

        void ClearSelection()
        {
            var entities = _unitQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                SetSelected(entities[i], false);
            }

            entities.Dispose();
        }

        static bool IsAttackMovePressed()
        {
            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.qKey.isPressed;
        }

        void IssueMoveOrders(float3 target, UnitCommandKind commandKind)
        {
            int selectedCount = _selectedQuery.CalculateEntityCount();
            if (selectedCount == 0)
                return;

            if (UseParallelLargeSelectionOrders &&
                selectedCount >= math.max(1, MinUnitsForParallelSelectionOrders))
            {
                CreateSelectedMoveOrderRequest(target, commandKind, selectedCount);
                return;
            }

            var selected = _selectedQuery.ToEntityArray(Allocator.Temp);
            if (selected.Length == 0)
            {
                selected.Dispose();
                return;
            }

            var transforms = _selectedQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var footprints = _selectedQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);
            bool useSharedGroupPath = UseSharedGroupPath && selected.Length <= math.max(1, MaxUnitsForSharedGroupPath);
            bool useOccupiedSlotSearch = AvoidOccupiedSlots && selected.Length <= math.max(1, MaxUnitsForOccupiedSlotSearch);
            byte skipPerUnitPathfinding = !useSharedGroupPath && selected.Length > math.max(1, MaxUnitsForSharedGroupPath)
                ? (byte)1
                : (byte)0;
            var allUnits = useOccupiedSlotSearch ? _unitQuery.ToEntityArray(Allocator.Temp) : default;
            var allTransforms = useOccupiedSlotSearch ? _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp) : default;
            var allFootprints = useOccupiedSlotSearch ? _unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp) : default;
            var allActivities = useOccupiedSlotSearch ? _unitQuery.ToComponentDataArray<UnitActivityState>(Allocator.Temp) : default;
            bool needsObstacleSnapshots = useSharedGroupPath || useOccupiedSlotSearch;
            var obstacleTransforms = needsObstacleSnapshots ? _obstacleQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp) : default;
            var obstacleFootprints = needsObstacleSnapshots ? _obstacleQuery.ToComponentDataArray<ObstacleFootprint>(Allocator.Temp) : default;
            bool hasNavigationGrid = TryGetNavigationGrid(out var navigationGrid);
            float3 selectionCenter = CalculateSelectionCenter(transforms);
            ClearAttackTargets(selected);
            float3 forward = target - selectionCenter;
            forward.y = 0f;

            if (math.lengthsq(forward) <= 0.0001f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);

            float3 right = math.normalize(math.cross(math.up(), forward));
            int columns = Mathf.CeilToInt(Mathf.Sqrt(selected.Length));
            int rows = Mathf.CeilToInt(selected.Length / (float)columns);
            var columnWidths = new NativeArray<float>(columns, Allocator.Temp);
            var rowDepths = new NativeArray<float>(rows, Allocator.Temp);
            int[] formationOrder = BuildFormationOrder(transforms, footprints, selectionCenter, right, forward, columns);
            var groupPath = new NativeList<float3>(Allocator.Temp);
            bool hasSharedGroupPath = useSharedGroupPath && TryBuildSharedGroupPath(
                hasNavigationGrid,
                navigationGrid,
                selectionCenter,
                target,
                footprints,
                obstacleTransforms,
                obstacleFootprints,
                groupPath);

            CalculateFormationBounds(footprints, formationOrder, columns, columnWidths, rowDepths);
            _moveSlotReservations.Clear();
            Entity directMoveBatch = Entity.Null;
            DynamicBuffer<UnitDirectMoveOrderElement> directMoveOrders = default;
            if (skipPerUnitPathfinding != 0)
            {
                directMoveBatch = _entityManager.CreateEntity(typeof(UnitDirectMoveOrderBatch));
                directMoveOrders = _entityManager.AddBuffer<UnitDirectMoveOrderElement>(directMoveBatch);
                directMoveOrders.EnsureCapacity(formationOrder.Length);
            }

            for (int orderIndex = 0; orderIndex < formationOrder.Length; orderIndex++)
            {
                int unitIndex = formationOrder[orderIndex];
                int x = orderIndex % columns;
                int z = orderIndex / columns;

                float offsetX = GetSlotCenterOffset(columnWidths, x);
                float offsetZ = GetSlotCenterOffset(rowDepths, z);
                float3 desiredDestination = target + right * offsetX + forward * offsetZ;
                float3 destination = useOccupiedSlotSearch
                    ? FindAvailableMoveSlot(
                        desiredDestination,
                        transforms[unitIndex].Position,
                        footprints[unitIndex],
                        right,
                        forward,
                        allUnits,
                        allTransforms,
                        allFootprints,
                        allActivities,
                        obstacleTransforms,
                        obstacleFootprints,
                        hasNavigationGrid,
                        navigationGrid)
                    : desiredDestination;

                _moveSlotReservations.Add(new MoveSlotReservation
                {
                    Position = destination,
                    Radius = math.max(0.01f, footprints[unitIndex].Radius),
                });

                if (skipPerUnitPathfinding != 0)
                {
                    directMoveOrders.Add(new UnitDirectMoveOrderElement
                    {
                        Unit = selected[unitIndex],
                        Target = destination,
                        StopDistance = MoveStopDistance,
                        CommandKind = commandKind,
                    });
                    continue;
                }

                Entity request = _entityManager.CreateEntity(typeof(MoveOrderRequest));
                _entityManager.SetComponentData(request, new MoveOrderRequest
                {
                    Unit = selected[unitIndex],
                    Target = destination,
                    StopDistance = MoveStopDistance,
                    CommandKind = commandKind,
                    SkipPathfinding = skipPerUnitPathfinding,
                });

                if (hasSharedGroupPath)
                    AddSharedGroupPathToRequest(request, transforms[unitIndex].Position, destination, groupPath, right, forward, offsetX, offsetZ);
            }

            groupPath.Dispose();
            if (needsObstacleSnapshots)
            {
                obstacleFootprints.Dispose();
                obstacleTransforms.Dispose();
            }

            if (useOccupiedSlotSearch)
            {
                allActivities.Dispose();
                allFootprints.Dispose();
                allTransforms.Dispose();
                allUnits.Dispose();
            }

            rowDepths.Dispose();
            columnWidths.Dispose();
            footprints.Dispose();
            transforms.Dispose();
            selected.Dispose();
        }

        void ClearAttackTargets(NativeArray<Entity> units)
        {
            for (int i = 0; i < units.Length; i++)
            {
                if (!_entityManager.HasComponent<CombatAttackTarget>(units[i]))
                    continue;

                _entityManager.SetComponentData(units[i], new CombatAttackTarget
                {
                    Target = Entity.Null,
                    ApproachRefreshTime = 0f,
                    HasTarget = 0,
                });
            }
        }

        void IssueAttackOrders(Entity target)
        {
            if (_selectedQuery.CalculateEntityCount() == 0)
                return;

            Entity request = _entityManager.CreateEntity(typeof(SelectedUnitAttackOrderRequest));
            _entityManager.SetComponentData(request, new SelectedUnitAttackOrderRequest
            {
                Target = target,
                CommandKind = UnitCommandKind.ForceAttack,
            });
        }

        void CreateSelectedMoveOrderRequest(float3 target, UnitCommandKind commandKind, int selectedCount)
        {
            Entity request = _entityManager.CreateEntity(typeof(SelectedUnitMoveOrderRequest));
            _entityManager.SetComponentData(request, new SelectedUnitMoveOrderRequest
            {
                Target = target,
                FormationForward = ResolveFastFormationForward(),
                StopDistance = MoveStopDistance,
                FormationSpacing = math.max(0.01f, FormationSpacing + FormationPadding),
                UnitCount = selectedCount,
                CommandKind = commandKind,
            });
        }

        float3 ResolveFastFormationForward()
        {
            float3 forward = SelectionCamera != null
                ? ToFloat3(SelectionCamera.transform.forward)
                : new float3(0f, 0f, 1f);
            forward.y = 0f;

            return math.lengthsq(forward) <= 0.0001f
                ? new float3(0f, 0f, 1f)
                : math.normalize(forward);
        }

        float3 CalculateSelectionCenter(NativeArray<LocalTransform> transforms)
        {
            float3 center = float3.zero;

            for (int i = 0; i < transforms.Length; i++)
                center += transforms[i].Position;

            return center / math.max(1, transforms.Length);
        }

        bool TryBuildSharedGroupPath(
            bool hasNavigationGrid,
            UnitNavigationGrid navigationGrid,
            float3 selectionCenter,
            float3 target,
            NativeArray<UnitFootprint> footprints,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            NativeList<float3> groupPath)
        {
            if (!UseSharedGroupPath || !hasNavigationGrid || footprints.Length <= 1)
                return false;

            float radius = CalculateRepresentativeGroupRadius(footprints);
            bool pathFound = UnitPathfinding.TryBuildPath(
                navigationGrid,
                selectionCenter,
                target,
                radius,
                obstacleTransforms,
                obstacleFootprints,
                groupPath,
                out bool reachedTarget);

            return pathFound && reachedTarget && groupPath.Length >= 2;
        }

        static float CalculateRepresentativeGroupRadius(NativeArray<UnitFootprint> footprints)
        {
            float radius = 0.01f;

            for (int i = 0; i < footprints.Length; i++)
                radius = math.max(radius, math.max(0.01f, footprints[i].Radius));

            return radius;
        }

        void AddSharedGroupPathToRequest(
            Entity request,
            float3 unitStart,
            float3 destination,
            NativeList<float3> groupPath,
            float3 right,
            float3 forward,
            float offsetX,
            float offsetZ)
        {
            var requestPath = _entityManager.AddBuffer<MoveOrderPathWaypoint>(request);
            requestPath.Add(new MoveOrderPathWaypoint
            {
                Position = unitStart,
            });

            for (int i = 1; i < groupPath.Length - 1; i++)
            {
                requestPath.Add(new MoveOrderPathWaypoint
                {
                    Position = groupPath[i] + right * offsetX + forward * offsetZ,
                });
            }

            requestPath.Add(new MoveOrderPathWaypoint
            {
                Position = destination,
            });
        }

        int[] BuildFormationOrder(
            NativeArray<LocalTransform> transforms,
            NativeArray<UnitFootprint> footprints,
            float3 selectionCenter,
            float3 right,
            float3 forward,
            int columns)
        {
            int count = transforms.Length;
            var order = new int[count];
            for (int i = 0; i < count; i++)
                order[i] = i;

            if (!PreserveRelativeFormation || count <= 1)
                return order;

            var entries = new FormationSortEntry[count];
            for (int i = 0; i < count; i++)
            {
                float3 local = transforms[i].Position - selectionCenter;
                local.y = 0f;
                entries[i] = new FormationSortEntry
                {
                    Index = i,
                    Lateral = math.dot(local, right),
                    Forward = math.dot(local, forward),
                    Radius = math.max(0.01f, footprints[i].Radius),
                };
            }

            System.Array.Sort(entries, CompareFormationEntries);

            for (int i = 0; i < count; i++)
                order[i] = entries[i].Index;

            return order;
        }

        static int CompareFormationEntries(FormationSortEntry a, FormationSortEntry b)
        {
            int forwardCompare = b.Forward.CompareTo(a.Forward);
            if (forwardCompare != 0)
                return forwardCompare;

            int lateralCompare = a.Lateral.CompareTo(b.Lateral);
            if (lateralCompare != 0)
                return lateralCompare;

            return b.Radius.CompareTo(a.Radius);
        }

        void CalculateFormationBounds(
            NativeArray<UnitFootprint> footprints,
            int[] formationOrder,
            int columns,
            NativeArray<float> columnWidths,
            NativeArray<float> rowDepths)
        {
            float fallback = math.max(0.1f, FormationSpacing);
            float padding = math.max(0f, FormationPadding);

            for (int orderIndex = 0; orderIndex < formationOrder.Length; orderIndex++)
            {
                int unitIndex = formationOrder[orderIndex];
                int x = orderIndex % columns;
                int z = orderIndex / columns;
                UnitFootprint footprint = footprints[unitIndex];

                float2 size = math.max(footprint.Size, new float2(footprint.Radius * 2f));
                float width = math.max(fallback, size.x + padding);
                float depth = math.max(fallback, size.y + padding);

                columnWidths[x] = math.max(columnWidths[x], width);
                rowDepths[z] = math.max(rowDepths[z], depth);
            }
        }

        static float GetSlotCenterOffset(NativeArray<float> sizes, int index)
        {
            float total = 0f;
            for (int i = 0; i < sizes.Length; i++)
                total += sizes[i];

            float offset = -total * 0.5f;
            for (int i = 0; i < index; i++)
                offset += sizes[i];

            return offset + sizes[index] * 0.5f;
        }

        float3 FindAvailableMoveSlot(
            float3 desiredDestination,
            float3 startPosition,
            UnitFootprint footprint,
            float3 right,
            float3 forward,
            NativeArray<Entity> allUnits,
            NativeArray<LocalTransform> allTransforms,
            NativeArray<UnitFootprint> allFootprints,
            NativeArray<UnitActivityState> allActivities,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            bool hasNavigationGrid,
            UnitNavigationGrid navigationGrid)
        {
            float radius = math.max(0.01f, footprint.Radius);
            float searchStep = math.max(FormationSpacing, radius * 2f + math.max(0f, FormationPadding));

            if (IsMoveSlotAvailable(
                    desiredDestination,
                    startPosition,
                    radius,
                    allUnits,
                    allTransforms,
                    allFootprints,
                    allActivities,
                    obstacleTransforms,
                    obstacleFootprints,
                    hasNavigationGrid,
                    navigationGrid))
                return desiredDestination;

            for (int ring = 1; ring <= SlotSearchRings; ring++)
            {
                for (int z = -ring; z <= ring; z++)
                {
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (math.max(math.abs(x), math.abs(z)) != ring)
                            continue;

                        float3 candidate = desiredDestination + right * (x * searchStep) + forward * (z * searchStep);
                        if (IsMoveSlotAvailable(
                                candidate,
                                startPosition,
                                radius,
                                allUnits,
                                allTransforms,
                                allFootprints,
                                allActivities,
                                obstacleTransforms,
                                obstacleFootprints,
                                hasNavigationGrid,
                                navigationGrid))
                            return candidate;
                    }
                }
            }

            return desiredDestination;
        }

        bool IsMoveSlotAvailable(
            float3 destination,
            float3 startPosition,
            float radius,
            NativeArray<Entity> allUnits,
            NativeArray<LocalTransform> allTransforms,
            NativeArray<UnitFootprint> allFootprints,
            NativeArray<UnitActivityState> allActivities,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            bool hasNavigationGrid,
            UnitNavigationGrid navigationGrid)
        {
            for (int i = 0; i < _moveSlotReservations.Count; i++)
            {
                float minDistance = radius + _moveSlotReservations[i].Radius + math.max(0f, FormationPadding);
                if (HorizontalDistanceSq(destination, _moveSlotReservations[i].Position) < minDistance * minDistance)
                    return false;
            }

            for (int i = 0; i < allUnits.Length; i++)
            {
                if (IsSelected(allUnits[i]))
                    continue;

                float otherRadius = math.max(0.01f, allFootprints[i].Radius);
                float minDistance = radius + otherRadius + GetOccupiedSlotPadding(allActivities[i]);
                if (HorizontalDistanceSq(destination, allTransforms[i].Position) < minDistance * minDistance)
                    return false;
            }

            for (int i = 0; i < obstacleFootprints.Length; i++)
            {
                float obstacleRadius = GetEffectiveObstacleRadius(obstacleFootprints[i]);
                float minDistance = radius + obstacleRadius + GetObstaclePadding(obstacleFootprints[i]);
                if (HorizontalDistanceSq(destination, obstacleTransforms[i].Position) < minDistance * minDistance)
                    return false;
            }

            return true;
        }

        bool TryGetNavigationGrid(out UnitNavigationGrid grid)
        {
            grid = default;
            using var query = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<UnitNavigationGrid>());
            if (query.CalculateEntityCount() != 1)
                return false;

            grid = query.GetSingleton<UnitNavigationGrid>();
            return true;
        }

        static float GetEffectiveObstacleRadius(ObstacleFootprint obstacle)
        {
            float sizeRadius = math.length(math.max(obstacle.Size, new float2(0.01f))) * 0.5f;
            return math.max(math.max(0.01f, obstacle.Radius), sizeRadius);
        }

        float GetObstaclePadding(ObstacleFootprint obstacle)
        {
            return math.max(0f, FormationPadding) + math.max(0f, obstacle.ExtraPadding);
        }

        float GetOccupiedSlotPadding(UnitActivityState activity)
        {
            float padding = math.max(0f, FormationPadding);

            switch (activity.Value)
            {
                case UnitActivityKind.Working:
                    return padding * 2f;
                case UnitActivityKind.Anchored:
                    return padding * 3f;
                default:
                    return padding;
            }
        }

        static float HorizontalDistanceSq(float3 a, float3 b)
        {
            float3 delta = a - b;
            delta.y = 0f;
            return math.lengthsq(delta);
        }

        bool TryGetGroundPoint(Vector2 screenPoint, out float3 point)
        {
            Ray ray = SelectionCamera.ScreenPointToRay(screenPoint);

            if (Physics.Raycast(ray, out var hit, 5000f, GroundLayerMask))
            {
                point = ToFloat3(hit.point);
                return true;
            }

            var plane = new Plane(Vector3.up, new Vector3(0f, GroundPlaneY, 0f));
            if (plane.Raycast(ray, out float enter))
            {
                point = ToFloat3(ray.GetPoint(enter));
                return true;
            }

            point = float3.zero;
            return false;
        }

        void UpdateSelectionRings()
        {
            if (!_hasWorld)
                return;

            EnsureRingAssets();

            var entities = _selectedQuery.ToEntityArray(Allocator.Temp);
            var transforms = _selectedQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var radii = _selectedQuery.ToComponentDataArray<UnitSelectionRadius>(Allocator.Temp);
            var footprints = _selectedQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);

            _removeBuffer.Clear();
            foreach (var pair in _rings)
            {
                bool stillSelected = false;
                for (int i = 0; i < entities.Length; i++)
                {
                    if (pair.Key == entities[i])
                    {
                        stillSelected = true;
                        break;
                    }
                }

                if (!stillSelected)
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
            {
                if (_rings.TryGetValue(_removeBuffer[i], out var ring))
                    Destroy(ring);

                _rings.Remove(_removeBuffer[i]);
            }

            for (int i = 0; i < entities.Length; i++)
            {
                if (!_rings.TryGetValue(entities[i], out var ring))
                {
                    ring = CreateRingObject();
                    _rings.Add(entities[i], ring);
                }

                float radius = math.max(0.01f, radii[i].RingRadius);
                float2 footprintSize = math.max(footprints[i].Size, new float2(0.01f, 0.01f));
                float3 position = transforms[i].Position;
                ring.transform.position = new Vector3(position.x, position.y + RingHeightOffset, position.z);
                ring.transform.rotation = ToQuaternion(transforms[i].Rotation);

                var filter = ring.GetComponent<MeshFilter>();
                if (MarkerShape == SelectionMarkerShape.CornerRectangle)
                {
                    filter.sharedMesh = _cornerRectangleMesh;
                    ring.transform.localScale = new Vector3(footprintSize.x, 1f, footprintSize.y);
                }
                else
                {
                    filter.sharedMesh = _ringMesh;
                    ring.transform.localScale = new Vector3(radius, 1f, radius);
                }
            }

            entities.Dispose();
            transforms.Dispose();
            radii.Dispose();
            footprints.Dispose();
        }

        void UpdateUnitNameVisuals()
        {
            if (!_hasWorld || !ShowUnitNames)
            {
                ClearUnitNameObjects();
                return;
            }

            EntityQuery nameQuery = UnitNamesSelectedOnly
                ? _selectedUnitNameQuery
                : _unitNameQuery;
            var entities = nameQuery.ToEntityArray(Allocator.Temp);
            var transforms = nameQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var names = nameQuery.ToComponentDataArray<UnitDisplayName>(Allocator.Temp);

            _debugNameActiveBuffer.Clear();

            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                _debugNameActiveBuffer.Add(entity);
                UpdateDebugNameLabel(entity, transforms[i].Position, names[i]);
            }

            RemoveInactiveNameObjects();

            names.Dispose();
            transforms.Dispose();
            entities.Dispose();
        }

        void UpdateDebugNameLabel(Entity entity, float3 position, UnitDisplayName displayName)
        {
            if (!_debugNameLabelObjects.TryGetValue(entity, out var labelObject))
            {
                labelObject = CreateNameLabelObject();
                _debugNameLabelObjects.Add(entity, labelObject);
            }

            labelObject.transform.position = new Vector3(
                position.x,
                position.y + math.max(1.35f, RingHeightOffset * 28f),
                position.z);

            if (SelectionCamera != null)
                labelObject.transform.rotation = SelectionCamera.transform.rotation;

            var text = labelObject.GetComponent<TextMesh>();
            text.color = DebugNameColor;

            // 명령 종류가 바뀔 때만 문자열 재조립(캐시) — 내용이 같으면 대입도 생략(메시 재생성 방지).
            var kind = _entityManager.HasComponent<UnitCommandState>(entity)
                ? _entityManager.GetComponentData<UnitCommandState>(entity).Kind
                : UnitCommandKind.None;
            if (!_nameTextCache.TryGetValue(entity, out var nc) || nc.Kind != kind)
            {
                nc = (kind, FormatDisplayName(entity, displayName));
                _nameTextCache[entity] = nc;
            }
            if (!ReferenceEquals(text.text, nc.Text)) text.text = nc.Text;
        }

        GameObject CreateNameLabelObject()
        {
            var labelObject = new GameObject("Unit Name Debug");
            labelObject.transform.SetParent(transform, false);

            var text = labelObject.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.16f;
            text.fontSize = 30;
            text.color = DebugNameColor;

            return labelObject;
        }

        string FormatDisplayName(Entity entity, UnitDisplayName displayName)
        {
            if (!_entityManager.HasComponent<UnitCommandState>(entity))
                return displayName.Value.ToString();

            UnitCommandState command = _entityManager.GetComponentData<UnitCommandState>(entity);
            if (command.Kind == UnitCommandKind.None)
                return displayName.Value.ToString();

            return $"{displayName.Value} [{command.Kind}]";
        }

        void UpdateMovementDebugVisuals()
        {
            if (!_hasWorld || !ShowMovementDebug)
            {
                ClearMovementDebugObjects();
                return;
            }

            EnsureDebugLineMaterial();

            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var footprints = _unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);

            _debugActiveBuffer.Clear();

            for (int i = 0; i < entities.Length; i++)
            {
                if (DebugSelectedOnly && !IsSelected(entities[i]))
                    continue;

                _debugActiveBuffer.Add(entities[i]);

                float3 position = transforms[i].Position;
                float radius = math.max(0.01f, footprints[i].Radius);
                UpdateDebugRadius(entities[i], position, radius);

                UnitMoveTarget target = UnitMoveTarget.None;
                bool hasMoveTargetComponent = _entityManager.HasComponent<UnitMoveTarget>(entities[i]);
                if (hasMoveTargetComponent)
                    target = _entityManager.GetComponentData<UnitMoveTarget>(entities[i]);

                UpdateDebugStatusLabel(entities[i], position, target, hasMoveTargetComponent);

                if (_entityManager.HasComponent<UnitMoveTarget>(entities[i]))
                {
                    if (target.HasTarget != 0)
                    {
                        UpdateDebugTargetLine(entities[i], position, target.Position, GetDebugPathStatusColor(target.PathStatus));
                        UpdateDebugPathLine(entities[i], position, target.Position);
                        continue;
                    }
                }

                RemoveDebugTargetLine(entities[i]);
                RemoveDebugPathLine(entities[i]);
            }

            UpdateObstacleDebugVisuals();
            RemoveInactiveDebugObjects();

            footprints.Dispose();
            transforms.Dispose();
            entities.Dispose();
        }

        void UpdateDebugRadius(Entity entity, float3 position, float radius)
        {
            if (!_debugRadiusObjects.TryGetValue(entity, out var radiusObject))
            {
                radiusObject = CreateLineObject("Unit Radius Debug", true);
                _debugRadiusObjects.Add(entity, radiusObject);
            }

            var line = radiusObject.GetComponent<LineRenderer>();
            int segments = math.max(16, DebugCircleSegments);
            line.positionCount = segments;
            line.loop = true;
            line.startColor = DebugRadiusColor;
            line.endColor = DebugRadiusColor;
            line.startWidth = DebugLineWidth;
            line.endWidth = DebugLineWidth;

            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * math.PI * 2f;
                line.SetPosition(i, new Vector3(
                    position.x + math.cos(angle) * radius,
                    position.y + RingHeightOffset * 1.5f,
                    position.z + math.sin(angle) * radius));
            }
        }

        void UpdateDebugTargetLine(Entity entity, float3 position, float3 target, Color color)
        {
            if (!_debugTargetLineObjects.TryGetValue(entity, out var lineObject))
            {
                lineObject = CreateLineObject("Unit Target Debug", false);
                _debugTargetLineObjects.Add(entity, lineObject);
            }

            var line = lineObject.GetComponent<LineRenderer>();
            line.positionCount = 2;
            line.loop = false;
            line.startColor = color;
            line.endColor = color;
            line.startWidth = DebugLineWidth;
            line.endWidth = DebugLineWidth;
            line.SetPosition(0, new Vector3(position.x, position.y + RingHeightOffset * 2f, position.z));
            line.SetPosition(1, new Vector3(target.x, target.y + RingHeightOffset * 2f, target.z));
        }

        void UpdateDebugPathLine(Entity entity, float3 position, float3 target)
        {
            if (!_entityManager.HasBuffer<UnitPathWaypoint>(entity))
            {
                RemoveDebugPathLine(entity);
                return;
            }

            DynamicBuffer<UnitPathWaypoint> waypoints = _entityManager.GetBuffer<UnitPathWaypoint>(entity, true);
            if (waypoints.Length == 0)
            {
                RemoveDebugPathLine(entity);
                return;
            }

            if (!_debugPathLineObjects.TryGetValue(entity, out var lineObject))
            {
                lineObject = CreateLineObject("Unit Path Debug", false);
                _debugPathLineObjects.Add(entity, lineObject);
            }

            var line = lineObject.GetComponent<LineRenderer>();
            line.positionCount = waypoints.Length + 2;
            line.loop = false;
            line.startColor = DebugWaypointColor;
            line.endColor = DebugWaypointColor;
            line.startWidth = DebugLineWidth * 0.75f;
            line.endWidth = DebugLineWidth * 0.75f;
            line.SetPosition(0, new Vector3(position.x, position.y + RingHeightOffset * 3f, position.z));

            for (int i = 0; i < waypoints.Length; i++)
            {
                float3 waypoint = waypoints[i].Position;
                line.SetPosition(i + 1, new Vector3(
                    waypoint.x,
                    waypoint.y + RingHeightOffset * 3f,
                    waypoint.z));
            }

            line.SetPosition(waypoints.Length + 1, new Vector3(
                target.x,
                target.y + RingHeightOffset * 3f,
                target.z));
        }

        void UpdateDebugStatusLabel(Entity entity, float3 position, UnitMoveTarget target, bool hasMoveTarget)
        {
            if (!_debugStatusLabelObjects.TryGetValue(entity, out var labelObject))
            {
                labelObject = CreateStatusLabelObject();
                _debugStatusLabelObjects.Add(entity, labelObject);
            }

            labelObject.transform.position = new Vector3(
                position.x,
                position.y + math.max(0.8f, RingHeightOffset * 16f),
                position.z);

            if (SelectionCamera != null)
                labelObject.transform.rotation = SelectionCamera.transform.rotation;

            var text = labelObject.GetComponent<TextMesh>();
            text.color = GetDebugPathStatusColor(target.PathStatus);
            text.text = hasMoveTarget
                ? FormatPathStatus(target)
                : "No Target";
        }

        GameObject CreateStatusLabelObject()
        {
            var labelObject = new GameObject("Unit Path Status Debug");
            labelObject.transform.SetParent(transform, false);

            var text = labelObject.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.18f;
            text.fontSize = 32;
            text.color = DebugStatusTextColor;

            return labelObject;
        }

        static string FormatPathStatus(UnitMoveTarget target)
        {
            if (target.HasTarget == 0)
                return target.PathStatus == UnitPathStatus.PathFailed ? "Failed" : "Idle";

            switch (target.PathStatus)
            {
                case UnitPathStatus.PathReady:
                    return "Ready";
                case UnitPathStatus.PathPartial:
                    return "Partial";
                case UnitPathStatus.PathFailed:
                    return "Failed";
                default:
                    return "Direct";
            }
        }

        Color GetDebugPathStatusColor(UnitPathStatus status)
        {
            switch (status)
            {
                case UnitPathStatus.PathPartial:
                    return DebugPartialPathColor;
                case UnitPathStatus.PathFailed:
                    return DebugFailedPathColor;
                case UnitPathStatus.Direct:
                    return DebugTargetLineColor;
                default:
                    return DebugTargetLineColor;
            }
        }

        void UpdateCombatDebugVisuals()
        {
            if (!_hasWorld || !ShowCombatDebug)
            {
                ClearCombatDebugObjects();
                return;
            }

            EnsureDebugLineMaterial();

            var entities = _unitQuery.ToEntityArray(Allocator.Temp);
            var transforms = _unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var weaponOwners = _weaponQuery.ToComponentDataArray<CombatWeaponOwner>(Allocator.Temp);
            var weapons = _weaponQuery.ToComponentDataArray<CombatWeapon>(Allocator.Temp);
            var weaponReadyStates = _weaponQuery.ToComponentDataArray<CombatWeaponReadyState>(Allocator.Temp);

            _debugCombatActiveBuffer.Clear();
            _debugCombatHealthActiveBuffer.Clear();

            for (int i = 0; i < entities.Length; i++)
            {
                Entity entity = entities[i];
                if (CombatDebugSelectedOnly && !IsSelected(entity))
                    continue;

                _debugCombatActiveBuffer.Add(entity);

                float3 position = transforms[i].Position;
                float range = GetMaxWeaponRange(entity, weaponOwners, weapons);
                if (range > 0f)
                    UpdateDebugCombatRange(entity, position, range);
                else
                    RemoveDebugCombatRange(entity);

                if (_entityManager.HasComponent<CombatHealth>(entity))
                    UpdateDebugCombatHealthLabel(entity, position, _entityManager.GetComponentData<CombatHealth>(entity));

                UpdateDebugCombatWeaponStateLabel(entity, position, weaponOwners, weaponReadyStates);

                if (TryGetAttackTargetPosition(entity, out Entity target, out float3 targetPosition))
                {
                    UpdateDebugCombatAttackLine(entity, position, targetPosition);

                    if (_entityManager.HasComponent<CombatHealth>(target))
                        UpdateDebugCombatHealthLabel(target, targetPosition, _entityManager.GetComponentData<CombatHealth>(target));
                }
                else
                {
                    RemoveDebugCombatAttackLine(entity);
                }
            }

            RemoveInactiveCombatDebugObjects();

            weaponReadyStates.Dispose();
            weapons.Dispose();
            weaponOwners.Dispose();
            transforms.Dispose();
            entities.Dispose();
        }

        bool IsSelected(Entity entity)
        {
            return _entityManager.HasComponent<SelectedUnit>(entity) &&
                   _entityManager.IsComponentEnabled<SelectedUnit>(entity);
        }

        void SetSelected(Entity entity, bool selected)
        {
            if (!_entityManager.Exists(entity))
                return;

            if (selected && !IsControllableUnit(entity))
                return;

            if (!_entityManager.HasComponent<SelectedUnit>(entity))
                _entityManager.AddComponent<SelectedUnit>(entity);

            _entityManager.SetComponentEnabled<SelectedUnit>(entity, selected);
        }

        bool IsControllableUnit(Entity entity)
        {
            if (!RestrictControlToLocalId)
                return true;

            return _entityManager.HasComponent<TeamInfoData>(entity) &&
                   _entityManager.GetComponentData<TeamInfoData>(entity).LocalID == ControllableLocalId;
        }

        bool TryGetAttackTargetPosition(Entity entity, out Entity target, out float3 targetPosition)
        {
            target = Entity.Null;
            targetPosition = float3.zero;

            if (!_entityManager.HasComponent<CombatAttackTarget>(entity))
                return false;

            CombatAttackTarget attackTarget = _entityManager.GetComponentData<CombatAttackTarget>(entity);
            target = attackTarget.Target;
            if (attackTarget.HasTarget == 0 ||
                target == Entity.Null ||
                !_entityManager.Exists(target) ||
                !_entityManager.HasComponent<LocalTransform>(target))
                return false;

            targetPosition = _entityManager.GetComponentData<LocalTransform>(target).Position;
            return true;
        }

        void UpdateDebugCombatRange(Entity entity, float3 position, float range)
        {
            if (!_debugCombatRangeObjects.TryGetValue(entity, out var rangeObject))
            {
                rangeObject = CreateLineObject("Combat Range Debug", true);
                _debugCombatRangeObjects.Add(entity, rangeObject);
            }

            var line = rangeObject.GetComponent<LineRenderer>();
            int segments = math.max(16, DebugCircleSegments);
            line.positionCount = segments;
            line.loop = true;
            line.startColor = DebugCombatRangeColor;
            line.endColor = DebugCombatRangeColor;
            line.startWidth = DebugLineWidth;
            line.endWidth = DebugLineWidth;

            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * math.PI * 2f;
                line.SetPosition(i, new Vector3(
                    position.x + math.cos(angle) * range,
                    position.y + RingHeightOffset * 2.5f,
                    position.z + math.sin(angle) * range));
            }
        }

        void UpdateDebugCombatAttackLine(Entity entity, float3 position, float3 target)
        {
            if (!_debugCombatAttackLineObjects.TryGetValue(entity, out var lineObject))
            {
                lineObject = CreateLineObject("Combat Attack Debug", false);
                _debugCombatAttackLineObjects.Add(entity, lineObject);
            }

            var line = lineObject.GetComponent<LineRenderer>();
            line.positionCount = 2;
            line.loop = false;
            line.startColor = DebugCombatAttackLineColor;
            line.endColor = DebugCombatAttackLineColor;
            line.startWidth = DebugLineWidth * 1.25f;
            line.endWidth = DebugLineWidth * 1.25f;
            line.SetPosition(0, new Vector3(position.x, position.y + RingHeightOffset * 4f, position.z));
            line.SetPosition(1, new Vector3(target.x, target.y + RingHeightOffset * 4f, target.z));
        }

        void UpdateDebugCombatHealthLabel(Entity entity, float3 position, CombatHealth healthData)
        {
            if (!_entityManager.Exists(entity))
                return;

            if (!_debugCombatHealthActiveBuffer.Contains(entity))
                _debugCombatHealthActiveBuffer.Add(entity);

            if (!_debugCombatHealthLabelObjects.TryGetValue(entity, out var labelObject))
            {
                labelObject = CreateCombatHealthLabelObject();
                _debugCombatHealthLabelObjects.Add(entity, labelObject);
            }

            labelObject.transform.position = new Vector3(
                position.x,
                position.y + math.max(1.1f, RingHeightOffset * 22f),
                position.z);

            if (SelectionCamera != null)
                labelObject.transform.rotation = SelectionCamera.transform.rotation;

            float maxHealth = math.max(0.01f, healthData.MaxHealth);
            float health = math.max(0f, healthData.Health);
            float ratio = math.saturate(health / maxHealth);

            var text = labelObject.GetComponent<TextMesh>();
            text.color = Color.Lerp(DebugFailedPathColor, DebugCombatHealthColor, ratio);

            // 표시 값(정수)이 바뀔 때만 문자열 재조립 — 매 프레임 보간은 라벨 수만큼 GC 쓰레기.
            var shown = new int2((int)math.round(health), (int)math.round(maxHealth));
            if (!_hpTextCache.TryGetValue(entity, out var hc) || !hc.Shown.Equals(shown))
            {
                hc = (shown, $"HP {health:0}/{maxHealth:0}");
                _hpTextCache[entity] = hc;
            }
            if (!ReferenceEquals(text.text, hc.Text)) text.text = hc.Text;
        }

        GameObject CreateCombatHealthLabelObject()
        {
            var labelObject = new GameObject("Combat Health Debug");
            labelObject.transform.SetParent(transform, false);

            var text = labelObject.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.16f;
            text.fontSize = 30;
            text.color = DebugCombatHealthColor;

            return labelObject;
        }

        void UpdateDebugCombatWeaponStateLabel(
            Entity entity,
            float3 position,
            NativeArray<CombatWeaponOwner> weaponOwners,
            NativeArray<CombatWeaponReadyState> weaponReadyStates)
        {
            string status = FormatWeaponReadyStates(entity, weaponOwners, weaponReadyStates, out bool allReady);
            if (string.IsNullOrEmpty(status))
            {
                RemoveDebugCombatWeaponStateLabel(entity);
                return;
            }

            if (!_debugCombatWeaponStateLabelObjects.TryGetValue(entity, out var labelObject))
            {
                labelObject = CreateCombatWeaponStateLabelObject();
                _debugCombatWeaponStateLabelObjects.Add(entity, labelObject);
            }

            labelObject.transform.position = new Vector3(
                position.x,
                position.y + math.max(1.45f, RingHeightOffset * 30f),
                position.z);

            if (SelectionCamera != null)
                labelObject.transform.rotation = SelectionCamera.transform.rotation;

            var text = labelObject.GetComponent<TextMesh>();
            text.color = allReady ? DebugCombatWeaponReadyColor : DebugCombatWeaponBlockedColor;
            text.text = status;
        }

        GameObject CreateCombatWeaponStateLabelObject()
        {
            var labelObject = new GameObject("Combat Weapon State Debug");
            labelObject.transform.SetParent(transform, false);

            var text = labelObject.AddComponent<TextMesh>();
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.14f;
            text.fontSize = 28;
            text.color = DebugCombatWeaponBlockedColor;

            return labelObject;
        }

        static string FormatWeaponReadyStates(
            Entity owner,
            NativeArray<CombatWeaponOwner> weaponOwners,
            NativeArray<CombatWeaponReadyState> weaponReadyStates,
            out bool allReady)
        {
            allReady = true;
            string status = string.Empty;

            for (int i = 0; i < weaponOwners.Length; i++)
            {
                if (weaponOwners[i].Owner != owner)
                    continue;

                CombatWeaponReadyState readyState = weaponReadyStates[i];
                if (readyState.CanFire == 0)
                    allReady = false;

                string line = readyState.CanFire != 0
                    ? $"W{weaponOwners[i].WeaponIndex} FIRE"
                    : $"W{weaponOwners[i].WeaponIndex} {FormatBlockReasons(readyState.BlockedReasons)}";
                status = string.IsNullOrEmpty(status)
                    ? line
                    : $"{status}\n{line}";
            }

            if (string.IsNullOrEmpty(status))
                allReady = false;

            return status;
        }

        static string FormatBlockReasons(CombatWeaponBlockReason reasons)
        {
            if (reasons == CombatWeaponBlockReason.None)
                return "Ready";

            string text = string.Empty;
            AppendReason(ref text, reasons, CombatWeaponBlockReason.NoOwner, "NoOwner");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.NoTarget, "NoTarget");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.InvalidTarget, "Invalid");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.UnsupportedTargetType, "Type");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.OutOfRange, "Range");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.NeedStop, "Stop");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.NeedBodyAim, "BodyAim");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.NeedTurretAim, "TurretAim");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.NeedSetup, "Setup");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.Cooldown, "Cooldown");
            AppendReason(ref text, reasons, CombatWeaponBlockReason.BlockedLineOfSight, "LOS");
            return text;
        }

        static void AppendReason(
            ref string text,
            CombatWeaponBlockReason reasons,
            CombatWeaponBlockReason reason,
            string label)
        {
            if ((reasons & reason) == 0)
                return;

            text = string.IsNullOrEmpty(text)
                ? label
                : $"{text}|{label}";
        }

        static float GetMaxWeaponRange(
            Entity owner,
            NativeArray<CombatWeaponOwner> weaponOwners,
            NativeArray<CombatWeapon> weapons)
        {
            float range = 0f;
            for (int i = 0; i < weapons.Length; i++)
            {
                if (weaponOwners[i].Owner != owner)
                    continue;

                range = math.max(range, math.max(0f, weapons[i].Range));
            }

            return range;
        }

        void UpdateObstacleDebugVisuals()
        {
            var entities = _obstacleQuery.ToEntityArray(Allocator.Temp);
            var transforms = _obstacleQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var footprints = _obstacleQuery.ToComponentDataArray<ObstacleFootprint>(Allocator.Temp);

            _debugObstacleActiveBuffer.Clear();

            for (int i = 0; i < entities.Length; i++)
            {
                _debugObstacleActiveBuffer.Add(entities[i]);
                if (ShowObstacleDebugBoxes)
                    UpdateDebugObstacleBox(entities[i], transforms[i], footprints[i]);
                else
                    RemoveDebugObstacleBox(entities[i]);

                if (ShowObstacleDebugRadius)
                {
                    float radius = GetEffectiveObstacleRadius(footprints[i]) + math.max(0f, footprints[i].ExtraPadding);
                    UpdateDebugObstacleRadius(entities[i], transforms[i].Position, radius);
                }
                else
                {
                    RemoveDebugObstacleRadius(entities[i]);
                }
            }

            footprints.Dispose();
            transforms.Dispose();
            entities.Dispose();
        }

        void UpdateDebugObstacleBox(Entity entity, LocalTransform transformData, ObstacleFootprint footprint)
        {
            if (!_debugObstacleBoxObjects.TryGetValue(entity, out var boxObject))
            {
                boxObject = CreateLineObject("Obstacle Box Debug", true);
                _debugObstacleBoxObjects.Add(entity, boxObject);
            }

            var line = boxObject.GetComponent<LineRenderer>();
            line.positionCount = 4;
            line.loop = true;
            line.startColor = DebugObstacleBoxColor;
            line.endColor = DebugObstacleBoxColor;
            line.startWidth = DebugLineWidth;
            line.endWidth = DebugLineWidth;

            float padding = math.max(0f, footprint.ExtraPadding);
            float2 halfSize = math.max(new float2(0.01f), footprint.Size * 0.5f + padding);
            float3 center = transformData.Position;
            center.y += RingHeightOffset * 1.35f;

            line.SetPosition(0, ToObstacleBoxCorner(center, transformData.Rotation, -halfSize.x, -halfSize.y));
            line.SetPosition(1, ToObstacleBoxCorner(center, transformData.Rotation, halfSize.x, -halfSize.y));
            line.SetPosition(2, ToObstacleBoxCorner(center, transformData.Rotation, halfSize.x, halfSize.y));
            line.SetPosition(3, ToObstacleBoxCorner(center, transformData.Rotation, -halfSize.x, halfSize.y));
        }

        static Vector3 ToObstacleBoxCorner(float3 center, quaternion rotation, float x, float z)
        {
            float3 corner = center + math.rotate(rotation, new float3(x, 0f, z));
            return new Vector3(corner.x, corner.y, corner.z);
        }

        void UpdateDebugObstacleRadius(Entity entity, float3 position, float radius)
        {
            if (!_debugObstacleRadiusObjects.TryGetValue(entity, out var radiusObject))
            {
                radiusObject = CreateLineObject("Obstacle Radius Debug", true);
                _debugObstacleRadiusObjects.Add(entity, radiusObject);
            }

            var line = radiusObject.GetComponent<LineRenderer>();
            int segments = math.max(16, DebugCircleSegments);
            line.positionCount = segments;
            line.loop = true;
            line.startColor = DebugObstacleRadiusColor;
            line.endColor = DebugObstacleRadiusColor;
            line.startWidth = DebugLineWidth;
            line.endWidth = DebugLineWidth;

            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * math.PI * 2f;
                line.SetPosition(i, new Vector3(
                    position.x + math.cos(angle) * radius,
                    position.y + RingHeightOffset * 1.25f,
                    position.z + math.sin(angle) * radius));
            }
        }

        GameObject CreateLineObject(string objectName, bool loop)
        {
            var lineObject = new GameObject(objectName);
            lineObject.transform.SetParent(transform, false);

            var line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = _debugLineMaterial;
            line.useWorldSpace = true;
            line.loop = loop;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.textureMode = LineTextureMode.Stretch;

            return lineObject;
        }

        void RemoveDebugObstacleBox(Entity entity)
        {
            if (_debugObstacleBoxObjects.TryGetValue(entity, out var boxObject))
                Destroy(boxObject);

            _debugObstacleBoxObjects.Remove(entity);
        }

        void RemoveDebugObstacleRadius(Entity entity)
        {
            if (_debugObstacleRadiusObjects.TryGetValue(entity, out var radiusObject))
                Destroy(radiusObject);

            _debugObstacleRadiusObjects.Remove(entity);
        }

        void RemoveInactiveDebugObjects()
        {
            _removeBuffer.Clear();
            foreach (var pair in _debugRadiusObjects)
            {
                if (!_debugActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
            {
                if (_debugRadiusObjects.TryGetValue(_removeBuffer[i], out var radiusObject))
                    Destroy(radiusObject);

                _debugRadiusObjects.Remove(_removeBuffer[i]);
            }

            _removeBuffer.Clear();
            foreach (var pair in _debugTargetLineObjects)
            {
                if (!_debugActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugTargetLine(_removeBuffer[i]);

            _removeBuffer.Clear();
            foreach (var pair in _debugPathLineObjects)
            {
                if (!_debugActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugPathLine(_removeBuffer[i]);

            _removeBuffer.Clear();
            foreach (var pair in _debugStatusLabelObjects)
            {
                if (!_debugActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
            {
                if (_debugStatusLabelObjects.TryGetValue(_removeBuffer[i], out var labelObject))
                    Destroy(labelObject);

                _debugStatusLabelObjects.Remove(_removeBuffer[i]);
            }

            _removeBuffer.Clear();
            foreach (var pair in _debugObstacleBoxObjects)
            {
                if (!_debugObstacleActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugObstacleBox(_removeBuffer[i]);

            _removeBuffer.Clear();
            foreach (var pair in _debugObstacleRadiusObjects)
            {
                if (!_debugObstacleActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugObstacleRadius(_removeBuffer[i]);
        }

        void RemoveInactiveNameObjects()
        {
            _removeBuffer.Clear();
            foreach (var pair in _debugNameLabelObjects)
            {
                if (!_debugNameActiveBuffer.Contains(pair.Key) ||
                    !_entityManager.Exists(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugNameLabel(_removeBuffer[i]);
        }

        void RemoveInactiveCombatDebugObjects()
        {
            _removeBuffer.Clear();
            foreach (var pair in _debugCombatRangeObjects)
            {
                if (!_debugCombatActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugCombatRange(_removeBuffer[i]);

            _removeBuffer.Clear();
            foreach (var pair in _debugCombatAttackLineObjects)
            {
                if (!_debugCombatActiveBuffer.Contains(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugCombatAttackLine(_removeBuffer[i]);

            _removeBuffer.Clear();
            foreach (var pair in _debugCombatHealthLabelObjects)
            {
                if (!_debugCombatHealthActiveBuffer.Contains(pair.Key) ||
                    !_entityManager.Exists(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugCombatHealthLabel(_removeBuffer[i]);

            _removeBuffer.Clear();
            foreach (var pair in _debugCombatWeaponStateLabelObjects)
            {
                if (!_debugCombatActiveBuffer.Contains(pair.Key) ||
                    !_entityManager.Exists(pair.Key))
                    _removeBuffer.Add(pair.Key);
            }

            for (int i = 0; i < _removeBuffer.Count; i++)
                RemoveDebugCombatWeaponStateLabel(_removeBuffer[i]);
        }

        void RemoveDebugTargetLine(Entity entity)
        {
            if (_debugTargetLineObjects.TryGetValue(entity, out var lineObject))
                Destroy(lineObject);

            _debugTargetLineObjects.Remove(entity);
        }

        void RemoveDebugPathLine(Entity entity)
        {
            if (_debugPathLineObjects.TryGetValue(entity, out var lineObject))
                Destroy(lineObject);

            _debugPathLineObjects.Remove(entity);
        }

        void RemoveDebugNameLabel(Entity entity)
        {
            if (_debugNameLabelObjects.TryGetValue(entity, out var labelObject))
                Destroy(labelObject);

            _debugNameLabelObjects.Remove(entity);
        }

        void RemoveDebugCombatRange(Entity entity)
        {
            if (_debugCombatRangeObjects.TryGetValue(entity, out var rangeObject))
                Destroy(rangeObject);

            _debugCombatRangeObjects.Remove(entity);
        }

        void RemoveDebugCombatAttackLine(Entity entity)
        {
            if (_debugCombatAttackLineObjects.TryGetValue(entity, out var lineObject))
                Destroy(lineObject);

            _debugCombatAttackLineObjects.Remove(entity);
        }

        void RemoveDebugCombatHealthLabel(Entity entity)
        {
            if (_debugCombatHealthLabelObjects.TryGetValue(entity, out var labelObject))
                Destroy(labelObject);

            _debugCombatHealthLabelObjects.Remove(entity);
        }

        void RemoveDebugCombatWeaponStateLabel(Entity entity)
        {
            if (_debugCombatWeaponStateLabelObjects.TryGetValue(entity, out var labelObject))
                Destroy(labelObject);

            _debugCombatWeaponStateLabelObjects.Remove(entity);
        }

        GameObject CreateRingObject()
        {
            var ring = new GameObject("Selection Ring");
            ring.transform.SetParent(transform, false);

            var filter = ring.AddComponent<MeshFilter>();
            filter.sharedMesh = _ringMesh;

            var renderer = ring.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _runtimeRingMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return ring;
        }

        void EnsureRingAssets()
        {
            if (_ringMesh == null)
                _ringMesh = BuildRingMesh(math.max(16, RingSegments), math.clamp(RingWidthRatio, 0.03f, 0.5f));

            if (_cornerRectangleMesh == null)
                _cornerRectangleMesh = BuildCornerRectangleMesh(
                    math.clamp(CornerLengthRatio, 0.05f, 0.5f),
                    math.clamp(CornerWidthRatio, 0.01f, 0.2f));

            if (_runtimeRingMaterial != null)
                return;

            if (RingMaterial != null)
            {
                _runtimeRingMaterial = new Material(RingMaterial);
                _runtimeRingMaterial.name = "Runtime Selection Ring";
                ApplyRingMaterialSettings(_runtimeRingMaterial, RingColor);
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");

            _runtimeRingMaterial = new Material(shader);
            _runtimeRingMaterial.name = "Runtime Selection Ring";
            ApplyRingMaterialSettings(_runtimeRingMaterial, RingColor);
        }

        void EnsureDebugLineMaterial()
        {
            if (_debugLineMaterial != null)
                return;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");

            _debugLineMaterial = new Material(shader);
            _debugLineMaterial.name = "Runtime Unit Movement Debug";
        }

        static Mesh BuildRingMesh(int segments, float widthRatio)
        {
            float inner = math.max(0.01f, 1f - widthRatio);
            var vertices = new Vector3[(segments + 1) * 2];
            var triangles = new int[segments * 12];

            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = t * math.PI * 2f;
                float x = math.cos(angle);
                float z = math.sin(angle);

                vertices[i * 2] = new Vector3(x, 0f, z);
                vertices[i * 2 + 1] = new Vector3(x * inner, 0f, z * inner);
            }

            for (int i = 0; i < segments; i++)
            {
                int v = i * 2;
                int tri = i * 12;

                triangles[tri] = v;
                triangles[tri + 1] = v + 1;
                triangles[tri + 2] = v + 2;
                triangles[tri + 3] = v + 1;
                triangles[tri + 4] = v + 3;
                triangles[tri + 5] = v + 2;

                triangles[tri + 6] = v;
                triangles[tri + 7] = v + 2;
                triangles[tri + 8] = v + 1;
                triangles[tri + 9] = v + 1;
                triangles[tri + 10] = v + 2;
                triangles[tri + 11] = v + 3;
            }

            var mesh = new Mesh
            {
                name = "Selection Ring Mesh",
                vertices = vertices,
                triangles = triangles,
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static Mesh BuildCornerRectangleMesh(float lengthRatio, float widthRatio)
        {
            var vertices = new List<Vector3>(64);
            var triangles = new List<int>(96);

            float half = 0.5f;
            float length = math.min(lengthRatio, 0.5f);
            float width = math.min(widthRatio, length * 0.5f);

            AddCorner(vertices, triangles, -half, half, length, width, true, true);
            AddCorner(vertices, triangles, half, half, length, width, false, true);
            AddCorner(vertices, triangles, -half, -half, length, width, true, false);
            AddCorner(vertices, triangles, half, -half, length, width, false, false);

            var mesh = new Mesh
            {
                name = "Selection Corner Rectangle Mesh",
                vertices = vertices.ToArray(),
                triangles = triangles.ToArray(),
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            return mesh;
        }

        static void AddCorner(
            List<Vector3> vertices,
            List<int> triangles,
            float cornerX,
            float cornerZ,
            float length,
            float width,
            bool left,
            bool top)
        {
            float x0 = left ? cornerX : cornerX - length;
            float x1 = left ? cornerX + length : cornerX;
            float zOuter0 = top ? cornerZ - width : cornerZ;
            float zOuter1 = top ? cornerZ : cornerZ + width;
            AddQuad(vertices, triangles, x0, zOuter0, x1, zOuter1);

            float xInner0 = left ? cornerX : cornerX - width;
            float xInner1 = left ? cornerX + width : cornerX;
            float z0 = top ? cornerZ - length : cornerZ;
            float z1 = top ? cornerZ : cornerZ + length;
            AddQuad(vertices, triangles, xInner0, z0, xInner1, z1);
        }

        static void AddQuad(List<Vector3> vertices, List<int> triangles, float x0, float z0, float x1, float z1)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(x0, 0f, z0));
            vertices.Add(new Vector3(x0, 0f, z1));
            vertices.Add(new Vector3(x1, 0f, z1));
            vertices.Add(new Vector3(x1, 0f, z0));

            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);

            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 1);
            triangles.Add(start);
            triangles.Add(start + 3);
            triangles.Add(start + 2);
        }

        static void ApplyRingMaterialSettings(Material material, Color color)
        {
            material.color = color;

            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_Cull"))
                material.SetFloat("_Cull", 0f);
        }

        void ClearRingObjects()
        {
            foreach (var pair in _rings)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            _rings.Clear();
        }

        void ClearMovementDebugObjects()
        {
            foreach (var pair in _debugRadiusObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugTargetLineObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugPathLineObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugStatusLabelObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugObstacleBoxObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugObstacleRadiusObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            _debugRadiusObjects.Clear();
            _debugTargetLineObjects.Clear();
            _debugPathLineObjects.Clear();
            _debugStatusLabelObjects.Clear();
            _debugObstacleBoxObjects.Clear();
            _debugObstacleRadiusObjects.Clear();
            _debugActiveBuffer.Clear();
            _debugObstacleActiveBuffer.Clear();
        }

        void ClearUnitNameObjects()
        {
            foreach (var pair in _debugNameLabelObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            _debugNameLabelObjects.Clear();
            _debugNameActiveBuffer.Clear();
        }

        void ClearCombatDebugObjects()
        {
            foreach (var pair in _debugCombatRangeObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugCombatAttackLineObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugCombatHealthLabelObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            foreach (var pair in _debugCombatWeaponStateLabelObjects)
            {
                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            _debugCombatRangeObjects.Clear();
            _debugCombatAttackLineObjects.Clear();
            _debugCombatHealthLabelObjects.Clear();
            _debugCombatWeaponStateLabelObjects.Clear();
            _debugCombatActiveBuffer.Clear();
            _debugCombatHealthActiveBuffer.Clear();
        }

        static bool IsAdditiveSelectionPressed()
        {
            var keyboard = Keyboard.current;
            return keyboard != null &&
                   (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        }

        static Rect BuildScreenRect(Vector2 a, Vector2 b)
        {
            float xMin = math.min(a.x, b.x);
            float yMin = math.min(a.y, b.y);
            float xMax = math.max(a.x, b.x);
            float yMax = math.max(a.y, b.y);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        static Rect BuildGuiRect(Vector2 a, Vector2 b)
        {
            float yA = Screen.height - a.y;
            float yB = Screen.height - b.y;
            float xMin = math.min(a.x, b.x);
            float yMin = math.min(yA, yB);
            float xMax = math.max(a.x, b.x);
            float yMax = math.max(yA, yB);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        static Vector3 ToVector3(float3 value)
            => new Vector3(value.x, value.y, value.z);

        static float3 ToFloat3(Vector3 value)
            => new float3(value.x, value.y, value.z);

        static Quaternion ToQuaternion(quaternion value)
            => new Quaternion(value.value.x, value.value.y, value.value.z, value.value.w);
    }
}
