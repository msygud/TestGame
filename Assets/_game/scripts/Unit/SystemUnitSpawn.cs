using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using Game.Combat;

namespace Game.Unit
{
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    partial struct SystemUnitSpawn : ISystem
    {
        private Unity.Mathematics.Random random;
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RequestUnit>();
            state.RequireForUpdate<PrefabInfoElemental>();
            state.RequireForUpdate<CombatGridInfo>();
            state.RequireForUpdate<GeneratedInstanceIdData>();
            state.RequireForUpdate<VisibleStateData>();

            random = new Unity.Mathematics.Random((uint)System.DateTime.Now.Ticks + 15829);
        }

        public void OnUpdate(ref SystemState state)
        {
            Debug.Log("spawn");
            var visibledata=SystemAPI.GetSingleton<VisibleStateData>();
            bool visible = visibledata.Visible == VisibleStateData.State.FullVisible ? true : false;

            DynamicBuffer<RequestUnit> requestUnitBuffer = SystemAPI.GetSingletonBuffer<RequestUnit>();
            
            if (requestUnitBuffer.Length > 0)
            {
                CombatGridInfo mapInfo = SystemAPI.GetSingleton<CombatGridInfo>();
                RefRW<GeneratedInstanceIdData> generatedInstanceIdData = SystemAPI.GetSingletonRW<GeneratedInstanceIdData>();
                EntityCommandBuffer ecb = SystemAPI.GetSingleton<EndInitializationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);
                
                for (int i = 0; i < requestUnitBuffer.Length; i++)
                {
                    var request = requestUnitBuffer[i];
                    int spawcount = request.SpawnCount > 0 ? request.SpawnCount : 1;
                    for (int j = 0; j < spawcount; j++)
                    {
                        float2 randomPosition = random.NextFloat2(mapInfo.MinPosition, mapInfo.MaxPosition);

                        var prefabEntity = SystemAPI.GetSingletonBuffer<PrefabInfoElemental>()[request.ID].Prefab;

                        var instance = ecb.Instantiate(prefabEntity);

                        ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(new float3(randomPosition.x, 5.1f, randomPosition.y), quaternion.identity, 1f));
                        generatedInstanceIdData.ValueRW.CurrentID += 1;
                        ecb.SetComponent(instance, new InstanceIDData { InstanceID = generatedInstanceIdData.ValueRO.CurrentID });


                        bool isplayerteam = false;
                        foreach (var (teaminfo, teamunitcount, teamtag, entity) in SystemAPI.Query<RefRO<TeamInfoData>, RefRW<TeamUnitCountData>, LocalPlayerTag>().WithEntityAccess())
                        {
                            Debug.Log($"teaminfo : {teaminfo.ValueRO.GetLocalID()} request : {request.LocalID}");

                            if (teaminfo.ValueRO.GetLocalID() == request.LocalID)
                            {
                                ecb.SetComponent(instance, teaminfo.ValueRO);
                                teamunitcount.ValueRW.UnitCount += 1;
                                isplayerteam = teaminfo.ValueRO.IsPlayerTeam();
                            }
                        }
                        ecb.SetComponentEnabled<VisibleOnMinimapData>(instance, visible);
                        ecb.SetComponentEnabled<VisibleOnScreenData>(instance, visible);
#if UNITY_EDITOR
                        var name = SystemAPI.GetComponent<UnitName>(prefabEntity);
                        if (isplayerteam)
                            ecb.SetName(instance, name.Name + " " + generatedInstanceIdData.ValueRO.CurrentID + " " + "playerteam");
                        else ecb.SetName(instance, name.Name + " " + generatedInstanceIdData.ValueRO.CurrentID);
#endif
                    }
                }
                requestUnitBuffer.Clear();
            }
        }

        public void OnDestroy(ref SystemState state)
        {

        }
    }
}
