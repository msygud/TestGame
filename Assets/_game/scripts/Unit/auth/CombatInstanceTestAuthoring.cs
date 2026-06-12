using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Game.Unit
{
    public sealed class CombatInstanceTestAuthoring : MonoBehaviour
    {
        [Header("Attackers")]
        public GameObject AttackerPrefab;
        public GameObject[] AttackerPrefabs;

        [Min(1)]
        public int AttackerCount = 4;

        public Vector3 AttackerOrigin = new Vector3(8f, 0f, 12f);

        [Range(0, 7)]
        public int AttackerLocalId = 0;

        public int AttackerTeamId = 0;

        public string AttackerNamePrefix = "A";

        [Header("Targets")]
        public GameObject TargetPrefab;
        public GameObject[] TargetPrefabs;

        [Min(1)]
        public int TargetCount = 1;

        public Vector3 TargetOrigin = new Vector3(18f, 0f, 12f);

        [Range(0, 7)]
        public int TargetLocalId = 1;

        public int TargetTeamId = 1;

        public string TargetNamePrefix = "T";

        [Header("Layout")]
        [Min(0.1f)]
        public float Spacing = 2f;

        [Min(0.01f)]
        public float Scale = 1f;

        [Header("Orders")]
        public bool AutoAttack = true;
        public bool TargetsAutoAttack;

        class Baker : Baker<CombatInstanceTestAuthoring>
        {
            public override void Bake(CombatInstanceTestAuthoring authoring)
            {
                if (!HasAnyPrefab(authoring.AttackerPrefab, authoring.AttackerPrefabs) ||
                    !HasAnyPrefab(authoring.TargetPrefab, authoring.TargetPrefabs))
                    return;

                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new CombatInstanceTestConfig
                {
                    AttackerCount = math.max(1, authoring.AttackerCount),
                    TargetCount = math.max(1, authoring.TargetCount),
                    AttackerOrigin = ToFloat3(authoring.AttackerOrigin),
                    TargetOrigin = ToFloat3(authoring.TargetOrigin),
                    Spacing = math.max(0.1f, authoring.Spacing),
                    Scale = math.max(0.01f, authoring.Scale),
                    AttackerLocalId = math.clamp(authoring.AttackerLocalId, 0, 7),
                    AttackerTeamId = authoring.AttackerTeamId,
                    TargetLocalId = math.clamp(authoring.TargetLocalId, 0, 7),
                    TargetTeamId = authoring.TargetTeamId,
                    AttackerNamePrefix = ResolvePrefix(authoring.AttackerNamePrefix, "A"),
                    TargetNamePrefix = ResolvePrefix(authoring.TargetNamePrefix, "T"),
                    AutoAttack = authoring.AutoAttack ? (byte)1 : (byte)0,
                    TargetsAutoAttack = authoring.TargetsAutoAttack ? (byte)1 : (byte)0,
                });

                var attackers = AddBuffer<CombatInstanceTestAttackerPrefab>(entity);
                AddPrefab(authoring.AttackerPrefab, attackers);
                AddPrefabs(authoring.AttackerPrefabs, attackers);

                var targets = AddBuffer<CombatInstanceTestTargetPrefab>(entity);
                AddPrefab(authoring.TargetPrefab, targets);
                AddPrefabs(authoring.TargetPrefabs, targets);
            }

            void AddPrefab(GameObject prefab, DynamicBuffer<CombatInstanceTestAttackerPrefab> buffer)
            {
                if (prefab == null)
                    return;

                buffer.Add(new CombatInstanceTestAttackerPrefab
                {
                    Prefab = GetEntity(prefab, TransformUsageFlags.Dynamic),
                });
            }

            void AddPrefabs(GameObject[] prefabs, DynamicBuffer<CombatInstanceTestAttackerPrefab> buffer)
            {
                if (prefabs == null)
                    return;

                for (int i = 0; i < prefabs.Length; i++)
                    AddPrefab(prefabs[i], buffer);
            }

            void AddPrefab(GameObject prefab, DynamicBuffer<CombatInstanceTestTargetPrefab> buffer)
            {
                if (prefab == null)
                    return;

                buffer.Add(new CombatInstanceTestTargetPrefab
                {
                    Prefab = GetEntity(prefab, TransformUsageFlags.Dynamic),
                });
            }

            void AddPrefabs(GameObject[] prefabs, DynamicBuffer<CombatInstanceTestTargetPrefab> buffer)
            {
                if (prefabs == null)
                    return;

                for (int i = 0; i < prefabs.Length; i++)
                    AddPrefab(prefabs[i], buffer);
            }

            static bool HasAnyPrefab(GameObject prefab, GameObject[] prefabs)
            {
                if (prefab != null)
                    return true;

                if (prefabs == null)
                    return false;

                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (prefabs[i] != null)
                        return true;
                }

                return false;
            }

            static float3 ToFloat3(Vector3 value)
                => new float3(value.x, value.y, value.z);

            static FixedString32Bytes ResolvePrefix(string value, string fallback)
            {
                return new FixedString32Bytes(string.IsNullOrWhiteSpace(value) ? fallback : value);
            }
        }
    }

    public struct CombatInstanceTestConfig : IComponentData
    {
        public int AttackerCount;
        public int TargetCount;
        public float3 AttackerOrigin;
        public float3 TargetOrigin;
        public float Spacing;
        public float Scale;
        public int AttackerLocalId;
        public int AttackerTeamId;
        public int TargetLocalId;
        public int TargetTeamId;
        public FixedString32Bytes AttackerNamePrefix;
        public FixedString32Bytes TargetNamePrefix;
        public byte AutoAttack;
        public byte TargetsAutoAttack;
    }

    public struct CombatInstanceTestAttackerPrefab : IBufferElementData
    {
        public Entity Prefab;
    }

    public struct CombatInstanceTestTargetPrefab : IBufferElementData
    {
        public Entity Prefab;
    }

    public struct CombatInstanceTestDone : IComponentData
    {
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct CombatInstanceTestSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatInstanceTestConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var entityManager = state.EntityManager;
            var ecb = new EntityCommandBuffer(Unity.Collections.Allocator.Temp);

            foreach (var (config, configEntity) in
                     SystemAPI.Query<RefRO<CombatInstanceTestConfig>>()
                         .WithNone<CombatInstanceTestDone>()
                         .WithEntityAccess())
            {
                var attackerPrefabs = SystemAPI.GetBuffer<CombatInstanceTestAttackerPrefab>(configEntity);
                var targetPrefabs = SystemAPI.GetBuffer<CombatInstanceTestTargetPrefab>(configEntity);

                if (attackerPrefabs.Length == 0 || targetPrefabs.Length == 0)
                {
                    ecb.AddComponent<CombatInstanceTestDone>(configEntity);
                    continue;
                }

                Entity firstTarget = Entity.Null;
                Entity firstAttacker = Entity.Null;
                var spawnedTargets = config.ValueRO.TargetsAutoAttack != 0
                    ? new NativeList<Entity>(config.ValueRO.TargetCount, Allocator.Temp)
                    : default;
                TeamInfoData attackerTeam = BuildTeam(
                    config.ValueRO.AttackerLocalId,
                    config.ValueRO.AttackerTeamId,
                    config.ValueRO.TargetLocalId,
                    config.ValueRO.AttackerTeamId == config.ValueRO.TargetTeamId);
                TeamInfoData targetTeam = BuildTeam(
                    config.ValueRO.TargetLocalId,
                    config.ValueRO.TargetTeamId,
                    config.ValueRO.AttackerLocalId,
                    config.ValueRO.TargetTeamId == config.ValueRO.AttackerTeamId);

                for (int i = 0; i < config.ValueRO.TargetCount; i++)
                {
                    Entity prefab = targetPrefabs[i % targetPrefabs.Length].Prefab;
                    Entity target = SpawnInstance(
                        ecb,
                        entityManager,
                        prefab,
                        GetGridPosition(config.ValueRO.TargetOrigin, i, config.ValueRO.TargetCount, config.ValueRO.Spacing),
                        config.ValueRO.Scale);
                    FixedString64Bytes targetName = BuildName(config.ValueRO.TargetNamePrefix, i);
                    SetOrAddTeam(ecb, entityManager, prefab, target, targetTeam);
                    SetOrAddName(ecb, entityManager, prefab, target, targetName);
                    ecb.SetName(target, targetName.ToString());

                    if (firstTarget == Entity.Null)
                        firstTarget = target;

                    if (config.ValueRO.TargetsAutoAttack != 0)
                        spawnedTargets.Add(target);
                }

                for (int i = 0; i < config.ValueRO.AttackerCount; i++)
                {
                    Entity prefab = attackerPrefabs[i % attackerPrefabs.Length].Prefab;
                    Entity attacker = SpawnInstance(
                        ecb,
                        entityManager,
                        prefab,
                        GetGridPosition(config.ValueRO.AttackerOrigin, i, config.ValueRO.AttackerCount, config.ValueRO.Spacing),
                        config.ValueRO.Scale);
                    FixedString64Bytes attackerName = BuildName(config.ValueRO.AttackerNamePrefix, i);
                    SetOrAddTeam(ecb, entityManager, prefab, attacker, attackerTeam);
                    SetOrAddName(ecb, entityManager, prefab, attacker, attackerName);
                    ecb.SetName(attacker, attackerName.ToString());

                    if (firstAttacker == Entity.Null)
                        firstAttacker = attacker;

                    if (config.ValueRO.AutoAttack != 0 && firstTarget != Entity.Null)
                    {
                        AddAttackOrder(ecb, attacker, firstTarget);
                    }
                }

                if (config.ValueRO.TargetsAutoAttack != 0 && firstAttacker != Entity.Null)
                {
                    for (int i = 0; i < spawnedTargets.Length; i++)
                        AddAttackOrder(ecb, spawnedTargets[i], firstAttacker);
                }

                if (config.ValueRO.TargetsAutoAttack != 0)
                    spawnedTargets.Dispose();

                ecb.AddComponent<CombatInstanceTestDone>(configEntity);
            }

            ecb.Playback(entityManager);
            ecb.Dispose();
        }

        static Entity SpawnInstance(
            EntityCommandBuffer ecb,
            EntityManager entityManager,
            Entity prefab,
            float3 position,
            float scale)
        {
            Entity instance = ecb.Instantiate(prefab);
            var transform = LocalTransform.FromPositionRotationScale(
                position,
                quaternion.identity,
                math.max(0.01f, scale));

            if (entityManager.HasComponent<LocalTransform>(prefab))
                ecb.SetComponent(instance, transform);
            else
                ecb.AddComponent(instance, transform);

            return instance;
        }

        static void AddAttackOrder(EntityCommandBuffer ecb, Entity attacker, Entity target)
        {
            Entity request = ecb.CreateEntity();
            ecb.AddComponent(request, new AttackOrderRequest
            {
                Attacker = attacker,
                Target = target,
                CommandKind = UnitCommandKind.ForceAttack,
            });
        }

        static float3 GetGridPosition(float3 origin, int index, int count, float spacing)
        {
            int columns = math.max(1, (int)math.ceil(math.sqrt(count)));
            int rows = math.max(1, (int)math.ceil(count / (float)columns));
            int x = index % columns;
            int z = index / columns;
            return origin + new float3(
                (x - (columns - 1) * 0.5f) * spacing,
                0f,
                (z - (rows - 1) * 0.5f) * spacing);
        }

        static TeamInfoData BuildTeam(int localId, int teamId, int otherLocalId, bool sameTeam)
        {
            TeamMask other = (TeamMask)(1 << math.clamp(otherLocalId, 0, 7));
            TeamInfoData team = TeamInfoData.CreateTeamInfo(
                math.clamp(localId, 0, 7),
                sameTeam ? TeamMask.None : other,
                sameTeam ? other : TeamMask.None,
                TeamMask.None);
            team.TeamID = teamId;
            team.LocalID = math.clamp(localId, 0, 7);
            return team;
        }

        static void SetOrAddTeam(
            EntityCommandBuffer ecb,
            EntityManager entityManager,
            Entity prefab,
            Entity entity,
            TeamInfoData team)
        {
            if (entityManager.HasComponent<TeamInfoData>(prefab))
                ecb.SetComponent(entity, team);
            else
                ecb.AddComponent(entity, team);
        }

        static FixedString64Bytes BuildName(FixedString32Bytes prefix, int index)
        {
            FixedString64Bytes name = prefix;
            name.Append(index + 1);
            return name;
        }

        static void SetOrAddName(
            EntityCommandBuffer ecb,
            EntityManager entityManager,
            Entity prefab,
            Entity entity,
            FixedString64Bytes name)
        {
            var displayName = new UnitDisplayName
            {
                Value = name,
            };

            if (entityManager.HasComponent<UnitDisplayName>(prefab))
                ecb.SetComponent(entity, displayName);
            else
                ecb.AddComponent(entity, displayName);
        }
    }
}
