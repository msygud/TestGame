using Unity.Burst;
using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ServiceStatsRolloverSystem — 손님 통계 일일 롤오버 (2026-07-07)
    //
    //  DayChanged마다 서비스 건물의 ServiceStats를 하루 넘김:
    //    YesterdayServed = TodayServed;  TodayServed = 0.
    //  접객은 ServiceDeskJob이 TodayServed++로 누적. 저빈도(하루 1회) Burst 잡.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ServiceStatsRolloverSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<ServiceStats>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.GetSingleton<GameClock>().DayChanged) return;
            state.Dependency = new RolloverJob().ScheduleParallel(state.Dependency);
        }
    }

    [BurstCompile]
    public partial struct RolloverJob : IJobEntity
    {
        void Execute(ref ServiceStats s)
        {
            s.YesterdayServed = s.TodayServed;
            s.TodayServed     = 0;
        }
    }
}
