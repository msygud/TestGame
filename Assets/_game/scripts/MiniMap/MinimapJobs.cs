using UnityEngine;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Transforms;
using Game.Unit;
using System.Runtime.InteropServices;
using Unity.Physics.Systems;
using Game.Combat;

namespace Game.Minimap
{
    public class MinimapJobs
    {

    }
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct MinimapSyncSystem : ISystem
    {
        ComponentTypeHandle<LocalTransform> _handleTransform;
        ComponentTypeHandle<TeamInfoData> _handleTeam;
        ComponentTypeHandle<VisibleOnMinimapData> _handleVisibleOnMinimap;
        ComponentTypeHandle<VisibleOnScreenData> _handleVisibleOnScreen;

        EntityQuery _queryEnemyOnMinimap;
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CombatGridInfo>();

            _handleTransform=state.GetComponentTypeHandle<LocalTransform>();
            _handleTeam=state.GetComponentTypeHandle<TeamInfoData>();
            _handleVisibleOnMinimap=state.GetComponentTypeHandle<VisibleOnMinimapData>();
            _queryEnemyOnMinimap = SystemAPI.QueryBuilder().WithAll<VisibleOnMinimapData, LocalTransform>().WithNone<PlayerTeam>().Build();
        }
        
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency.Complete();
            CombatGridInfo map = SystemAPI.GetSingleton<CombatGridInfo>();

            int count = _queryEnemyOnMinimap.CalculateEntityCount();
            MinimapRenderer.Instance.SetUnitCount(count);
            //Debug.Log(count + " minimapcount");
            if (count < 1)
                return;
            _handleTeam.Update(ref state);
            _handleTransform.Update(ref state);
            _handleVisibleOnMinimap.Update(ref state);
            _handleVisibleOnScreen.Update(ref state);
        }
    }

    public partial struct SyncMinimapJob : IJobEntity
    {
        [WriteOnly]
        [NativeDisableParallelForRestriction]
        public NativeArray<MinimapUnitData> UnitDataBuffer;
        public float4 WorldBounds; // xMin, zMin, xMax, zMax

        public void Execute([EntityIndexInQuery]int index, in TeamInfoData team
            ,in VisibleOnMinimapData radar,in LocalTransform transform)
        {
           
            UnitDataBuffer[index] = new MinimapUnitData() { UV = transform.Position.xz, TeamIndex = team.GetLocalID(), UnitTypeIndex=0 };
        }
    }
    
}
