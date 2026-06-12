using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LogisticsPushSystem (slice 2) — 출력 재고 창고로 배출(push), stamp 경유
    //
    //  출력(Output) 재고가 discharge 위로 차면, 건물이 자기 입구 도로셀의 stamp에서
    //  `Kind=Warehouse` 도장을 읽어 **그 품목을 수용할 여유가 있는 최근접 창고**를 골라
    //  floor(0)까지 덜어 넣는다. pull의 대칭.
    //
    //  - 건물 owner = BuildingFootprint.OwnerLocalId → stamp[owner].
    //  - 창고 여유(Capacity−Current)는 stamp에 없음 → 후보 창고 stock에서 그 순간 읽음.
    //  - 여유 있는 창고 중 최단 Dist. 어느 창고도 여유 없으면 출력이 찬 채 머묾(=창고 풀).
    //
    //  anti-oscillation: Current > Discharge일 때만, floor까지 덜어냄(hysteresis 밴드).
    //
    //  ※ 완성품(Final)은 창고로 안 감(로컬 보관) → Output 칸을 갖지 않으므로 무관.
    //  ※ 메인스레드·후보 창고 GetBuffer — pull과 동일.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LogisticsPushSystem : ISystem
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

                var output = SystemAPI.GetBuffer<StockEntry>(entity);

                for (int i = 0; i < output.Length; i++)
                {
                    var e = output[i];
                    if (e.Role != StockRole.Output) continue;  // 출력 칸만
                    if (e.Current <= e.Discharge) continue;    // 덜 찼음 → 스킵(hysteresis)

                    const int floor = 0;                       // stub: 0까지 비움
                    int want = e.Current - floor;
                    if (want <= 0) continue;

                    // 그 품목 수용 여유 있는 최근접 창고 찾기.
                    Entity bestW   = Entity.Null;
                    int    bestDst = int.MaxValue;
                    if (map.TryGetFirstValue(roadCell, out var sr, out var it))
                    {
                        do
                        {
                            if (sr.Kind != StampKind.Warehouse) continue;
                            if (sr.Dist >= bestDst) continue;
                            if (!SystemAPI.HasBuffer<StockEntry>(sr.Supplier)) continue;
                            if (!HasStoreSpace(SystemAPI.GetBuffer<StockEntry>(sr.Supplier), e.Commodity))
                                continue;
                            bestW   = sr.Supplier;
                            bestDst = sr.Dist;
                        }
                        while (map.TryGetNextValue(out sr, ref it));
                    }

                    if (bestW != Entity.Null)
                    {
                        var store = SystemAPI.GetBuffer<StockEntry>(bestW);
                        int put = DepositToStore(ref store, e.Commodity, want);
                        if (put > 0) { e.Current -= put; output[i] = e; }
                    }
                }
            }
        }

        // 창고에 그 품목 Store 여유(Capacity−Current)가 1 이상 있나.
        static bool HasStoreSpace(DynamicBuffer<StockEntry> store, Commodity c)
        {
            for (int j = 0; j < store.Length; j++)
            {
                var s = store[j];
                if (s.Role == StockRole.Store && s.Commodity == c && (s.Capacity - s.Current) > 0)
                    return true;
            }
            return false;
        }

        // 창고의 같은 품목 Store 칸(들)에 want만큼(여유만큼) 채움. 넣은 양 반환.
        static int DepositToStore(ref DynamicBuffer<StockEntry> store, Commodity c, int want)
        {
            int put = 0;
            for (int j = 0; j < store.Length && put < want; j++)
            {
                var s = store[j];
                if (s.Role != StockRole.Store || s.Commodity != c) continue;

                int spare = s.Capacity - s.Current;
                if (spare <= 0) continue;

                int remain = want - put;
                int take   = remain < spare ? remain : spare;
                s.Current += take;
                store[j]   = s;
                put       += take;
            }
            return put;
        }
    }
}
