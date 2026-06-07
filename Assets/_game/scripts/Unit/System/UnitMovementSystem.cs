using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Game.Unit
{
    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMoveOrderSystem))]
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

            foreach (var (transform, speed, target, motion, activity) in
                     SystemAPI.Query<
                         RefRW<LocalTransform>,
                         RefRO<UnitMoveSpeed>,
                         RefRW<UnitMoveTarget>,
                         RefRW<UnitMotionState>,
                         RefRW<UnitActivityState>>()
                         .WithAll<UnitTag>())
            {
                if (target.ValueRO.HasTarget == 0)
                {
                    Stop(ref motion.ValueRW);
                    SetRestActivity(ref activity.ValueRW, deltaTime);
                    continue;
                }

                float3 position = transform.ValueRO.Position;
                float3 toTarget = target.ValueRO.Position - position;
                toTarget.y = 0f;

                float stopDistance = math.max(0.01f, target.ValueRO.StopDistance);
                float distanceSq = math.lengthsq(toTarget);

                if (distanceSq <= stopDistance * stopDistance)
                {
                    target.ValueRW.HasTarget = 0;
                    Stop(ref motion.ValueRW);
                    SetActivity(ref activity.ValueRW, UnitActivityKind.Settled, deltaTime);
                    continue;
                }

                float distance = math.sqrt(distanceSq);
                float3 direction = toTarget / distance;
                float maxAdvance = math.max(0.01f, speed.ValueRO.MetersPerSecond) * deltaTime;
                float advance = math.min(maxAdvance, distance - stopDistance);

                transform.ValueRW.Position = position + direction * advance;
                transform.ValueRW.Rotation = RotateTowards(
                    transform.ValueRO.Rotation,
                    quaternion.LookRotationSafe(direction, math.up()),
                    speed.ValueRO.TurnSpeedRadians * deltaTime);

                motion.ValueRW.Velocity = direction * (advance / deltaTime);
                motion.ValueRW.DesiredForward = direction;
                motion.ValueRW.IsMoving = 1;
                SetActivity(ref activity.ValueRW, UnitActivityKind.Moving, deltaTime);
            }
        }

        static void Stop(ref UnitMotionState motion)
        {
            motion.Velocity = float3.zero;
            motion.IsMoving = 0;
        }

        static void SetRestActivity(ref UnitActivityState activity, float deltaTime)
        {
            if (activity.Value == UnitActivityKind.Working ||
                activity.Value == UnitActivityKind.Anchored)
            {
                activity.TimeInState += deltaTime;
                return;
            }

            SetActivity(ref activity, UnitActivityKind.Settled, deltaTime);
        }

        static void SetActivity(ref UnitActivityState activity, UnitActivityKind value, float deltaTime)
        {
            if (activity.Value != value)
            {
                activity.Value = value;
                activity.TimeInState = 0f;
                return;
            }

            activity.TimeInState += deltaTime;
        }

        static quaternion RotateTowards(quaternion from, quaternion to, float maxRadiansDelta)
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
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementSystem))]
    public partial struct UnitSeparationSystem : ISystem
    {
        const float MaxPushPerTick = 0.2f;
        const float MinimumSpatialCellSize = 0.5f;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitFootprint>();
        }

        public void OnUpdate(ref SystemState state)
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            var query = SystemAPI.QueryBuilder()
                .WithAll<UnitTag, UnitFootprint, UnitMotionState, UnitActivityState, LocalTransform>()
                .Build();

            int count = query.CalculateEntityCount();
            if (count <= 1)
                return;

            var entities = query.ToEntityArray(Allocator.Temp);
            var transforms = query.ToComponentDataArray<LocalTransform>(Allocator.Temp);
            var footprints = query.ToComponentDataArray<UnitFootprint>(Allocator.Temp);
            var motions = query.ToComponentDataArray<UnitMotionState>(Allocator.Temp);
            var activities = query.ToComponentDataArray<UnitActivityState>(Allocator.Temp);
            var offsets = new NativeArray<float3>(count, Allocator.Temp);

            float cellSize = CalculateCellSize(footprints);
            var spatialHash = new NativeParallelMultiHashMap<int2, int>(count, Allocator.Temp);

            for (int i = 0; i < count; i++)
                spatialHash.Add(CellOf(transforms[i].Position, cellSize), i);

            for (int a = 0; a < count; a++)
            {
                int2 originCell = CellOf(transforms[a].Position, cellSize);

                for (int z = -1; z <= 1; z++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        int2 cell = originCell + new int2(x, z);

                        if (!spatialHash.TryGetFirstValue(cell, out int b, out var iterator))
                            continue;

                        do
                        {
                            if (b <= a)
                                continue;

                            AddSeparationOffset(a, b, transforms, footprints, motions, activities, offsets);
                        }
                        while (spatialHash.TryGetNextValue(out b, ref iterator));
                    }
                }
            }

            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(false);
            float maxPush = MaxPushPerTick * math.max(1f, deltaTime * 60f);

            for (int i = 0; i < count; i++)
            {
                float3 offset = offsets[i];
                offset.y = 0f;

                float lengthSq = math.lengthsq(offset);
                if (lengthSq <= 0.000001f)
                    continue;

                float length = math.sqrt(lengthSq);
                if (length > maxPush)
                    offset *= maxPush / length;

                var transform = transformLookup[entities[i]];
                transform.Position += offset;
                transformLookup[entities[i]] = transform;
            }

            offsets.Dispose();
            spatialHash.Dispose();
            activities.Dispose();
            motions.Dispose();
            footprints.Dispose();
            transforms.Dispose();
            entities.Dispose();
        }

        static void AddSeparationOffset(
            int a,
            int b,
            NativeArray<LocalTransform> transforms,
            NativeArray<UnitFootprint> footprints,
            NativeArray<UnitMotionState> motions,
            NativeArray<UnitActivityState> activities,
            NativeArray<float3> offsets)
        {
            float radiusA = math.max(0.01f, footprints[a].Radius);
            float radiusB = math.max(0.01f, footprints[b].Radius);
            float minDistance = radiusA + radiusB;

            float3 delta = transforms[a].Position - transforms[b].Position;
            delta.y = 0f;
            float distanceSq = math.lengthsq(delta);

            if (distanceSq >= minDistance * minDistance)
                return;

            float distance = math.sqrt(distanceSq);
            float3 direction = distance > 0.0001f
                ? delta / distance
                : StablePairDirection(a, b);

            float penetration = minDistance - distance;
            float weightA = EffectivePushWeight(footprints[a], motions[a], activities[a]);
            float weightB = EffectivePushWeight(footprints[b], motions[b], activities[b]);
            float totalWeight = weightA + weightB;

            if (totalWeight <= 0.0001f)
                return;

            float3 push = direction * penetration;
            offsets[a] += push * (weightA / totalWeight);
            offsets[b] -= push * (weightB / totalWeight);
        }

        static float EffectivePushWeight(UnitFootprint footprint, UnitMotionState motion, UnitActivityState activity)
        {
            float weight = math.max(0f, footprint.SeparationWeight);

            switch (activity.Value)
            {
                case UnitActivityKind.Anchored:
                    return weight * math.saturate(footprint.AnchoredPushScale);
                case UnitActivityKind.Working:
                    return weight * math.saturate(footprint.WorkingPushScale);
                case UnitActivityKind.Settled:
                    return weight * math.saturate(footprint.SettledPushScale);
            }

            if (motion.IsMoving == 0)
                return weight * math.saturate(footprint.SettledPushScale);

            return weight;
        }

        static float CalculateCellSize(NativeArray<UnitFootprint> footprints)
        {
            float maxRadius = 0f;

            for (int i = 0; i < footprints.Length; i++)
                maxRadius = math.max(maxRadius, math.max(0.01f, footprints[i].Radius));

            return math.max(MinimumSpatialCellSize, maxRadius * 2f);
        }

        static int2 CellOf(float3 position, float cellSize)
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
