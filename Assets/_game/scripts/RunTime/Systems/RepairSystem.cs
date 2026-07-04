using Unity.Collections;
using Unity.Entities;
using Game.Unit;   // CombatHealth, CombatDeadTag

namespace CitySim
{
    /// <summary>
    /// 수리 명령(단발성) — 손상(<100%)된 건물/도로 대상. UI/AI가 발행, RepairSystem이 처리.
    /// ※ 현재는 즉시 풀-수리(placeholder). 팩션·업그레이드별 '가능 여부/비용/시간(진행형)'은
    ///   RepairSystem에서 게이트할 예정 — 명령 모양은 유지(발행자는 정책에 무지).
    /// </summary>
    public struct RepairRequest : IComponentData
    {
        public Entity Target;            // CombatHealth 보유 엔티티(건물/도로)
        public int    RequesterLocalId;  // 비용/권한 판정용(추후)
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RepairSystem — 수리 처리 (현재: 즉시 풀-수리 placeholder)
    //  추후 여기서: ① 권한(자기 소유/자기 영토) ② 팩션·업그레이드별 가능 여부
    //  ③ 비용 차감 ④ 진행형 수리(시간) — 명령·발행자는 그대로 두고 이 시스템만 확장.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct RepairSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<RepairRequest>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, e) in SystemAPI.Query<RefRO<RepairRequest>>().WithEntityAccess())
            {
                var target = req.ValueRO.Target;
                if (target != Entity.Null
                    && SystemAPI.HasComponent<CombatHealth>(target)
                    && !SystemAPI.HasComponent<CombatDeadTag>(target))   // 죽은 것은 수리 불가
                {
                    var h = SystemAPI.GetComponent<CombatHealth>(target);
                    if (h.Health > 0f && h.Health < h.MaxHealth)
                    {
                        h.Health = h.MaxHealth;   // placeholder: 즉시 풀-수리
                        SystemAPI.SetComponent(target, h);
                    }
                }
                ecb.DestroyEntity(e);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
