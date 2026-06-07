using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  NeedDecisionSystem (A-3) — "이번에 추구할 욕구" 결정
    //
    //  끊겨 있던 시민 루프의 keystone. 지금까지 CitizenNeeds.Pursuing을 실제 욕구로
    //  set하는 곳이 없어(스폰에서 None만) ServiceSearchSystem이 전원 None을 반환했고,
    //  그 결과 이동/도착이 한 번도 일어나지 않았다. 이 시스템이 그 공백을 메운다.
    //
    //  역할:
    //    시민이 결정 가능한 상태(Idle/AtHome)이고 아직 추구 욕구가 없으면
    //    (Pursuing==None), 활성 욕구 중 "가장 급한" 하나를 골라 Pursuing에 set.
    //    이후 파이프라인:
    //      HungerSystem(증가) → [이 시스템](Pursuing set) → ServiceSearchSystem
    //      (stamp에서 공급자 탐색 → ServiceTarget) → CitizenMovementSystem(이동/도착).
    //
    //  우선순위(다중 활성 시):
    //    초과량(Level − Threshold)이 가장 큰 욕구 = 가장 급한 것. 동률이면 먼저
    //    검사된 욕구. 단순하게 시작 — 추후 가중치/욕구별 우선도로 고도화 가능.
    //
    //  팩션 비대칭 대응(중요):
    //    욕구는 종류별 개별 컴포넌트이고, 팩션마다 가진 욕구 "조합"이 다르다
    //    (휴먼 {Hunger, …} vs 메카닉 {EnergyLevel}). 따라서 특정 욕구 컴포넌트를
    //    쿼리에 강제하지 않는다 — 그러면 그 컴포넌트가 없는 팩션 시민이 통째로
    //    누락된다. 공통(CitizenNeeds/CitizenState)만 쿼리하고, 각 욕구는
    //    HasComponent로 "있으면 검사"한다. 욕구 추가 = 아래 Consider 블록 하나 추가.
    //    (공통 시스템은 결과 컴포넌트 Pursuing에만 의존 — 메모리 불변식 준수.)
    //
    //  주쿼리 = 변하는 데이터: CitizenState(Activity) + CitizenNeeds(Pursuing). 둘 다 핫.
    //
    //  ※ 게이팅: 현재는 매 프레임 + 쿼리/early-skip로 1차 필터(Idle·AtHome,
    //    Pursuing==None인 시민만 실제 처리). ServiceSearch/StampRebuild와 동일하게
    //    저빈도화(스태거링/이벤트)는 후속(§10).
    //  ※ Pursuing을 여기서 비우지 않는다 — clear는 해소/리셋(A-2b)이 담당.
    //    Pursuing!=None이면 이미 추구·이동 중이므로 건드리지 않는다(재결정 방지).
    //  ※ Burst 미적용: 욕구 조합이 팩션별로 달라 HasComponent 분기를 쓰며, 저빈도
    //    결정이라 비용이 작다(ServiceSearchSystem과 동일한 메인스레드 방식).
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
            foreach (var (needsRW, stateRO, entity) in
                     SystemAPI.Query<RefRW<CitizenNeeds>, RefRO<CitizenState>>()
                         .WithAll<CitizenTag>()
                         .WithEntityAccess())
            {
                // 이미 추구 중이면(이동/도착 인계 중) 건드리지 않음 — 재결정 방지.
                if (needsRW.ValueRO.Pursuing != NeedType.None)
                    continue;

                // 결정 가능한 상태에서만(이동 중·근무 중 등은 제외).
                var act = stateRO.ValueRO.Activity;
                if (act != CitizenActivity.Idle && act != CitizenActivity.AtHome)
                    continue;

                // ── 활성 욕구 중 가장 급한 것 선택 ─────────────────────────
                //   초과량(Level − Threshold) 최대. 욕구 추가 시 Consider 호출만 늘림.
                NeedType best        = NeedType.None;
                float    bestUrgency = 0f;

                // [Hunger] — A-1의 유일한 욕구. 컴포넌트 없는 팩션 시민은 자동 스킵.
                if (SystemAPI.HasComponent<Hunger>(entity))
                {
                    var h = SystemAPI.GetComponent<Hunger>(entity);
                    if (h.IsActive)
                        Consider(NeedType.Hunger, h.Level - h.Threshold,
                                 ref best, ref bestUrgency);
                }

                // ── 욕구 추가 위치 (같은 패턴 복사) ──────────────────────────
                //   if (SystemAPI.HasComponent<Thirst>(entity)) {
                //       var t = SystemAPI.GetComponent<Thirst>(entity);
                //       if (t.IsActive) Consider(NeedType.Thirst, t.Level - t.Threshold,
                //                                 ref best, ref bestUrgency);
                //   }
                //   메카닉: EnergyLevel 등도 동일하게.

                // 활성 욕구가 하나도 없으면 Pursuing은 None 유지(검색도 자동 스킵).
                if (best != NeedType.None)
                    needsRW.ValueRW.Pursuing = best;
            }
        }

        // 후보 비교: 더 급한(초과량 큰) 욕구로 갱신. 동률은 먼저 검사된 쪽 유지.
        static void Consider(NeedType need, float urgency,
                             ref NeedType best, ref float bestUrgency)
        {
            if (urgency > bestUrgency)
            {
                best        = need;
                bestUrgency = urgency;
            }
        }
    }
}
