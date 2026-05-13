using Game.Unit;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

partial struct SystemFilterMinimap : ISystem
{
    NativeParallelMultiHashMap<int2, PlayTeamRadarUnit> _gridRadarUnit;

    EntityQuery _teamQuery;
    EntityQuery _targetGroup;
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<LocalPlayerTag>();

        _teamQuery = SystemAPI.QueryBuilder().WithAll<LocalPlayerTag>().Build();
        _targetGroup=SystemAPI.QueryBuilder().WithAll<LocalTransform,DetectedByRadar>().WithPresentRW<VisibleOnMinimapData>().Build();

        _gridRadarUnit = new NativeParallelMultiHashMap<int2, PlayTeamRadarUnit>(100, Allocator.Persistent);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var tic = Time.frameCount;
        if (tic / 5 != 0)
            return;
        TeamInfoData teamdata=new TeamInfoData();
        foreach (var (tag,team) in SystemAPI.Query<LocalPlayerTag,TeamInfoData>())
        {
            if (team.IsPlayer())
            {
                teamdata = team;
                break;
            }
        }
        
        foreach (var (a,radar,index) in SystemAPI.Query<PlayerTeam,Radar,GridPositionData>())
        {
            _gridRadarUnit.Add(index.Index, new PlayTeamRadarUnit { Position = index.CurrentPos,SqrRadius=math.pow( radar.Range,2f),Team=teamdata.AllyTeam });
        }
    }

    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {
        
    }

    public struct PlayTeamRadarUnit
    {
        public TeamMask Team;
        public float3 Position;
        public float SqrRadius;
    }
}

public partial struct JobVIsibleOnMinimap:IJobChunk
{
    public void Execute(in ArchetypeChunk cunks,int chunkindex,bool useEnable,in v128 mask)
    {

    }
}
