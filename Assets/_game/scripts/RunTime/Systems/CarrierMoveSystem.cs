using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CarrierMoveSystem
    //
    //  CarrierTag 엔티티를 도로 경로(CarrierPathCell 버퍼)를 따라
    //  실시간으로 이동시킨다. 목적지 도착 시 엔티티 소멸.
    //
    //  이동 방식:
    //    - MoveTimer [0, 1): 현재 셀 → 다음 셀 선형 보간.
    //    - 매 프레임 MoveTimer += dt * Speed (셀/초).
    //    - MoveTimer ≥ 1 이면 PathIndex 증가, 나머지를 다음 보간에 이월.
    //    - PathIndex ≥ path.Length - 1 이면 목적지 도착 → DestroyEntity.
    //
    //  재고 영향 없음 — 순수 비주얼.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CarrierSpawnSystem))]
    public partial struct CarrierMoveSystem : ISystem
    {
        /// <summary>이동 속도 (도로 셀/현실초). 낮출수록 느리게 이동.</summary>
        const float Speed   = 1.5f;
        /// <summary>도로 메시 위로 띄우는 높이 (월드 단위).</summary>
        const float YOffset = 0.5f;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridSettings>();
        }

        public void OnUpdate(ref SystemState state)
        {
            float dt          = SystemAPI.Time.DeltaTime;
            var   gridSettings = SystemAPI.GetSingleton<GridSettings>();
            var   ecb         = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (stateRW, transformRW, path, entity) in
                     SystemAPI.Query<RefRW<CarrierState>, RefRW<LocalTransform>,
                                     DynamicBuffer<CarrierPathCell>>()
                         .WithAll<CarrierTag>()
                         .WithEntityAccess())
            {
                ref var cs      = ref stateRW.ValueRW;
                int     pathLen = path.Length;

                if (pathLen == 0)
                {
                    ecb.DestroyEntity(entity);
                    continue;
                }

                // 이미 도착 상태
                if (cs.PathIndex >= pathLen - 1)
                {
                    ecb.DestroyEntity(entity);
                    continue;
                }

                // 시간 전진 (고속 이동 시 여러 셀 넘을 수 있으므로 while)
                cs.MoveTimer += dt * Speed;
                while (cs.MoveTimer >= 1f && cs.PathIndex < pathLen - 1)
                {
                    cs.PathIndex++;
                    cs.MoveTimer -= 1f;
                }

                // 이번 프레임 목적지 도착
                if (cs.PathIndex >= pathLen - 1)
                {
                    int2 last = path[pathLen - 1].Cell;
                    transformRW.ValueRW.Position = gridSettings.CellCenter(last.x, last.y) + new float3(0, YOffset, 0);
                    ecb.DestroyEntity(entity);
                    continue;
                }

                // 현재 셀 → 다음 셀 선형 보간
                int2   from2 = path[cs.PathIndex].Cell;
                int2   to2   = path[cs.PathIndex + 1].Cell;
                float3 from  = gridSettings.CellCenter(from2.x, from2.y) + new float3(0, YOffset, 0);
                float3 to    = gridSettings.CellCenter(to2.x, to2.y)   + new float3(0, YOffset, 0);

                transformRW.ValueRW.Position = math.lerp(from, to, math.saturate(cs.MoveTimer));
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
