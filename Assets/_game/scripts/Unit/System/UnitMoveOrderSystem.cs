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
    [UpdateBefore(typeof(UnitDirectMoveOrderSystem))]
    public partial struct SelectedUnitMoveOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SelectedUnitMoveOrderRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = SystemAPI
                .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);
            JobHandle dependency = state.Dependency;

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<SelectedUnitMoveOrderRequest>>()
                         .WithEntityAccess())
            {
                var job = new SelectedUnitMoveOrderJob
                {
                    Request = request.ValueRO,
                };

                dependency = job.ScheduleParallel(dependency);
                ecb.DestroyEntity(requestEntity);
            }

            state.Dependency = dependency;
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag), typeof(SelectedUnit))]
    partial struct SelectedUnitMoveOrderJob : IJobEntity
    {
        public SelectedUnitMoveOrderRequest Request;

        public void Execute(
            [EntityIndexInQuery] int entityIndex,
            ref UnitMoveTarget moveTarget,
            ref UnitMotionState motion,
            ref UnitCommandState commandState,
            ref CombatAttackTarget attackTarget,
            ref CombatEngagementDecision engagementDecision,
            DynamicBuffer<UnitPathWaypoint> waypoints)
        {
            int unitCount = math.max(1, Request.UnitCount);
            int columns = math.max(1, (int)math.ceil(math.sqrt(unitCount)));
            int rows = math.max(1, (int)math.ceil(unitCount / (float)columns));
            int x = entityIndex % columns;
            int z = entityIndex / columns;

            float3 forward = Request.FormationForward;
            forward.y = 0f;
            forward = math.lengthsq(forward) <= 0.0001f
                ? new float3(0f, 0f, 1f)
                : math.normalize(forward);
            float3 right = math.cross(math.up(), forward);
            right = math.lengthsq(right) <= 0.0001f
                ? new float3(1f, 0f, 0f)
                : math.normalize(right);

            float spacing = math.max(0.01f, Request.FormationSpacing);
            float offsetX = (x - (columns - 1) * 0.5f) * spacing;
            float offsetZ = (z - (rows - 1) * 0.5f) * spacing;
            float3 destination = Request.Target + right * offsetX + forward * offsetZ;

            moveTarget = new UnitMoveTarget
            {
                Position = destination,
                StopDistance = Request.StopDistance,
                RepathCount = 0,
                PathStatus = UnitPathStatus.Direct,
                HasTarget = 1,
                RepathRequested = 0,
            };

            motion.LastTargetDistance = float.MaxValue;
            motion.StuckTime = 0f;

            if (Request.CommandKind != UnitCommandKind.None)
            {
                commandState = new UnitCommandState
                {
                    Kind = Request.CommandKind,
                    TargetEntity = Entity.Null,
                    TargetPosition = destination,
                    HasTargetEntity = 0,
                };
            }

            attackTarget = new CombatAttackTarget
            {
                Target = Entity.Null,
                ApproachRefreshTime = 0f,
                HasTarget = 0,
            };
            engagementDecision = new CombatEngagementDecision
            {
                PreferredRange = 0f,
                TargetPosition = float3.zero,
                ShouldApproach = 0,
                HasUsableWeapon = 0,
            };
            waypoints.Clear();
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AttackOrderSystem))]
    public partial struct SelectedUnitAttackOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SelectedUnitAttackOrderRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = SystemAPI
                .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);
            JobHandle dependency = state.Dependency;

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<SelectedUnitAttackOrderRequest>>()
                         .WithEntityAccess())
            {
                var job = new SelectedUnitAttackOrderJob
                {
                    Target = request.ValueRO.Target,
                    CommandKind = request.ValueRO.CommandKind == UnitCommandKind.None
                        ? UnitCommandKind.ForceAttack
                        : request.ValueRO.CommandKind,
                };

                dependency = job.ScheduleParallel(dependency);
                ecb.DestroyEntity(requestEntity);
            }

            state.Dependency = dependency;
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag), typeof(SelectedUnit))]
    partial struct SelectedUnitAttackOrderJob : IJobEntity
    {
        public Entity Target;
        public UnitCommandKind CommandKind;

        public void Execute(
            ref CombatAttackTarget attackTarget,
            ref UnitCommandState commandState)
        {
            attackTarget = new CombatAttackTarget
            {
                Target = Target,
                ApproachRefreshTime = 0f,
                HasTarget = Target == Entity.Null ? (byte)0 : (byte)1,
            };
            commandState = new UnitCommandState
            {
                Kind = CommandKind,
                TargetEntity = Target,
                TargetPosition = float3.zero,
                HasTargetEntity = Target == Entity.Null ? (byte)0 : (byte)1,
            };
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(UnitDirectMoveOrderSystem))]
    public partial struct UnitGroupMoveOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitGroupMoveOrderRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = SystemAPI
                .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);
            JobHandle dependency = state.Dependency;

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<UnitGroupMoveOrderRequest>>()
                         .WithEntityAccess())
            {
                var job = new UnitGroupMoveOrderJob
                {
                    Request = request.ValueRO,
                };

                dependency = job.ScheduleParallel(dependency);
                ecb.DestroyEntity(requestEntity);
            }

            state.Dependency = dependency;
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct UnitGroupMoveOrderJob : IJobEntity
    {
        public UnitGroupMoveOrderRequest Request;

        public void Execute(
            [EntityIndexInQuery] int entityIndex,
            in TeamInfoData team,
            in UnitCommandGroupMember group,
            ref UnitMoveTarget moveTarget,
            ref UnitMotionState motion,
            ref UnitCommandState commandState,
            ref CombatAttackTarget attackTarget,
            ref CombatEngagementDecision engagementDecision,
            DynamicBuffer<UnitPathWaypoint> waypoints)
        {
            if (team.LocalID != Request.LocalId ||
                group.GroupId != Request.GroupId)
                return;

            float3 destination = ResolveFormationDestination(entityIndex, Request);
            moveTarget = new UnitMoveTarget
            {
                Position = destination,
                StopDistance = Request.StopDistance,
                RepathCount = 0,
                PathStatus = UnitPathStatus.Direct,
                HasTarget = 1,
                RepathRequested = 0,
            };
            motion.LastTargetDistance = float.MaxValue;
            motion.StuckTime = 0f;

            if (Request.CommandKind != UnitCommandKind.None)
            {
                commandState = new UnitCommandState
                {
                    Kind = Request.CommandKind,
                    TargetEntity = Entity.Null,
                    TargetPosition = destination,
                    HasTargetEntity = 0,
                };
            }

            attackTarget = new CombatAttackTarget
            {
                Target = Entity.Null,
                ApproachRefreshTime = 0f,
                HasTarget = 0,
            };
            engagementDecision = new CombatEngagementDecision
            {
                PreferredRange = 0f,
                TargetPosition = float3.zero,
                ShouldApproach = 0,
                HasUsableWeapon = 0,
            };
            waypoints.Clear();
        }

        static float3 ResolveFormationDestination(int entityIndex, UnitGroupMoveOrderRequest request)
        {
            int unitCount = math.max(1, request.UnitCount);
            int columns = math.max(1, (int)math.ceil(math.sqrt(unitCount)));
            int rows = math.max(1, (int)math.ceil(unitCount / (float)columns));
            int x = entityIndex % columns;
            int z = entityIndex / columns;

            float3 forward = request.FormationForward;
            forward.y = 0f;
            forward = math.lengthsq(forward) <= 0.0001f
                ? new float3(0f, 0f, 1f)
                : math.normalize(forward);

            float3 right = math.cross(math.up(), forward);
            right = math.lengthsq(right) <= 0.0001f
                ? new float3(1f, 0f, 0f)
                : math.normalize(right);

            float spacing = math.max(0.01f, request.FormationSpacing);
            float offsetX = (x - (columns - 1) * 0.5f) * spacing;
            float offsetZ = (z - (rows - 1) * 0.5f) * spacing;
            return request.Target + right * offsetX + forward * offsetZ;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(AttackOrderSystem))]
    public partial struct UnitGroupAttackOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitGroupAttackOrderRequest>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = SystemAPI
                .GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);
            JobHandle dependency = state.Dependency;

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<UnitGroupAttackOrderRequest>>()
                         .WithEntityAccess())
            {
                var job = new UnitGroupAttackOrderJob
                {
                    LocalId = request.ValueRO.LocalId,
                    GroupId = request.ValueRO.GroupId,
                    Target = request.ValueRO.Target,
                    CommandKind = request.ValueRO.CommandKind == UnitCommandKind.None
                        ? UnitCommandKind.ForceAttack
                        : request.ValueRO.CommandKind,
                };

                dependency = job.ScheduleParallel(dependency);
                ecb.DestroyEntity(requestEntity);
            }

            state.Dependency = dependency;
        }
    }

    [BurstCompile]
    [WithAll(typeof(UnitTag))]
    partial struct UnitGroupAttackOrderJob : IJobEntity
    {
        public int LocalId;
        public int GroupId;
        public Entity Target;
        public UnitCommandKind CommandKind;

        public void Execute(
            in TeamInfoData team,
            in UnitCommandGroupMember group,
            ref CombatAttackTarget attackTarget,
            ref UnitCommandState commandState)
        {
            if (team.LocalID != LocalId ||
                group.GroupId != GroupId)
                return;

            attackTarget = new CombatAttackTarget
            {
                Target = Target,
                ApproachRefreshTime = 0f,
                HasTarget = Target == Entity.Null ? (byte)0 : (byte)1,
            };
            commandState = new UnitCommandState
            {
                Kind = CommandKind,
                TargetEntity = Target,
                TargetPosition = float3.zero,
                HasTargetEntity = Target == Entity.Null ? (byte)0 : (byte)1,
            };
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(UnitMoveOrderSystem))]
    public partial struct UnitDirectMoveOrderSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitDirectMoveOrderBatch>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.TempJob);

            foreach (var (orders, batchEntity) in
                     SystemAPI.Query<DynamicBuffer<UnitDirectMoveOrderElement>>()
                         .WithAll<UnitDirectMoveOrderBatch>()
                         .WithEntityAccess())
            {
                var orderArray = orders.ToNativeArray(Allocator.TempJob);
                var job = new UnitDirectMoveOrderJob
                {
                    Orders = orderArray,
                    MoveTargets = SystemAPI.GetComponentLookup<UnitMoveTarget>(false),
                    MotionStates = SystemAPI.GetComponentLookup<UnitMotionState>(false),
                    CommandStates = SystemAPI.GetComponentLookup<UnitCommandState>(false),
                    AttackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(false),
                    EngagementDecisions = SystemAPI.GetComponentLookup<CombatEngagementDecision>(false),
                    Waypoints = SystemAPI.GetBufferLookup<UnitPathWaypoint>(false),
                    Ecb = ecb.AsParallelWriter(),
                };

                JobHandle handle = job.Schedule(orderArray.Length, 64, state.Dependency);
                handle.Complete();
                orderArray.Dispose();
                state.Dependency = default;
                ecb.DestroyEntity(batchEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }

    [BurstCompile]
    struct UnitDirectMoveOrderJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<UnitDirectMoveOrderElement> Orders;
        [NativeDisableParallelForRestriction] public ComponentLookup<UnitMoveTarget> MoveTargets;
        [NativeDisableParallelForRestriction] public ComponentLookup<UnitMotionState> MotionStates;
        [NativeDisableParallelForRestriction] public ComponentLookup<UnitCommandState> CommandStates;
        [NativeDisableParallelForRestriction] public ComponentLookup<CombatAttackTarget> AttackTargets;
        [NativeDisableParallelForRestriction] public ComponentLookup<CombatEngagementDecision> EngagementDecisions;
        [NativeDisableParallelForRestriction] public BufferLookup<UnitPathWaypoint> Waypoints;
        public EntityCommandBuffer.ParallelWriter Ecb;

        public void Execute(int index)
        {
            UnitDirectMoveOrderElement order = Orders[index];
            Entity unit = order.Unit;
            var target = new UnitMoveTarget
            {
                Position = order.Target,
                StopDistance = order.StopDistance,
                RepathCount = 0,
                PathStatus = UnitPathStatus.Direct,
                HasTarget = 1,
                RepathRequested = 0,
            };

            if (MoveTargets.HasComponent(unit))
                MoveTargets[unit] = target;
            else
                Ecb.AddComponent(index, unit, target);

            if (MotionStates.HasComponent(unit))
            {
                var motion = MotionStates[unit];
                motion.LastTargetDistance = float.MaxValue;
                motion.StuckTime = 0f;
                MotionStates[unit] = motion;
            }

            if (order.CommandKind != UnitCommandKind.None)
            {
                var commandState = new UnitCommandState
                {
                    Kind = order.CommandKind,
                    TargetEntity = Entity.Null,
                    TargetPosition = order.Target,
                    HasTargetEntity = 0,
                };

                if (CommandStates.HasComponent(unit))
                    CommandStates[unit] = commandState;
                else
                    Ecb.AddComponent(index, unit, commandState);
            }

            if (order.CommandKind == UnitCommandKind.ForceMove)
            {
                if (AttackTargets.HasComponent(unit))
                {
                    var attackTarget = AttackTargets[unit];
                    attackTarget.Target = Entity.Null;
                    attackTarget.ApproachRefreshTime = 0f;
                    attackTarget.HasTarget = 0;
                    AttackTargets[unit] = attackTarget;
                }

                if (EngagementDecisions.HasComponent(unit))
                {
                    EngagementDecisions[unit] = new CombatEngagementDecision
                    {
                        PreferredRange = 0f,
                        TargetPosition = float3.zero,
                        ShouldApproach = 0,
                        HasUsableWeapon = 0,
                    };
                }
            }

            if (Waypoints.HasBuffer(unit))
                Waypoints[unit].Clear();

        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct UnitMoveOrderSystem : ISystem
    {
        const int MaxPathBuildsPerFrame = 12;
        const float PathCacheRadiusStep = 0.25f;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<MoveOrderRequest>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 메인스레드 ComponentLookup 읽기/쓰기(아래 targets[unit]= 등) 전에 앞서 스케줄된
            //   이동 잡(UnitMovementArbiterJob 등 UnitMoveTarget RW)을 완료해야 한다 —
            //   미완료 접근은 InvalidOperationException(2026-07-19 유저 실측). 요청이 있는
            //   프레임에만 도는 시스템이라 동기화 비용은 국소적.
            state.CompleteDependency();

            var targets = SystemAPI.GetComponentLookup<UnitMoveTarget>(false);
            var motions = SystemAPI.GetComponentLookup<UnitMotionState>(false);
            var transforms = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var footprints = SystemAPI.GetComponentLookup<UnitFootprint>(true);
            var commandStates = SystemAPI.GetComponentLookup<UnitCommandState>(false);
            var attackTargets = SystemAPI.GetComponentLookup<CombatAttackTarget>(false);
            var engagementDecisions = SystemAPI.GetComponentLookup<CombatEngagementDecision>(false);
            var waypointBuffers = SystemAPI.GetBufferLookup<UnitPathWaypoint>(false);
            var requestPathBuffers = SystemAPI.GetBufferLookup<MoveOrderPathWaypoint>(true);
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            bool hasGrid = SystemAPI.TryGetSingleton<UnitNavigationGrid>(out var grid);
            // 이동 도메인(2026-07-19 해상): NavalUnit = 물만 항해 / 지상(기본) = 물 차단.
            //   물 마스크 미빌드(맵 미로드 등)면 구 동작(물 무시) 폴백.
            var navalLookup = SystemAPI.GetComponentLookup<NavalUnit>(true);
            UnitWaterMask waterMask = default;
            bool hasWater = hasGrid
                && SystemAPI.TryGetSingleton(out waterMask)
                && waterMask.IsUsable(grid.Size);
            var obstacleQuery = SystemAPI.QueryBuilder()
                .WithAll<ObstacleFootprint, LocalTransform>()
                .Build();
            int obstacleCount = obstacleQuery.CalculateEntityCount();
            var obstacleTransforms = obstacleCount > 0
                ? obstacleQuery.ToComponentDataArray<LocalTransform>(Allocator.Temp)
                : default;
            var obstacleFootprints = obstacleCount > 0
                ? obstacleQuery.ToComponentDataArray<ObstacleFootprint>(Allocator.Temp)
                : default;
            var pathCache = new NativeList<PathCacheEntry>(Allocator.Temp);
            var pathCachePoints = new NativeList<float3>(Allocator.Temp);
            var blockedGridOffsets = new NativeParallelHashMap<int, int>(MaxPathBuildsPerFrame, Allocator.Temp);
            var blockedGridData = new NativeList<byte>(Allocator.Temp);
            int pathBuildsThisFrame = 0;

            foreach (var (request, requestEntity) in
                     SystemAPI.Query<RefRO<MoveOrderRequest>>()
                         .WithEntityAccess())
            {
                var unit = request.ValueRO.Unit;
                if (SystemAPI.Exists(unit))
                {
                    bool pathFound = false;
                    bool reachedTarget = false;
                    bool pathAttempted = false;
                    bool naval = navalLookup.HasComponent(unit);
                    var path = new NativeList<float3>(Allocator.Temp);

                    if (TryCopyRequestPath(requestEntity, requestPathBuffers, path))
                    {
                        pathAttempted = true;
                        pathFound = true;
                        reachedTarget = true;
                    }
                    else if (request.ValueRO.SkipPathfinding == 0 &&
                        hasGrid &&
                        transforms.HasComponent(unit) &&
                        footprints.HasComponent(unit) &&
                        // 그리드 밖 출발/목표(2026-07-19): 경로 시도 없이 Direct 폴백 —
                        //   시도 후 실패(PathFailed)는 HasTarget=0 = 영구 정지가 되므로,
                        //   커버 밖은 "경로 없이 직진"이 옳다(그리드 확장 전 유조선 정지 원인).
                        UnitPathfinding.IsInsideGrid(grid, transforms[unit].Position) &&
                        UnitPathfinding.IsInsideGrid(grid, request.ValueRO.Target))
                    {
                        float unitRadius = math.max(0.01f, footprints[unit].Radius);
                        var cacheKey = BuildPathCacheKey(
                            grid,
                            transforms[unit].Position,
                            request.ValueRO.Target,
                            unitRadius,
                            naval);
                        pathAttempted = true;
                        if (TryCopyCachedPath(cacheKey, pathCache, pathCachePoints, path, out pathFound, out reachedTarget))
                        {
                        }
                        else
                        {
                            if (pathBuildsThisFrame >= MaxPathBuildsPerFrame)
                            {
                                if (request.ValueRO.CommandKind != UnitCommandKind.None)
                                    pathAttempted = false;
                                else
                                {
                                    path.Dispose();
                                    continue;
                                }
                            }
                            else
                            {
                                pathBuildsThisFrame++;
                                var blockedGrid = GetBlockedGrid(
                                    grid,
                                    unitRadius,
                                    cacheKey.RadiusStep,
                                    obstacleTransforms,
                                    obstacleFootprints,
                                    waterMask,
                                    hasWater,
                                    naval,
                                    blockedGridOffsets,
                                    blockedGridData);
                                pathFound = UnitPathfinding.TryBuildPathWithBlockedGrid(
                                    grid,
                                    transforms[unit].Position,
                                    request.ValueRO.Target,
                                    blockedGrid,
                                    path,
                                    out reachedTarget);
                                AddCachedPath(cacheKey, pathFound, reachedTarget, path, pathCache, pathCachePoints);
                            }
                        }
                    }

                    // 해상 유닛 경로 실패 가시화(2026-07-19 유조선 디버깅): PathFailed는
                    //   HasTarget=0 = 정지라서 조용히 삼키면 "배가 안 움직임"으로만 보인다.
                    if (pathAttempted && !pathFound && naval && transforms.HasComponent(unit))
                        UnityEngine.Debug.LogWarning(
                            $"[UnitPath] naval 경로 실패: {transforms[unit].Position.xz} → " +
                            $"{request.ValueRO.Target.xz} (물 마스크·그리드 커버 확인)");

                    float3 resolvedTarget = pathFound && !reachedTarget && path.Length > 0
                        ? path[path.Length - 1]
                        : request.ValueRO.Target;
                    var target = new UnitMoveTarget
                    {
                        Position = resolvedTarget,
                        StopDistance = request.ValueRO.StopDistance,
                        RepathCount = request.ValueRO.RepathCount,
                        PathStatus = ResolvePathStatus(pathAttempted, pathFound, reachedTarget),
                        HasTarget = pathAttempted && !pathFound ? (byte)0 : (byte)1,
                        RepathRequested = 0,
                    };

                    if (targets.HasComponent(unit))
                        targets[unit] = target;
                    else
                        ecb.AddComponent(unit, target);

                    if (motions.HasComponent(unit))
                    {
                        var motion = motions[unit];
                        motion.LastTargetDistance = float.MaxValue;
                        motion.StuckTime = 0f;
                        motions[unit] = motion;
                    }

                    if (request.ValueRO.CommandKind != UnitCommandKind.None)
                    {
                        var commandState = new UnitCommandState
                        {
                            Kind = request.ValueRO.CommandKind,
                            TargetEntity = Entity.Null,
                            TargetPosition = request.ValueRO.Target,
                            HasTargetEntity = 0,
                        };

                        if (commandStates.HasComponent(unit))
                            commandStates[unit] = commandState;
                        else
                            ecb.AddComponent(unit, commandState);
                    }

                    if (request.ValueRO.CommandKind == UnitCommandKind.ForceMove)
                    {
                        if (attackTargets.HasComponent(unit))
                        {
                            var attackTarget = attackTargets[unit];
                            attackTarget.Target = Entity.Null;
                            attackTarget.ApproachRefreshTime = 0f;
                            attackTarget.HasTarget = 0;
                            attackTargets[unit] = attackTarget;
                        }

                        if (engagementDecisions.HasComponent(unit))
                        {
                            engagementDecisions[unit] = new CombatEngagementDecision
                            {
                                PreferredRange = 0f,
                                TargetPosition = float3.zero,
                                ShouldApproach = 0,
                                HasUsableWeapon = 0,
                            };
                        }
                    }

                    if (waypointBuffers.HasBuffer(unit))
                    {
                        var waypoints = waypointBuffers[unit];
                        waypoints.Clear();

                        if (pathFound)
                        {
                            for (int i = 1; i < path.Length - 1; i++)
                            {
                                waypoints.Add(new UnitPathWaypoint
                                {
                                    Position = path[i],
                                });
                            }
                        }
                    }

                    path.Dispose();
                }

                ecb.DestroyEntity(requestEntity);
            }

            blockedGridData.Dispose();
            blockedGridOffsets.Dispose();
            pathCachePoints.Dispose();
            pathCache.Dispose();

            if (obstacleCount > 0)
            {
                obstacleFootprints.Dispose();
                obstacleTransforms.Dispose();
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static UnitPathStatus ResolvePathStatus(bool pathAttempted, bool pathFound, bool reachedTarget)
        {
            if (!pathAttempted)
                return UnitPathStatus.Direct;

            if (!pathFound)
                return UnitPathStatus.PathFailed;

            return reachedTarget ? UnitPathStatus.PathReady : UnitPathStatus.PathPartial;
        }

        struct PathCacheKey
        {
            public int2 StartCell;
            public int2 TargetCell;
            public int RadiusStep;
            public byte Naval;   // 이동 도메인(2026-07-19) — 도메인 다르면 경로 재사용 금지
        }

        struct PathCacheEntry
        {
            public PathCacheKey Key;
            public int PointStart;
            public int PointCount;
            public byte Found;
            public byte ReachedTarget;
        }

        static PathCacheKey BuildPathCacheKey(UnitNavigationGrid grid, float3 start, float3 target, float radius, bool naval)
        {
            return new PathCacheKey
            {
                StartCell = UnitPathfinding.WorldToCell(grid, start),
                TargetCell = UnitPathfinding.WorldToCell(grid, target),
                RadiusStep = (int)math.ceil(radius / PathCacheRadiusStep),
                Naval = naval ? (byte)1 : (byte)0,
            };
        }

        static bool TryCopyCachedPath(
            PathCacheKey key,
            NativeList<PathCacheEntry> cache,
            NativeList<float3> cachePoints,
            NativeList<float3> path,
            out bool pathFound,
            out bool reachedTarget)
        {
            pathFound = false;
            reachedTarget = false;

            for (int i = 0; i < cache.Length; i++)
            {
                PathCacheEntry entry = cache[i];
                if (!PathCacheKeyEquals(entry.Key, key))
                    continue;

                pathFound = entry.Found != 0;
                reachedTarget = entry.ReachedTarget != 0;

                for (int pointIndex = 0; pointIndex < entry.PointCount; pointIndex++)
                    path.Add(cachePoints[entry.PointStart + pointIndex]);

                return true;
            }

            return false;
        }

        static void AddCachedPath(
            PathCacheKey key,
            bool pathFound,
            bool reachedTarget,
            NativeList<float3> path,
            NativeList<PathCacheEntry> cache,
            NativeList<float3> cachePoints)
        {
            int pointStart = cachePoints.Length;
            for (int i = 0; i < path.Length; i++)
                cachePoints.Add(path[i]);

            cache.Add(new PathCacheEntry
            {
                Key = key,
                PointStart = pointStart,
                PointCount = path.Length,
                Found = pathFound ? (byte)1 : (byte)0,
                ReachedTarget = reachedTarget ? (byte)1 : (byte)0,
            });
        }

        static bool PathCacheKeyEquals(PathCacheKey a, PathCacheKey b)
        {
            return a.StartCell.x == b.StartCell.x &&
                   a.StartCell.y == b.StartCell.y &&
                   a.TargetCell.x == b.TargetCell.x &&
                   a.TargetCell.y == b.TargetCell.y &&
                   a.RadiusStep == b.RadiusStep &&
                   a.Naval == b.Naval;
        }

        static bool TryCopyRequestPath(
            Entity requestEntity,
            BufferLookup<MoveOrderPathWaypoint> requestPathBuffers,
            NativeList<float3> path)
        {
            if (!requestPathBuffers.HasBuffer(requestEntity))
                return false;

            var requestPath = requestPathBuffers[requestEntity];
            if (requestPath.Length < 2)
                return false;

            for (int i = 0; i < requestPath.Length; i++)
                path.Add(requestPath[i].Position);

            return true;
        }

        static NativeArray<byte> GetBlockedGrid(
            UnitNavigationGrid grid,
            float unitRadius,
            int radiusStep,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            UnitWaterMask waterMask,
            bool hasWater,
            bool naval,
            NativeParallelHashMap<int, int> blockedGridOffsets,
            NativeList<byte> blockedGridData)
        {
            int cellCount = grid.Size.x * grid.Size.y;
            int cacheKey = radiusStep * 2 + (naval ? 1 : 0);   // 도메인별 캐시 분리(2026-07-19)
            if (blockedGridOffsets.TryGetValue(cacheKey, out int offset))
                return blockedGridData.AsArray().GetSubArray(offset, cellCount);

            offset = blockedGridData.Length;
            blockedGridData.ResizeUninitialized(offset + cellCount);
            var blockedGrid = blockedGridData.AsArray().GetSubArray(offset, cellCount);
            for (int i = 0; i < blockedGrid.Length; i++)
                blockedGrid[i] = 0;

            UnitPathfinding.BuildBlockedGrid(
                grid,
                unitRadius,
                obstacleTransforms,
                obstacleFootprints,
                blockedGrid);
            if (hasWater)
                UnitPathfinding.ApplyWaterDomain(waterMask, naval, blockedGrid);
            blockedGridOffsets.Add(cacheKey, offset);
            return blockedGrid;
        }

    }

    public static class UnitPathfinding
    {
        public static bool TryBuildPath(
            UnitNavigationGrid grid,
            float3 startPosition,
            float3 targetPosition,
            float unitRadius,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            NativeList<float3> path,
            out bool reachedTarget)
        {
            reachedTarget = false;
            int cellCount = grid.Size.x * grid.Size.y;
            if (cellCount <= 0)
                return false;

            var blocked = new NativeArray<byte>(cellCount, Allocator.Temp);
            BuildBlockedGrid(grid, unitRadius, obstacleTransforms, obstacleFootprints, blocked);
            bool pathFound = TryBuildPathWithBlockedGrid(
                grid,
                startPosition,
                targetPosition,
                blocked,
                path,
                out reachedTarget);
            blocked.Dispose();
            return pathFound;
        }

        public static bool TryBuildPathWithBlockedGrid(
            UnitNavigationGrid grid,
            float3 startPosition,
            float3 targetPosition,
            NativeArray<byte> blocked,
            NativeList<float3> path,
            out bool reachedTarget)
        {
            reachedTarget = false;
            int cellCount = grid.Size.x * grid.Size.y;
            if (cellCount <= 0 || blocked.Length < cellCount)
                return false;

            int2 startCell = WorldToCell(grid, startPosition);
            int2 targetCell = WorldToCell(grid, targetPosition);
            if (!IsInside(grid, startCell) || !IsInside(grid, targetCell))
                return false;

            int startIndex = ToIndex(grid, startCell);
            int requestedTargetIndex = ToIndex(grid, targetCell);
            int targetIndex = FindNearestWalkableIndex(grid, targetCell, blocked);
            if (targetIndex < 0)
                return false;

            byte originalStartBlocked = blocked[startIndex];
            blocked[startIndex] = 0;

            bool targetCellWasWalkable = targetIndex == requestedTargetIndex;
            if (targetCellWasWalkable &&
                HasGridLineOfSight(grid, startPosition, targetPosition, blocked))
            {
                path.Add(startPosition);
                path.Add(targetPosition);
                blocked[startIndex] = originalStartBlocked;
                reachedTarget = true;
                return true;
            }

            var cameFrom = new NativeArray<int>(cellCount, Allocator.Temp);
            var gScore = new NativeArray<float>(cellCount, Allocator.Temp);
            var closed = new NativeArray<byte>(cellCount, Allocator.Temp);
            var open = new NativeList<PathOpenNode>(Allocator.Temp);

            for (int i = 0; i < cellCount; i++)
            {
                cameFrom[i] = -1;
                gScore[i] = float.MaxValue;
            }

            gScore[startIndex] = 0f;
            int2 targetCellResolved = ToCell(grid, targetIndex);
            PushOpenNode(open, startIndex, Heuristic(startCell, targetCellResolved));

            bool found = false;
            int bestReachable = startIndex;
            float bestReachableScore = Heuristic(startCell, targetCellResolved);
            int searched = 0;

            while (open.Length > 0 && searched < grid.MaxSearchNodes)
            {
                int current = PopBestOpenNode(open);

                if (closed[current] != 0)
                    continue;

                if (current == targetIndex)
                {
                    found = true;
                    break;
                }

                closed[current] = 1;
                searched++;

                int2 cell = ToCell(grid, current);
                float reachableScore = Heuristic(cell, targetCellResolved);
                if (reachableScore < bestReachableScore)
                {
                    bestReachableScore = reachableScore;
                    bestReachable = current;
                }

                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(1, 0), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(-1, 0), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(0, 1), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(0, -1), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(1, 1), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(1, -1), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(-1, 1), current, blocked, closed, cameFrom, gScore, open);
                TryVisitNeighbor(grid, cell, targetCellResolved, new int2(-1, -1), current, blocked, closed, cameFrom, gScore, open);
            }

            int resolvedTargetIndex = found ? targetIndex : bestReachable;
            bool hasPath = found || resolvedTargetIndex != startIndex;
            if (hasPath)
            {
                var rawPath = new NativeList<float3>(Allocator.Temp);
                ReconstructPath(grid, resolvedTargetIndex, cameFrom, rawPath);
                SmoothPath(grid, rawPath, blocked, path);
                rawPath.Dispose();
            }

            open.Dispose();
            closed.Dispose();
            gScore.Dispose();
            cameFrom.Dispose();
            blocked[startIndex] = originalStartBlocked;

            reachedTarget = found && targetCellWasWalkable;
            return hasPath && path.Length > 1;
        }

        public static int2 WorldToCell(UnitNavigationGrid grid, float3 position)
        {
            float inverseCellSize = 1f / math.max(0.01f, grid.CellSize);
            float3 local = position - grid.Origin;
            return new int2(
                (int)math.floor(local.x * inverseCellSize),
                (int)math.floor(local.z * inverseCellSize));
        }

        /// <summary>
        /// 이동 도메인 오버레이(2026-07-19 해상): 장애물 차단 그리드 위에 물 마스크를 겹친다.
        ///   수상(naval) = 물 아닌 셀 차단(항해 전용) / 지상 = 물 셀 차단(도하 금지).
        /// A*·직선 가시선·인근 보행 셀 탐색 전부 blocked 내용만 보므로 이 한 겹으로
        /// 도메인이 전 경로 기계에 일관 적용된다.
        /// </summary>
        public static void ApplyWaterDomain(
            UnitWaterMask waterMask,
            bool naval,
            NativeArray<byte> blocked)
        {
            int n = math.min(blocked.Length, waterMask.Water.Length);
            if (naval)
            {
                for (int i = 0; i < n; i++)
                    if (waterMask.Water[i] == 0) blocked[i] = 1;
            }
            else
            {
                for (int i = 0; i < n; i++)
                    if (waterMask.Water[i] != 0) blocked[i] = 1;
            }
        }

        public static void BuildBlockedGrid(
            UnitNavigationGrid grid,
            float unitRadius,
            NativeArray<LocalTransform> obstacleTransforms,
            NativeArray<ObstacleFootprint> obstacleFootprints,
            NativeArray<byte> blocked)
        {
            for (int i = 0; i < obstacleFootprints.Length; i++)
            {
                float radius = unitRadius + GetEffectiveObstacleRadius(obstacleFootprints[i]) +
                               math.max(0f, obstacleFootprints[i].ExtraPadding);
                int2 center = WorldToCell(grid, obstacleTransforms[i].Position);
                int cellRadius = (int)math.ceil(radius / grid.CellSize);

                for (int z = center.y - cellRadius; z <= center.y + cellRadius; z++)
                {
                    for (int x = center.x - cellRadius; x <= center.x + cellRadius; x++)
                    {
                        var cell = new int2(x, z);
                        if (!IsInside(grid, cell))
                            continue;

                        float3 cellCenter = CellToWorld(grid, cell);
                        if (HorizontalDistanceSq(cellCenter, obstacleTransforms[i].Position) <= radius * radius)
                            blocked[ToIndex(grid, cell)] = 1;
                    }
                }
            }
        }

        static int FindNearestWalkableIndex(UnitNavigationGrid grid, int2 targetCell, NativeArray<byte> blocked)
        {
            if (!IsInside(grid, targetCell))
                return -1;

            int targetIndex = ToIndex(grid, targetCell);
            if (blocked[targetIndex] == 0)
                return targetIndex;

            int maxRing = math.max(grid.Size.x, grid.Size.y);
            for (int ring = 1; ring <= maxRing; ring++)
            {
                int bestIndex = -1;
                int bestDistanceSq = int.MaxValue;

                for (int z = -ring; z <= ring; z++)
                {
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (math.max(math.abs(x), math.abs(z)) != ring)
                            continue;

                        var cell = targetCell + new int2(x, z);
                        if (!IsInside(grid, cell))
                            continue;

                        int index = ToIndex(grid, cell);
                        if (blocked[index] == 0)
                        {
                            int distanceSq = x * x + z * z;
                            if (distanceSq < bestDistanceSq)
                            {
                                bestDistanceSq = distanceSq;
                                bestIndex = index;
                            }
                        }
                    }
                }

                if (bestIndex >= 0)
                    return bestIndex;
            }

            return -1;
        }

        struct PathOpenNode
        {
            public int Index;
            public float Score;
        }

        static void PushOpenNode(NativeList<PathOpenNode> open, int index, float score)
        {
            int child = open.Length;
            open.Add(new PathOpenNode
            {
                Index = index,
                Score = score,
            });

            while (child > 0)
            {
                int parent = (child - 1) >> 1;
                if (open[parent].Score <= open[child].Score)
                    break;

                SwapOpenNodes(open, parent, child);
                child = parent;
            }
        }

        static int PopBestOpenNode(NativeList<PathOpenNode> open)
        {
            int best = open[0].Index;
            int last = open.Length - 1;
            if (last == 0)
            {
                open.RemoveAtSwapBack(0);
                return best;
            }

            open[0] = open[last];
            open.RemoveAtSwapBack(last);

            int parent = 0;
            while (true)
            {
                int left = parent * 2 + 1;
                if (left >= open.Length)
                    break;

                int right = left + 1;
                int bestChild = right < open.Length && open[right].Score < open[left].Score
                    ? right
                    : left;

                if (open[parent].Score <= open[bestChild].Score)
                    break;

                SwapOpenNodes(open, parent, bestChild);
                parent = bestChild;
            }

            return best;
        }

        static void SwapOpenNodes(NativeList<PathOpenNode> open, int a, int b)
        {
            PathOpenNode temp = open[a];
            open[a] = open[b];
            open[b] = temp;
        }

        static void TryVisitNeighbor(
            UnitNavigationGrid grid,
            int2 cell,
            int2 targetCell,
            int2 offset,
            int current,
            NativeArray<byte> blocked,
            NativeArray<byte> closed,
            NativeArray<int> cameFrom,
            NativeArray<float> gScore,
            NativeList<PathOpenNode> open)
        {
            int2 nextCell = cell + offset;
            if (!IsInside(grid, nextCell))
                return;

            int next = ToIndex(grid, nextCell);
            if (blocked[next] != 0 || closed[next] != 0)
                return;

            if (math.abs(offset.x) + math.abs(offset.y) == 2)
            {
                int sideA = ToIndex(grid, cell + new int2(offset.x, 0));
                int sideB = ToIndex(grid, cell + new int2(0, offset.y));
                if (blocked[sideA] != 0 || blocked[sideB] != 0)
                    return;
            }

            float stepCost = math.abs(offset.x) + math.abs(offset.y) == 2 ? 1.4142135f : 1f;
            float tentativeScore = gScore[current] + stepCost;
            if (tentativeScore >= gScore[next])
                return;

            cameFrom[next] = current;
            gScore[next] = tentativeScore;
            PushOpenNode(open, next, tentativeScore + Heuristic(nextCell, targetCell));
        }

        static void ReconstructPath(UnitNavigationGrid grid, int targetIndex, NativeArray<int> cameFrom, NativeList<float3> path)
        {
            var reversed = new NativeList<int>(Allocator.Temp);
            int current = targetIndex;

            while (current >= 0)
            {
                reversed.Add(current);
                current = cameFrom[current];
            }

            for (int i = reversed.Length - 1; i >= 0; i--)
                path.Add(CellToWorld(grid, ToCell(grid, reversed[i])));

            reversed.Dispose();
        }

        static void SmoothPath(
            UnitNavigationGrid grid,
            NativeList<float3> rawPath,
            NativeArray<byte> blocked,
            NativeList<float3> path)
        {
            if (rawPath.Length <= 2)
            {
                for (int i = 0; i < rawPath.Length; i++)
                    path.Add(rawPath[i]);

                return;
            }

            int anchor = 0;
            path.Add(rawPath[anchor]);

            while (anchor < rawPath.Length - 1)
            {
                int farthest = rawPath.Length - 1;
                while (farthest > anchor + 1)
                {
                    if (HasGridLineOfSight(grid, rawPath[anchor], rawPath[farthest], blocked))
                        break;

                    farthest--;
                }

                path.Add(rawPath[farthest]);
                anchor = farthest;
            }
        }

        public static bool HasGridLineOfSight(UnitNavigationGrid grid, float3 start, float3 end, NativeArray<byte> blocked)
        {
            return HasGridLineOfSight(grid, start, end, blocked, false);
        }

        public static bool HasGridLineOfSight(
            UnitNavigationGrid grid,
            float3 start,
            float3 end,
            NativeArray<byte> blocked,
            bool allowBlockedEndCell)
        {
            int2 a = WorldToCell(grid, start);
            int2 b = WorldToCell(grid, end);
            int dx = math.abs(b.x - a.x);
            int dz = math.abs(b.y - a.y);
            int steps = math.max(dx, dz);
            if (steps == 0)
                return IsInside(grid, a) &&
                       (blocked[ToIndex(grid, a)] == 0 || allowBlockedEndCell);

            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                int x = (int)math.round(math.lerp(a.x, b.x, t));
                int z = (int)math.round(math.lerp(a.y, b.y, t));
                var cell = new int2(x, z);
                bool isEndCell = cell.x == b.x && cell.y == b.y;
                if (!IsInside(grid, cell) ||
                    blocked[ToIndex(grid, cell)] != 0 && !(allowBlockedEndCell && isEndCell))
                    return false;
            }

            return true;
        }

        static float Heuristic(int2 a, int2 b)
        {
            int2 delta = math.abs(a - b);
            int diagonal = math.min(delta.x, delta.y);
            int straight = math.max(delta.x, delta.y) - diagonal;
            return diagonal * 1.4142135f + straight;
        }

        static float3 CellToWorld(UnitNavigationGrid grid, int2 cell)
        {
            return grid.Origin + new float3(
                (cell.x + 0.5f) * grid.CellSize,
                0f,
                (cell.y + 0.5f) * grid.CellSize);
        }

        static int ToIndex(UnitNavigationGrid grid, int2 cell)
        {
            return cell.y * grid.Size.x + cell.x;
        }

        static int2 ToCell(UnitNavigationGrid grid, int index)
        {
            return new int2(index % grid.Size.x, index / grid.Size.x);
        }

        static bool IsInside(UnitNavigationGrid grid, int2 cell)
        {
            return cell.x >= 0 && cell.y >= 0 && cell.x < grid.Size.x && cell.y < grid.Size.y;
        }

        /// <summary>월드 좌표가 내비 그리드 범위 안인가(2026-07-19). 그리드 밖 출발/목표는
        /// 경로 실패(HasTarget=0 = 영구 정지)가 아니라 직진(Direct) 폴백으로 처리해야 한다 —
        /// 호출측(UnitMoveOrderSystem)이 이 검사로 경로 시도 자체를 건너뛴다.</summary>
        public static bool IsInsideGrid(UnitNavigationGrid grid, float3 position)
            => IsInside(grid, WorldToCell(grid, position));

        static float GetEffectiveObstacleRadius(ObstacleFootprint obstacle)
        {
            float sizeRadius = math.length(math.max(obstacle.Size, new float2(0.01f))) * 0.5f;
            return math.max(math.max(0.01f, obstacle.Radius), sizeRadius);
        }

        static float HorizontalDistanceSq(float3 a, float3 b)
        {
            float3 delta = a - b;
            delta.y = 0f;
            return math.lengthsq(delta);
        }
    }
}
