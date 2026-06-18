using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Game.Unit
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitAvoidanceOffsetApplySystem))]
    public partial struct AttackOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<AttackOrderRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var attackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(false);
            var commandStates = SystemAPI.GetComponentLookup<UnitCommandState>(false);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<AttackOrderRequest>>()
                         .WithEntityAccess())
            {
                Entity attacker = request.ValueRO.Attacker;
                if (SystemAPI.Exists(attacker) && attackTargets.HasComponent(attacker))
                {
                    attackTargets[attacker] = new CombatAttackTarget
                    {
                        Target = request.ValueRO.Target,
                        ApproachRefreshTime = 0f,
                        HasTarget = request.ValueRO.Target == Entity.Null ? (byte)0 : (byte)1,
                    };

                    UnitCommandKind commandKind = request.ValueRO.CommandKind == UnitCommandKind.None
                        ? UnitCommandKind.ForceAttack
                        : request.ValueRO.CommandKind;
                    var commandState = new UnitCommandState
                    {
                        Kind = commandKind,
                        TargetEntity = request.ValueRO.Target,
                        TargetPosition = float3.zero,
                        HasTargetEntity = request.ValueRO.Target == Entity.Null ? (byte)0 : (byte)1,
                    };

                    if (commandStates.HasComponent(attacker))
                        commandStates[attacker] = commandState;
                    else
                        ecb.AddComponent(attacker, commandState);
                }

                ecb.DestroyEntity(requestEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AttackOrderSystem))]
    public partial struct UnitIdleAutoEngageSystem : ISystem
    {
        const double AutoEngageInterval = 0.05;
        const int AutoEngagePhaseCount = 4;
        double _nextAutoEngageTime;
        int _autoEngagePhase;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatTargetable>();
            state.RequireForUpdate<CombatHealth>();
            state.RequireForUpdate<CombatAttackTarget>();
            _nextAutoEngageTime = 0;
            _autoEngagePhase = 0;
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            double elapsedTime = SystemAPI.Time.ElapsedTime;
            if (elapsedTime < _nextAutoEngageTime)
                return;

            _nextAutoEngageTime = elapsedTime + AutoEngageInterval;
            int scanPhase = _autoEngagePhase;
            _autoEngagePhase = (_autoEngagePhase + 1) % AutoEngagePhaseCount;

            var targetQuery = SystemAPI.QueryBuilder()
                .WithAll<CombatTargetable, CombatHealth, TeamInfoData, LocalTransform>()
                .Build();
            int targetCount = targetQuery.CalculateEntityCount();
            if (targetCount == 0)
                return;

            var targetEntities = targetQuery.ToEntityArray(Allocator.TempJob);
            var targetables = targetQuery.ToComponentDataArray<CombatTargetable>(Allocator.TempJob);
            var targetHealth = targetQuery.ToComponentDataArray<CombatHealth>(Allocator.TempJob);
            var targetTeams = targetQuery.ToComponentDataArray<TeamInfoData>(Allocator.TempJob);
            var targetTransforms = targetQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob);
            var weaponQuery = SystemAPI.QueryBuilder()
                .WithAll<CombatWeaponOwner, CombatWeapon, CombatWeaponEnabled>()
                .Build();
            var primaryWeapons = SystemAPI.GetComponentLookup<PrimaryEngagementWeapon>(true);
            int weaponCount = weaponQuery.CalculateEntityCount();
            var weaponEntities = weaponCount > 0
                ? weaponQuery.ToEntityArray(Allocator.TempJob)
                : default;
            var weaponOwners = weaponCount > 0
                ? weaponQuery.ToComponentDataArray<CombatWeaponOwner>(Allocator.TempJob)
                : default;
            var weapons = weaponCount > 0
                ? weaponQuery.ToComponentDataArray<CombatWeapon>(Allocator.TempJob)
                : default;
            NativeParallelHashMap<Entity, CombatWeaponRangeSummary> weaponRanges =
                CombatWeaponRangeUtility.BuildRangeSummaries(
                    weaponEntities,
                    weaponOwners,
                    weapons,
                    primaryWeapons,
                    Allocator.TempJob);
            var targetSpatialHash = CombatAutoEngageUtility.BuildTargetSpatialHash(
                targetTransforms,
                targetHealth,
                CombatAutoEngageUtility.TargetSpatialCellSize,
                Allocator.TempJob);

            var job = new UnitIdleAutoEngageJob
            {
                TargetEntities = targetEntities,
                Targetables = targetables,
                TargetHealth = targetHealth,
                TargetTeams = targetTeams,
                TargetTransforms = targetTransforms,
                WeaponRanges = weaponRanges,
                TargetSpatialHash = targetSpatialHash,
                CellSize = CombatAutoEngageUtility.TargetSpatialCellSize,
                ScanPhase = scanPhase,
                ScanPhaseCount = AutoEngagePhaseCount,
            };
            JobHandle autoEngageHandle = job.ScheduleParallel(state.Dependency);
            autoEngageHandle = targetTransforms.Dispose(autoEngageHandle);
            autoEngageHandle = targetTeams.Dispose(autoEngageHandle);
            autoEngageHandle = targetHealth.Dispose(autoEngageHandle);
            autoEngageHandle = targetables.Dispose(autoEngageHandle);
            autoEngageHandle = targetEntities.Dispose(autoEngageHandle);
            autoEngageHandle = targetSpatialHash.Dispose(autoEngageHandle);
            autoEngageHandle = weaponRanges.Dispose(autoEngageHandle);
            if (weaponCount > 0)
            {
                autoEngageHandle = weapons.Dispose(autoEngageHandle);
                autoEngageHandle = weaponOwners.Dispose(autoEngageHandle);
                autoEngageHandle = weaponEntities.Dispose(autoEngageHandle);
            }

            state.Dependency = autoEngageHandle;
        }

    }

    static class CombatAutoEngageUtility
    {
        public const float TargetSpatialCellSize = 8f;

        public static bool CanAutoEngage(
            UnitCommandState commandState,
            CombatAttackTarget attackTarget,
            UnitMoveTarget moveTarget)
        {
            if (attackTarget.HasTarget != 0)
                return false;

            if (commandState.Kind == UnitCommandKind.AttackMove)
                return true;

            if (commandState.Kind == UnitCommandKind.ForceMove)
                return true;

            if (moveTarget.HasTarget != 0)
                return false;

            return commandState.Kind == UnitCommandKind.None ||
                   commandState.Kind == UnitCommandKind.IdleAutoEngage;
        }

        public static UnitCommandKind ResolveAutoEngageCommandKind(UnitCommandKind currentKind)
        {
            if (currentKind == UnitCommandKind.AttackMove ||
                currentKind == UnitCommandKind.ForceMove)
                return currentKind;

            return UnitCommandKind.IdleAutoEngage;
        }

        public static bool IsValidAutoTarget(
            TeamInfoData attackerTeam,
            TeamInfoData targetTeam,
            CombatHealth targetHealth)
        {
            return targetHealth.Health > 0f &&
                   attackerTeam.IsEnemy(targetTeam.teamMask);
        }

        public static NativeParallelMultiHashMap<int2, int> BuildTargetSpatialHash(
            NativeArray<LocalTransform> targetTransforms,
            NativeArray<CombatHealth> targetHealth,
            float cellSize,
            Allocator allocator)
        {
            var spatialHash = new NativeParallelMultiHashMap<int2, int>(
                math.max(1, targetTransforms.Length),
                allocator);

            for (int i = 0; i < targetTransforms.Length; i++)
            {
                if (targetHealth[i].Health <= 0f)
                    continue;

                spatialHash.Add(CellOf(targetTransforms[i].Position, cellSize), i);
            }

            return spatialHash;
        }

        public static int CellRadius(float range, float cellSize)
        {
            return math.max(1, (int)math.ceil(math.max(0.01f, range) / math.max(0.01f, cellSize)));
        }

        public static int2 CellOf(float3 position, float cellSize)
        {
            float inverseCellSize = 1f / math.max(0.01f, cellSize);
            return new int2(
                (int)math.floor(position.x * inverseCellSize),
                (int)math.floor(position.z * inverseCellSize));
        }

    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct UnitIdleAutoEngageJob : IJobEntity
    {
        [ReadOnly] public NativeArray<Entity> TargetEntities;
        [ReadOnly] public NativeArray<CombatTargetable> Targetables;
        [ReadOnly] public NativeArray<CombatHealth> TargetHealth;
        [ReadOnly] public NativeArray<TeamInfoData> TargetTeams;
        [ReadOnly] public NativeArray<LocalTransform> TargetTransforms;
        [ReadOnly] public NativeParallelHashMap<Entity, CombatWeaponRangeSummary> WeaponRanges;
        [ReadOnly] public NativeParallelMultiHashMap<int2, int> TargetSpatialHash;
        public float CellSize;
        public int ScanPhase;
        public int ScanPhaseCount;

        public void Execute(
            [EntityIndexInQuery] int entityIndexInQuery,
            Entity entity,
            in TeamInfoData attackerTeam,
            ref CombatAttackTarget attackTarget,
            ref UnitCommandState commandState,
            in UnitMoveTarget moveTarget,
            in LocalTransform transform)
        {
            if (ScanPhaseCount > 1 &&
                entityIndexInQuery % ScanPhaseCount != ScanPhase)
                return;

            if (!CombatAutoEngageUtility.CanAutoEngage(commandState, attackTarget, moveTarget))
                return;

            Entity bestTarget = Entity.Null;
            float bestDistanceSq = float.MaxValue;
            float maxSearchRange = CombatWeaponRangeUtility.GetMaxRange(WeaponRanges, entity);
            if (maxSearchRange <= 0f)
                return;

            float maxSearchRangeSq = maxSearchRange * maxSearchRange;
            int2 originCell = CombatAutoEngageUtility.CellOf(transform.Position, CellSize);
            int cellRadius = CombatAutoEngageUtility.CellRadius(maxSearchRange, CellSize);

            for (int z = -cellRadius; z <= cellRadius; z++)
            {
                for (int x = -cellRadius; x <= cellRadius; x++)
                {
                    int2 cell = originCell + new int2(x, z);
                    if (!TargetSpatialHash.TryGetFirstValue(cell, out int targetIndex, out var iterator))
                        continue;

                    do
                    {
                        if (TargetEntities[targetIndex] == entity ||
                            !CombatAutoEngageUtility.IsValidAutoTarget(attackerTeam, TargetTeams[targetIndex], TargetHealth[targetIndex]))
                            continue;

                        float distanceSq = CombatWeaponUtility.HorizontalDistanceSq(
                            transform.Position,
                            TargetTransforms[targetIndex].Position);
                        if (distanceSq > maxSearchRangeSq ||
                            distanceSq >= bestDistanceSq)
                            continue;

                        float range = CombatWeaponRangeUtility.GetAutoEngageRange(
                            WeaponRanges,
                            entity,
                            Targetables[targetIndex].TargetType);
                        if (range <= 0f)
                            continue;

                        if (distanceSq > range * range || distanceSq >= bestDistanceSq)
                            continue;

                        bestTarget = TargetEntities[targetIndex];
                        bestDistanceSq = distanceSq;
                    }
                    while (TargetSpatialHash.TryGetNextValue(out targetIndex, ref iterator));
                }
            }

            if (bestTarget == Entity.Null)
                return;

            UnitCommandKind nextCommandKind = CombatAutoEngageUtility.ResolveAutoEngageCommandKind(commandState.Kind);

            attackTarget.Target = bestTarget;
            attackTarget.ApproachRefreshTime = 0f;
            attackTarget.HasTarget = 1;

            commandState.Kind = nextCommandKind;
            if (nextCommandKind == UnitCommandKind.ForceMove)
            {
                commandState.TargetEntity = Entity.Null;
                commandState.HasTargetEntity = 0;
            }
            else
            {
                commandState.TargetEntity = bestTarget;
                commandState.HasTargetEntity = 1;
            }

            if (nextCommandKind == UnitCommandKind.IdleAutoEngage)
                commandState.TargetPosition = float3.zero;
        }
    }

    struct CombatWeaponRangeSummary
    {
        public float GroundMax;
        public float GroundMin;
        public float GroundPrimaryMax;
        public float GroundPrimaryMin;
        public float AirMax;
        public float AirMin;
        public float AirPrimaryMax;
        public float AirPrimaryMin;
        public float BuildingMax;
        public float BuildingMin;
        public float BuildingPrimaryMax;
        public float BuildingPrimaryMin;
        public float NavalMax;
        public float NavalMin;
        public float NavalPrimaryMax;
        public float NavalPrimaryMin;
        public float ResourceMax;
        public float ResourceMin;
        public float ResourcePrimaryMax;
        public float ResourcePrimaryMin;
    }

    static class CombatWeaponRangeUtility
    {
        public static NativeParallelHashMap<Entity, CombatWeaponRangeSummary> BuildRangeSummaries(
            NativeArray<Entity> weaponEntities,
            NativeArray<CombatWeaponOwner> weaponOwners,
            NativeArray<CombatWeapon> weapons,
            ComponentLookup<PrimaryEngagementWeapon> primaryWeapons,
            Allocator allocator)
        {
            var summaries = new NativeParallelHashMap<Entity, CombatWeaponRangeSummary>(
                math.max(1, weapons.Length),
                allocator);

            for (int i = 0; i < weapons.Length; i++)
            {
                CombatWeapon weapon = weapons[i];
                if (weaponOwners[i].Owner == Entity.Null ||
                    weapon.Damage <= 0f ||
                    weapon.Range <= 0f ||
                    weapon.TargetMask == CombatTargetMask.None)
                    continue;

                Entity owner = weaponOwners[i].Owner;
                summaries.TryGetValue(owner, out CombatWeaponRangeSummary summary);
                AddWeaponRange(ref summary, weapon, primaryWeapons.HasComponent(weaponEntities[i]));
                if (summaries.ContainsKey(owner))
                    summaries[owner] = summary;
                else
                    summaries.Add(owner, summary);
            }

            return summaries;
        }

        public static float GetAutoEngageRange(
            NativeParallelHashMap<Entity, CombatWeaponRangeSummary> summaries,
            Entity owner,
            CombatTargetMask targetType)
        {
            if (!summaries.TryGetValue(owner, out CombatWeaponRangeSummary summary))
                return 0f;

            float primaryRange = SelectRange(summary, targetType, true, false);
            return primaryRange > 0f
                ? primaryRange
                : SelectRange(summary, targetType, false, false);
        }

        public static float GetMaxRange(
            NativeParallelHashMap<Entity, CombatWeaponRangeSummary> summaries,
            Entity owner)
        {
            if (!summaries.TryGetValue(owner, out CombatWeaponRangeSummary summary))
                return 0f;

            return math.max(
                math.max(summary.GroundMax, summary.AirMax),
                math.max(
                    math.max(summary.BuildingMax, summary.NavalMax),
                    summary.ResourceMax));
        }

        public static bool TryGetPreferredRange(
            NativeParallelHashMap<Entity, CombatWeaponRangeSummary> summaries,
            Entity owner,
            CombatTargetMask targetType,
            UnitCommandKind commandKind,
            out float preferredRange)
        {
            preferredRange = 0f;
            if (!summaries.TryGetValue(owner, out CombatWeaponRangeSummary summary))
                return false;

            float primaryRange = SelectRange(summary, targetType, true, false);
            float fallbackRange = SelectRange(summary, targetType, false, false);
            float selectedRange = primaryRange > 0f ? primaryRange : fallbackRange;
            if (selectedRange <= 0f)
                return false;

            preferredRange = math.max(0.01f, selectedRange);
            return true;
        }

        static void AddWeaponRange(ref CombatWeaponRangeSummary summary, CombatWeapon weapon, bool isPrimary)
        {
            if ((weapon.TargetMask & CombatTargetMask.Ground) != 0)
                AddRange(ref summary.GroundMax, ref summary.GroundMin, ref summary.GroundPrimaryMax, ref summary.GroundPrimaryMin, weapon.Range, isPrimary);
            if ((weapon.TargetMask & CombatTargetMask.Air) != 0)
                AddRange(ref summary.AirMax, ref summary.AirMin, ref summary.AirPrimaryMax, ref summary.AirPrimaryMin, weapon.Range, isPrimary);
            if ((weapon.TargetMask & CombatTargetMask.Building) != 0)
                AddRange(ref summary.BuildingMax, ref summary.BuildingMin, ref summary.BuildingPrimaryMax, ref summary.BuildingPrimaryMin, weapon.Range, isPrimary);
            if ((weapon.TargetMask & CombatTargetMask.Naval) != 0)
                AddRange(ref summary.NavalMax, ref summary.NavalMin, ref summary.NavalPrimaryMax, ref summary.NavalPrimaryMin, weapon.Range, isPrimary);
            if ((weapon.TargetMask & CombatTargetMask.Resource) != 0)
                AddRange(ref summary.ResourceMax, ref summary.ResourceMin, ref summary.ResourcePrimaryMax, ref summary.ResourcePrimaryMin, weapon.Range, isPrimary);
        }

        static void AddRange(
            ref float maxRange,
            ref float minRange,
            ref float primaryMaxRange,
            ref float primaryMinRange,
            float range,
            bool isPrimary)
        {
            maxRange = math.max(maxRange, range);
            minRange = minRange <= 0f ? range : math.min(minRange, range);
            if (!isPrimary)
                return;

            primaryMaxRange = math.max(primaryMaxRange, range);
            primaryMinRange = primaryMinRange <= 0f ? range : math.min(primaryMinRange, range);
        }

        static float SelectRange(
            CombatWeaponRangeSummary summary,
            CombatTargetMask targetType,
            bool primaryOnly,
            bool shortest)
        {
            float selected = 0f;
            if ((targetType & CombatTargetMask.Ground) != 0)
                selected = SelectRange(selected, primaryOnly ? summary.GroundPrimaryMax : summary.GroundMax, primaryOnly ? summary.GroundPrimaryMin : summary.GroundMin, shortest);
            if ((targetType & CombatTargetMask.Air) != 0)
                selected = SelectRange(selected, primaryOnly ? summary.AirPrimaryMax : summary.AirMax, primaryOnly ? summary.AirPrimaryMin : summary.AirMin, shortest);
            if ((targetType & CombatTargetMask.Building) != 0)
                selected = SelectRange(selected, primaryOnly ? summary.BuildingPrimaryMax : summary.BuildingMax, primaryOnly ? summary.BuildingPrimaryMin : summary.BuildingMin, shortest);
            if ((targetType & CombatTargetMask.Naval) != 0)
                selected = SelectRange(selected, primaryOnly ? summary.NavalPrimaryMax : summary.NavalMax, primaryOnly ? summary.NavalPrimaryMin : summary.NavalMin, shortest);
            if ((targetType & CombatTargetMask.Resource) != 0)
                selected = SelectRange(selected, primaryOnly ? summary.ResourcePrimaryMax : summary.ResourceMax, primaryOnly ? summary.ResourcePrimaryMin : summary.ResourceMin, shortest);

            return selected;
        }

        static float SelectRange(float current, float maxRange, float minRange, bool shortest)
        {
            float candidate = shortest ? minRange : maxRange;
            if (candidate <= 0f)
                return current;

            if (current <= 0f)
                return candidate;

            return shortest
                ? math.min(current, candidate)
                : math.max(current, candidate);
        }
    }

    static class CombatWeaponUtility
    {
        public const float ApproachRefreshInterval = 0.35f;
        public const float RangeTolerance = 0.15f;

        public static void TickWeaponCooldown(ref CombatWeaponCooldown cooldown, float deltaTime)
        {
            cooldown.Remaining = math.max(0f, cooldown.Remaining - deltaTime);
        }

        public static void ResetWeaponCooldown(ref CombatWeaponCooldown cooldown)
        {
            cooldown.Remaining = math.max(0.01f, cooldown.Duration);
        }

        public static bool IsValidTarget(TeamInfoData attackerTeam, TeamInfoData targetTeam, CombatHealth targetHealth)
        {
            return targetHealth.Health > 0f &&
                   attackerTeam.IsEnemy(targetTeam.teamMask);
        }

        public static float3 ResolveAimPosition(
            Entity entity,
            float3 basePosition,
            ComponentLookup<CombatTargetBounds> boundsLookup)
        {
            if (!boundsLookup.HasComponent(entity))
                return basePosition;

            CombatTargetBounds bounds = boundsLookup[entity];
            float height = math.max(0.01f, bounds.Size.y);
            return basePosition + new float3(0f, height * math.saturate(bounds.AimHeightRatio), 0f);
        }

        public static bool CanWeaponPrepare(
            Entity weaponEntity,
            CombatWeapon weapon,
            CombatTargetMask targetType,
            float distanceSq,
            byte isMoving,
            float3 forward,
            float3 targetDirection,
            ComponentLookup<BodyForwardWeapon> bodyForwardWeapons,
            ComponentLookup<CombatWeaponFireArc> fireArcs,
            ComponentLookup<RequiresStoppedToFire> stoppedRequired)
        {
            if (!IsWeaponUsableForTarget(weapon, targetType))
                return false;

            if (stoppedRequired.HasComponent(weaponEntity) &&
                isMoving != 0)
                return false;

            float range = math.max(0f, weapon.Range);
            if (distanceSq > (range + RangeTolerance) * (range + RangeTolerance))
                return false;

            if (!bodyForwardWeapons.HasComponent(weaponEntity))
                return true;

            if (math.lengthsq(targetDirection) <= 0.0001f)
                return true;

            float fireArcCosine = fireArcs.HasComponent(weaponEntity)
                ? fireArcs[weaponEntity].FireArcCosine
                : -1f;
            return math.dot(math.normalizesafe(forward), targetDirection) >= fireArcCosine;
        }

        public static void UpdateWeaponSetup(
            RequiresWeaponSetup setup,
            bool canPrepare,
            float deltaTime,
            ref CombatWeaponSetupState setupState)
        {
            if (canPrepare)
            {
                setupState.Progress = SetupProgress(setupState.Progress, setup.SetupTime, deltaTime);
                return;
            }

            setupState.Progress = PackDownSetupProgress(setupState.Progress, setup.PackTime, deltaTime);
        }

        public static float SetupProgress(float progress, float setupTime, float deltaTime)
        {
            if (setupTime <= 0f)
                return 1f;

            return math.min(1f, progress + deltaTime / math.max(0.0001f, setupTime));
        }

        public static float PackDownSetupProgress(float progress, float packTime, float deltaTime)
        {
            if (packTime <= 0f)
                return 0f;

            return math.max(0f, progress - deltaTime / math.max(0.0001f, packTime));
        }

        public static bool IsWeaponUsableForTarget(CombatWeapon weapon, CombatTargetMask targetType)
        {
            return weapon.Damage > 0f &&
                   (weapon.TargetMask & targetType) != 0 &&
                   weapon.Range > 0f;
        }

        public static float3 GetHorizontalDirection(float3 from, float3 to)
        {
            float3 direction = to - from;
            direction.y = 0f;
            return math.normalizesafe(direction);
        }

        public static quaternion RotateTowards(quaternion from, quaternion to, float maxRadiansDelta)
        {
            if (maxRadiansDelta <= 0f)
                return to;

            float dot = math.dot(from.value, to.value);
            if (dot < 0f)
            {
                to.value = -to.value;
                dot = -dot;
            }

            dot = math.clamp(dot, -1f, 1f);
            float angle = 2f * math.acos(dot);
            if (angle < 0.0001f)
                return to;

            float t = math.saturate(maxRadiansDelta / angle);
            return math.normalize(math.slerp(from, to, t));
        }

        public static void RefreshApproachOrder(
            EntityCommandBuffer ecb,
            Entity attacker,
            float3 targetPosition,
            float stopDistance,
            ref CombatAttackTarget attackTarget,
            float deltaTime)
        {
            attackTarget.ApproachRefreshTime -= deltaTime;
            if (attackTarget.ApproachRefreshTime > 0f)
                return;

            Entity request = ecb.CreateEntity();
            ecb.AddComponent(request, new MoveOrderRequest
            {
                Unit = attacker,
                Target = targetPosition,
                StopDistance = math.max(0.1f, stopDistance),
                RepathCount = 0,
            });
            attackTarget.ApproachRefreshTime = CombatWeaponUtility.ApproachRefreshInterval;
        }

        public static void ResumeAttackMoveIfNeeded(
            EntityCommandBuffer ecb,
            ComponentLookup<UnitCommandState> commandStates,
            Entity attacker)
        {
            if (!commandStates.HasComponent(attacker))
                return;

            UnitCommandState commandState = commandStates[attacker];
            if (commandState.Kind != UnitCommandKind.AttackMove ||
                commandState.HasTargetEntity == 0)
                return;

            Entity request = ecb.CreateEntity();
            ecb.AddComponent(request, new MoveOrderRequest
            {
                Unit = attacker,
                Target = commandState.TargetPosition,
                StopDistance = 0.25f,
                RepathCount = 0,
                CommandKind = UnitCommandKind.None,
            });

            commandState.TargetEntity = Entity.Null;
            commandState.HasTargetEntity = 0;
            commandStates[attacker] = commandState;
        }

        public static void ClearAttackTarget(ref CombatAttackTarget attackTarget)
        {
            attackTarget.Target = Entity.Null;
            attackTarget.ApproachRefreshTime = 0f;
            attackTarget.HasTarget = 0;
        }

        public static void ClearForceAttackCommand(ComponentLookup<UnitCommandState> commandStates, Entity entity)
        {
            if (!commandStates.HasComponent(entity))
                return;

            UnitCommandState commandState = commandStates[entity];
            if (commandState.Kind != UnitCommandKind.ForceAttack)
                return;

            commandState.Kind = UnitCommandKind.None;
            commandState.TargetEntity = Entity.Null;
            commandState.TargetPosition = float3.zero;
            commandState.HasTargetEntity = 0;
            commandStates[entity] = commandState;
        }

        public static float HorizontalDistanceSq(float3 a, float3 b)
        {
            float3 delta = a - b;
            delta.y = 0f;
            return math.lengthsq(delta);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitIdleAutoEngageSystem))]
    public partial struct CombatAttackTargetValidationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatAttackTarget>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var targetables = SystemAPI.GetComponentLookup<CombatTargetable>(true);
            var healthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true);
            var targetTeams = SystemAPI.GetComponentLookup<TeamInfoData>(true);
            var transforms = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var commandStates = SystemAPI.GetComponentLookup<UnitCommandState>(false);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (attackTarget, attackerTeam, attackerEntity) in
                     SystemAPI.Query<RefRW<CombatAttackTarget>, RefRO<TeamInfoData>>()
                         .WithEntityAccess())
            {
                CombatAttackTarget target = attackTarget.ValueRO;
                if (target.HasTarget == 0)
                    continue;

                bool valid =
                    target.Target != Entity.Null &&
                    targetables.HasComponent(target.Target) &&
                    healthLookup.HasComponent(target.Target) &&
                    targetTeams.HasComponent(target.Target) &&
                    transforms.HasComponent(target.Target) &&
                    CombatWeaponUtility.IsValidTarget(attackerTeam.ValueRO, targetTeams[target.Target], healthLookup[target.Target]);

                if (valid)
                    continue;

                CombatWeaponUtility.ResumeAttackMoveIfNeeded(ecb, commandStates, attackerEntity);
                CombatWeaponUtility.ClearAttackTarget(ref target);
                attackTarget.ValueRW = target;
                CombatWeaponUtility.ClearForceAttackCommand(commandStates, attackerEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatAttackTargetValidationSystem))]
    public partial struct CombatWeaponUnlockGroupSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponUnlockGroupRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var weaponQuery = SystemAPI.QueryBuilder()
                .WithAll<CombatWeaponOwner, CombatWeaponUnlockGroup, CombatWeaponEnabled>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState)
                .Build();
            int weaponCount = weaponQuery.CalculateEntityCount();
            var weaponEntities = weaponCount > 0
                ? weaponQuery.ToEntityArray(Allocator.Temp)
                : default;
            var weaponOwners = weaponCount > 0
                ? weaponQuery.ToComponentDataArray<CombatWeaponOwner>(Allocator.Temp)
                : default;
            var weaponGroups = weaponCount > 0
                ? weaponQuery.ToComponentDataArray<CombatWeaponUnlockGroup>(Allocator.Temp)
                : default;

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<CombatWeaponUnlockGroupRequest>>()
                         .WithEntityAccess())
            {
                Entity owner = request.ValueRO.Owner;
                int groupId = math.max(0, request.ValueRO.GroupId);
                if (owner != Entity.Null)
                {
                    for (int i = 0; i < weaponCount; i++)
                    {
                        if (weaponOwners[i].Owner == owner &&
                            weaponGroups[i].GroupId == groupId)
                            ecb.SetComponentEnabled<CombatWeaponEnabled>(weaponEntities[i], true);
                    }
                }

                ecb.DestroyEntity(requestEntity);
            }

            if (weaponCount > 0)
            {
                weaponGroups.Dispose();
                weaponOwners.Dispose();
                weaponEntities.Dispose();
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponUnlockGroupSystem))]
    [UpdateBefore(typeof(CombatWeaponCooldownSystem))]
    public partial struct CombatWeaponRuntimeInitializeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeapon>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (_, weaponEntity) in
                     SystemAPI.Query<RefRO<CombatWeapon>>()
                         .WithNone<CombatWeaponEnabled>()
                         .WithEntityAccess())
            {
                ecb.AddComponent<CombatWeaponEnabled>(weaponEntity);
                ecb.SetComponentEnabled<CombatWeaponEnabled>(weaponEntity, true);
            }

            foreach (var (_, weaponEntity) in
                     SystemAPI.Query<RefRO<CombatWeapon>>()
                         .WithNone<CombatWeaponSetupState>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(weaponEntity, new CombatWeaponSetupState
                {
                    Progress = 1f,
                });
            }

            foreach (var (_, weaponEntity) in
                     SystemAPI.Query<RefRO<CombatWeapon>>()
                         .WithNone<CombatWeaponReadyState>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(weaponEntity, new CombatWeaponReadyState
                {
                    Target = Entity.Null,
                    BlockedReasons = CombatWeaponBlockReason.NoTarget,
                    CanFire = 0,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponRuntimeInitializeSystem))]
    public partial struct CombatWeaponCooldownSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponCooldown>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            state.Dependency = new CombatWeaponCooldownJob
            {
                DeltaTime = deltaTime,
            }.ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled))]
    partial struct CombatWeaponCooldownJob : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref CombatWeaponCooldown cooldown)
        {
            CombatWeaponUtility.TickWeaponCooldown(ref cooldown, DeltaTime);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponCooldownSystem))]
    public partial struct CombatEngagementDecisionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatEngagementDecision>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var targetables = SystemAPI.GetComponentLookup<CombatTargetable>(true);
            var healthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true);
            var teams = SystemAPI.GetComponentLookup<TeamInfoData>(true);
            var transforms = SystemAPI.GetComponentLookup<LocalTransform>(true);
            bool hasGrid = SystemAPI.TryGetSingleton<UnitNavigationGrid>(out var grid);
            var obstacleQuery = SystemAPI.QueryBuilder()
                .WithAll<ObstacleFootprint, LocalTransform>()
                .Build();
            int obstacleCount = obstacleQuery.CalculateEntityCount();
            var obstacleTransforms = obstacleCount > 0
                ? obstacleQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
                : default;
            var obstacleFootprints = obstacleCount > 0
                ? obstacleQuery.ToComponentDataArray<ObstacleFootprint>(Allocator.TempJob)
                : default;
            bool hasLineOfSightGrid = hasGrid && obstacleCount > 0 && grid.Size.x * grid.Size.y > 0;
            var blocked = hasLineOfSightGrid
                ? new NativeArray<byte>(grid.Size.x * grid.Size.y, Allocator.TempJob)
                : default;
            if (hasLineOfSightGrid)
                UnitPathfinding.BuildBlockedGrid(grid, 0f, obstacleTransforms, obstacleFootprints, blocked);
            if (obstacleCount > 0)
            {
                obstacleFootprints.Dispose();
                obstacleTransforms.Dispose();
            }

            var weaponQuery = SystemAPI.QueryBuilder()
                .WithAll<CombatWeaponOwner, CombatWeapon, CombatWeaponEnabled>()
                .Build();
            var primaryWeapons = SystemAPI.GetComponentLookup<PrimaryEngagementWeapon>(true);
            int weaponCount = weaponQuery.CalculateEntityCount();
            var weaponEntities = weaponCount > 0
                ? weaponQuery.ToEntityArray(Allocator.TempJob)
                : default;
            var weaponOwners = weaponCount > 0
                ? weaponQuery.ToComponentDataArray<CombatWeaponOwner>(Allocator.TempJob)
                : default;
            var weapons = weaponCount > 0
                ? weaponQuery.ToComponentDataArray<CombatWeapon>(Allocator.TempJob)
                : default;
            NativeParallelHashMap<Entity, CombatWeaponRangeSummary> weaponRanges =
                CombatWeaponRangeUtility.BuildRangeSummaries(
                    weaponEntities,
                    weaponOwners,
                    weapons,
                    primaryWeapons,
                    Allocator.TempJob);
            if (weaponCount > 0)
            {
                weapons.Dispose();
                weaponOwners.Dispose();
                weaponEntities.Dispose();
            }

            var decisionJob = new CombatEngagementDecisionJob
            {
                Targetables = targetables,
                HealthLookup = healthLookup,
                Teams = teams,
                Transforms = transforms,
                WeaponRanges = weaponRanges,
                Blocked = blocked,
                Grid = grid,
                HasLineOfSightGrid = hasLineOfSightGrid,
            };

            JobHandle decisionHandle = decisionJob.ScheduleParallel(state.Dependency);
            decisionHandle = weaponRanges.Dispose(decisionHandle);
            if (hasLineOfSightGrid)
                decisionHandle = blocked.Dispose(decisionHandle);

            state.Dependency = decisionHandle;
        }

    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct CombatEngagementDecisionJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<CombatTargetable> Targetables;
        [ReadOnly] public ComponentLookup<CombatHealth> HealthLookup;
        [ReadOnly] public ComponentLookup<TeamInfoData> Teams;
        [ReadOnly] public ComponentLookup<LocalTransform> Transforms;
        [ReadOnly] public NativeParallelHashMap<Entity, CombatWeaponRangeSummary> WeaponRanges;
        [ReadOnly] public NativeArray<byte> Blocked;
        public UnitNavigationGrid Grid;
        public bool HasLineOfSightGrid;

        public void Execute(
            Entity entity,
            in TeamInfoData attackerTeam,
            in CombatAttackTarget attackTarget,
            in UnitCommandState commandState,
            in LocalTransform transform,
            ref CombatEngagementDecision decision,
            ref CombatLineOfSightState lineOfSightState)
        {
            decision = default;
            lineOfSightState = default;

            if (attackTarget.HasTarget == 0 ||
                attackTarget.Target == Entity.Null ||
                !Targetables.HasComponent(attackTarget.Target) ||
                !HealthLookup.HasComponent(attackTarget.Target) ||
                !Teams.HasComponent(attackTarget.Target) ||
                !Transforms.HasComponent(attackTarget.Target) ||
                !CombatWeaponUtility.IsValidTarget(attackerTeam, Teams[attackTarget.Target], HealthLookup[attackTarget.Target]))
                return;

            if (!CombatWeaponRangeUtility.TryGetPreferredRange(
                    WeaponRanges,
                    entity,
                    Targetables[attackTarget.Target].TargetType,
                    commandState.Kind,
                    out float preferredRange))
                return;

            float3 attackerPosition = transform.Position;
            float3 targetPosition = Transforms[attackTarget.Target].Position;
            float distanceSq = CombatWeaponUtility.HorizontalDistanceSq(attackerPosition, targetPosition);
            bool hasLineOfSight = !HasLineOfSightGrid ||
                                  UnitPathfinding.HasGridLineOfSight(Grid, attackerPosition, targetPosition, Blocked, true);
            bool shouldApproach = !hasLineOfSight ||
                                  distanceSq > (preferredRange + CombatWeaponUtility.RangeTolerance) * (preferredRange + CombatWeaponUtility.RangeTolerance);

            lineOfSightState.Target = attackTarget.Target;
            lineOfSightState.HasLineOfSight = hasLineOfSight ? (byte)1 : (byte)0;
            lineOfSightState.HasState = 1;
            decision.PreferredRange = preferredRange;
            decision.TargetPosition = targetPosition;
            decision.ShouldApproach = shouldApproach ? (byte)1 : (byte)0;
            decision.HasUsableWeapon = 1;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatEngagementDecisionSystem))]
    public partial struct CombatMoveIntentSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatMoveIntent>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new CombatMoveIntentJob().ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct CombatMoveIntentJob : IJobEntity
    {
        public void Execute(
            in CombatAttackTarget attackTarget,
            in CombatEngagementDecision decision,
            ref CombatMoveIntent intent)
        {
            intent = default;
            if (attackTarget.HasTarget == 0 ||
                decision.HasUsableWeapon == 0)
                return;

            intent = new CombatMoveIntent
            {
                Kind = decision.ShouldApproach != 0
                    ? CombatMoveIntentKind.ApproachTarget
                    : CombatMoveIntentKind.StopForAttack,
                TargetPosition = decision.TargetPosition,
                StopDistance = decision.PreferredRange,
            };
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatMoveIntentSystem))]
    public partial struct UnitMovementArbiterSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatMoveIntent>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var commandStates = SystemAPI.GetComponentLookup<UnitCommandState>(true);
            var job = new UnitMovementArbiterJob
            {
                CommandStates = commandStates,
                DeltaTime = deltaTime,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct UnitMovementArbiterJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<UnitCommandState> CommandStates;
        public float DeltaTime;

        public void Execute(
            Entity entity,
            in LocalTransform transform,
            ref CombatAttackTarget attackTarget,
            ref UnitMoveTarget moveTarget,
            ref UnitMotionState motion,
            DynamicBuffer<UnitPathWaypoint> waypoints,
            in CombatMoveIntent intent)
        {
            if (attackTarget.HasTarget == 0 ||
                intent.Kind == CombatMoveIntentKind.None)
                return;

            if (CombatMovementCommandUtility.IsForceMoveCommand(CommandStates, entity))
            {
                attackTarget.ApproachRefreshTime = 0f;
                return;
            }

            if (intent.Kind == CombatMoveIntentKind.ApproachTarget)
            {
                attackTarget.ApproachRefreshTime -= DeltaTime;
                if (attackTarget.ApproachRefreshTime <= 0f)
                {
                    float stopDistance = math.max(0.1f, intent.StopDistance);
                    float3 toTarget = intent.TargetPosition - transform.Position;
                    toTarget.y = 0f;
                    float distanceSq = math.lengthsq(toTarget);
                    float inRangeDistance = math.max(0.1f, stopDistance - CombatWeaponUtility.RangeTolerance);
                    if (distanceSq <= inRangeDistance * inRangeDistance)
                    {
                        moveTarget.HasTarget = 0;
                        motion.LastTargetDistance = float.MaxValue;
                        motion.StuckTime = 0f;
                        waypoints.Clear();
                        attackTarget.ApproachRefreshTime = CombatWeaponUtility.ApproachRefreshInterval;
                        return;
                    }

                    float3 approachPosition = intent.TargetPosition;
                    if (distanceSq > 0.0001f)
                    {
                        float currentDistance = math.sqrt(distanceSq);
                        float3 directionToTarget = toTarget * math.rsqrt(distanceSq);
                        float desiredStandOffDistance = math.max(0.1f, stopDistance - CombatWeaponUtility.RangeTolerance * 0.5f);
                        float standOffDistance = math.min(desiredStandOffDistance, math.max(0.1f, currentDistance - 0.25f));
                        approachPosition = intent.TargetPosition - directionToTarget * standOffDistance;
                    }

                    moveTarget = new UnitMoveTarget
                    {
                        Position = approachPosition,
                        StopDistance = math.max(0.1f, math.min(0.5f, stopDistance * 0.1f)),
                        RepathCount = 0,
                        PathStatus = UnitPathStatus.Direct,
                        HasTarget = 1,
                        RepathRequested = 0,
                    };
                    motion.LastTargetDistance = float.MaxValue;
                    motion.StuckTime = 0f;
                    waypoints.Clear();
                    attackTarget.ApproachRefreshTime = CombatWeaponUtility.ApproachRefreshInterval;
                }

                return;
            }

            if (intent.Kind == CombatMoveIntentKind.StopForAttack)
            {
                moveTarget.HasTarget = 0;
                attackTarget.ApproachRefreshTime = 0f;
            }
        }
    }

    static class CombatMovementCommandUtility
    {
        public static bool IsForceMoveCommand(ComponentLookup<UnitCommandState> commandStates, Entity entity)
        {
            return commandStates.HasComponent(entity) &&
                   commandStates[entity].Kind == UnitCommandKind.ForceMove;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementArbiterSystem))]
    public partial struct BodyForwardWeaponAimSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<BodyForwardWeapon>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var job = new BodyForwardWeaponAimJob
            {
                DeltaTime = deltaTime,
                Targetables = SystemAPI.GetComponentLookup<CombatTargetable>(true),
                HealthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true),
                Teams = SystemAPI.GetComponentLookup<TeamInfoData>(true),
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(false),
                AttackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(true),
                Speeds = SystemAPI.GetComponentLookup<UnitMoveSpeed>(true),
                CommandStates = SystemAPI.GetComponentLookup<UnitCommandState>(true),
                MoveTargets = SystemAPI.GetComponentLookup<UnitMoveTarget>(true),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled), typeof(BodyForwardWeapon), typeof(PrimaryEngagementWeapon))]
    partial struct BodyForwardWeaponAimJob : IJobEntity
    {
        public float DeltaTime;
        [ReadOnly] public ComponentLookup<CombatTargetable> Targetables;
        [ReadOnly] public ComponentLookup<CombatHealth> HealthLookup;
        [ReadOnly] public ComponentLookup<TeamInfoData> Teams;
        [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> Transforms;
        [ReadOnly] public ComponentLookup<CombatAttackTarget> AttackTargets;
        [ReadOnly] public ComponentLookup<UnitMoveSpeed> Speeds;
        [ReadOnly] public ComponentLookup<UnitCommandState> CommandStates;
        [ReadOnly] public ComponentLookup<UnitMoveTarget> MoveTargets;

        public void Execute(in CombatWeaponOwner owner, in CombatWeapon weapon)
        {
            Entity ownerEntity = owner.Owner;
            if (ownerEntity == Entity.Null ||
                !AttackTargets.HasComponent(ownerEntity) ||
                !Teams.HasComponent(ownerEntity) ||
                !Transforms.HasComponent(ownerEntity))
                return;

            CombatAttackTarget attackTarget = AttackTargets[ownerEntity];
            if (attackTarget.HasTarget == 0 ||
                attackTarget.Target == Entity.Null ||
                !Targetables.HasComponent(attackTarget.Target) ||
                !HealthLookup.HasComponent(attackTarget.Target) ||
                !Teams.HasComponent(attackTarget.Target) ||
                !Transforms.HasComponent(attackTarget.Target) ||
                !CombatWeaponUtility.IsValidTarget(Teams[ownerEntity], Teams[attackTarget.Target], HealthLookup[attackTarget.Target]) ||
                !CombatWeaponUtility.IsWeaponUsableForTarget(weapon, Targetables[attackTarget.Target].TargetType))
                return;

            if (CombatWeaponAimUtility.IsForceMoveMovementActive(ownerEntity, CommandStates, MoveTargets))
                return;

            LocalTransform ownerTransform = Transforms[ownerEntity];
            float3 attackerPosition = ownerTransform.Position;
            float3 targetPosition = Transforms[attackTarget.Target].Position;
            float distanceSq = CombatWeaponUtility.HorizontalDistanceSq(attackerPosition, targetPosition);
            float range = math.max(0f, weapon.Range);
            if (distanceSq > (range + CombatWeaponUtility.RangeTolerance) * (range + CombatWeaponUtility.RangeTolerance))
                return;

            float3 targetDirection = CombatWeaponUtility.GetHorizontalDirection(attackerPosition, targetPosition);
            if (math.lengthsq(targetDirection) <= 0f)
                return;

            float turnSpeedRadians = Speeds.HasComponent(ownerEntity)
                ? Speeds[ownerEntity].TurnSpeedRadians
                : 0f;
            ownerTransform.Rotation = CombatWeaponUtility.RotateTowards(
                ownerTransform.Rotation,
                quaternion.LookRotationSafe(targetDirection, math.up()),
                turnSpeedRadians * DeltaTime);
            Transforms[ownerEntity] = ownerTransform;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BodyForwardWeaponAimSystem))]
    public partial struct TurretWeaponAimSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponTurretReference>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var job = new TurretWeaponAimJob
            {
                DeltaTime = deltaTime,
                Targetables = SystemAPI.GetComponentLookup<CombatTargetable>(true),
                HealthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true),
                Teams = SystemAPI.GetComponentLookup<TeamInfoData>(true),
                LocalTransforms = SystemAPI.GetComponentLookup<LocalTransform>(false),
                WorldTransforms = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                Parents = SystemAPI.GetComponentLookup<Parent>(true),
                AttackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(true),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled), typeof(TurretWeapon))]
    partial struct TurretWeaponAimJob : IJobEntity
    {
        public float DeltaTime;
        [ReadOnly] public ComponentLookup<CombatTargetable> Targetables;
        [ReadOnly] public ComponentLookup<CombatHealth> HealthLookup;
        [ReadOnly] public ComponentLookup<TeamInfoData> Teams;
        [NativeDisableParallelForRestriction] public ComponentLookup<LocalTransform> LocalTransforms;
        [ReadOnly] public ComponentLookup<LocalToWorld> WorldTransforms;
        [ReadOnly] public ComponentLookup<Parent> Parents;
        [ReadOnly] public ComponentLookup<CombatAttackTarget> AttackTargets;

        public void Execute(
            in CombatWeaponOwner owner,
            in CombatWeapon weapon,
            in CombatWeaponTurretReference turretReference,
            in CombatWeaponTurretAim turretAim)
        {
            Entity ownerEntity = owner.Owner;
            Entity turretEntity = turretReference.Turret;
            if (ownerEntity == Entity.Null ||
                turretEntity == Entity.Null ||
                !AttackTargets.HasComponent(ownerEntity) ||
                !Teams.HasComponent(ownerEntity) ||
                !LocalTransforms.HasComponent(turretEntity) ||
                !WorldTransforms.HasComponent(turretEntity))
                return;

            CombatAttackTarget attackTarget = AttackTargets[ownerEntity];
            if (attackTarget.HasTarget == 0 ||
                attackTarget.Target == Entity.Null ||
                !Targetables.HasComponent(attackTarget.Target) ||
                !HealthLookup.HasComponent(attackTarget.Target) ||
                !Teams.HasComponent(attackTarget.Target) ||
                !WorldTransforms.HasComponent(attackTarget.Target) ||
                !CombatWeaponUtility.IsValidTarget(Teams[ownerEntity], Teams[attackTarget.Target], HealthLookup[attackTarget.Target]) ||
                !CombatWeaponUtility.IsWeaponUsableForTarget(weapon, Targetables[attackTarget.Target].TargetType))
                return;

            float3 turretPosition = WorldTransforms[turretEntity].Position;
            float3 targetPosition = WorldTransforms[attackTarget.Target].Position;
            float3 targetDirection = CombatWeaponUtility.GetHorizontalDirection(turretPosition, targetPosition);
            if (math.lengthsq(targetDirection) <= 0.0001f)
                return;

            quaternion desiredWorldRotation = quaternion.LookRotationSafe(targetDirection, math.up());
            quaternion desiredLocalRotation = CombatWeaponAimUtility.ResolveLocalRotation(
                turretEntity,
                desiredWorldRotation,
                Parents,
                WorldTransforms);
            LocalTransform turretTransform = LocalTransforms[turretEntity];
            turretTransform.Rotation = CombatWeaponUtility.RotateTowards(
                turretTransform.Rotation,
                desiredLocalRotation,
                turretAim.TurnSpeedRadians * DeltaTime);
            LocalTransforms[turretEntity] = turretTransform;
        }
    }

    static class CombatWeaponAimUtility
    {
        public static bool IsForceMoveMovementActive(
            Entity ownerEntity,
            ComponentLookup<UnitCommandState> commandStates,
            ComponentLookup<UnitMoveTarget> moveTargets)
        {
            return commandStates.HasComponent(ownerEntity) &&
                   commandStates[ownerEntity].Kind == UnitCommandKind.ForceMove &&
                   moveTargets.HasComponent(ownerEntity) &&
                   moveTargets[ownerEntity].HasTarget != 0;
        }

        public static quaternion ResolveLocalRotation(
            Entity entity,
            quaternion desiredWorldRotation,
            ComponentLookup<Parent> parents,
            ComponentLookup<LocalToWorld> worldTransforms)
        {
            if (!parents.HasComponent(entity) ||
                !worldTransforms.HasComponent(parents[entity].Value))
                return desiredWorldRotation;

            LocalToWorld parentWorld = worldTransforms[parents[entity].Value];
            float3 parentForward = math.normalizesafe(parentWorld.Value.c2.xyz, new float3(0f, 0f, 1f));
            float3 parentUp = math.normalizesafe(parentWorld.Value.c1.xyz, math.up());
            quaternion parentRotation = quaternion.LookRotationSafe(parentForward, parentUp);
            return math.mul(math.inverse(parentRotation), desiredWorldRotation);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(TurretWeaponAimSystem))]
    public partial struct CombatWeaponSetupSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RequiresWeaponSetup>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var job = new CombatWeaponSetupJob
            {
                DeltaTime = deltaTime,
                Targetables = SystemAPI.GetComponentLookup<CombatTargetable>(true),
                HealthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true),
                Teams = SystemAPI.GetComponentLookup<TeamInfoData>(true),
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(true),
                Motions = SystemAPI.GetComponentLookup<UnitMotionState>(true),
                AttackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(true),
                BodyForwardWeapons = SystemAPI.GetComponentLookup<BodyForwardWeapon>(true),
                FireArcs = SystemAPI.GetComponentLookup<CombatWeaponFireArc>(true),
                StoppedRequired = SystemAPI.GetComponentLookup<RequiresStoppedToFire>(true),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled))]
    partial struct CombatWeaponSetupJob : IJobEntity
    {
        public float DeltaTime;
        [ReadOnly] public ComponentLookup<CombatTargetable> Targetables;
        [ReadOnly] public ComponentLookup<CombatHealth> HealthLookup;
        [ReadOnly] public ComponentLookup<TeamInfoData> Teams;
        [ReadOnly] public ComponentLookup<LocalTransform> Transforms;
        [ReadOnly] public ComponentLookup<UnitMotionState> Motions;
        [ReadOnly] public ComponentLookup<CombatAttackTarget> AttackTargets;
        [ReadOnly] public ComponentLookup<BodyForwardWeapon> BodyForwardWeapons;
        [ReadOnly] public ComponentLookup<CombatWeaponFireArc> FireArcs;
        [ReadOnly] public ComponentLookup<RequiresStoppedToFire> StoppedRequired;

        public void Execute(
            Entity weaponEntity,
            in CombatWeaponOwner owner,
            in CombatWeapon weapon,
            in RequiresWeaponSetup setup,
            ref CombatWeaponSetupState setupState)
        {
            Entity ownerEntity = owner.Owner;
            bool canPrepare = false;

            if (ownerEntity != Entity.Null &&
                AttackTargets.HasComponent(ownerEntity) &&
                Teams.HasComponent(ownerEntity) &&
                Transforms.HasComponent(ownerEntity))
            {
                CombatAttackTarget attackTarget = AttackTargets[ownerEntity];
                if (attackTarget.HasTarget != 0 &&
                    attackTarget.Target != Entity.Null &&
                    Targetables.HasComponent(attackTarget.Target) &&
                    HealthLookup.HasComponent(attackTarget.Target) &&
                    Teams.HasComponent(attackTarget.Target) &&
                    Transforms.HasComponent(attackTarget.Target) &&
                    CombatWeaponUtility.IsValidTarget(Teams[ownerEntity], Teams[attackTarget.Target], HealthLookup[attackTarget.Target]))
                {
                    LocalTransform ownerTransform = Transforms[ownerEntity];
                    float3 attackerPosition = ownerTransform.Position;
                    float3 targetPosition = Transforms[attackTarget.Target].Position;
                    float3 targetDirection = CombatWeaponUtility.GetHorizontalDirection(attackerPosition, targetPosition);
                    float3 forward = math.normalizesafe(
                        math.mul(ownerTransform.Rotation, new float3(0f, 0f, 1f)),
                        new float3(0f, 0f, 1f));
                    byte isMoving = Motions.HasComponent(ownerEntity)
                        ? Motions[ownerEntity].IsMoving
                        : (byte)0;
                    canPrepare = CombatWeaponUtility.CanWeaponPrepare(
                        weaponEntity,
                        weapon,
                        Targetables[attackTarget.Target].TargetType,
                        CombatWeaponUtility.HorizontalDistanceSq(attackerPosition, targetPosition),
                        isMoving,
                        forward,
                        targetDirection,
                        BodyForwardWeapons,
                        FireArcs,
                        StoppedRequired);
                }
            }

            CombatWeaponUtility.UpdateWeaponSetup(setup, canPrepare, DeltaTime, ref setupState);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponSetupSystem))]
    [UpdateBefore(typeof(CombatWeaponReadyStateSystem))]
    public partial struct CombatWeaponDeployRotationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponDeployRotation>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new CombatWeaponDeployRotationJob
            {
                SetupStates = SystemAPI.GetComponentLookup<CombatWeaponSetupState>(true),
                EnabledWeapons = SystemAPI.GetComponentLookup<CombatWeaponEnabled>(true),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    partial struct CombatWeaponDeployRotationJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<CombatWeaponSetupState> SetupStates;
        [ReadOnly] public ComponentLookup<CombatWeaponEnabled> EnabledWeapons;

        public void Execute(ref LocalTransform transform, in CombatWeaponDeployRotation deployRotation)
        {
            Entity weapon = deployRotation.Weapon;
            float progress = 0f;
            if (weapon != Entity.Null &&
                SetupStates.HasComponent(weapon) &&
                (!EnabledWeapons.HasComponent(weapon) || EnabledWeapons.IsComponentEnabled(weapon)))
            {
                progress = math.saturate(SetupStates[weapon].Progress);
            }

            transform.Rotation = math.slerp(
                deployRotation.OffLocalRotation,
                deployRotation.OnLocalRotation,
                progress);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponSetupSystem))]
    public partial struct CombatWeaponReadyStateSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponReadyState>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new CombatWeaponReadyStateJob
            {
                Targetables = SystemAPI.GetComponentLookup<CombatTargetable>(true),
                HealthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true),
                Teams = SystemAPI.GetComponentLookup<TeamInfoData>(true),
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(true),
                Motions = SystemAPI.GetComponentLookup<UnitMotionState>(true),
                AttackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(true),
                LineOfSightStates = SystemAPI.GetComponentLookup<CombatLineOfSightState>(true),
                BodyForwardWeapons = SystemAPI.GetComponentLookup<BodyForwardWeapon>(true),
                TurretWeapons = SystemAPI.GetComponentLookup<TurretWeapon>(true),
                TurretReferences = SystemAPI.GetComponentLookup<CombatWeaponTurretReference>(true),
                WorldTransforms = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                FireArcs = SystemAPI.GetComponentLookup<CombatWeaponFireArc>(true),
                StoppedRequired = SystemAPI.GetComponentLookup<RequiresStoppedToFire>(true),
                SetupRequired = SystemAPI.GetComponentLookup<RequiresWeaponSetup>(true),
                LineOfSightRequired = SystemAPI.GetComponentLookup<RequiresLineOfSight>(true),
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled))]
    partial struct CombatWeaponReadyStateJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<CombatTargetable> Targetables;
        [ReadOnly] public ComponentLookup<CombatHealth> HealthLookup;
        [ReadOnly] public ComponentLookup<TeamInfoData> Teams;
        [ReadOnly] public ComponentLookup<LocalTransform> Transforms;
        [ReadOnly] public ComponentLookup<UnitMotionState> Motions;
        [ReadOnly] public ComponentLookup<CombatAttackTarget> AttackTargets;
        [ReadOnly] public ComponentLookup<CombatLineOfSightState> LineOfSightStates;
        [ReadOnly] public ComponentLookup<BodyForwardWeapon> BodyForwardWeapons;
        [ReadOnly] public ComponentLookup<TurretWeapon> TurretWeapons;
        [ReadOnly] public ComponentLookup<CombatWeaponTurretReference> TurretReferences;
        [ReadOnly] public ComponentLookup<LocalToWorld> WorldTransforms;
        [ReadOnly] public ComponentLookup<CombatWeaponFireArc> FireArcs;
        [ReadOnly] public ComponentLookup<RequiresStoppedToFire> StoppedRequired;
        [ReadOnly] public ComponentLookup<RequiresWeaponSetup> SetupRequired;
        [ReadOnly] public ComponentLookup<RequiresLineOfSight> LineOfSightRequired;

        public void Execute(
            Entity weaponEntity,
            in CombatWeaponOwner owner,
            in CombatWeapon weapon,
            in CombatWeaponCooldown cooldown,
            in CombatWeaponSetupState setupState,
            ref CombatWeaponReadyState readyState)
        {
            readyState.Target = Entity.Null;
            readyState.BlockedReasons = CombatWeaponBlockReason.None;
            readyState.CanFire = 0;

            Entity ownerEntity = owner.Owner;
            if (ownerEntity == Entity.Null ||
                !AttackTargets.HasComponent(ownerEntity) ||
                !Teams.HasComponent(ownerEntity) ||
                !Transforms.HasComponent(ownerEntity))
            {
                readyState.BlockedReasons = CombatWeaponBlockReason.NoOwner;
                return;
            }

            CombatAttackTarget attackTarget = AttackTargets[ownerEntity];
            readyState.Target = attackTarget.Target;
            if (attackTarget.HasTarget == 0 ||
                attackTarget.Target == Entity.Null ||
                !Targetables.HasComponent(attackTarget.Target) ||
                !HealthLookup.HasComponent(attackTarget.Target) ||
                !Teams.HasComponent(attackTarget.Target) ||
                !Transforms.HasComponent(attackTarget.Target))
            {
                readyState.BlockedReasons = CombatWeaponBlockReason.NoTarget;
                return;
            }

            if (!CombatWeaponUtility.IsValidTarget(Teams[ownerEntity], Teams[attackTarget.Target], HealthLookup[attackTarget.Target]))
            {
                readyState.BlockedReasons = CombatWeaponBlockReason.InvalidTarget;
                return;
            }

            LocalTransform ownerTransform = Transforms[ownerEntity];
            float3 attackerPosition = ownerTransform.Position;
            float3 targetPosition = Transforms[attackTarget.Target].Position;
            float3 targetDirection = CombatWeaponUtility.GetHorizontalDirection(attackerPosition, targetPosition);
            float3 forward = math.normalizesafe(
                math.mul(ownerTransform.Rotation, new float3(0f, 0f, 1f)),
                new float3(0f, 0f, 1f));
            byte isMoving = Motions.HasComponent(ownerEntity)
                ? Motions[ownerEntity].IsMoving
                : (byte)0;
            CombatTargetMask targetType = Targetables[attackTarget.Target].TargetType;
            float distanceSq = CombatWeaponUtility.HorizontalDistanceSq(attackerPosition, targetPosition);
            CombatWeaponBlockReason blockedReasons = CombatWeaponBlockReason.None;

            if (!CombatWeaponUtility.IsWeaponUsableForTarget(weapon, targetType))
                blockedReasons |= CombatWeaponBlockReason.UnsupportedTargetType;

            if (cooldown.Remaining > 0f)
                blockedReasons |= CombatWeaponBlockReason.Cooldown;

            if (StoppedRequired.HasComponent(weaponEntity) && isMoving != 0)
                blockedReasons |= CombatWeaponBlockReason.NeedStop;

            float range = math.max(0f, weapon.Range);
            if (distanceSq > (range + CombatWeaponUtility.RangeTolerance) * (range + CombatWeaponUtility.RangeTolerance))
                blockedReasons |= CombatWeaponBlockReason.OutOfRange;

            if (BodyForwardWeapons.HasComponent(weaponEntity) &&
                math.lengthsq(targetDirection) > 0.0001f)
            {
                float fireArcCosine = FireArcs.HasComponent(weaponEntity)
                    ? FireArcs[weaponEntity].FireArcCosine
                    : -1f;
                if (math.dot(math.normalizesafe(forward), targetDirection) < fireArcCosine)
                    blockedReasons |= CombatWeaponBlockReason.NeedBodyAim;
            }

            if (TurretWeapons.HasComponent(weaponEntity) &&
                TurretReferences.HasComponent(weaponEntity))
            {
                Entity turretEntity = TurretReferences[weaponEntity].Turret;
                if (turretEntity == Entity.Null ||
                    !WorldTransforms.HasComponent(turretEntity))
                {
                    blockedReasons |= CombatWeaponBlockReason.NeedTurretAim;
                }
                else
                {
                    float3 turretPosition = WorldTransforms[turretEntity].Position;
                    float3 turretTargetDirection = CombatWeaponUtility.GetHorizontalDirection(turretPosition, targetPosition);
                    if (math.lengthsq(turretTargetDirection) > 0.0001f)
                    {
                        float3 turretForward = math.normalizesafe(
                            WorldTransforms[turretEntity].Value.c2.xyz,
                            new float3(0f, 0f, 1f));
                        float fireArcCosine = FireArcs.HasComponent(weaponEntity)
                            ? FireArcs[weaponEntity].FireArcCosine
                            : -1f;
                        if (math.dot(turretForward, turretTargetDirection) < fireArcCosine)
                            blockedReasons |= CombatWeaponBlockReason.NeedTurretAim;
                    }
                }
            }

            if (SetupRequired.HasComponent(weaponEntity) &&
                setupState.Progress < 1f)
                blockedReasons |= CombatWeaponBlockReason.NeedSetup;

            if (LineOfSightRequired.HasComponent(weaponEntity) &&
                LineOfSightStates.HasComponent(ownerEntity))
            {
                CombatLineOfSightState lineOfSightState = LineOfSightStates[ownerEntity];
                if (lineOfSightState.HasState != 0 &&
                    lineOfSightState.Target == attackTarget.Target &&
                    lineOfSightState.HasLineOfSight == 0)
                    blockedReasons |= CombatWeaponBlockReason.BlockedLineOfSight;
            }

            readyState.BlockedReasons = blockedReasons;
            readyState.CanFire = blockedReasons == CombatWeaponBlockReason.None ? (byte)1 : (byte)0;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponReadyStateSystem))]
    public partial struct CombatWeaponFireRequestSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponOwner>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            var job = new CombatWeaponFireRequestJob
            {
                BoundsLookup = SystemAPI.GetComponentLookup<CombatTargetBounds>(true),
                Transforms = SystemAPI.GetComponentLookup<LocalTransform>(true),
                MuzzleReferences = SystemAPI.GetBufferLookup<CombatWeaponMuzzleReference>(true),
                MuzzleTransforms = SystemAPI.GetComponentLookup<LocalToWorld>(true),
                MuzzleCycles = SystemAPI.GetComponentLookup<CombatWeaponMuzzleCycle>(false),
                Ecb = ecb.AsParallelWriter(),
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled))]
    partial struct CombatWeaponFireRequestJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<CombatTargetBounds> BoundsLookup;
        [ReadOnly] public ComponentLookup<LocalTransform> Transforms;
        [ReadOnly] public BufferLookup<CombatWeaponMuzzleReference> MuzzleReferences;
        [ReadOnly] public ComponentLookup<LocalToWorld> MuzzleTransforms;
        [NativeDisableParallelForRestriction] public ComponentLookup<CombatWeaponMuzzleCycle> MuzzleCycles;
        public EntityCommandBuffer.ParallelWriter Ecb;

        public void Execute(
            [EntityIndexInQuery] int sortKey,
            Entity weaponEntity,
            in CombatWeaponOwner owner,
            in CombatWeapon weapon,
            ref CombatWeaponCooldown cooldown,
            in CombatWeaponReadyState readyState)
        {
            if (readyState.CanFire == 0 ||
                owner.Owner == Entity.Null ||
                readyState.Target == Entity.Null ||
                !Transforms.HasComponent(owner.Owner) ||
                !Transforms.HasComponent(readyState.Target))
                return;

            float3 attackerPosition = Transforms[owner.Owner].Position;
            float3 targetPosition = Transforms[readyState.Target].Position;
            float3 sourcePosition = CombatWeaponFireUtility.TryResolveMuzzleSourcePosition(
                weaponEntity,
                MuzzleReferences,
                MuzzleTransforms,
                MuzzleCycles,
                out float3 muzzlePosition)
                ? muzzlePosition
                : CombatWeaponUtility.ResolveAimPosition(owner.Owner, attackerPosition, BoundsLookup);

            Entity fireRequest = Ecb.CreateEntity(sortKey);
            Ecb.AddComponent(sortKey, fireRequest, new CombatWeaponFireRequest
            {
                Source = owner.Owner,
                Target = readyState.Target,
                SourcePosition = sourcePosition,
                TargetPosition = CombatWeaponUtility.ResolveAimPosition(readyState.Target, targetPosition, BoundsLookup),
                Damage = math.max(0f, weapon.Damage),
                WeaponIndex = owner.WeaponIndex,
            });

            CombatWeaponUtility.ResetWeaponCooldown(ref cooldown);
        }
    }

    static class CombatWeaponFireUtility
    {
        public static bool TryResolveMuzzleSourcePosition(
            Entity weaponEntity,
            BufferLookup<CombatWeaponMuzzleReference> muzzleReferences,
            ComponentLookup<LocalToWorld> muzzleTransforms,
            ComponentLookup<CombatWeaponMuzzleCycle> muzzleCycles,
            out float3 sourcePosition)
        {
            sourcePosition = float3.zero;
            if (!muzzleReferences.HasBuffer(weaponEntity))
                return false;

            DynamicBuffer<CombatWeaponMuzzleReference> muzzles = muzzleReferences[weaponEntity];
            int desiredMuzzleIndex = muzzleCycles.HasComponent(weaponEntity)
                ? muzzleCycles[weaponEntity].NextMuzzleIndex
                : 0;
            int selectedArrayIndex = -1;
            int selectedMuzzleIndex = int.MaxValue;
            int maxMuzzleIndex = -1;

            for (int i = 0; i < muzzles.Length; i++)
            {
                if (!muzzleTransforms.HasComponent(muzzles[i].Muzzle))
                    continue;

                maxMuzzleIndex = math.max(maxMuzzleIndex, muzzles[i].MuzzleIndex);
                if (muzzles[i].MuzzleIndex == desiredMuzzleIndex)
                {
                    selectedArrayIndex = i;
                    selectedMuzzleIndex = muzzles[i].MuzzleIndex;
                    break;
                }

                if (selectedArrayIndex < 0 ||
                    muzzles[i].MuzzleIndex < selectedMuzzleIndex)
                {
                    selectedArrayIndex = i;
                    selectedMuzzleIndex = muzzles[i].MuzzleIndex;
                }
            }

            if (selectedArrayIndex < 0)
                return false;

            sourcePosition = muzzleTransforms[muzzles[selectedArrayIndex].Muzzle].Value.c3.xyz;

            if (muzzleCycles.HasComponent(weaponEntity))
            {
                CombatWeaponMuzzleCycle cycle = muzzleCycles[weaponEntity];
                cycle.NextMuzzleIndex = selectedMuzzleIndex >= maxMuzzleIndex
                    ? 0
                    : selectedMuzzleIndex + 1;
                muzzleCycles[weaponEntity] = cycle;
            }

            return true;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponFireRequestSystem))]
    public partial struct CombatDirectFireResolveSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponFireRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);
            var job = new CombatDirectFireResolveJob
            {
                HealthLookup = SystemAPI.GetComponentLookup<CombatHealth>(true),
                Ecb = ecb.AsParallelWriter(),
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    partial struct CombatDirectFireResolveJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<CombatHealth> HealthLookup;
        public EntityCommandBuffer.ParallelWriter Ecb;

        public void Execute(
            [EntityIndexInQuery] int sortKey,
            Entity requestEntity,
            in CombatWeaponFireRequest fireRequest)
        {
            if (fireRequest.Target != Entity.Null &&
                HealthLookup.HasComponent(fireRequest.Target) &&
                HealthLookup[fireRequest.Target].Health > 0f &&
                fireRequest.Damage > 0f)
            {
                Entity damageRequest = Ecb.CreateEntity(sortKey);
                Ecb.AddComponent(sortKey, damageRequest, new CombatDamageRequest
                {
                    Source = fireRequest.Source,
                    Target = fireRequest.Target,
                    Damage = fireRequest.Damage,
                });
            }

            Ecb.DestroyEntity(sortKey, requestEntity);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatDirectFireResolveSystem))]
    public partial struct CombatDamageApplySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatDamageRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var damageQuery = SystemAPI.QueryBuilder()
                .WithAll<CombatDamageRequest>()
                .Build();
            int damageCount = damageQuery.CalculateEntityCount();
            if (damageCount == 0)
                return;

            var damageEntities = damageQuery.ToEntityArray(Allocator.Temp);
            var damageRequests = damageQuery.ToComponentDataArray<CombatDamageRequest>(Allocator.Temp);
            var damageByTarget = new NativeParallelHashMap<Entity, float>(math.max(1, damageCount), Allocator.Temp);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int i = 0; i < damageRequests.Length; i++)
            {
                Entity target = damageRequests[i].Target;
                float damage = math.max(0f, damageRequests[i].Damage);
                if (target == Entity.Null ||
                    damage <= 0f)
                    continue;

                if (damageByTarget.TryGetValue(target, out float existingDamage))
                    damageByTarget[target] = existingDamage + damage;
                else
                    damageByTarget.Add(target, damage);
            }

            foreach (var (health, entity) in
                     SystemAPI.Query<RefRW<CombatHealth>>()
                         .WithEntityAccess())
            {
                if (!damageByTarget.TryGetValue(entity, out float damage) ||
                    damage <= 0f)
                    continue;

                health.ValueRW.Health = math.max(0f, health.ValueRO.Health - damage);
                if (health.ValueRW.Health <= 0f &&
                    !SystemAPI.HasComponent<CombatDeadTag>(entity))
                {
                    ecb.AddComponent<CombatDeadTag>(entity);
                }
            }

            for (int i = 0; i < damageEntities.Length; i++)
                ecb.DestroyEntity(damageEntities[i]);

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            damageByTarget.Dispose();
            damageRequests.Dispose();
            damageEntities.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatDamageApplySystem))]
    public partial struct CombatDeathSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatDeadTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (_, entity) in
                     SystemAPI.Query<RefRO<CombatDeadTag>>()
                         .WithAll<CombatDestroyOnDeath>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
