using Game.Combat;
using Game.Unit;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

[UpdateInGroup(typeof(SimulationSystemGroup))]
partial struct SystemJammRadar : ISystem
{
    NativeList<RadarSample> _listRadar;
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<CombatGridInfo>();
        state.RequireForUpdate<Radar>();
        state.RequireForUpdate<VisibleStateData>();
        _listRadar = new NativeList<RadarSample>(50, Allocator.Persistent);
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (Time.frameCount % 8 != 0) return;

        VisibleStateData visible = SystemAPI.GetSingleton<VisibleStateData>();

        if (visible.Visible == VisibleStateData.State.FullVisible)
            return;

        _listRadar.Clear();
        foreach (var (position,team,radar) in SystemAPI.Query<RefRO<GridPositionData>,RefRO<TeamInfoData>,RefRO<Radar>>())
        {
            _listRadar.Add( new RadarSample
            {
                SqrtRange = Mathf.Pow(radar.ValueRO.Range, 2f),
                Strngth=radar.ValueRO.Strength,
                Position=position.ValueRO.CurrentPos,
                UnitTeam=team.ValueRO.teamMask
            });
        }

        var job = new JobRadarVSJammer
        {
            radarUnit = _listRadar
        };

        var handle = job.ScheduleParallel(state.Dependency);
    }

    [BurstCompile]
    public void OnDestroy(ref SystemState state)
    {
        _listRadar.Dispose();
    }

    public struct RadarSample
    {
        public TeamMask UnitTeam;
        public float SqrtRange;
        public byte Strngth;
        public float3 Position;
    }

    public partial struct JobRadarVSJammer : IJobEntity
    {
        public NativeList<RadarSample> radarUnit;

        public void Execute(in TeamInfoData teamInfo,in LocalTransform position, in GridPositionData grid, ref Jammer jamming)
        {
            jamming.Reset();

            int team = teamInfo.LocalID;
            int2 unitgrid = grid.Index;
            for (int i = 0; radarUnit.Length > 0; i++)
            {
                var sample= radarUnit[i];
                if (teamInfo.IsEnemy(sample.UnitTeam))
                {
                    bool registfromradar = sample.Strngth < jamming.Strength ? true : false;
                    if (registfromradar)
                    {
                        jamming.BeatenTeam |= sample.UnitTeam;
                    }
                }
            }
        }
    }
}
