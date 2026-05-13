using Game.Unit;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Game.Combat
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SystemJammRadar))]
    partial struct JammingSystem : ISystem
    {
        NativeList<JammerUnitData> _listJammerUnit;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<Jammer>();
            state.RequireForUpdate<DetectedByRadar>();
            state.RequireForUpdate<VisibleStateData>();
            _listJammerUnit = new NativeList<JammerUnitData>(50, Allocator.Persistent);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Time.frameCount % 6 != 0) return;

            VisibleStateData visible=SystemAPI.GetSingleton<VisibleStateData>();

            if (visible.Visible == VisibleStateData.State.FullVisible)
                return;

            _listJammerUnit.Clear();
            foreach (var (grid,team, position,jammer) in SystemAPI.Query<GridPositionData,TeamInfoData,LocalTransform,Jammer>())
            {
                _listJammerUnit.Add(new JammerUnitData
                {
                    SqrtRadius = math.pow(jammer.Range, 2f),
                    Index=grid.Index,
                    JamedTeam=jammer.BeatenTeam,
                    JammingType=jammer.Offensive,
                    Position=position.Position
                });
            }

            var job = new JobUnitJam();
            var handle = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
            _listJammerUnit.Dispose();
        }
    }

    public partial struct JobUnitJam:IJobEntity
    {
        [ReadOnly]
        NativeArray<JammerUnitData> _listJammerUnit;
        public void Execute(in GridPositionData grid,in TeamInfoData team, ref DetectedByRadar detected )
        {
            detected.Reset();
            for (int i = 0; i < _listJammerUnit.Length; i++)
            {
                var jammer = _listJammerUnit[i];
                if((jammer.Ally&team.teamMask)==team.teamMask)
                {
                    int2 gap = math.abs(grid.Index - jammer.Index);
                    if(gap.x<=1&&gap.y<=1)
                    {
                        float sqrtdis = math.distancesq(jammer.Position.xz, grid.CurrentPos.xz);
                        if(sqrtdis<jammer.SqrtRadius)
                        {
                            switch (jammer.JammingType)
                            {
                                case ProtectiveJamming.None:
                                    break;
                                case ProtectiveJamming.HideFromRadar:
                                    detected.FullHide |= jammer.JamedTeam;
                                    break;
                                case ProtectiveJamming.SpoofPosition:
                                    detected.FakeDetected = jammer.JamedTeam;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                }
            }
        }
    }

    public struct JammerUnitData
    {
        public int2 Index;
        public float3 Position;
        public float SqrtRadius;
        public TeamMask Ally;
        public TeamMask JamedTeam;
        public ProtectiveJamming JammingType;
    }

}
