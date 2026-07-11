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
    //    · 커버 있음 + 풀 만석 = 과잉생산 — **수요 발행 안 함**(계획 P, 아래 본문). 즉 용량 압력은
    //      창고 수요를 만들지 않는다(용량 확장은 추후 "기존 창고 Capacity 업그레이드" 경로로 확정,
    //      PROGRESS 참조). WarehouseId 수요는 오직 **미커버**(풀 미접속) 건물에서만 나온다.
    //
    //  anti-oscillation: Current > Discharge일 때만, floor까지 덜어냄(hysteresis 밴드).
    //  ※ 완성품(Final)은 창고로 안 감(로컬 보관) → Output 칸을 갖지 않으므로 무관.
    //  ※ 메인스레드·건물 GetBuffer — pull과 동일. 풀 용량은 LogisticsPoolSystem이 선반영.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LogisticsPushSystem : ISystem
    {
        double _nextGameSec;   // 게임초 게이트(P v2) — Pull과 동일

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<LogisticsPool>();
            state.RequireForUpdate<LogisticsMissLog>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton<GameClock>(out var clk))
            {
                if (clk.TotalSeconds < _nextGameSec) return;
                _nextGameSec = clk.TotalSeconds + clk.SecondsPerDay / 48f;
            }

            var stamp = SystemAPI.GetSingleton<StampLayers>();
            var pool  = SystemAPI.GetSingleton<LogisticsPool>();
            var miss  = SystemAPI.GetSingleton<LogisticsMissLog>();

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

                var output = SystemAPI.GetBuffer<StockEntry>(entity);

                // 커버리지: 그 플레이어 창고가 이 도로셀에 하나라도 닿는가(풀 접속 여부).
                if (!HasWarehouseCoverage(in map, roadCell))
                {
                    // 풀 미접속: 배출 대기 출력이 있으면 커버 미스 → 그 셀의 창고 수요(지역 채널, 계획 P).
                    for (int i = 0; i < output.Length; i++)
                    {
                        var e = output[i];
                        if (e.Role == StockRole.Output && e.Current > e.Discharge)
                        {
                            miss.Record(owner, footprint.ValueRO.Origin, DemandResource.WarehouseId);
                            break;
                        }
                    }
                    continue;
                }

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
                    // 커버 있음 + 풀 만석 = 과잉생산 — 창고 수요로 답하지 않음(계획 P: 무한 버퍼
                    //   증식 차단. 생산은 출력 포화 클램프가 유휴 처리, 진짜 답은 하류 소비자 = tier 수요).
                    if (free <= 0) continue;

                    int put = want < free ? want : free;
                    cell.Stored    += put;
                    pool.Cells[key]  = cell;
                    pool.RecordDeposit(key, put);   // 유입 계측(P v2) — 생산측 공급 능력의 실측
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
