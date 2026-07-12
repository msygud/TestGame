using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  BoredomSystem — 따분함 욕구 전담: 증가 + 체류 해소 (체류형 v1, 2026-07-12)
    //
    //  체류형 = 방문형에서 재화를 뺀 것: 발견(stamp)·이동·좌석(VisitorOccupancy)은
    //  공통 파이프라인 그대로 타고, 해소만 다르다 —
    //    · 식당(재화 양자): 도착 시 재고 1 차감 + 머무름 종료 시 일괄 Level=0.
    //    · 공원(시간 양자): 재고 없음, **머무는 동안 시간 비례 적분 감소**.
    //
    //  조기 퇴장(체류형 규약): 다 풀리면(Level≤0) 머무름 타이머(ActionEndTime)를
    //  지금으로 당긴다 → 공통 Movement는 "타이머 만료"만 보고 떠난다(욕구 무지 유지).
    //  심심한 만큼 머물다 가므로 좌석 회전율이 수요를 자연 반영 — 용량(VisitorSlots)이
    //  공원의 실질 밸런싱 레버가 된다. 최대 체류는 NeedDwell 표(2게임시간)가 상한.
    //
    //  파이프라인: [이 시스템](증가→해소) → NeedDecision → ServiceSearch → Movement.
    //  두 잡 모두 자기(시민) 컴포넌트만 접근 — 건물 룩업 0(합의 골격).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(NeedDecisionSystem))]
    public partial struct BoredomSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<GameClock>()) return;
            var clock = SystemAPI.GetSingleton<GameClock>();

            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지 → 증가·해소 모두 멈춤

            state.Dependency = new BoredomTickJob   { Dt = gameDt }
                .ScheduleParallel(state.Dependency);
            state.Dependency = new BoredomReliefJob { Dt = gameDt, Now = clock.TotalSeconds }
                .ScheduleParallel(state.Dependency);
        }
    }

    // ── 증가: 매 틱 누적. v1 능력치 보정 없음(순정 Rate — 밸런싱 #1 대상). ──
    [BurstCompile]
    public partial struct BoredomTickJob : IJobEntity
    {
        public float Dt;

        void Execute(ref CitizenBoredom boredom)
            => boredom.Level = math.saturate(boredom.Level + boredom.Rate * Dt);
    }

    // ── 해소: 체류 적분 — AtDestination + target이 이 욕구 해소처인 동안 감소. ──
    //   완료 시 타이머 당김(조기 퇴장) — CitizenState 쓰기는 이 조기 퇴장 한 줄뿐이고,
    //   Movement보다 앞 순서라 dependency 체인으로 직렬화된다.
    [BurstCompile]
    public partial struct BoredomReliefJob : IJobEntity
    {
        // 체류 해소 배속: 증가율의 8배 — 최대 체류(2게임시간)면 16시간치 따분함을 상쇄.
        const float ReliefFactor = 8f;

        public float  Dt;
        public double Now;

        void Execute(ref CitizenBoredom boredom, ref CitizenState st, in ServiceTarget target)
        {
            if (st.Activity != CitizenActivity.AtDestination) return;
            if ((target.Relief & NeedType.LowEntertainment) == NeedType.None) return;

            boredom.Level = math.saturate(boredom.Level - boredom.Rate * ReliefFactor * Dt);

            if (boredom.Level <= 0f && Now < st.ActionEndTime)
                st.ActionEndTime = Now;   // 다 풀림 → 조기 퇴장(자리 반납은 Movement가 처리)
        }
    }
}
