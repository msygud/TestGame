using Game.Unit;
using Game.Utility;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Game.Combat
{
    partial struct SystemSightRadar : ISystem
    {
        //플레이어팀
        public NativeParallelMultiHashMap<int2, EntityInfo> _enemyGrid;

        EntityQuery _playerTeam;
        EntityQuery _allEnemy;
        EntityQuery _playerteamSight;
        EntityQuery _playerteamRadar;
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatGridInfo>();

            _enemyGrid = new NativeParallelMultiHashMap<int2,EntityInfo>(5000, Allocator.Persistent);

            _allEnemy = SystemAPI.QueryBuilder().WithAll<LocalTransform, DetectedByRadar>().WithPresent<VisibleOnMinimapData>().WithNone<PlayerTeam>().Build();
            _playerteamSight = SystemAPI.QueryBuilder().WithAll<LocalTransform>().Build();
            _playerteamRadar = SystemAPI.QueryBuilder().WithAll<LocalTransform, Radar>().Build();
        }
        public void OnUpdate(ref SystemState state)
        {
            CombatGridInfo gridinfo = SystemAPI.GetSingleton<CombatGridInfo>();
            float cellsize = gridinfo.Cellsize;
            int2 gridcount = (int2)math.ceil((gridinfo.MapSize - gridinfo.MinPosition) / cellsize);

            var source = _enemyGrid.GetValuesForKey(new int2(0, 0));
            NativeParallelMultiHashMapIterator<int2> o=new Unity.Collections.NativeParallelMultiHashMapIterator<int2>();
            o.GetEntryIndex();
            var e = _enemyGrid.GetValuesForKey(int2.zero);
            
        }
        public void OnDestroy(ref SystemState state) {
            if (_enemyGrid.IsCreated)
                _enemyGrid.Dispose();
        }
    }
    public struct EntityInfo
    {
        public float3 Position;
        public Entity Target;
    }
}
