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
            //Debug.Log("update");
            //var ecb = new EntityCommandBuffer(Allocator.Temp);
        }
    }
}
