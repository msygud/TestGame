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
    //  동역학(Energy — 매 프레임) + **욕구 투영(느슨한 주기 ~1초, 2026-07-12 합의 골격)**:
    //  소비자(생산·미래 전투)는 원천 욕구(Hunger/CitizenCivic)를 룩업하지 않는다 —
    //  여기서 만족도를 CitizenConditions 스냅샷에 복사하고 소비자는 그것만 청크-선형
    //  으로 읽는다(룩업 0). 팩션 비대칭도 여기서 흡수(욕구 미보유 = 중립 1).
    //  새 욕구 추가 = 투영 잡 1개(소비자 무수정). Morale·Stress·Health는 해당 시스템이
    //  생길 때 같은 패턴으로 추가. 게임시간 기반(TimeScale 반영) — HungerSystem과 동일 dt.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct ConditionUpdateSystem : ISystem
    {
        double _nextProj;

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

            // ── 욕구 → 컨디션 투영(느슨한 주기) — 셋 다 CitizenConditions 쓰기라 순차 체인.
            //   DefaultSafetyJob은 구세이브의 신설 필드 0(최악) 유입도 1틱 안에 중립 교정.
            double now = SystemAPI.Time.ElapsedTime;
            if (now >= _nextProj)
            {
                _nextProj = now + 1.0;
                var h1 = new SyncSatietyJob().ScheduleParallel(state.Dependency);
                var h2 = new SyncSafetyJob().ScheduleParallel(h1);
                var h3 = new DefaultSafetyJob().ScheduleParallel(h2);
                state.Dependency = new SyncHealthJob().ScheduleParallel(h3);
            }
        }
    }

    // ── 욕구 만족도 투영(합의 골격) — 증가/해소는 각 욕구 전담 시스템 소관, 여기는 복사만 ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SyncSatietyJob : IJobEntity
    {
        void Execute(ref CitizenConditions cond, in Hunger hunger)
            => cond.Satiety = math.saturate(1f - hunger.Level);
    }

    // 안심도 원천 = 공무불만(CitizenCivic — 치안·소방·환경·행정 가중합, 2026-07-17 통합.
    //   구 CitizenSafety 단독 투영 대체 — 축 이름(Safety)과 소비자는 그대로).
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SyncSafetyJob : IJobEntity
    {
        void Execute(ref CitizenConditions cond, in CitizenCivic civic)
            => cond.Safety = math.saturate(1f - civic.Level);
    }

    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    [WithNone(typeof(CitizenCivic))]
    public partial struct DefaultSafetyJob : IJobEntity
    {
        void Execute(ref CitizenConditions cond) => cond.Safety = 1f;
    }

    // 건강 = 1 − 병세(생애주기 v1) — 결근 게이트(WorkforceProductivity)가 소비.
    //   질병 컴포넌트는 AgingSystem 마이그레이션이 전 시민에 보장 → 기본값 잡 불요.
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SyncHealthJob : IJobEntity
    {
        void Execute(ref CitizenConditions cond, in CitizenSickness sickness)
            => cond.Health = math.saturate(1f - sickness.Level);
    }

    // ── Energy: 근무 소모 / 집 휴식 회복 (그 외 활동은 중립) ────────────────────
    //   비율은 밸런스 노브(로직 우선 방침 — 상수로 시작): 10h 근무 ≈ −0.6, 14h 휴식 ≈ +1.4
    //   → 매일 완충 가능하되 과로(휴식 부족)가 누적되는 여지.
    //   나이 가산(생애주기 v1, 2026-07-12): 40세부터 근무 드레인 가중(60세 1.4배, 90세 2배)
    //   — 고령 노동력은 같은 교대에도 컨디션이 빨리 깎여 staff 기여가 하락(관리 비용 축).
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct EnergyTickJob : IJobEntity
    {
        const float WorkDrainPerHour   = 0.06f;
        const float RestRecoverPerHour = 0.10f;
        const float AgeDrainPerYear    = 0.02f;   // 40세 초과 1세당 드레인 +2%

        public float DtHours;   // 이번 틱에 흐른 게임 '시간'(h) — 시스템이 환산해서 전달

        void Execute(ref CitizenConditions cond, in CitizenState st, in CitizenAge age)
        {
            switch (st.Activity)
            {
                case CitizenActivity.AtWork:
                {
                    float ageFactor = 1f + math.max(0f, age.Years - 40f) * AgeDrainPerYear;
                    cond.Energy = math.saturate(cond.Energy - WorkDrainPerHour * ageFactor * DtHours);
                    break;
                }
                case CitizenActivity.AtHome:
                    cond.Energy = math.saturate(cond.Energy + RestRecoverPerHour * DtHours);
                    break;
                // 이동/식사/대기 = 중립(v1). 수면 욕구 도입 시 재조정.
            }
        }
    }
}
