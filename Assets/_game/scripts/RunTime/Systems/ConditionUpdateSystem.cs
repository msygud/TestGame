using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  ConditionUpdateSystem — 컨디션 동역학 (고용 2차, 2026-07-06: Energy부터)
    //
    //  시간 예산 모델의 "회복 투자" 축: 근무는 Energy를 소모하고 집 휴식이 회복한다.
    //  Energy는 개인 생산성의 당일 승수(WorkforceProductivitySystem이 읽음)라,
    //  휴식의 가치가 별도 보상 없이 자동 발생 — 쥐어짜기(긴 근무) vs 여유의 트레이드오프.
    //
    //  v1 범위: Energy만. Satiety(허기 연동)·Morale·Stress·Health는 해당 시스템이
    //  생길 때 같은 패턴(활동/욕구 → 컨디션 잡)으로 추가.
    //  게임시간 기반(TimeScale 반영) — HungerSystem과 동일 dt 소스.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ConditionUpdateSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;

            // ⚠ 게임 1시간 = SecondsPerDay/24 게임초(기본 50초) — 3600초 아님!
            //   (하드코딩 3600으로 Energy가 72배 느리게 흐르던 버그, 2026-07-07 실측 수정.)
            float secPerGameHour = math.max(1f, clock.SecondsPerDay) / 24f;

            state.Dependency = new EnergyTickJob { DtHours = gameDt / secPerGameHour }
                .ScheduleParallel(state.Dependency);
        }
    }

    // ── Energy: 근무 소모 / 집 휴식 회복 (그 외 활동은 중립) ────────────────────
    //   비율은 밸런스 노브(로직 우선 방침 — 상수로 시작): 10h 근무 ≈ −0.6, 14h 휴식 ≈ +1.4
    //   → 매일 완충 가능하되 과로(휴식 부족)가 누적되는 여지.
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct EnergyTickJob : IJobEntity
    {
        const float WorkDrainPerHour   = 0.06f;
        const float RestRecoverPerHour = 0.10f;

        public float DtHours;   // 이번 틱에 흐른 게임 '시간'(h) — 시스템이 환산해서 전달

        void Execute(ref CitizenConditions cond, in CitizenState st)
        {
            switch (st.Activity)
            {
                case CitizenActivity.AtWork:
                    cond.Energy = math.saturate(cond.Energy - WorkDrainPerHour * DtHours);
                    break;
                case CitizenActivity.AtHome:
                    cond.Energy = math.saturate(cond.Energy + RestRecoverPerHour * DtHours);
                    break;
                // 이동/식사/대기 = 중립(v1). 수면 욕구 도입 시 재조정.
            }
        }
    }
}
