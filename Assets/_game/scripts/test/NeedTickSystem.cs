using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage B — 욕구 틱 (Need Tick)
    //
    //  시민의 욕구 게이지를 매 틱 증가시키고, 임계치를 넘은 욕구를
    //  ActiveMask에 비트로 켠다(불만 표출 / 행동 트리거 / AI 신호).
    //
    //  설계(§0):
    //    - 주쿼리 = "변하는 데이터": NeedElement(버퍼) + CitizenNeeds. 둘 다 핫.
    //    - 게이지 증가·ActiveMask 갱신은 값 수정(구조 변경 아님) → Job에서 직접.
    //    - 능력치(인내심 Resilience)가 누적 둔화에 영향 → CitizenAttributes 읽기
    //      (콜드지만 주쿼리에 포함, 룩업 아님).
    //    - IJobEntity + Burst 병렬: 시민 수가 많아도 청크 분산으로 가볍게.
    //
    //  부정 방향: Level 0=만족 → 1=최악. 증가가 나빠짐.
    //  해결(게이지 감소)은 Stage F(욕구 해소)에서 행동으로 처리.
    //
    //  메모:
    //    - Rate=0인 욕구(Homeless/Unemployed)는 여기서 안 변함. 배정 상태에서
    //      파생되도록 별도 처리 예정(Stage B 후반 또는 ConditionUpdate).
    //    - 스태거링: 현재는 전원 매 틱(deltaTime 비례라 프레임률 독립). 부담 시
    //      청크 분할로 N틱마다 처리로 전환(§10).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct NeedTickSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // 게임시간 기반: 실시간이 아니라 게임초 증가분을 사용.
            // → 일시정지(TimeScale=0)면 0, 배속이면 그만큼 빨라짐. 일관성.
            if (!SystemAPI.HasSingleton<GameClock>()) return;
            var clock = SystemAPI.GetSingleton<GameClock>();

            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지 등

            new NeedTickJob { Dt = gameDt }.ScheduleParallel();
        }
    }

    [BurstCompile]
    public partial struct NeedTickJob : IJobEntity
    {
        public float Dt;

        void Execute(
            ref CitizenNeeds                  needs,
            DynamicBuffer<NeedElement>        gauges,
            in  CitizenAttributes             attr)
        {
            // 인내심(Resilience): 높을수록 누적이 느려짐.
            // 배수 0.5~1.0 (인내심 255면 절반 속도, 0이면 전속).
            float resilienceFactor = 1f - 0.5f * attr.ResilienceN;

            NeedType active = NeedType.None;

            for (int i = 0; i < gauges.Length; i++)
            {
                var g = gauges[i];

                if (g.Rate > 0f)
                {
                    g.Level = math.saturate(g.Level + g.Rate * resilienceFactor * Dt);
                    gauges[i] = g;
                }

                // 임계치 초과 → 활성 비트 ON
                if (g.Level > g.Threshold)
                    active |= g.Type;
            }

            needs.ActiveMask = active;
        }
    }
}
