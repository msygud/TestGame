using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  GameClockSystem — 게임 시간 전진
    //
    //  매 틱:
    //    1. TotalSeconds += 실시간 deltaTime × TimeScale (일시정지=0)
    //    2. TotalSeconds에서 시/일/주/월 파생 계산 (단일 원천 → 도출)
    //    3. 이전 값과 비교해 경계 플래그(HourChanged 등) 설정
    //
    //  InitializationSystemGroup에 두어 시뮬레이션 시스템보다 먼저 갱신.
    //  → 다른 시스템(욕구·스케줄·체증)은 같은 프레임에 최신 시간을 읽는다.
    //
    //  생성: 이 시스템이 직접 싱글톤을 만든다(없으면). Authoring 불필요.
    //  (TimeScale·SecondsPerDay를 인스펙터로 노출하려면 별도 Authoring 추가 가능.)
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct GameClockSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // 싱글톤이 없으면 기본값으로 생성.
            if (!SystemAPI.HasSingleton<GameClock>())
            {
                var e = state.EntityManager.CreateEntity();
                state.EntityManager.AddComponentData(e, GameClock.Default);
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            var clockRef = SystemAPI.GetSingletonRW<GameClock>();
            ref var clock = ref clockRef.ValueRW;

            // ── 1. 시간 전진 ──────────────────────────────────────────────
            float dt = SystemAPI.Time.DeltaTime;
            double advance = dt * clock.TimeScale;

            // 이전 단위 값 보관(경계 비교용)
            int prevHour  = clock.Hour;
            int prevDay   = clock.Day;
            int prevWeek  = clock.Week;
            int prevMonth = clock.Month;

            clock.TotalSeconds += advance;

            // ── 2. 파생 계산 ──────────────────────────────────────────────
            Recalculate(ref clock);

            // ── 3. 경계 플래그 ────────────────────────────────────────────
            //  일시정지(advance==0)거나 시간이 안 흘렀으면 모두 false.
            clock.HourChanged  = clock.Hour  != prevHour;
            clock.DayChanged   = clock.Day   != prevDay;
            clock.WeekChanged  = clock.Week  != prevWeek;
            clock.MonthChanged = clock.Month != prevMonth;
        }

        // ── 누적초 → 시/일/주/월 ───────────────────────────────────────────
        static void Recalculate(ref GameClock clock)
        {
            if (clock.SecondsPerDay <= 0f)
            {
                clock.Hour = clock.DayOfWeek = clock.Day = clock.Week = clock.Month = 0;
                return;
            }

            double totalDays = clock.TotalSeconds / clock.SecondsPerDay;
            int    dayIndex  = (int)totalDays;                       // 누적 일수(0부터)

            // 하루 중 시각 0~23
            double dayFraction = totalDays - dayIndex;
            int    hour        = (int)(dayFraction * GameClock.HoursPerDay);
            if (hour >= GameClock.HoursPerDay) hour = GameClock.HoursPerDay - 1;

            clock.Day       = dayIndex;
            clock.Hour      = hour;
            clock.DayOfWeek = dayIndex % GameClock.DaysPerWeek;
            clock.Week      = dayIndex / GameClock.DaysPerWeek;
            clock.Month     = dayIndex / GameClock.DaysPerMonth;
        }
    }
}
