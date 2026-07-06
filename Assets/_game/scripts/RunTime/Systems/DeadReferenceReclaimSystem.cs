using Unity.Collections;
using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DeadReferenceReclaimSystem — 죽은 건물을 가리키는 시민 참조 복구
    // ──────────────────────────────────────────────────────────────────────────
    //  건물은 전투/raze/영토 전환(capture=파괴)으로 수시로 죽는다. 참조하는 쪽이
    //  가드된 룩업이라 예외는 없지만, 복구가 없으면 '영구 고아'가 된다:
    //    · Home/Work가 죽은 시민 — UnassignedTag가 없어 재배정 큐에 다시 안 들어감.
    //    · 죽은 공급자(ServiceTarget)로 여행 중인 시민 — 영원히 도착 못 함.
    //    · 죽은 건물 '안'에 있는 시민(CurrentBuilding) — 활동이 죽은 앵커에 고정.
    //
    //  복구 규칙(1초 주기 스캔 — 시민 수천 × Exists 몇 회라 저비용):
    //    · Home/Work 죽음 → 해당 슬롯 Null + UnassignedTag 재부착(재배정 큐 복귀).
    //    · ServiceTarget.Supplier 죽음 → Null(결정 시스템이 재탐색).
    //    · CurrentBuilding 죽음 → Null + Activity=Idle(즉시 재결정).
    //    · 위 중 하나라도 죽었고 Traveling 중이면 → Idle(죽은 목적지로의 여행 취소.
    //      목적지가 명시 저장되지 않아 보수적으로 리셋 — 재결정 1회면 회복).
    //  ※ 캐리어는 복구 불필요 — 목적지가 엔티티가 아닌 '셀 경로'(순수 비주얼).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct DeadReferenceReclaimSystem : ISystem
    {
        double _nextPass;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenTag>();
            _nextPass = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextPass) return;
            _nextPass = now + 1.0;

            var em  = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (resRW, stRW, e) in
                     SystemAPI.Query<RefRW<CitizenResidence>, RefRW<CitizenState>>()
                              .WithAll<CitizenTag>().WithEntityAccess())
            {
                bool anyDead = false;

                // 집/직장 — 죽었으면 비우고 각자의 재배정 큐로 복귀(큐 분리, 2026-07-06).
                ref var res = ref resRW.ValueRW;
                if (res.Home != Entity.Null && !em.Exists(res.Home))
                {
                    res.Home = Entity.Null; anyDead = true;
                    if (!SystemAPI.HasComponent<UnassignedTag>(e))
                        ecb.AddComponent<UnassignedTag>(e);          // 주거 대기 큐
                }
                if (res.Work != Entity.Null && !em.Exists(res.Work))
                {
                    res.Work = Entity.Null; anyDead = true;
                    if (SystemAPI.HasComponent<JobData>(e))
                        SystemAPI.GetComponentRW<JobData>(e).ValueRW.Job = JobType.Unemployed;
                    if (!SystemAPI.HasComponent<JobSeekerTag>(e))
                        ecb.AddComponent<JobSeekerTag>(e);           // 고용 대기 큐(재고용)
                }

                // 추구 중인 공급자 — 죽었으면 재탐색 유도.
                if (SystemAPI.HasComponent<ServiceTarget>(e))
                {
                    ref var tgt = ref SystemAPI.GetComponentRW<ServiceTarget>(e).ValueRW;
                    if (tgt.Supplier != Entity.Null && !em.Exists(tgt.Supplier))
                    { tgt.Supplier = Entity.Null; anyDead = true; }
                }

                // 현재 건물 / 여행 취소 — 죽은 앵커·죽은 목적지에서 벗어나 재결정.
                ref var st = ref stRW.ValueRW;
                if (st.CurrentBuilding != Entity.Null && !em.Exists(st.CurrentBuilding))
                {
                    st.CurrentBuilding = Entity.Null;
                    st.Activity = CitizenActivity.Idle;
                }
                else if (anyDead && st.Activity == CitizenActivity.Traveling)
                {
                    // Service 이동 취소면 방문 예약 해제 + target 비움(좌석 누수 방지, 2026-07-07).
                    //   공급자 자체가 죽은 경우는 엔티티와 함께 좌석도 소멸 — 해제 불필요.
                    if (st.Purpose == TravelPurpose.Service && SystemAPI.HasComponent<ServiceTarget>(e))
                    {
                        ref var tgt2 = ref SystemAPI.GetComponentRW<ServiceTarget>(e).ValueRW;
                        if (tgt2.Supplier != Entity.Null && em.Exists(tgt2.Supplier)
                            && SystemAPI.HasComponent<VisitorOccupancy>(tgt2.Supplier))
                        {
                            ref var vo = ref SystemAPI
                                .GetComponentRW<VisitorOccupancy>(tgt2.Supplier).ValueRW;
                            vo.Release();
                        }
                        tgt2 = ServiceTarget.None;
                    }
                    st.Activity = CitizenActivity.Idle;
                    st.Purpose  = TravelPurpose.None;
                }

                // 좌초 복구(2026-07-06): Idle인데 기준 건물이 없고 집은 살아있음 → 집에 앉힘.
                //   CurrentBuilding이 없으면 ServiceSearch 출발점이 없어 욕구를 영영 해소 못 함
                //   (실측: 인구 1.8만 중 UNMET 40 잔류 = 전부 이 상태). 위 복구가 Idle+Null로
                //   내려놓기만 하고 '재착석' 경로가 없던 공백을 메운다.
                if (st.Activity == CitizenActivity.Idle && st.CurrentBuilding == Entity.Null
                    && res.Home != Entity.Null && em.Exists(res.Home))
                {
                    st.CurrentBuilding = res.Home;
                    st.Activity        = CitizenActivity.AtHome;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
