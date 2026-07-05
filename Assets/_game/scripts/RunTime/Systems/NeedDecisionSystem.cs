using Unity.Burst;
using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  NeedDecisionSystem (A-3) — "이번에 추구할 욕구" 결정 (2패스 잡, 2026-07-06)
    //
    //  역할: 시민이 결정 가능한 상태(Idle/AtHome)이고 아직 추구 욕구가 없으면
    //  (Pursuing==None), 활성 욕구 중 "가장 급한" 하나를 골라 Pursuing에 set.
    //  파이프라인: HungerSystem(증가) → [이 시스템] → ServiceSearch → Movement.
    //
    //  실행 모델 — 2패스 Burst 잡 (구 메인스레드 HasComponent 랜덤 액세스는
    //  인구 1.8만에서 정체 실측 → 교체. CLAUDE.md 시민 설계 절에 예정돼 있던 전환):
    //    ① 욕구별 긴급도 잡 — 그 욕구 컴포넌트를 "가진" 시민 청크만 선형 순회,
    //       활성이고 더 급하면 CitizenNeeds.Candidate{Need,Urgency}를 갱신.
    //       욕구 추가 = 잡 하나 추가 + 아래 스케줄 한 줄(팩션 비대칭은 쿼리가 처리 —
    //       그 컴포넌트 없는 팩션 시민은 청크째 제외).
    //    ② 공통 선택 잡 — Candidate를 소비해 Pursuing set(결정 가능 상태일 때만),
    //       후보 리셋. 욕구 타입을 모름(NeedType 비트만) — 메모리 불변식 준수.
    //    두 패스 모두 CitizenNeeds RW → state.Dependency로 자동 직렬 체이닝.
    //    후속 ServiceSearch(메인스레드 foreach)가 CitizenNeeds 접근 시 자동 완료 대기
    //    (Burst 잡 2개라 대기 사실상 0 — 구 구현의 메인스레드 ms급 정체 제거).
    //
    //  우선순위(다중 활성 시): 긴급도(Level − Threshold) 최대. 동률이면 먼저
    //  스케줄된 욕구 잡의 값 유지(> 비교 — 나중 잡이 동률로 못 뺏음, 결정적).
    //
    //  ※ Pursuing을 여기서 비우지 않는다 — clear는 해소/리셋(Movement A-2b)이 담당.
    //    Pursuing!=None이면 이미 추구·이동 중이므로 건드리지 않는다(재결정 방지).
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
            // ① 욕구별 긴급도 후보 갱신 — 욕구 추가 시 여기 한 줄씩.
            state.Dependency = new HungerUrgencyJob().ScheduleParallel(state.Dependency);
            //  (미래) state.Dependency = new ThirstUrgencyJob().ScheduleParallel(state.Dependency);
            //  (메카닉) EnergyLevel 등도 동일 패턴.

            // ② 공통 선택 — 후보 소비 + 리셋.
            state.Dependency = new NeedSelectJob().ScheduleParallel(state.Dependency);
        }
    }

    // ── ① [Hunger] 긴급도 후보 — Hunger 보유 시민 청크만 순회(팩션 자동 필터) ──
    [BurstCompile]
    public partial struct HungerUrgencyJob : IJobEntity
    {
        void Execute(ref CitizenNeeds needs, in Hunger hunger, in CitizenState st)
        {
            // 이미 추구 중이거나 결정 불가 상태면 후보 생성 불필요(선택 잡도 무시하지만
            // 여기서 걸러 쓰기 자체를 줄인다 — 대다수 시민이 이 분기로 빠짐).
            if (needs.Pursuing != NeedType.None) return;
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

    // ── ② 공통 선택 — 후보 소비(Pursuing set) + 리셋. 욕구 타입 무지(비트만). ──
    [BurstCompile]
    public partial struct NeedSelectJob : IJobEntity
    {
        void Execute(ref CitizenNeeds needs, in CitizenState st)
        {
            if (needs.CandidateNeed == NeedType.None) return;

            if (needs.Pursuing == NeedType.None)
            {
                var act = st.Activity;
                if (act == CitizenActivity.Idle || act == CitizenActivity.AtHome)
                    needs.Pursuing = needs.CandidateNeed;
            }

            needs.CandidateNeed    = NeedType.None;   // 소비 후 리셋(다음 프레임 재계산)
            needs.CandidateUrgency = 0f;
        }
    }
}
