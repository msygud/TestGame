using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CitizenMovementSystem (A-2a/A-2b) — 이동 골격 + 도착 후 리셋(공통)
    //
    //  목적지(ServiceTarget)가 정해진 시민을 Traveling으로 보내고, 거리 기반 도착
    //  타이머가 만료되면 AtDestination으로 도착시킨 뒤, 머무름(서비스) 시간이 지나면
    //  추구/목적지를 비우고 귀가시켜 사이클을 닫는다.
    //
    //  ※ 물리 이동 없음 — 타이머만. LocalTransform 미사용(시각 이동은 후속).
    //  ※ 게임시간 기반: 도착/머무름 시각 = GameClock.TotalSeconds + 소요초.
    //    일시정지(TimeScale=0)면 TotalSeconds가 안 흘러 자동으로 멈춤.
    //
    //  상태 전이:
    //    (Idle | AtHome) + ServiceTarget.Has
    //        → Traveling,      ActionEndTime = now + Dist*SecPerCell, CurBuilding=Null
    //    Traveling:  now >= ActionEndTime
    //        → AtDestination,  ActionEndTime = now + EatSeconds,      CurBuilding=Supplier
    //    AtDestination:  now >= ActionEndTime  (= 머무름 종료)  ← A-2b
    //        → [리셋] Pursuing=None, ServiceTarget=None,
    //          귀가(집 있으면 AtHome+CurBuilding=Home, 없으면 Idle 제자리)
    //
    //  팩션 경계(중요):
    //    이 시스템은 "상태 전이/리셋"만 한다(공통, 팩션 무관). 실제 욕구 Level 감소는
    //    욕구별 시스템이 담당한다 — 예: Hunger 해소는 휴먼 트랙의 HungerSystem가
    //    같은 프레임에서 이 시스템보다 먼저(파이프라인 앞) 처리한다. 따라서 여기서는
    //    Hunger 등 팩션 고유 타입을 절대 참조하지 않는다. 리셋은 어떤 욕구였는지와
    //    무관하게 Pursuing/ServiceTarget만 비운다(메모리 불변식).
    //
    //  주쿼리 = 변하는 데이터: CitizenState(Activity) + ServiceTarget + CitizenNeeds.
    //    CitizenResidence(귀가 목적지)는 콜드 — HasComponent로 옵셔널 조회
    //    (없으면 제자리 Idle 폴백 → 테스트/노숙 시민도 안 깨짐).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServiceSearchSystem))]
    public partial struct CitizenMovementSystem : ISystem
    {
        // 도로 한 칸당 이동 게임초(정적). 추후 교통통계(혼잡도)로 동적 보정.
        const double SecPerCell = 0.5;

        // 목적지에서 머무는(서비스/식사) 게임초. 추후 서비스 def별 시간으로 분리.
        const double EatSeconds = 3.0;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.GetSingleton<GameClock>().TotalSeconds;

            foreach (var (stateRW, targetRW, needsRW, entity) in
                     SystemAPI.Query<RefRW<CitizenState>, RefRW<ServiceTarget>,
                                     RefRW<CitizenNeeds>>()
                         .WithAll<CitizenTag>()
                         .WithEntityAccess())
            {
                ref var cs = ref stateRW.ValueRW;

                switch (cs.Activity)
                {
                    // ── 목적지가 정해졌고 대기/집 → 출발 ──────────────────
                    case CitizenActivity.Idle:
                    case CitizenActivity.AtHome:
                        if (targetRW.ValueRO.Has)
                        {
                            cs.Activity        = CitizenActivity.Traveling;
                            cs.ActionEndTime   = now + targetRW.ValueRO.Dist * SecPerCell;
                            cs.CurrentBuilding = Entity.Null;          // 이동 중
                        }
                        break;

                    // ── 이동 중 → 타이머 만료 시 도착 + 머무름 타이머 시작 ──
                    case CitizenActivity.Traveling:
                        if (now >= cs.ActionEndTime)
                        {
                            cs.Activity        = CitizenActivity.AtDestination;
                            cs.CurrentBuilding = targetRW.ValueRO.Supplier; // 공급자 도착
                            cs.ActionEndTime   = now + EatSeconds;          // 머무름 시작
                        }
                        break;

                    // ── 머무름 종료 → [공통] 리셋 + 귀가 (A-2b) ────────────
                    //   욕구 Level 감소는 욕구별 시스템(휴먼 HungerSystem)이 같은
                    //   프레임 앞에서 이미 처리. 여기선 상태만 정리한다.
                    case CitizenActivity.AtDestination:
                        if (now >= cs.ActionEndTime)
                        {
                            needsRW.ValueRW.Pursuing = NeedType.None;        // 추구 종료
                            targetRW.ValueRW         = ServiceTarget.None;   // 목적지 비움

                            // 귀가: 집이 있으면 집으로(다음 검색의 출발점),
                            //       없으면 제자리에서 Idle(폴백).
                            Entity home = Entity.Null;
                            if (SystemAPI.HasComponent<CitizenResidence>(entity))
                                home = SystemAPI.GetComponent<CitizenResidence>(entity).Home;

                            if (home != Entity.Null)
                            {
                                cs.Activity        = CitizenActivity.AtHome;
                                cs.CurrentBuilding = home;
                            }
                            else
                            {
                                cs.Activity = CitizenActivity.Idle;   // CurrentBuilding 유지
                            }
                        }
                        break;

                    // 그 외(AtWork/Stuck)는 여기서 다루지 않음.
                    default:
                        break;
                }
            }
        }
    }
}
