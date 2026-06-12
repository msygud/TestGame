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
    //  LogisticsPullSystem (slice 2) — 입력 재고 par-level 보충(pull), stamp 경유
    //
    //  입력(Input) 재고가 reorder 밑으로 떨어지면, 건물이 자기 입구 도로셀의 stamp에서
    //  `Kind=Warehouse` 도장을 읽어 **그 품목을 보유한 최근접 창고**를 골라 target까지
    //  끌어온다(가상 이동, 즉시 수량 이전).
    //
    //  - 건물 owner = BuildingFootprint.OwnerLocalId → stamp[owner] 슬롯.
    //  - 도로셀 = EntranceOps.EntranceRoadCell(footprint, entrance) (ServiceSearch와 동일).
    //  - 창고 재고/용량은 stamp에 없음 → 후보 창고 stock 버퍼에서 그 순간 직접 읽음
    //    (capacity 직접읽기 원칙). 보유량 있는(>0) 창고 중 최단 Dist 선택.
    //
    //  anti-oscillation: Current < Reorder일 때만, Target까지 채움(hysteresis 밴드).
    //
    //  ※ 메인스레드: 건물 버퍼 + 후보 창고 버퍼를 GetBuffer로(StockEntry를 쿼리에
    //    안 넣어 alias 회피). 저빈도라 충분(게이팅 후속).
    //  ※ 건물 후보 = footprint+입구 있는 모든 건물; StockEntry 버퍼 없으면 스킵,
    //    Input 칸 없으면 무동작(창고=Store, 생산자=Output은 자연 스킵).
    //  ※ slice 2 단순화: 최근접 1개 창고에서 가능한 만큼. 부족분은 다음 틱 재시도
    //    (또는 다음 최근접). 여러 창고 합산·운송지연·혼잡은 후속.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LogisticsPullSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<StampLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var stamp = SystemAPI.GetSingleton<StampLayers>();

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

                var input = SystemAPI.GetBuffer<StockEntry>(entity);

                for (int i = 0; i < input.Length; i++)
                {
                    var e = input[i];
                    if (e.Role != StockRole.Input) continue;   // 입력 칸만
                    if (e.Current >= e.Reorder) continue;      // 충분 → 스킵(hysteresis)
                    int want = e.Target - e.Current;
                    if (want <= 0) continue;

                    // 그 품목 보유한 최근접 창고(Kind=Warehouse) 찾기.
                    Entity bestW   = Entity.Null;
                    int    bestDst = int.MaxValue;
                    if (map.TryGetFirstValue(roadCell, out var sr, out var it))
                    {
                        do
                        {
                            if (sr.Kind != StampKind.Warehouse) continue;
                            if (sr.Dist >= bestDst) continue;
                            if (!SystemAPI.HasBuffer<StockEntry>(sr.Supplier)) continue;
                            if (!HasStoreStock(SystemAPI.GetBuffer<StockEntry>(sr.Supplier), e.Commodity))
                                continue;
                            bestW   = sr.Supplier;
                            bestDst = sr.Dist;
                        }
                        while (map.TryGetNextValue(out sr, ref it));
                    }

                    if (bestW != Entity.Null)
                    {
                        var store = SystemAPI.GetBuffer<StockEntry>(bestW);
                        int got = DrawFromStore(ref store, e.Commodity, want);
                        if (got > 0) { e.Current += got; input[i] = e; }
                    }
                }
            }
        }

        // 창고에 그 품목 Store 재고가 1 이상 있나.
        static bool HasStoreStock(DynamicBuffer<StockEntry> store, Commodity c)
        {
            for (int j = 0; j < store.Length; j++)
            {
                var s = store[j];
                if (s.Role == StockRole.Store && s.Commodity == c && s.Current > 0)
                    return true;
            }
            return false;
        }

        // 창고의 같은 품목 Store 칸(들)에서 want만큼(있는 만큼) 차감. 가져온 양 반환.
        static int DrawFromStore(ref DynamicBuffer<StockEntry> store, Commodity c, int want)
        {
            int drawn = 0;
            for (int j = 0; j < store.Length && drawn < want; j++)
            {
                var s = store[j];
                if (s.Role != StockRole.Store || s.Commodity != c) continue;
                if (s.Current <= 0) continue;

                int remain = want - drawn;
                int take   = remain < s.Current ? remain : s.Current;
                s.Current -= take;
                store[j]   = s;
                drawn     += take;
            }
            return drawn;
        }
    }
}
