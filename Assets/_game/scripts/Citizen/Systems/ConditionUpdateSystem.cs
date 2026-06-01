using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage B — 컨디션 갱신 (Condition Update)
    //
    //  욕구 게이지(부정: 높을수록 나쁨)에서 컨디션(대부분 긍정: 높을수록 좋음)을
    //  파생한다. 컨디션은 독립 변수가 아니라 "욕구의 결과".
    //
    //  설계(§0):
    //    - 주쿼리 = NeedElement(버퍼, 읽기) + CitizenConditions(쓰기). 둘 다 핫.
    //    - 매핑(어느 욕구→어느 컨디션)은 코드(정적). 게임 로직 본질이라 박음.
    //      증가율·가중치 같은 파라미터는 추후 Blob/SO로 분리(밸런싱 대상).
    //    - IJobEntity + Burst 병렬.
    //
    //  매핑 (현재 NeedType에 정의된 욕구 기준):
    //    Satiety(포만도)  = 1 - Hunger
    //    Morale(사기)     = 1 - avg(LowEntertainment, LowReligion, LowEducation)
    //    Stress(스트레스)  = 활성 생활욕구 누적 (부정 방향 유지: 높을수록 나쁨)
    //    Health(신체건강)  = 1 - (Hunger 영향) — 굶주림이 건강 악화 (단순화)
    //    Loyalty(충성도)  = 전반 만족 (1 - 전체 평균)
    //    Energy(활력)     = Sleep 욕구 미정의 → 일단 Stress 역으로 근사
    //
    //  확장 메모:
    //    - Sleep/Thirst NeedType 추가 시 Energy/Satiety 매핑 정교화.
    //    - 직업군별 생산성 계수는 별도 CitizenProductivity로(Stage F).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NeedTickSystem))]
    public partial struct ConditionUpdateSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            new ConditionUpdateJob().ScheduleParallel();
        }
    }

    [BurstCompile]
    public partial struct ConditionUpdateJob : IJobEntity
    {
        void Execute(
            ref CitizenConditions          cond,
            in  DynamicBuffer<NeedElement>  gauges)
        {
            // 게이지 추출(없으면 0 = 만족).
            float hunger        = Get(gauges, NeedType.Hunger);
            float entertainment = Get(gauges, NeedType.LowEntertainment);
            float religion      = Get(gauges, NeedType.LowReligion);
            float education     = Get(gauges, NeedType.LowEducation);
            float homeless      = Get(gauges, NeedType.Homeless);
            float unemployed    = Get(gauges, NeedType.Unemployed);

            // ── 포만도: 배고픔의 역 ──────────────────────────────────────
            cond.Satiety = 1f - hunger;

            // ── 사기: 사회·자아 욕구 평균의 역 ──────────────────────────
            float socialBad = (entertainment + religion + education) / 3f;
            cond.Morale = 1f - socialBad;

            // ── 스트레스: 생활 욕구 누적(부정 방향, 높을수록 나쁨) ──────
            //  주거·고용 불안이 스트레스에 크게 기여.
            float stress = (hunger * 0.3f)
                         + (homeless * 0.3f)
                         + (unemployed * 0.2f)
                         + (socialBad * 0.2f);
            cond.Stress = math.saturate(stress);

            // ── 신체건강: 굶주림이 악화시킴(단순화) ─────────────────────
            cond.Health = 1f - (hunger * 0.5f);

            // ── 활력: Sleep 욕구 미정의 → 스트레스 역으로 근사 ──────────
            cond.Energy = 1f - (cond.Stress * 0.5f);

            // ── 충성도: 전반 만족(전체 평균의 역) ───────────────────────
            float overallBad = (hunger + homeless + unemployed
                              + entertainment + religion + education) / 6f;
            cond.Loyalty = 1f - overallBad;

            // 안전 클램프
            cond.Satiety = math.saturate(cond.Satiety);
            cond.Morale  = math.saturate(cond.Morale);
            cond.Health  = math.saturate(cond.Health);
            cond.Energy  = math.saturate(cond.Energy);
            cond.Loyalty = math.saturate(cond.Loyalty);
        }

        // 버퍼에서 특정 욕구 게이지 추출(없으면 0).
        static float Get(in DynamicBuffer<NeedElement> gauges, NeedType type)
        {
            for (int i = 0; i < gauges.Length; i++)
                if (gauges[i].Type == type)
                    return gauges[i].Level;
            return 0f;
        }
    }
}
