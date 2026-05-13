using Game.Unit;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using UnityEngine;

namespace Game.Combat
{
    partial struct SystemPreJobMinimap : ISystem
    {
        EntityQuery _detetedUnitByRadar;
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<DetectedByRadar>();
            state.RequireForUpdate<UserPlayer>();
            state.RequireForUpdate<LocalPlayerTag>();
            state.RequireForUpdate<VisibleStateData>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (Time.frameCount % 5 != 0) return;

            VisibleStateData visible = SystemAPI.GetSingleton<VisibleStateData>();

            if (visible.Visible == VisibleStateData.State.FullVisible)
                return;

            var user = SystemAPI.GetSingleton<UserPlayer>();
            var userteam = new TeamInfoData();
            foreach (var (player, teamdata) in SystemAPI.Query<UserPlayer, TeamInfoData>())
            {
                if (player.LocalID == user.LocalID)
                {
                    userteam = teamdata; break;
                }
            }

            var job = new JobPreMinimap()
            {
                playerteam = userteam.AllyTeam
            };

            var handle = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {

        }
    }

    public partial struct JobPreMinimap:IJobEntity
    {
        public TeamMask playerteam;
        public void Execute(in DetectedByRadar unit, EnabledRefRW<VisibleOnMinimapData> visible)
        {
            bool isvisible = ((unit.FakeDetected | unit.FullHide) & playerteam) == playerteam ? true : false;
            visible.ValueRW = isvisible;
        }
    }
}
