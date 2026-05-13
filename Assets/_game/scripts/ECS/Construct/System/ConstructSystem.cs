using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Game.Construct
{
    partial struct ConstructSystem : ISystem
    {
        NativeArray<CellTerrain> _terrainCell;
        NativeArray<CellOccupancy> _occupancyCell;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<ConstructGridSettingData>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {

        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {

        }
    }
}
