using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  HungerSystem — 배고픔 욕구 전담 (증가 + [A-2]해소)
    //
    //  버퍼+단일 NeedTickSystem을 대체하는 "욕구별 시스템"의 첫 사례.
    //  한 시스템이 한 욕구의 증가와 해소를 모두 책임진다(확장성·관심사 분리).
    //
    //  A-1 (현재): 증가만.
    //    매 틱 Hunger.Level += Rate * resilienceFactor * gameDt.
    //    게임시간 기반(TimeScale 반영) → 일시정지=0, 배속이면 비례.
    //
    //  A-2 (예정): 해소 분기 추가.
    //    State + ServiceTarget을 함께 읽어,
    //      목표가 Hunger && 도착 && 물품 선택됨 → Level 감소(선택물품 Rate),
    //      아니면 → 통상 증가.
    //    그때 stamp/도착 판정과 얽히며 플레이어별(CitizenOwner 필터)로 전환.
    //
    //  ActiveMask 없음: 활성 여부는 IsActive(Level>Threshold)로 그때그때 판정.
    //  추구 욕구 결정은 결정 시스템(A-3)이 ServiceTarget으로 넘긴다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct HungerSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            // 게임시간 기반: 실시간이 아니라 게임초 증가분.
            if (!SystemAPI.HasSingleton<GameClock>()) return;
            var clock = SystemAPI.GetSingleton<GameClock>();

            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지 등

            new HungerTickJob { Dt = gameDt }.ScheduleParallel();
        }
    }

    [BurstCompile]
    public partial struct HungerTickJob : IJobEntity
    {
        public float Dt;

        void Execute(
            ref Hunger             hunger,
            in  CitizenAttributes  attr)
        {
            // 인내심(Resilience): 높을수록 누적이 느려짐(0.5~1.0배).
            float resilienceFactor = 1f - 0.5f * attr.ResilienceN;

            hunger.Level = math.saturate(
                hunger.Level + hunger.Rate * resilienceFactor * Dt);
        }
    }
}
