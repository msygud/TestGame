using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  EnvironmentClearSystem — 환경물 셀 단위 일괄 제거
    //
    //  EnvironmentClearRequest(셀)를 모아, 해당 셀의 EnvironmentInstance를
    //  한 번의 패스로 destroy한다. 도로/건물 배치 시스템은 직접 destroy하지
    //  않고 요청만 발행 → ECB playback 예외 위험 없이 안전하게 정리.
    //
    //  배치는 저빈도라 환경물 전수 1패스 스캔으로 충분(핫패스 아님).
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RoadSystem))]
    [UpdateAfter(typeof(BuildingPlacementSystem))]
    public partial struct EnvironmentClearSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate(
                SystemAPI.QueryBuilder().WithAll<EnvironmentClearRequest>().Build());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 1) 요청 셀 수집
            var cells = new NativeHashSet<Unity.Mathematics.int2>(16, Allocator.Temp);
            var ecb   = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, e) in
                     SystemAPI.Query<RefRO<EnvironmentClearRequest>>().WithEntityAccess())
            {
                cells.Add(req.ValueRO.Cell);
                ecb.DestroyEntity(e);
            }

            // 2) 해당 셀의 환경물 인스턴스 destroy (단일 패스)
            foreach (var (inst, e) in
                     SystemAPI.Query<RefRO<EnvironmentInstance>>().WithEntityAccess())
            {
                if (cells.Contains(inst.ValueRO.Cell))
                    ecb.DestroyEntity(e);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            cells.Dispose();
        }
    }
}
