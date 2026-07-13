using Unity.Burst;
using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  NeedDecisionSystem (A-3) — "이번에 추구할 욕구" 결정 (2패스 잡, 2026-07-06)
    //
    //  역할: 결정 가능한 상태(Idle/AtHome)의 시민에 대해 활성 욕구 중 "가장 급한" 하나를
    //  매 프레임 Pursuing에 (재)선택. 파이프라인: HungerSystem(증가) → [이 시스템] →
    //  ServiceSearch → Movement.
    //
    //  ⚠ 재선택(2026-07-13, #1 영구 잠금 픽스): 구현은 Pursuing이 이미 있으면 재결정을
    //  건너뛰었다(재결정 방지). 그러나 그 탓에 "공급자를 못 찾은 욕구"가 Pursuing에 영구
    //  고착 → 다른 모든 욕구를 막고(허기도 못 감) 불사 시민이 영영 굶는 잠금이 있었다.
    //  픽스 = **결정 가능 상태이면 매 프레임 가장 급한 활성 욕구로 재선택**(Idle/AtHome 게이트는
    //  유지 — 이동/도착 중엔 잡이 안 돎). 성공해 출발하면 같은 프레임에 Traveling이 되어
    //  다음 프레임 재선택 대상에서 빠지므로, 재선택은 오직 "막혀서 못 떠난" 시민에게만
    //  일어난다 = 잠금 없이 최급 욕구가 항상 승리(수요는 CollectDemandJob이 Pursuing 기준
    //  독립 발행 — "막히면 자리 날 때까지 요청" 유지).
    //
    //  질병 = 상태(2026-07-13): 정상 욕구 잡은 WithDisabled<DiseasedTag>로 앓는 시민을 자동
    //  제외하고, DiseaseRouteJob(WithAll<DiseasedTag>)이 "다른 생활로직"(근무 중단·병원 직행)을
    //  수행한다. 구 SicknessUrgencyJob(욕구 긴급도 경쟁)은 은퇴.
    //
    //  우선순위(다중 활성 시): 긴급도(Level − Threshold) 최대. 동률이면 먼저 스케줄된
    //  욕구 잡의 값 유지(> 비교 — 결정적).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(HungerSystem))]
    [UpdateBefore(typeof(ServiceSearchSystem))]
    public partial struct NeedDecisionSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // ① 욕구별 긴급도 후보 갱신 — 욕구 추가 시 여기 한 줄씩. (앓는 시민 제외)
            state.Dependency = new HungerUrgencyJob().ScheduleParallel(state.Dependency);
            state.Dependency = new BoredomUrgencyJob().ScheduleParallel(state.Dependency);

            // ② 공통 선택 — 후보 소비(재선택) + 리셋. (앓는 시민 제외)
            state.Dependency = new NeedSelectJob().ScheduleParallel(state.Dependency);

            // ③ 질병 상태 라우팅(별도 쿼리) — 앓는 시민은 모든 것 중단하고 병원 강제 추구.
            state.Dependency = new DiseaseRouteJob().ScheduleParallel(state.Dependency);
        }
    }

    // ── ① [Hunger] 긴급도 후보 — Hunger 보유 & 비질병 시민만 순회(팩션·상태 자동 필터) ──
    [BurstCompile]
    [WithDisabled(typeof(DiseasedTag))]
    public partial struct HungerUrgencyJob : IJobEntity
    {
        void Execute(ref CitizenNeeds needs, in Hunger hunger, in CitizenState st)
        {
            // 결정 가능 상태만 후보 생성(재선택 대상). 이동/도착 중엔 건드리지 않음.
            var act = st.Activity;
            if (act != CitizenActivity.Idle && act != CitizenActivity.AtHome) return;

            if (!hunger.IsActive) return;
            float urgency = hunger.Level - hunger.Threshold;
            if (urgency > needs.CandidateUrgency)
            {
                needs.CandidateNeed    = NeedType.Hunger;
                needs.CandidateUrgency = urgency;
            }
        }
    }

    // ── ① [Boredom] 긴급도 후보 — 체류형(2026-07-12). Hunger와 동형. ──
    [BurstCompile]
    [WithDisabled(typeof(DiseasedTag))]
    public partial struct BoredomUrgencyJob : IJobEntity
    {
        void Execute(ref CitizenNeeds needs, in CitizenBoredom boredom, in CitizenState st)
        {
            var act = st.Activity;
            if (act != CitizenActivity.Idle && act != CitizenActivity.AtHome) return;

            if (!boredom.IsActive) return;
            float urgency = boredom.Level - boredom.Threshold;
            if (urgency > needs.CandidateUrgency)
            {
                needs.CandidateNeed    = NeedType.LowEntertainment;
                needs.CandidateUrgency = urgency;
            }
        }
    }

    // ── ② 공통 선택(재선택) — 결정 가능 상태이면 Pursuing = 후보(가장 급한 활성 욕구).
    //    후보 None(활성 욕구 없음) = Pursuing None으로 정리(막힌 욕구 자연 해제). 리셋. ──
    [BurstCompile]
    [WithDisabled(typeof(DiseasedTag))]
    public partial struct NeedSelectJob : IJobEntity
    {
        void Execute(ref CitizenNeeds needs, in CitizenState st)
        {
            var act = st.Activity;
            if (act == CitizenActivity.Idle || act == CitizenActivity.AtHome)
                needs.Pursuing = needs.CandidateNeed;   // 재선택(잠금 방지) — None이면 정리

            needs.CandidateNeed    = NeedType.None;   // 소비 후 리셋(다음 프레임 재계산)
            needs.CandidateUrgency = 0f;
        }
    }

    // ── ③ 질병 상태 라우팅(2026-07-13) — 앓는 시민(WithAll<DiseasedTag>)의 "다른 생활로직":
    //    근무 중이면 즉시 중단(AtWork→Idle) + 결정 가능하면 병원(Disease) 강제 추구.
    //    ServiceSearch가 Disease relief(병원)를 찾고, 못 찾으면 Movement가 귀가+재요청
    //    (막히면 집으로) + CollectDemandJob이 병원 건설 수요 발행. ──
    [BurstCompile]
    [WithAll(typeof(DiseasedTag))]
    public partial struct DiseaseRouteJob : IJobEntity
    {
        void Execute(ref CitizenState st, ref CitizenNeeds needs)
        {
            if (st.Activity == CitizenActivity.AtWork)
                st.Activity = CitizenActivity.Idle;   // 모든 것 그만두고(근무 중단)

            var act = st.Activity;
            if (act == CitizenActivity.Idle || act == CitizenActivity.AtHome)
                needs.Pursuing = NeedType.Disease;    // 병원 강제 추구(욕구 경쟁 무관)
        }
    }
}
