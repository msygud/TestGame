using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  HungerSystem (휴먼 트랙) — 배고픔 욕구 전담: 증가 + [A-2b]해소
    //
    //  "욕구별 시스템"의 첫 사례. 한 시스템이 한 욕구의 증가와 해소를 책임진다
    //  (확장성·관심사 분리). Hunger는 휴먼 팩션의 욕구 — 메카닉(EnergyLevel 등)은
    //  자기 시스템에서 동일 패턴으로 처리하고, 공통 시스템은 결과만 본다.
    //
    //  증가(A-1):  매 틱 Level += Rate * resilienceFactor * gameDt. 게임시간 기반
    //              (TimeScale 반영). CitizenAttributes(인내심)에 의존.
    //  해소(A-2b): 도착해 머무름(식사)이 끝나면 Level=0(포만).
    //              조건: 이번 ServiceTarget.Relief가 Hunger 해소처 + AtDestination +
    //                    now >= ActionEndTime(머무름 종료).
    //              ※ CitizenAttributes에 의존하지 않는다 — 해소는 능력치와 무관.
    //                (증가 잡과 컴포넌트 요구를 분리해야 능력치 없는 시민도 해소됨.)
    //
    //  상태 리셋(Pursuing/ServiceTarget 비움·귀가)은 여기서 하지 않는다 — 그건
    //  공통 CitizenMovementSystem이 같은 프레임 뒤(파이프라인 후단)에서 담당한다.
    //  여기선 오직 Hunger 값만 내린다.
    //
    //  파이프라인 순서: HungerSystem(증가→해소) → NeedDecision → ServiceSearch →
    //    Movement(리셋). 같은 프레임에 해소(여기)가 리셋(Movement)보다 먼저 일어난다.
    //
    //  ※ 두 잡 모두 Hunger를 쓰므로 Dependency로 순차 체이닝(증가 → 해소).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct HungerSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<GameClock>()) return;
            var clock = SystemAPI.GetSingleton<GameClock>();

            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지 등 → 증가·해소 모두 멈춤

            // 증가(Hunger+Attributes) → 해소(Hunger+State+ServiceTarget) 순차.
            state.Dependency = new HungerTickJob   { Dt  = gameDt }
                .ScheduleParallel(state.Dependency);
            state.Dependency = new HungerReliefJob { Now = clock.TotalSeconds }
                .ScheduleParallel(state.Dependency);
        }
    }

    // ── 증가: 매 틱 누적(인내심 높을수록 느림 0.5~1.0배) ──
    [BurstCompile]
    public partial struct HungerTickJob : IJobEntity
    {
        public float Dt;

        void Execute(
            ref Hunger             hunger,
            in  CitizenAttributes  attr)
        {
            float resilienceFactor = 1f - 0.5f * attr.ResilienceN;
            hunger.Level = math.saturate(
                hunger.Level + hunger.Rate * resilienceFactor * Dt);
        }
    }

    // ── 해소(A-2b): 도착·머무름 종료 시 포만(Level=0). Attributes 불요. ──
    [BurstCompile]
    public partial struct HungerReliefJob : IJobEntity
    {
        public double Now;

        void Execute(
            ref Hunger        hunger,
            in  CitizenState  st,
            in  ServiceTarget target)
        {
            if (st.Activity != CitizenActivity.AtDestination) return;
            if ((target.Relief & NeedType.Hunger) == NeedType.None) return; // 이번 목적지가 Hunger 해소처가 아님
            if (Now < st.ActionEndTime) return;                              // 아직 머무는 중(식사 중)
            hunger.Level = 0f;                                               // 식사 완료 → 포만
        }
    }
}
