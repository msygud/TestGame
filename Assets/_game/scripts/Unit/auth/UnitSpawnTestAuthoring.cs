using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Game.Unit
{
    public sealed class UnitSpawnTestAuthoring : MonoBehaviour
    {
        public GameObject UnitPrefab;
        public GameObject[] UnitPrefabs;

        [Min(1)]
        public int Count = 4;

        public Vector3 Origin = new Vector3(12f, 0f, 12f);

        [Min(0.1f)]
        public float Spacing = 2f;

        [Min(0.01f)]
        public float Scale = 1f;

        class Baker : Baker<UnitSpawnTestAuthoring>
        {
            public override void Bake(UnitSpawnTestAuthoring authoring)
            {
                if (authoring.UnitPrefab == null && !HasAnyPrefab(authoring.UnitPrefabs))
                    return;

                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new UnitSpawnTestConfig
                {
                    Count = math.max(1, authoring.Count),
                    Origin = new float3(authoring.Origin.x, authoring.Origin.y, authoring.Origin.z),
                    Spacing = math.max(0.1f, authoring.Spacing),
                    Scale = math.max(0.01f, authoring.Scale),
                });

                var prefabBuffer = AddBuffer<UnitSpawnTestPrefab>(entity);

                if (authoring.UnitPrefab != null)
                {
                    prefabBuffer.Add(new UnitSpawnTestPrefab
                    {
                        Prefab = GetEntity(authoring.UnitPrefab, TransformUsageFlags.Dynamic),
                    });
                }

                if (authoring.UnitPrefabs == null)
                    return;

                for (int i = 0; i < authoring.UnitPrefabs.Length; i++)
                {
                    if (authoring.UnitPrefabs[i] == null)
                        continue;

                    prefabBuffer.Add(new UnitSpawnTestPrefab
                    {
                        Prefab = GetEntity(authoring.UnitPrefabs[i], TransformUsageFlags.Dynamic),
                    });
                }
            }

            static bool HasAnyPrefab(GameObject[] prefabs)
            {
                if (prefabs == null)
                    return false;

                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (prefabs[i] != null)
                        return true;
                }

                return false;
            }
        }
    }

    public struct UnitSpawnTestConfig : IComponentData
    {
        public int Count;
        public float3 Origin;
        public float Spacing;
        public float Scale;
    }

    public struct UnitSpawnTestPrefab : IBufferElementData
    {
        public Entity Prefab;
    }

    public struct UnitSpawnTestDone : IComponentData
    {
    }

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct UnitSpawnTestSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitSpawnTestConfig>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (config, configEntity) in
                     SystemAPI.Query<RefRO<UnitSpawnTestConfig>>()
                         .WithNone<UnitSpawnTestDone>()
                         .WithEntityAccess())
            {
                var prefabs = SystemAPI.GetBuffer<UnitSpawnTestPrefab>(configEntity);
                if (prefabs.Length == 0)
                {
                    ecb.AddComponent<UnitSpawnTestDone>(configEntity);
                    continue;
                }

                int columns = math.max(1, (int)math.ceil(math.sqrt(config.ValueRO.Count)));
                int rows = math.max(1, (int)math.ceil(config.ValueRO.Count / (float)columns));

                for (int i = 0; i < config.ValueRO.Count; i++)
                {
                    int x = i % columns;
                    int z = i / columns;

                    float offsetX = (x - (columns - 1) * 0.5f) * config.ValueRO.Spacing;
                    float offsetZ = (z - (rows - 1) * 0.5f) * config.ValueRO.Spacing;
                    float3 position = config.ValueRO.Origin + new float3(offsetX, 0f, offsetZ);

                    Entity prefab = prefabs[i % prefabs.Length].Prefab;
                    if (prefab == Entity.Null)
                        continue;

                    Entity unit = ecb.Instantiate(prefab);
                    ecb.SetComponent(unit, LocalTransform.FromPositionRotationScale(
                        position,
                        quaternion.identity,
                        config.ValueRO.Scale));
                }

                ecb.AddComponent<UnitSpawnTestDone>(configEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
