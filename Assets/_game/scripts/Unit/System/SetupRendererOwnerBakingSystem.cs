using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.UniversalDelegates;
using Unity.Rendering;
using UnityEngine;

namespace Game.Unit
{
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    [UpdateInGroup(typeof(PostBakingSystemGroup))]
    public partial struct SetupRendererOwnerBakingSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            Debug.Log("start");
        }
        public void OnUpdate(ref SystemState state)
        {
            Debug.Log("update");
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            var query = SystemAPI.QueryBuilder()
            .WithAll<LinkedEntityGroup, VisibleOnScreenData>()
            .WithOptions(EntityQueryOptions.IncludePrefab
                       | EntityQueryOptions.IncludeDisabledEntities)
            .Build();
            Debug.Log(query.CalculateEntityCount());
            foreach (var (leg, rootEntity) in SystemAPI
                .Query<DynamicBuffer<LinkedEntityGroup>>()
                .WithPresent<VisibleOnScreenData>()
                .WithOptions(EntityQueryOptions.IncludePrefab
                           | EntityQueryOptions.IncludeDisabledEntities)
                .WithEntityAccess())
            {
                Debug.Log(leg.IsEmpty);
                for (int i = 0; i < leg.Length; i++)
                {
                    var child = leg[i].Value;

                    // 렌더러가 달린 자식만
                    if (SystemAPI.HasComponent<MaterialMeshInfo>(child))
                    {
                        var mmi = SystemAPI.GetComponentRW<MaterialMeshInfo>(child);

                        ecb.AddComponent(child, new OriginalMeshInfo
                        {
                            MeshID = mmi.ValueRO.Mesh
                        });
                        ecb.AddComponent(child, new RendererOwner
                        {
                            Root = rootEntity
                        });
                        ecb.AddComponent(child,new RendererOwner() { Root = rootEntity });

                        mmi.ValueRW.Mesh = 0;
                    }
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
