using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ──────────────────────────────────────────────────────────────────────────
    //  WarehouseLink — RETIRED (slice 2에서 stamp 기반 창고 탐색으로 대체).
    //   더 이상 어떤 시스템도 읽지 않는다. slice 1 테스트 부트스트랩 호환을 위해
    //   정의만 남겨 둔다. step 3(도로/stamp 시나리오) 정비 때 부트스트랩과 함께 제거.
    // ──────────────────────────────────────────────────────────────────────────
    public struct WarehouseLink : IComponentData
    {
        public Entity Warehouse;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  LogisticsPullSystem — 입력 재고 par-level 보충(pull), 창고 공유 풀 경유
    //  (2026-07-09 재작성: 개별 창고 직거래 → LogisticsPool)
    //
    //  입력(Input) 재고가 reorder 밑으로 떨어지면, 건물이 자기 입구 도로셀의 stamp에
    //  **그 플레이어 창고(Kind=Warehouse)가 하나라도 닿는지**(커버리지)만 확인하고,
    //  닿으면 **공유 풀 전체**(그 플레이어 모든 창고 합)에서 target까지 끌어온다.
    //    · 커버리지 = 이진(닿음/안 닿음). 개별 창고 반경·재고에 안 갇힘 → 창고를 더
    //      지어 커버를 넓히거나 용량을 키우면 곧바로 이 건물이 혜택(요청 #1 해소).
    //    · 재고는 창고 buffer가 아니라 LogisticsPool.Cells[(owner,commodity)]에서 차감.
    //    · 운반자 비주얼 출발점 = 최근접 창고 입구(코스메틱 — 실제 재고는 풀이 진실).
    //
    //  anti-oscillation: Current < Reorder일 때만, Target까지 채움(hysteresis 밴드).
    //  ※ 메인스레드: 건물 버퍼를 GetBuffer로(StockEntry를 쿼리에 안 넣어 alias 회피).
    //    저빈도라 충분(게이팅 후속). 풀 용량은 LogisticsPoolSystem이 선반영.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LogisticsPullSystem : ISystem
    {
        double _nextGameSec;   // 게임초 게이트(P v2)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StampLayers>();
            state.RequireForUpdate<LogisticsPool>();
            state.RequireForUpdate<LogisticsMissLog>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 게이팅(P v2): 반 게임시간마다 — 흐름 계측(Out/In)·미스 창이 배속 불변이 되고
            //   메인 비용도 절감(기존 "매 프레임, 게이팅 후속" TODO 해소). 소비·생산이 게임시간
            //   기반이라 반 시간 배치 지연은 reorder 버퍼 안(결과 지연 허용 원칙).
            if (SystemAPI.TryGetSingleton<GameClock>(out var clk))
            {
                if (clk.TotalSeconds < _nextGameSec) return;
                _nextGameSec = clk.TotalSeconds + clk.SecondsPerDay / 48f;
            }

            var stamp = SystemAPI.GetSingleton<StampLayers>();
            var pool  = SystemAPI.GetSingleton<LogisticsPool>();
            var miss  = SystemAPI.GetSingleton<LogisticsMissLog>();
            var fpL   = SystemAPI.GetComponentLookup<BuildingFootprint>(true);
            var entL  = SystemAPI.GetComponentLookup<BuildingEntrance>(true);
            var ecb   = new EntityCommandBuffer(Allocator.Temp);

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

                // ── 커버리지 + 운반자 출발 창고(최근접, 비주얼용) — stamp 1회 스캔 ──
                Entity nearestW = FindNearestWarehouse(in map, roadCell);
                var input = SystemAPI.GetBuffer<StockEntry>(entity);
                if (nearestW == Entity.Null)
                {
                    // 풀 미접속(창고 stamp 안 닿음): 보충이 필요한 입력이 있으면 커버 미스
                    //   → 그 셀의 창고 수요(지역 채널, 계획 P).
                    for (int i = 0; i < input.Length; i++)
                    {
                        var e = input[i];
                        if (e.Role == StockRole.Input && e.Current < e.Reorder && e.Target > e.Current)
                        {
                            miss.Record(owner, footprint.ValueRO.Origin, DemandResource.WarehouseId);
                            break;
                        }
                    }
                    continue;
                }

                for (int i = 0; i < input.Length; i++)
                {
                    var e = input[i];
                    if (e.Role != StockRole.Input) continue;   // 입력 칸만
                    if (e.Current >= e.Reorder) continue;      // 충분 → 스킵(hysteresis)
                    int want = e.Target - e.Current;
                    if (want <= 0) continue;

                    // ── 공유 풀에서 draw — 장부 분리(2026-07-10 층 분리 합의) ──
                    //   풀 장부(Flow) = 실제 꺼낸 양(물리 유출)만. 못 채운 요구(결핍)는 풀의 사실이
                    //   아니라 소비자의 사실 → 수요층(miss → DemandField)으로. 풀은 알아도 모른척.
                    var key = LogisticsPool.Key(owner, e.Commodity);
                    pool.Cells.TryGetValue(key, out var cell);
                    int got = want < cell.Stored ? want : cell.Stored;
                    if (got < 0) got = 0;
                    if (got < want)   // 양적 미스(부트스트랩·절대 결핍) — 수요층 채널
                        miss.Record(owner, footprint.ValueRO.Origin, DemandResource.ForCommodity(e.Commodity));
                    if (got <= 0) continue;
                    pool.RecordDraw(key, got);

                    cell.Stored   -= got;
                    pool.Cells[key] = cell;
                    e.Current += got;
                    input[i]   = e;

                    // 운반자 비주얼(최근접 창고 입구 → 건물 입구). 실제 재고는 풀이 진실.
                    int2 warehouseCell = WarehouseEntranceCell(nearestW, roadCell, in fpL, in entL);
                    var reqE = ecb.CreateEntity();
                    ecb.AddComponent(reqE, new LogisticsCarrierRequest
                    {
                        SourceRoadCell = warehouseCell,
                        DestRoadCell   = roadCell,
                        OwnerLocalId   = owner,
                    });
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // 도로셀에 닿는 창고(Kind=Warehouse) 중 최근접 엔티티(커버리지 확인 겸 비주얼 출발점).
        //   Null = 창고 stamp가 안 닿음(= 풀 미접속).
        static Entity FindNearestWarehouse(
            in NativeParallelMultiHashMap<int2, SupplierRef> map, int2 roadCell)
        {
            Entity best    = Entity.Null;
            int    bestDst = int.MaxValue;
            if (map.TryGetFirstValue(roadCell, out var sr, out var it))
            {
                do
                {
                    if (sr.Kind != StampKind.Warehouse) continue;
                    if (sr.Dist >= bestDst) continue;
                    best    = sr.Supplier;
                    bestDst = sr.Dist;
                }
                while (map.TryGetNextValue(out sr, ref it));
            }
            return best;
        }

        // 창고 입구 도로셀(운반자 비주얼 출발점). 입구 정보 없으면 폴백 셀 반환.
        static int2 WarehouseEntranceCell(
            Entity warehouse, int2 fallback,
            in ComponentLookup<BuildingFootprint> fpL, in ComponentLookup<BuildingEntrance> entL)
        {
            if (fpL.HasComponent(warehouse) && entL.HasComponent(warehouse))
            {
                var wfp  = fpL[warehouse];
                var went = entL[warehouse];
                return EntranceOps.EntranceRoadCell(wfp.Origin, wfp.Size, in went.Entrance, wfp.RotSteps);
            }
            return fallback;
        }
    }
}
