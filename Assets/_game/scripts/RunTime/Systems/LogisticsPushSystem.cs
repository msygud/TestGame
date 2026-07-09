using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LogisticsPushSystem — 출력 재고 공유 풀로 배출(push)
    //  (2026-07-09 재작성: 개별 창고 직거래 → LogisticsPool)
    //
    //  출력(Output) 재고가 discharge 위로 차면, 건물이 자기 입구 도로셀의 stamp에
    //  **그 플레이어 창고가 하나라도 닿는지**(커버리지)만 확인하고, 닿으면 **공유 풀**
    //  (그 플레이어 모든 창고 용량 합)에 여유가 있는 만큼 floor(0)까지 덜어 넣는다. pull의 대칭.
    //    · 커버리지 = 이진. 어느 창고 하나만 닿아도 풀 전체 용량에 접근.
    //    · 어느 창고에도 여유 없음(풀 만석) = 출력이 찬 채 머묾 → DemandAggregation의
    //      공급 수요 수집이 "창고 수요"(WarehouseId) 발행 → AI가 창고 증설(용량↑).
    //
    //  anti-oscillation: Current > Discharge일 때만, floor까지 덜어냄(hysteresis 밴드).
    //  ※ 완성품(Final)은 창고로 안 감(로컬 보관) → Output 칸을 갖지 않으므로 무관.
    //  ※ 메인스레드·건물 GetBuffer — pull과 동일. 풀 용량은 LogisticsPoolSystem이 선반영.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LogisticsPushSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<LogisticsPool>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var stamp = SystemAPI.GetSingleton<StampLayers>();
            var pool  = SystemAPI.GetSingleton<LogisticsPool>();

            foreach (var (footprint, entrance, entity) in
                     SystemAPI.Query<RefRO<BuildingFootprint>, RefRO<BuildingEntrance>>()
                         .WithEntityAccess())
            {
                if (!SystemAPI.HasBuffer<StockEntry>(entity)) continue;

                int owner = footprint.ValueRO.OwnerLocalId;
                if ((uint)owner >= StampLayers.MaxPlayers) continue;
                var map = stamp[owner];
                if (!map.IsCreated) continue;

                int2 roadCell = EntranceOps.EntranceRoadCell(
                    footprint.ValueRO.Origin, footprint.ValueRO.Size,
                    in entrance.ValueRO.Entrance, footprint.ValueRO.RotSteps);

                // 커버리지: 그 플레이어 창고가 이 도로셀에 하나라도 닿는가(풀 접속 여부).
                if (!HasWarehouseCoverage(in map, roadCell)) continue;

                var output = SystemAPI.GetBuffer<StockEntry>(entity);
                for (int i = 0; i < output.Length; i++)
                {
                    var e = output[i];
                    if (e.Role != StockRole.Output) continue;  // 출력 칸만
                    if (e.Current <= e.Discharge) continue;    // 덜 찼음 → 스킵(hysteresis)

                    const int floor = 0;                       // stub: 0까지 비움
                    int want = e.Current - floor;
                    if (want <= 0) continue;

                    // ── 공유 풀에 deposit(여유만큼) ──
                    var key = LogisticsPool.Key(owner, e.Commodity);
                    pool.Cells.TryGetValue(key, out var cell);   // 없으면 Capacity 0(용량 미반영)
                    int free = cell.Capacity - cell.Stored;
                    if (free <= 0) continue;                     // 풀 만석 → 출력이 참(창고 수요 신호)

                    int put = want < free ? want : free;
                    cell.Stored    += put;
                    pool.Cells[key]  = cell;
                    e.Current -= put;
                    output[i]  = e;
                }
            }
        }

        // 도로셀에 그 플레이어 창고(Kind=Warehouse)가 하나라도 닿는가(풀 접속 = 이진 커버리지).
        static bool HasWarehouseCoverage(
            in NativeParallelMultiHashMap<int2, SupplierRef> map, int2 roadCell)
        {
            if (map.TryGetFirstValue(roadCell, out var sr, out var it))
            {
                do
                {
                    if (sr.Kind == StampKind.Warehouse) return true;
                }
                while (map.TryGetNextValue(out sr, ref it));
            }
            return false;
        }
    }
}
