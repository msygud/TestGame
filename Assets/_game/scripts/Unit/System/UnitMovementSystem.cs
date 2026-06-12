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
    [UpdateAfter(typeof(UnitMoveOrderSystem))]
    public partial struct CombatWeaponPackBeforeMoveSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatWeaponOwner>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var job = new CombatWeaponPackBeforeMoveJob
            {
                MoveTargets = SystemAPI.GetComponentLookup<UnitMoveTarget>(true),
                DeltaTime = deltaTime,
            };
            state.Dependency = job.ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    [WithAll(typeof(CombatWeaponEnabled), typeof(BlocksOwnerMovementWhileSetup))]
    partial struct CombatWeaponPackBeforeMoveJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<UnitMoveTarget> MoveTargets;
        public float DeltaTime;

        public void Execute(
            in CombatWeaponOwner owner,
            in RequiresWeaponSetup weapon,
            ref CombatWeaponSetupState setupState)
        {
            if (setupState.Progress <= 0f ||
                !MoveTargets.HasComponent(owner.Owner) ||
                MoveTargets[owner.Owner].HasTarget == 0)
                return;

            setupState.Progress = CombatWeaponUtility.PackDownSetupProgress(
                setupState.Progress,
                weapon.PackTime,
                DeltaTime);
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatWeaponPackBeforeMoveSystem))]
    public partial struct UnitMovementBlockerSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitMovementBlocker>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var weaponQuery = SystemAPI.QueryBuilder()
                .WithAll<CombatWeaponOwner, CombatWeaponSetupState, CombatWeaponEnabled, BlocksOwnerMovementWhileSetup>()
                .Build();
            int weaponCount = weaponQuery.CalculateEntityCount();
            var blockedOwners = new NativeParallelHashSet<Entity>(math.max(1, weaponCount), Allocator.Temp);

            foreach (var (owner, setupState) in
                     SystemAPI.Query<RefRO<CombatWeaponOwner>, RefRO<CombatWeaponSetupState>>()
                         .WithAll<CombatWeaponEnabled, BlocksOwnerMovementWhileSetup>())
            {
                if (owner.ValueRO.Owner != Entity.Null &&
                    setupState.ValueRO.Progress > 0f)
                    blockedOwners.Add(owner.ValueRO.Owner);
            }

            foreach (var (blocker, entity) in
                     SystemAPI.Query<RefRW<UnitMovementBlocker>>()
                         .WithAll<UnitTag>()
                         .WithEntityAccess())
            {
                blocker.ValueRW.Reasons = blockedOwners.Contains(entity)
                    ? UnitMovementBlockReason.WeaponSetup
                    : UnitMovementBlockReason.None;
            }

            blockedOwners.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementBlockerSystem))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    public partial struct UnitMovementOffsetRuntimeInitializeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (_, entity) in
                     SystemAPI.Query<RefRO<UnitTag>>()
                         .WithNone<UnitObstacleAvoidanceOffset>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new UnitObstacleAvoidanceOffset
                {
                    Value = float3.zero,
                });
            }

            foreach (var (_, entity) in
                     SystemAPI.Query<RefRO<UnitTag>>()
                         .WithNone<UnitSeparationOffset>()
                         .WithEntityAccess())
            {
                ecb.AddComponent(entity, new UnitSeparationOffset
                {
                    Value = float3.zero,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementOffsetRuntimeInitializeSystem))]
    public partial struct UnitMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var waypointLookup = SystemAPI.GetBufferLookup<UnitPathWaypoint>(false);
            var commandStates = SystemAPI.GetComponentLookup<UnitCommandState>(false);
            var movementBlockers = SystemAPI.GetComponentLookup<UnitMovementBlocker>(true);
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            foreach (var yieldState in SystemAPI.Query<RefRW<UnitYieldState>>())
                yieldState.ValueRW.Cooldown = math.max(0f, yieldState.ValueRO.Cooldown - deltaTime);

            var yieldLookup = SystemAPI.GetComponentLookup<UnitYieldState>(false);
            bool hasActiveMoveTarget = false;
            bool hasYieldCandidate = false;
            bool hasDetourRefreshCandidate = false;
            foreach (var (target, motion, steering) in
                     SystemAPI.Query<
                         RefRO<UnitMoveTarget>,
                         RefRO<UnitMotionState>,
                         RefRO<UnitPathSteeringState>>()
                         .WithAll<UnitTag>())
            {
                if (target.ValueRO.HasTarget == 0)
                    continue;

                hasActiveMoveTarget = true;

                if (target.ValueRO.RepathRequested == 0 &&
                    motion.ValueRO.StuckTime + deltaTime >= UnitMovementUtility.YieldRequestStuckTime)
                    hasYieldCandidate = true;

                if (steering.ValueRO.HasDetour != 0 ||
                    steering.ValueRO.DetourRefreshTime <= deltaTime)
                    hasDetourRefreshCandidate = true;

                if (hasYieldCandidate && hasDetourRefreshCandidate)
                    break;
            }

            if (!hasActiveMoveTarget)
            {
                ecb.Dispose();
                state.Dependency = new UnitNoMoveTargetRestJob
                {
                    DeltaTime = deltaTime,
                }.ScheduleParallel(state.Dependency);
                return;
            }

            bool needsObstacleData = hasYieldCandidate || hasDetourRefreshCandidate;
            int obstacleCount = 0;
            NativeArray<LocalTransform> obstacleTransforms = default;
            NativeArray<ObstacleFootprint> obstacleFootprints = default;
            if (needsObstacleData)
            {
                var obstacleQuery = SystemAPI.QueryBuilder()
                    .WithAll<ObstacleFootprint, LocalTransform>()
                    .Build();
                obstacleCount = obstacleQuery.CalculateEntityCount();
                obstacleTransforms = obstacleCount > 0
                    ? obstacleQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp)
                    : default;
                obstacleFootprints = obstacleCount > 0
                    ? obstacleQuery.ToComponentDataArray<ObstacleFootprint>(Allocator.Temp)
                    : default;
            }

            NativeArray<Entity> unitEntities = default;
            NativeArray<LocalTransform> unitTransforms = default;
            NativeArray<UnitFootprint> unitFootprints = default;
            NativeArray<UnitMoveTarget> unitTargets = default;
            NativeArray<UnitActivityState> unitActivities = default;
            bool hasYieldSnapshots = false;
            if (hasYieldCandidate)
            {
                var unitQuery = SystemAPI.QueryBuilder()
                    .WithAll<UnitTag, UnitFootprint, UnitMoveTarget, UnitActivityState, UnitYieldState, LocalTransform>()
                    .Build();
                int unitCount = unitQuery.CalculateEntityCount();
                if (unitCount > 0)
                {
                    unitEntities = unitQuery.ToEntityArray(Allocator.Temp);
                    unitTransforms = unitQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp);
                    unitFootprints = unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.Temp);
                    unitTargets = unitQuery.ToComponentDataArray<UnitMoveTarget>(Allocator.Temp);
                    unitActivities = unitQuery.ToComponentDataArray<UnitActivityState>(Allocator.Temp);
                    hasYieldSnapshots = true;
                }
            }

            int yieldAttemptsThisFrame = 0;
            int yieldRequestsThisFrame = 0;
            int repathRequestsThisFrame = 0;
            foreach (var (transform, speed, target, footprint, motion, steering, activity, entity) in
                     SystemAPI.Query<
                         RefRW<LocalTransform>,
                         RefRO<UnitMoveSpeed>,
                         RefRW<UnitMoveTarget>,
                         RefRO<UnitFootprint>,
                         RefRW<UnitMotionState>,
                         RefRW<UnitPathSteeringState>,
                         RefRW<UnitActivityState>>()
                         .WithAll<UnitTag>()
                         .WithEntityAccess())
            {
                if (target.ValueRO.HasTarget == 0)
                {
                    UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                    UnitMovementUtility.Stop(ref motion.ValueRW);
                    UnitMovementUtility.SetRestActivity(ref activity.ValueRW, deltaTime);
                    continue;
                }

                if (movementBlockers.HasComponent(entity) &&
                    movementBlockers[entity].Reasons != UnitMovementBlockReason.None)
                {
                    UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                    UnitMovementUtility.Stop(ref motion.ValueRW);
                    UnitMovementUtility.SetActivity(ref activity.ValueRW, UnitActivityKind.Settled, deltaTime);
                    continue;
                }

                float3 position = transform.ValueRO.Position;
                float3 finalTarget = target.ValueRO.Position;
                steering.ValueRW.DetourRefreshTime = math.max(0f, steering.ValueRO.DetourRefreshTime - deltaTime);
                float unitRadius = math.max(0.01f, footprint.ValueRO.Radius);
                float waypointArrivalDistance = math.max(UnitMovementUtility.DetourArrivalDistance, math.min(1.5f, unitRadius * 0.75f));
                bool hasPathWaypoint = UnitMovementUtility.TryGetPathWaypoint(
                    entity,
                    position,
                    waypointArrivalDistance,
                    waypointLookup,
                    out float3 pathWaypoint);
                float3 navigationTarget = hasPathWaypoint
                    ? pathWaypoint
                    : finalTarget;
                float3 toFinalTarget = finalTarget - position;
                toFinalTarget.y = 0f;

                float stopDistance = math.max(0.01f, target.ValueRO.StopDistance);
                float finalDistanceSq = math.lengthsq(toFinalTarget);
                float finalDistance = math.sqrt(finalDistanceSq);
                float relaxedStopDistance = math.max(stopDistance, unitRadius * 0.8f);
                UnitMovementUtility.UpdateStuckTimer(finalDistance, relaxedStopDistance, deltaTime, ref motion.ValueRW);

                if (UnitMovementUtility.ShouldArrive(finalDistance, stopDistance, relaxedStopDistance, motion.ValueRO, hasPathWaypoint))
                {
                    target.ValueRW.HasTarget = 0;
                    UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                    UnitMovementUtility.ClearPath(entity, waypointLookup);
                    UnitMovementUtility.Stop(ref motion.ValueRW);
                    UnitMovementUtility.SetActivity(ref activity.ValueRW, UnitActivityKind.Settled, deltaTime);
                    UnitMovementUtility.CompleteMoveCommand(commandStates, entity, finalTarget);
                    continue;
                }

                if (hasYieldSnapshots &&
                    yieldAttemptsThisFrame < UnitMovementUtility.MaxYieldAttemptsPerFrame &&
                    yieldRequestsThisFrame < UnitMovementUtility.MaxYieldRequestsPerFrame &&
                    UnitMovementUtility.ShouldRequestYield(finalDistance, relaxedStopDistance, target.ValueRO, motion.ValueRO) &&
                    TryIssueBudgetedYieldRequest(
                        ref yieldAttemptsThisFrame,
                        ref yieldRequestsThisFrame,
                        ecb,
                        entity,
                        position,
                        navigationTarget,
                        unitRadius,
                        unitEntities,
                        unitTransforms,
                        unitFootprints,
                        unitTargets,
                        unitActivities,
                        obstacleTransforms,
                        obstacleFootprints,
                        yieldLookup))
                {
                    UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                    UnitMovementUtility.Stop(ref motion.ValueRW);
                    continue;
                }

                if (repathRequestsThisFrame < UnitMovementUtility.MaxRepathRequestsPerFrame &&
                    UnitMovementUtility.ShouldRequestRepath(finalDistance, relaxedStopDistance, target.ValueRO, motion.ValueRO))
                {
                    if (target.ValueRO.RepathCount >= UnitMovementUtility.MaxRepathCount)
                    {
                        target.ValueRW.HasTarget = 0;
                        target.ValueRW.PathStatus = UnitPathStatus.PathFailed;
                        UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                        UnitMovementUtility.ClearPath(entity, waypointLookup);
                        UnitMovementUtility.Stop(ref motion.ValueRW);
                        UnitMovementUtility.SetActivity(ref activity.ValueRW, UnitActivityKind.Settled, deltaTime);
                        UnitMovementUtility.CompleteMoveCommand(commandStates, entity, finalTarget);
                        continue;
                    }

                    Entity request = ecb.CreateEntity();
                    ecb.AddComponent(request, new MoveOrderRequest
                    {
                        Unit = entity,
                        Target = finalTarget,
                        StopDistance = target.ValueRO.StopDistance,
                        RepathCount = target.ValueRO.RepathCount + 1,
                    });

                    target.ValueRW.RepathRequested = 1;
                    UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                    UnitMovementUtility.ClearPath(entity, waypointLookup);
                    motion.ValueRW.StuckTime = 0f;
                    motion.ValueRW.LastTargetDistance = float.MaxValue;
                    UnitMovementUtility.Stop(ref motion.ValueRW);
                    repathRequestsThisFrame++;
                    continue;
                }

                if (!hasPathWaypoint && obstacleCount > 0)
                    UnitMovementUtility.UpdateDetour(
                        position,
                        navigationTarget,
                        unitRadius,
                        obstacleTransforms,
                        obstacleFootprints,
                        ref steering.ValueRW);

                float3 moveGoal = steering.ValueRO.HasDetour != 0
                    ? steering.ValueRO.DetourPosition
                    : navigationTarget;
                float3 toTarget = moveGoal - position;
                toTarget.y = 0f;

                float distanceSq = math.lengthsq(toTarget);
                float arrivalDistance = steering.ValueRO.HasDetour != 0
                    ? waypointArrivalDistance
                    : hasPathWaypoint
                        ? waypointArrivalDistance
                    : relaxedStopDistance;

                if (distanceSq <= arrivalDistance * arrivalDistance)
                {
                    if (steering.ValueRW.HasDetour != 0)
                    {
                        UnitMovementUtility.ClearSteering(ref steering.ValueRW);
                        continue;
                    }

                    if (hasPathWaypoint)
                    {
                        UnitMovementUtility.RemoveCurrentPathWaypoint(entity, waypointLookup);
                        continue;
                    }

                    target.ValueRW.HasTarget = 0;
                    UnitMovementUtility.Stop(ref motion.ValueRW);
                    UnitMovementUtility.SetActivity(ref activity.ValueRW, UnitActivityKind.Settled, deltaTime);
                    UnitMovementUtility.CompleteMoveCommand(commandStates, entity, finalTarget);
                    continue;
                }

                float distance = math.sqrt(distanceSq);
                float3 direction = toTarget / distance;
                float maxAdvance = math.max(0.01f, speed.ValueRO.MetersPerSecond) * deltaTime;
                float advance = math.min(maxAdvance, math.max(0f, distance - arrivalDistance));

                transform.ValueRW.Position = position + direction * advance;
                transform.ValueRW.Rotation = UnitMovementUtility.RotateTowards(
                    transform.ValueRO.Rotation,
                    quaternion.LookRotationSafe(direction, math.up()),
                    speed.ValueRO.TurnSpeedRadians * deltaTime);

                motion.ValueRW.Velocity = direction * (advance / deltaTime);
                motion.ValueRW.DesiredForward = direction;
                motion.ValueRW.IsMoving = 1;
                UnitMovementUtility.SetActivity(ref activity.ValueRW, UnitActivityKind.Moving, deltaTime);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            if (hasYieldSnapshots)
            {
                unitActivities.Dispose();
                unitTargets.Dispose();
                unitFootprints.Dispose();
                unitTransforms.Dispose();
                unitEntities.Dispose();
            }

            if (obstacleCount > 0)
            {
                obstacleFootprints.Dispose();
                obstacleTransforms.Dispose();
            }
        }

        static bool TryIssueBudgetedYieldRequest(
            ref int attemptsThisFrame,
            ref int requestsThisFrame,
            EntityCommandBuffer ecb,
            Entity requester,
            float3 requesterPosition,
            float3 navigationTarget,
            float requesterRadius,
            NativeArray<Entity> unitEntities,
            NativeArray<LocalTransform> unitTransforms,
            NativeArray<UnitFootprint> unitFootprints,
            NativeArray<UnitMoveTarget> unitTargets,
            NativeArray<UnitActivityState> unitActivities,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            ComponentLookup<UnitYieldState> yieldLookup)
        {
            attemptsThisFrame++;
            if (!UnitMovementUtility.TryIssueYieldRequest(
                    ecb,
                    requester,
                    requesterPosition,
                    navigationTarget,
                    requesterRadius,
                    unitEntities,
                    unitTransforms,
                    unitFootprints,
                    unitTargets,
                    unitActivities,
                    obstacleTransforms,
                    obstacleFootprints,
                    yieldLookup))
                return false;

            requestsThisFrame++;
            return true;
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct UnitNoMoveTargetRestJob : IJobEntity
    {
        public float DeltaTime;

        public void Execute(
            in UnitMoveTarget target,
            ref UnitMotionState motion,
            ref UnitActivityState activity)
        {
            if (target.HasTarget != 0)
                return;

            UnitMovementUtility.Stop(ref motion);
            UnitMovementUtility.SetRestActivity(ref activity, DeltaTime);
        }
    }

    static class UnitMovementUtility
    {
        public const float DetourArrivalDistance = 0.35f;
        public const float DetourClearanceScale = 1.25f;
        public const float DetourRefreshInterval = 0.18f;
        public const float StuckArrivalTime = 1.25f;
        public const float YieldRequestStuckTime = 0.85f;
        public const float YieldCooldownTime = 2.5f;
        public const float YieldBlockerPadding = 0.2f;
        public const float YieldSideStepScale = 1.35f;
        public const float RepathStuckTime = 2.25f;
        public const float StuckProgressEpsilon = 0.05f;
        public const int MaxRepathCount = 3;
        public const int MaxYieldAttemptsPerFrame = 48;
        public const int MaxYieldRequestsPerFrame = 8;
        public const int MaxRepathRequestsPerFrame = 24;
        public static bool TryGetPathWaypoint(
            Entity entity,
            float3 position,
            float arrivalDistance,
            BufferLookup<UnitPathWaypoint> waypointLookup,
            out float3 waypoint)
        {
            waypoint = float3.zero;
            if (!waypointLookup.HasBuffer(entity))
                return false;

            var waypoints = waypointLookup[entity];
            while (waypoints.Length > 0 &&
                   HorizontalDistanceSq(position, waypoints[0].Position) <= arrivalDistance * arrivalDistance)
            {
                waypoints.RemoveAt(0);
            }

            if (waypoints.Length == 0)
                return false;

            waypoint = waypoints[0].Position;
            return true;
        }

        public static void RemoveCurrentPathWaypoint(Entity entity, BufferLookup<UnitPathWaypoint> waypointLookup)
        {
            if (!waypointLookup.HasBuffer(entity))
                return;

            var waypoints = waypointLookup[entity];
            if (waypoints.Length > 0)
                waypoints.RemoveAt(0);
        }

        public static void ClearPath(Entity entity, BufferLookup<UnitPathWaypoint> waypointLookup)
        {
            if (!waypointLookup.HasBuffer(entity))
                return;

            waypointLookup[entity].Clear();
        }

        public static void ClearSteering(ref UnitPathSteeringState steering)
        {
            steering.DetourPosition = float3.zero;
            steering.DetourRefreshTime = 0f;
            steering.HasDetour = 0;
        }

        public static void Stop(ref UnitMotionState motion)
        {
            motion.Velocity = float3.zero;
            motion.IsMoving = 0;
            motion.LastTargetDistance = float.MaxValue;
            motion.StuckTime = 0f;
        }

        public static void CompleteMoveCommand(
            ComponentLookup<UnitCommandState> commandStates,
            Entity entity,
            float3 finalTarget)
        {
            if (!commandStates.HasComponent(entity))
                return;

            UnitCommandState commandState = commandStates[entity];
            if (commandState.Kind != UnitCommandKind.ForceMove &&
                commandState.Kind != UnitCommandKind.AttackMove)
                return;

            commandState.Kind = UnitCommandKind.IdleAutoEngage;
            commandState.TargetEntity = Entity.Null;
            commandState.TargetPosition = finalTarget;
            commandState.HasTargetEntity = 0;
            commandStates[entity] = commandState;
        }

        public static void UpdateStuckTimer(
            float finalDistance,
            float relaxedStopDistance,
            float deltaTime,
            ref UnitMotionState motion)
        {
            if (motion.LastTargetDistance == float.MaxValue ||
                finalDistance < motion.LastTargetDistance - StuckProgressEpsilon)
            {
                motion.LastTargetDistance = finalDistance;
                motion.StuckTime = 0f;
                return;
            }

            motion.StuckTime += deltaTime;

            motion.LastTargetDistance = math.min(motion.LastTargetDistance, finalDistance);
        }

        public static bool ShouldArrive(
            float finalDistance,
            float stopDistance,
            float relaxedStopDistance,
            UnitMotionState motion,
            bool hasPathWaypoint)
        {
            if (finalDistance <= stopDistance)
                return true;

            if (hasPathWaypoint)
                return false;

            if (finalDistance <= relaxedStopDistance &&
                motion.StuckTime >= StuckArrivalTime * 0.5f)
                return true;

            return finalDistance <= relaxedStopDistance * 1.35f &&
                   motion.StuckTime >= StuckArrivalTime;
        }

        public static bool ShouldRequestRepath(
            float finalDistance,
            float relaxedStopDistance,
            UnitMoveTarget target,
            UnitMotionState motion)
        {
            if (target.RepathRequested != 0)
                return false;

            if (target.PathStatus == UnitPathStatus.Direct)
                return false;

            if (finalDistance <= relaxedStopDistance * 1.35f)
                return false;

            return motion.StuckTime >= RepathStuckTime;
        }

        public static bool ShouldRequestYield(
            float finalDistance,
            float relaxedStopDistance,
            UnitMoveTarget target,
            UnitMotionState motion)
        {
            if (target.HasTarget == 0)
                return false;

            if (target.RepathRequested != 0)
                return false;

            if (finalDistance <= relaxedStopDistance * 1.35f)
                return false;

            return motion.StuckTime >= YieldRequestStuckTime;
        }

        public static bool TryIssueYieldRequest(
            EntityCommandBuffer ecb,
            Entity requester,
            float3 requesterPosition,
            float3 navigationTarget,
            float requesterRadius,
            NativeArray<Entity> unitEntities,
            NativeArray<LocalTransform> unitTransforms,
            NativeArray<UnitFootprint> unitFootprints,
            NativeArray<UnitMoveTarget> unitTargets,
            NativeArray<UnitActivityState> unitActivities,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            ComponentLookup<UnitYieldState> yieldLookup)
        {
            float3 toTarget = navigationTarget - requesterPosition;
            toTarget.y = 0f;
            float distanceSq = math.lengthsq(toTarget);
            if (distanceSq <= 0.0001f)
                return false;

            float3 direction = toTarget / math.sqrt(distanceSq);
            float lookAhead = math.max(2.5f, requesterRadius * 3f);
            int blockerIndex = -1;
            float bestAlong = float.MaxValue;

            for (int i = 0; i < unitEntities.Length; i++)
            {
                if (unitEntities[i] == requester)
                    continue;

                if (unitTargets[i].HasTarget != 0)
                    continue;

                if (unitActivities[i].Value != UnitActivityKind.Settled)
                    continue;

                UnitYieldState yieldState = yieldLookup[unitEntities[i]];
                if (yieldState.Cooldown > 0f)
                    continue;

                float blockerRadius = math.max(0.01f, unitFootprints[i].Radius);
                float3 toBlocker = unitTransforms[i].Position - requesterPosition;
                toBlocker.y = 0f;
                float along = math.dot(toBlocker, direction);
                if (along <= 0f || along > lookAhead || along >= bestAlong)
                    continue;

                float3 lateral = toBlocker - direction * along;
                float clearance = requesterRadius + blockerRadius + YieldBlockerPadding;
                if (math.lengthsq(lateral) > clearance * clearance)
                    continue;

                blockerIndex = i;
                bestAlong = along;
            }

            if (blockerIndex < 0)
                return false;

            float3 blockerPosition = unitTransforms[blockerIndex].Position;
            float blockerRadiusResolved = math.max(0.01f, unitFootprints[blockerIndex].Radius);
            if (!TryFindYieldDestination(
                    blockerIndex,
                    blockerPosition,
                    blockerRadiusResolved,
                    requesterPosition,
                    requesterRadius,
                    direction,
                    unitEntities,
                    unitTransforms,
                    unitFootprints,
                    obstacleTransforms,
                    obstacleFootprints,
                    out float3 yieldDestination))
                return false;

            Entity request = ecb.CreateEntity();
            ecb.AddComponent(request, new MoveOrderRequest
            {
                Unit = unitEntities[blockerIndex],
                Target = yieldDestination,
                StopDistance = math.max(0.1f, blockerRadiusResolved * 0.35f),
                RepathCount = 0,
            });

            var blockerYield = yieldLookup[unitEntities[blockerIndex]];
            blockerYield.Cooldown = YieldCooldownTime;
            yieldLookup[unitEntities[blockerIndex]] = blockerYield;
            return true;
        }

        public static bool TryFindYieldDestination(
            int blockerIndex,
            float3 blockerPosition,
            float blockerRadius,
            float3 requesterPosition,
            float requesterRadius,
            float3 direction,
            NativeArray<Entity> unitEntities,
            NativeArray<LocalTransform> unitTransforms,
            NativeArray<UnitFootprint> unitFootprints,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            out float3 destination)
        {
            destination = float3.zero;
            float3 right = new float3(direction.z, 0f, -direction.x);
            float requesterSide = math.dot(blockerPosition - requesterPosition, right);
            float firstSide = requesterSide >= 0f ? 1f : -1f;
            float sideStep = math.max(1f, (requesterRadius + blockerRadius) * YieldSideStepScale);

            for (int attempt = 0; attempt < 4; attempt++)
            {
                float side = attempt % 2 == 0 ? firstSide : -firstSide;
                float distanceScale = attempt < 2 ? 1f : 1.6f;
                float3 candidate = blockerPosition + right * (side * sideStep * distanceScale);
                candidate.y = blockerPosition.y;

                if (!IsYieldDestinationAvailable(
                        blockerIndex,
                        candidate,
                        blockerRadius,
                        unitTransforms,
                        unitFootprints,
                        obstacleTransforms,
                        obstacleFootprints))
                    continue;

                destination = candidate;
                return true;
            }

            return false;
        }

        public static bool IsYieldDestinationAvailable(
            int blockerIndex,
            float3 candidate,
            float blockerRadius,
            NativeArray<LocalTransform> unitTransforms,
            NativeArray<UnitFootprint> unitFootprints,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints)
        {
            for (int i = 0; i < unitTransforms.Length; i++)
            {
                if (i == blockerIndex)
                    continue;

                float otherRadius = math.max(0.01f, unitFootprints[i].Radius);
                float minDistance = blockerRadius + otherRadius + YieldBlockerPadding;
                if (HorizontalDistanceSq(candidate, unitTransforms[i].Position) < minDistance * minDistance)
                    return false;
            }

            for (int i = 0; i < obstacleFootprints.Length; i++)
            {
                float obstacleRadius = GetEffectiveObstacleRadius(obstacleFootprints[i]);
                float minDistance = blockerRadius + obstacleRadius + math.max(0f, obstacleFootprints[i].ExtraPadding);
                if (HorizontalDistanceSq(candidate, obstacleTransforms[i].Position) < minDistance * minDistance)
                    return false;
            }

            return true;
        }

        public static void SetRestActivity(ref UnitActivityState activity, float deltaTime)
        {
            if (activity.Value == UnitActivityKind.Working ||
                activity.Value == UnitActivityKind.Anchored)
            {
                activity.TimeInState += deltaTime;
                return;
            }

            SetActivity(ref activity, UnitActivityKind.Settled, deltaTime);
        }

        public static void SetActivity(ref UnitActivityState activity, UnitActivityKind value, float deltaTime)
        {
            if (activity.Value != value)
            {
                activity.Value = value;
                activity.TimeInState = 0f;
                return;
            }

            activity.TimeInState += deltaTime;
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

        public static void UpdateDetour(
            float3 position,
            float3 finalTarget,
            float unitRadius,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            ref UnitPathSteeringState steering)
        {
            if (steering.HasDetour != 0)
            {
                float detourDistanceSq = HorizontalDistanceSq(position, steering.DetourPosition);
                if (detourDistanceSq <= DetourArrivalDistance * DetourArrivalDistance)
                {
                    ClearSteering(ref steering);
                    return;
                }

                if (steering.DetourRefreshTime > 0f)
                    return;

                steering.DetourRefreshTime = DetourRefreshInterval;
                if (IsSegmentClear(position, finalTarget, unitRadius, obstacleTransforms, obstacleFootprints))
                    ClearSteering(ref steering);

                return;
            }

            if (steering.DetourRefreshTime > 0f)
                return;

            steering.DetourRefreshTime = DetourRefreshInterval;
            if (!TryFindBlockingObstacle(
                    position,
                    finalTarget,
                    unitRadius,
                    obstacleTransforms,
                    obstacleFootprints,
                    out int obstacleIndex))
                return;

            if (TryFindDetour(
                    position,
                    finalTarget,
                    unitRadius,
                    obstacleIndex,
                    obstacleTransforms,
                    obstacleFootprints,
                    out float3 detour))
            {
                steering.DetourPosition = detour;
                steering.DetourRefreshTime = DetourRefreshInterval;
                steering.HasDetour = 1;
            }
        }

        public static bool TryFindBlockingObstacle(
            float3 start,
            float3 end,
            float unitRadius,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            out int obstacleIndex)
        {
            obstacleIndex = -1;
            float bestT = float.MaxValue;

            for (int i = 0; i < obstacleFootprints.Length; i++)
            {
                float clearance = unitRadius + GetEffectiveObstacleRadius(obstacleFootprints[i]) +
                                  math.max(0f, obstacleFootprints[i].ExtraPadding);
                if (!SegmentIntersectsCircle(
                        start,
                        end,
                        obstacleTransforms[i].Position,
                        clearance,
                        out float t))
                    continue;

                if (t >= bestT)
                    continue;

                bestT = t;
                obstacleIndex = i;
            }

            return obstacleIndex >= 0;
        }

        public static bool TryFindDetour(
            float3 start,
            float3 end,
            float unitRadius,
            int obstacleIndex,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            out float3 detour)
        {
            detour = float3.zero;

            float3 toTarget = end - start;
            toTarget.y = 0f;
            float distanceSq = math.lengthsq(toTarget);
            if (distanceSq <= 0.0001f)
                return false;

            float distance = math.sqrt(distanceSq);
            float3 forward = toTarget / distance;
            float3 right = new float3(forward.z, 0f, -forward.x);

            float3 obstaclePosition = obstacleTransforms[obstacleIndex].Position;
            float clearance = unitRadius + GetEffectiveObstacleRadius(obstacleFootprints[obstacleIndex]) +
                              math.max(0f, obstacleFootprints[obstacleIndex].ExtraPadding);
            float along = math.clamp(math.dot(obstaclePosition - start, forward), 0f, distance);
            float3 aheadPoint = start + forward * math.min(distance, along + clearance * 0.75f);
            float lateral = clearance * DetourClearanceScale;

            float3 rightCandidate = aheadPoint + right * lateral;
            float3 leftCandidate = aheadPoint - right * lateral;
            bool rightClear = IsDetourClear(start, rightCandidate, end, unitRadius, obstacleTransforms, obstacleFootprints);
            bool leftClear = IsDetourClear(start, leftCandidate, end, unitRadius, obstacleTransforms, obstacleFootprints);

            if (!rightClear && !leftClear)
                return false;

            if (rightClear && !leftClear)
            {
                detour = rightCandidate;
                return true;
            }

            if (leftClear && !rightClear)
            {
                detour = leftCandidate;
                return true;
            }

            float rightCost = HorizontalDistance(start, rightCandidate) + HorizontalDistance(rightCandidate, end);
            float leftCost = HorizontalDistance(start, leftCandidate) + HorizontalDistance(leftCandidate, end);
            detour = rightCost <= leftCost ? rightCandidate : leftCandidate;
            return true;
        }

        public static bool IsDetourClear(
            float3 start,
            float3 detour,
            float3 end,
            float unitRadius,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints)
        {
            return IsSegmentClear(start, detour, unitRadius, obstacleTransforms, obstacleFootprints) &&
                   IsSegmentClear(detour, end, unitRadius, obstacleTransforms, obstacleFootprints);
        }

        public static bool IsSegmentClear(
            float3 start,
            float3 end,
            float unitRadius,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints)
        {
            return !TryFindBlockingObstacle(start, end, unitRadius, obstacleTransforms, obstacleFootprints, out _);
        }

        public static bool SegmentIntersectsCircle(float3 start, float3 end, float3 center, float radius, out float t)
        {
            float3 segment = end - start;
            segment.y = 0f;
            float lengthSq = math.lengthsq(segment);
            if (lengthSq <= 0.0001f)
            {
                t = 0f;
                return false;
            }

            float3 toCenter = center - start;
            toCenter.y = 0f;
            t = math.saturate(math.dot(toCenter, segment) / lengthSq);
            float3 closest = start + segment * t;
            return HorizontalDistanceSq(closest, center) < radius * radius;
        }

        public static float GetEffectiveObstacleRadius(ObstacleFootprint obstacle)
        {
            float sizeRadius = math.length(math.max(obstacle.Size, new float2(0.01f))) * 0.5f;
            return math.max(math.max(0.01f, obstacle.Radius), sizeRadius);
        }

        public static float HorizontalDistance(float3 a, float3 b)
        {
            return math.sqrt(HorizontalDistanceSq(a, b));
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
    [UpdateAfter(typeof(UnitMovementSystem))]
    public partial struct UnitObstacleAvoidanceSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitFootprint>();
            state.RequireForUpdate<ObstacleFootprint>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var unitQuery = SystemAPI.QueryBuilder()
                .WithAll<UnitTag, UnitFootprint, UnitObstacleAvoidanceOffset, LocalTransform>()
                .Build();
            var obstacleQuery = SystemAPI.QueryBuilder()
                .WithAll<ObstacleFootprint, LocalTransform>()
                .Build();

            int unitCount = unitQuery.CalculateEntityCount();
            int obstacleCount = obstacleQuery.CalculateEntityCount();
            if (unitCount == 0 || obstacleCount == 0)
                return;

            var units = unitQuery.ToEntityArray(Allocator.TempJob);
            var unitTransforms = unitQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob);
            var unitFootprints = unitQuery.ToComponentDataArray<UnitFootprint>(Allocator.TempJob);
            var obstacleTransforms = obstacleQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob);
            var obstacleFootprints = obstacleQuery.ToComponentDataArray<ObstacleFootprint>(Allocator.TempJob);
            var offsets = new NativeArray<float3>(unitCount, Allocator.TempJob);

            var obstacleAvoidanceJob = new UnitObstacleAvoidanceOffsetsJob
            {
                UnitTransforms = unitTransforms,
                UnitFootprints = unitFootprints,
                ObstacleTransforms = obstacleTransforms,
                ObstacleFootprints = obstacleFootprints,
                Offsets = offsets,
            };
            var offsetLookup = SystemAPI.GetComponentLookup<UnitObstacleAvoidanceOffset>(false);
            float maxPush = UnitObstacleAvoidanceUtility.MaxPushPerTick * math.max(1f, deltaTime * 60f);
            var writeJob = new UnitObstacleAvoidanceOffsetWriteJob
            {
                Entities = units,
                Offsets = offsets,
                ObstacleOffsets = offsetLookup,
                MaxPush = maxPush,
            };

            JobHandle obstacleHandle = obstacleAvoidanceJob.Schedule(unitCount, 32, state.Dependency);
            JobHandle writeHandle = writeJob.Schedule(unitCount, 32, obstacleHandle);
            writeHandle = offsets.Dispose(writeHandle);
            writeHandle = obstacleFootprints.Dispose(writeHandle);
            writeHandle = obstacleTransforms.Dispose(writeHandle);
            writeHandle = unitFootprints.Dispose(writeHandle);
            writeHandle = unitTransforms.Dispose(writeHandle);
            writeHandle = units.Dispose(writeHandle);
            state.Dependency = writeHandle;
        }
    }

    static class UnitObstacleAvoidanceUtility
    {
        public const float MaxPushPerTick = 0.35f;

        public static float3 StablePairDirection(int unitIndex, int obstacleIndex)
        {
            uint hash = math.hash(new int2(unitIndex, obstacleIndex));
            float angle = (hash & 0xffff) / 65535f * math.PI * 2f;
            return new float3(math.cos(angle), 0f, math.sin(angle));
        }
    }

    [BurstCompile]
    struct UnitObstacleAvoidanceOffsetsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<LocalTransform> UnitTransforms;
        [ReadOnly] public NativeArray<UnitFootprint> UnitFootprints;
        [ReadOnly] public NativeArray<LocalTransform> ObstacleTransforms;
        [ReadOnly] public NativeArray<ObstacleFootprint> ObstacleFootprints;
        public NativeArray<float3> Offsets;

        public void Execute(int unitIndex)
        {
            float3 offset = float3.zero;
            float unitRadius = math.max(0.01f, UnitFootprints[unitIndex].Radius);

            for (int obstacleIndex = 0; obstacleIndex < ObstacleFootprints.Length; obstacleIndex++)
            {
                float obstacleRadius = UnitMovementUtility.GetEffectiveObstacleRadius(ObstacleFootprints[obstacleIndex]);
                float minDistance = unitRadius + obstacleRadius + math.max(0f, ObstacleFootprints[obstacleIndex].ExtraPadding);

                float3 delta = UnitTransforms[unitIndex].Position - ObstacleTransforms[obstacleIndex].Position;
                delta.y = 0f;
                float distanceSq = math.lengthsq(delta);

                if (distanceSq >= minDistance * minDistance)
                    continue;

                float distance = math.sqrt(distanceSq);
                float3 direction = distance > 0.0001f
                    ? delta / distance
                    : UnitObstacleAvoidanceUtility.StablePairDirection(unitIndex, obstacleIndex);

                offset += direction * (minDistance - distance);
            }

            Offsets[unitIndex] = offset;
        }
    }

    [BurstCompile]
    struct UnitObstacleAvoidanceOffsetWriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public NativeArray<float3> Offsets;
        [NativeDisableParallelForRestriction] public ComponentLookup<UnitObstacleAvoidanceOffset> ObstacleOffsets;
        public float MaxPush;

        public void Execute(int index)
        {
            Entity entity = Entities[index];
            var offset = new UnitObstacleAvoidanceOffset
            {
                Value = UnitAvoidanceOffsetUtility.ClampHorizontalOffset(Offsets[index], MaxPush),
            };
            ObstacleOffsets[entity] = offset;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitObstacleAvoidanceSystem))]
    public partial struct UnitSeparationSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitFootprint>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var query = SystemAPI.QueryBuilder()
                .WithAll<UnitTag, UnitFootprint, UnitMoveSpeed, UnitMotionState, UnitActivityState, UnitAvoidancePriority, UnitSeparationOffset>()
                .WithAll<LocalTransform>()
                .Build();

            int count = query.CalculateEntityCount();
            if (count <= 1)
                return;

            var entities = query.ToEntityArray(Allocator.TempJob);
            var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.TempJob);
            var footprints = query.ToComponentDataArray<UnitFootprint>(Allocator.TempJob);
            var speeds = query.ToComponentDataArray<UnitMoveSpeed>(Allocator.TempJob);
            var motions = query.ToComponentDataArray<UnitMotionState>(Allocator.TempJob);
            var activities = query.ToComponentDataArray<UnitActivityState>(Allocator.TempJob);
            var priorities = query.ToComponentDataArray<UnitAvoidancePriority>(Allocator.TempJob);
            var offsets = new NativeArray<float3>(count, Allocator.TempJob);

            float cellSize = UnitSeparationUtility.CalculateCellSize(footprints);
            var spatialHash = new NativeParallelMultiHashMap<int2, int>(count, Allocator.TempJob);

            for (int i = 0; i < count; i++)
                spatialHash.Add(UnitSeparationUtility.CellOf(transforms[i].Position, cellSize), i);

            var separationJob = new UnitSeparationOffsetsJob
            {
                Transforms = transforms,
                Footprints = footprints,
                Speeds = speeds,
                Motions = motions,
                Activities = activities,
                Priorities = priorities,
                SpatialHash = spatialHash,
                Offsets = offsets,
                CellSize = cellSize,
            };
            var offsetLookup = SystemAPI.GetComponentLookup<UnitSeparationOffset>(false);
            float maxPush = UnitSeparationUtility.MaxPushPerTick * math.max(1f, deltaTime * 60f);
            var writeJob = new UnitSeparationOffsetWriteJob
            {
                Entities = entities,
                Offsets = offsets,
                SeparationOffsets = offsetLookup,
                MaxPush = maxPush,
            };

            JobHandle separationHandle = separationJob.Schedule(count, 32, state.Dependency);
            JobHandle writeHandle = writeJob.Schedule(count, 32, separationHandle);
            writeHandle = offsets.Dispose(writeHandle);
            writeHandle = spatialHash.Dispose(writeHandle);
            writeHandle = priorities.Dispose(writeHandle);
            writeHandle = activities.Dispose(writeHandle);
            writeHandle = motions.Dispose(writeHandle);
            writeHandle = speeds.Dispose(writeHandle);
            writeHandle = footprints.Dispose(writeHandle);
            writeHandle = transforms.Dispose(writeHandle);
            writeHandle = entities.Dispose(writeHandle);
            state.Dependency = writeHandle;
        }
    }

    [BurstCompile]
    struct UnitSeparationOffsetWriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public NativeArray<float3> Offsets;
        [NativeDisableParallelForRestriction] public ComponentLookup<UnitSeparationOffset> SeparationOffsets;
        public float MaxPush;

        public void Execute(int index)
        {
            Entity entity = Entities[index];
            var offset = new UnitSeparationOffset
            {
                Value = UnitAvoidanceOffsetUtility.ClampHorizontalOffset(Offsets[index], MaxPush),
            };
            SeparationOffsets[entity] = offset;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitSeparationSystem))]
    public partial struct UnitAvoidanceOffsetApplySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitObstacleAvoidanceOffset>();
            state.RequireForUpdate<UnitSeparationOffset>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new UnitAvoidanceOffsetApplyJob();
            state.Dependency = job.ScheduleParallel(state.Dependency);
            state.Dependency.Complete();
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct UnitAvoidanceOffsetApplyJob : IJobEntity
    {
        public void Execute(
            ref LocalTransform transform,
            ref UnitObstacleAvoidanceOffset obstacleOffset,
            ref UnitSeparationOffset separationOffset)
        {
            float3 offset = obstacleOffset.Value + separationOffset.Value;
            offset.y = 0f;

            if (math.lengthsq(offset) > 0.000001f)
                transform.Position += offset;

            obstacleOffset.Value = float3.zero;
            separationOffset.Value = float3.zero;
        }
    }

    static class UnitAvoidanceOffsetUtility
    {
        public static float3 ClampHorizontalOffset(float3 offset, float maxPush)
        {
            offset.y = 0f;

            float lengthSq = math.lengthsq(offset);
            if (lengthSq <= 0.000001f)
                return float3.zero;

            float length = math.sqrt(lengthSq);
            if (length > maxPush)
                offset *= maxPush / length;

            return offset;
        }
    }

    [BurstCompile]
    struct UnitSeparationOffsetsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<LocalTransform> Transforms;
        [ReadOnly] public NativeArray<UnitFootprint> Footprints;
        [ReadOnly] public NativeArray<UnitMoveSpeed> Speeds;
        [ReadOnly] public NativeArray<UnitMotionState> Motions;
        [ReadOnly] public NativeArray<UnitActivityState> Activities;
        [ReadOnly] public NativeArray<UnitAvoidancePriority> Priorities;
        [ReadOnly] public NativeParallelMultiHashMap<int2, int> SpatialHash;
        public NativeArray<float3> Offsets;
        public float CellSize;

        public void Execute(int unitIndex)
        {
            float3 offset = float3.zero;
            int2 originCell = UnitSeparationUtility.CellOf(Transforms[unitIndex].Position, CellSize);

            for (int z = -1; z <= 1; z++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    int2 cell = originCell + new int2(x, z);

                    if (!SpatialHash.TryGetFirstValue(cell, out int otherIndex, out var iterator))
                        continue;

                    do
                    {
                        if (otherIndex == unitIndex)
                            continue;

                        offset += UnitSeparationUtility.CalculateSeparationOffsetForUnit(
                            unitIndex,
                            otherIndex,
                            Transforms,
                            Footprints,
                            Speeds,
                            Motions,
                            Activities,
                            Priorities);
                    }
                    while (SpatialHash.TryGetNextValue(out otherIndex, ref iterator));
                }
            }

            Offsets[unitIndex] = offset;
        }
    }

    static class UnitSeparationUtility
    {
        public const float MaxPushPerTick = 0.2f;
        public const float MinimumSpatialCellSize = 0.5f;

        public static float3 CalculateSeparationOffsetForUnit(
            int unitIndex,
            int otherIndex,
            NativeArray<LocalTransform> transforms,
            NativeArray<UnitFootprint> footprints,
            NativeArray<UnitMoveSpeed> speeds,
            NativeArray<UnitMotionState> motions,
            NativeArray<UnitActivityState> activities,
            NativeArray<UnitAvoidancePriority> priorities)
        {
            float radius = math.max(0.01f, footprints[unitIndex].Radius);
            float otherRadius = math.max(0.01f, footprints[otherIndex].Radius);
            float minDistance = radius + otherRadius;
            float softAvoidanceDistance = minDistance + math.max(
                math.max(0f, priorities[unitIndex].SoftAvoidanceDistance),
                math.max(0f, priorities[otherIndex].SoftAvoidanceDistance));

            float3 delta = transforms[unitIndex].Position - transforms[otherIndex].Position;
            delta.y = 0f;
            float distanceSq = math.lengthsq(delta);

            if (distanceSq >= softAvoidanceDistance * softAvoidanceDistance)
                return float3.zero;

            float distance = math.sqrt(distanceSq);
            float3 direction = distance > 0.0001f
                ? delta / distance
                : StablePairDirection(unitIndex, otherIndex);

            float penetration = minDistance - distance;
            if (penetration <= 0f && motions[unitIndex].IsMoving == 0 && motions[otherIndex].IsMoving == 0)
                return float3.zero;

            float weight = EffectivePushWeight(
                footprints[unitIndex],
                speeds[unitIndex],
                motions[unitIndex],
                activities[unitIndex],
                priorities[unitIndex]);
            float otherWeight = EffectivePushWeight(
                footprints[otherIndex],
                speeds[otherIndex],
                motions[otherIndex],
                activities[otherIndex],
                priorities[otherIndex]);
            float totalWeight = weight + otherWeight;

            if (totalWeight <= 0.0001f)
                return float3.zero;

            float pushAmount = penetration > 0f
                ? penetration
                : CalculateSoftPushAmount(distance, minDistance, softAvoidanceDistance);
            return direction * pushAmount * (weight / totalWeight);
        }

        public static void AddSeparationOffset(
            int a,
            int b,
            NativeArray<LocalTransform> transforms,
            NativeArray<UnitFootprint> footprints,
            NativeArray<UnitMoveSpeed> speeds,
            NativeArray<UnitMotionState> motions,
            NativeArray<UnitActivityState> activities,
            NativeArray<UnitAvoidancePriority> priorities,
            NativeArray<float3> offsets)
        {
            float radiusA = math.max(0.01f, footprints[a].Radius);
            float radiusB = math.max(0.01f, footprints[b].Radius);
            float minDistance = radiusA + radiusB;
            float softAvoidanceDistance = minDistance + math.max(
                math.max(0f, priorities[a].SoftAvoidanceDistance),
                math.max(0f, priorities[b].SoftAvoidanceDistance));

            float3 delta = transforms[a].Position - transforms[b].Position;
            delta.y = 0f;
            float distanceSq = math.lengthsq(delta);

            if (distanceSq >= softAvoidanceDistance * softAvoidanceDistance)
                return;

            float distance = math.sqrt(distanceSq);
            float3 direction = distance > 0.0001f
                ? delta / distance
                : StablePairDirection(a, b);

            float penetration = minDistance - distance;
            if (penetration <= 0f && motions[a].IsMoving == 0 && motions[b].IsMoving == 0)
                return;

            float weightA = EffectivePushWeight(footprints[a], speeds[a], motions[a], activities[a], priorities[a]);
            float weightB = EffectivePushWeight(footprints[b], speeds[b], motions[b], activities[b], priorities[b]);
            float totalWeight = weightA + weightB;

            if (totalWeight <= 0.0001f)
                return;

            float pushAmount = penetration > 0f
                ? penetration
                : CalculateSoftPushAmount(distance, minDistance, softAvoidanceDistance);
            float3 push = direction * pushAmount;
            offsets[a] += push * (weightA / totalWeight);
            offsets[b] -= push * (weightB / totalWeight);
        }

        static float CalculateSoftPushAmount(float distance, float minDistance, float softAvoidanceDistance)
        {
            float softRange = math.max(0.0001f, softAvoidanceDistance - minDistance);
            float t = math.saturate((softAvoidanceDistance - distance) / softRange);
            return t * t * math.max(0.05f, minDistance * 0.08f);
        }

        static float EffectivePushWeight(
            UnitFootprint footprint,
            UnitMoveSpeed speed,
            UnitMotionState motion,
            UnitActivityState activity,
            UnitAvoidancePriority priority)
        {
            float weight = math.max(0f, footprint.SeparationWeight);

            switch (activity.Value)
            {
                case UnitActivityKind.Anchored:
                    weight *= math.saturate(footprint.AnchoredPushScale);
                    break;
                case UnitActivityKind.Working:
                    weight *= math.saturate(footprint.WorkingPushScale);
                    break;
                case UnitActivityKind.Settled:
                    weight *= math.saturate(footprint.SettledPushScale);
                    break;
            }

            if (activity.Value == UnitActivityKind.Moving && motion.IsMoving == 0)
                weight *= math.saturate(footprint.SettledPushScale);

            float effectivePriority = CalculateEffectivePriority(footprint, speed, activity, priority);
            return weight * math.max(0f, priority.YieldStrength) / effectivePriority;
        }

        static float CalculateEffectivePriority(
            UnitFootprint footprint,
            UnitMoveSpeed speed,
            UnitActivityState activity,
            UnitAvoidancePriority priority)
        {
            float sizePriority = math.max(0.01f, footprint.Radius) * 0.5f;
            float slowPriority = 0.75f / math.max(0.5f, speed.MetersPerSecond);
            float activityPriority = 0f;

            switch (activity.Value)
            {
                case UnitActivityKind.Working:
                    activityPriority = 4f;
                    break;
                case UnitActivityKind.Anchored:
                    activityPriority = 100f;
                    break;
                case UnitActivityKind.Settled:
                    activityPriority = 1.5f;
                    break;
            }

            return math.max(0.05f, priority.BasePriority + sizePriority + slowPriority + activityPriority);
        }

        public static float CalculateCellSize(NativeArray<UnitFootprint> footprints)
        {
            float maxRadius = 0f;

            for (int i = 0; i < footprints.Length; i++)
                maxRadius = math.max(maxRadius, math.max(0.01f, footprints[i].Radius));

            return math.max(MinimumSpatialCellSize, maxRadius * 2f);
        }

        public static int2 CellOf(float3 position, float cellSize)
        {
            float inverseCellSize = 1f / math.max(MinimumSpatialCellSize, cellSize);
            return new int2(
                (int)math.floor(position.x * inverseCellSize),
                (int)math.floor(position.z * inverseCellSize));
        }

        static float3 StablePairDirection(int a, int b)
        {
            uint hash = math.hash(new int2(a, b));
            float angle = (hash & 0xffff) / 65535f * math.PI * 2f;
            return new float3(math.cos(angle), 0f, math.sin(angle));
        }
    }
}
