using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  EducationSystem — 교육 욕구 전담: 증가 + 체류 해소 (방문형 학교, 2026-07-17)
    //
    //  BoredomSystem(공원)의 동형 복제 — 체류형 규약:
    //    · 발견(stamp: 학교 프리팹 StampSupplier.Relief=LowEducation)·이동·좌석
    //      (VisitorOccupancy)은 공통 파이프라인 그대로. 재화 소비 없음.
    //    · 해소 = 학교에 머무는 동안 시간 비례 적분 감소(수업 = 시간이 양자).
    //    · 조기 퇴장: 다 풀리면(Level≤0) ActionEndTime을 지금으로 당김 — Movement는
    //      타이머 만료만 본다(욕구 무지 유지). 최대 체류는 NeedDwell 표(2게임시간).
    //    · 영업시간 = NeedServiceHours(bit5, 8~16시) — Teacher 근무창(8~16)과 일치.
    //  파이프라인: [이 시스템](증가→해소) → NeedDecision(EducationUrgencyJob) →
    //  ServiceSearch → Movement. 두 잡 모두 시민 컴포넌트만 접근(건물 룩업 0).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(NeedDecisionSystem))]
    public partial struct EducationSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<GameClock>()) return;
            var clock = SystemAPI.GetSingleton<GameClock>();

            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지 → 증가·해소 모두 멈춤

            state.Dependency = new EducationTickJob   { Dt = gameDt }
                .ScheduleParallel(state.Dependency);
            state.Dependency = new EducationReliefJob { Dt = gameDt, Now = clock.TotalSeconds }
                .ScheduleParallel(state.Dependency);
        }
    }

    // ── 증가: 매 틱 누적(v1 능력치 보정 없음 — 밸런싱 #1 대상). ──
    [BurstCompile]
    public partial struct EducationTickJob : IJobEntity
    {
        public float Dt;

        void Execute(ref CitizenEducation edu)
            => edu.Level = math.saturate(edu.Level + edu.Rate * Dt);
    }

    // ── 해소: 체류 적분 — AtDestination + target이 이 욕구 해소처인 동안 감소. ──
    [BurstCompile]
    public partial struct EducationReliefJob : IJobEntity
    {
        // 체류 해소 배속(Boredom과 동일 8배) — 최대 체류(2게임시간)면 16시간치 상쇄.
        const float ReliefFactor = 8f;

        public float  Dt;
        public double Now;

        void Execute(ref CitizenEducation edu, ref CitizenState st, in ServiceTarget target)
        {
            if (st.Activity != CitizenActivity.AtDestination) return;
            if ((target.Relief & NeedType.LowEducation) == NeedType.None) return;

            edu.Level = math.saturate(edu.Level - edu.Rate * ReliefFactor * Dt);

            if (edu.Level <= 0f && Now < st.ActionEndTime)
                st.ActionEndTime = Now;   // 다 배움 → 조기 하교(자리 반납은 Movement가 처리)
        }
    }
}
