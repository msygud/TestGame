using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  LogisticsPoolSystem — 창고 공유 풀 용량 재계산 (2026-07-09, 요청 #1)
    // ──────────────────────────────────────────────────────────────────────────
    //  풀 싱글톤 alloc/dispose + (owner,commodity)별 Capacity = 그 플레이어 창고들의
    //  Store 칸 Capacity 합으로 재계산. Stored는 풀이 진실로 보유(Pull/Push가 증감) —
    //  여기선 건드리지 않되, 창고 소실로 Capacity가 줄면 초과분을 clamp(용량=Store 합 규약).
    //
    //  게이트: 창고 add/remove는 저빈도(도시 건설 페이스)라 HourChanged(+최초 1회)에만
    //  재계산. 창고 배치~반영 지연 ≤ 1 게임시간(결과 지연 허용 원칙). Pull/Push보다 먼저
    //  실행(용량 선반영). 창고 수는 적어(수십) 메인 단일 패스로 충분.
    //
    //  ⚙ 용량 확장 2경로(2026-07-10 유저 결정 — 업그레이드 방식 열어둠):
    //    ① 신축 — 커버(공간)도 함께 필요할 때(타일링 배치).
    //    ② **업그레이드** — 용량만 필요할 때: 기존 창고의 Store 칸 StockEntry.Capacity를 상향
    //       (값 쓰기, 구조 변경 0 — CLAUDE.md "머티리얼라이즈" 원칙). 이 시스템이 Store 합으로
    //       Capacity를 재계산하므로 **추가 배선 없이 다음 HourChanged에 풀에 자동 반영**된다.
    //       발동 정책(PushMiss 지속/ΣTarget 대비 부족 시 ①vs② 선택)은 용량 규칙 도입 때 결정.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(LogisticsPushSystem))]
    [UpdateBefore(typeof(LogisticsPullSystem))]
    public partial struct LogisticsPoolSystem : ISystem
    {
        bool _init;

        public void OnCreate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<LogisticsPool>())
            {
                var e = state.EntityManager.CreateEntity(typeof(LogisticsPool));
                state.EntityManager.SetComponentData(e, new LogisticsPool
                {
                    Cells = new NativeHashMap<int2, PoolCell>(64, Allocator.Persistent),
                    Flow  = new NativeHashMap<int2, PoolFlow>(64, Allocator.Persistent),
                });
            }
            if (!SystemAPI.HasSingleton<LogisticsMissLog>())
            {
                var e = state.EntityManager.CreateEntity(typeof(LogisticsMissLog));
                state.EntityManager.SetComponentData(e, new LogisticsMissLog
                {
                    Window = new NativeHashMap<int4, byte>(128, Allocator.Persistent),
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<LogisticsPool>())
            {
                var pool = SystemAPI.GetSingleton<LogisticsPool>();
                if (pool.Cells.IsCreated) pool.Cells.Dispose();
                if (pool.Flow.IsCreated) pool.Flow.Dispose();
            }
            if (SystemAPI.HasSingleton<LogisticsMissLog>())
            {
                var miss = SystemAPI.GetSingleton<LogisticsMissLog>();
                if (miss.Window.IsCreated) miss.Window.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // 최초 1회 + 이후 HourChanged에만(저빈도). GameClock 없어도 최초 계산은 돈다.
            bool hourChanged = SystemAPI.TryGetSingleton<GameClock>(out var clk) && clk.HourChanged;
            if (_init && !hourChanged) return;
            _init = true;

            var pool = SystemAPI.GetSingleton<LogisticsPool>();

            // ── 거주 인구(owner별) — 비축 목표의 앵커(P2, 2026-07-17). 주택 점유율 게이트
            //   (AiCityGrowth)와 동일 기준: 순수 주택(ResidenceBuilding, 비직장)의 Occupancy.Current
            //   합. 시간당 1회 메인 패스(수백 채)라 비용 무시. ──
            var pop = new NativeArray<int>(StampLayers.MaxPlayers, Allocator.Temp);
            foreach (var (occ, fp) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                         .WithAll<ResidenceBuilding>().WithNone<WorkplaceBuilding>())
            {
                int o = fp.ValueRO.OwnerLocalId;
                if ((uint)o < StampLayers.MaxPlayers) pop[o] += occ.ValueRO.Current;
            }

            // ── 창고 Store 용량 합산 (owner,commodity) ──
            var cap = new NativeHashMap<int2, int>(64, Allocator.Temp);
            foreach (var (wh, entity) in
                     SystemAPI.Query<RefRO<WarehouseTag>>().WithEntityAccess())
            {
                if (!SystemAPI.HasBuffer<StockEntry>(entity)) continue;
                int owner = wh.ValueRO.OwnerLocalId;
                if ((uint)owner >= StampLayers.MaxPlayers) continue;

                var store = SystemAPI.GetBuffer<StockEntry>(entity);
                for (int i = 0; i < store.Length; i++)
                {
                    if (store[i].Role != StockRole.Store) continue;
                    var k = new int2(owner, (int)store[i].Commodity);
                    cap.TryGetValue(k, out int c);
                    cap[k] = c + store[i].Capacity;
                }
            }

            // ── 기존 풀 셀: Capacity 갱신 + Stored clamp (map 순회 중 수정 불가 → 키 배열) ──
            var keys = pool.Cells.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                var cell = pool.Cells[k];
                cap.TryGetValue(k, out int newCap);        // 창고 사라졌으면 0
                cell.Capacity = newCap;
                if (cell.Stored > newCap) cell.Stored = newCap;   // 용량 축소 → 초과분 소실
                // 인구 비례 목표 재계산(P2) — 인구·용량이 바뀌면 시간당 자동 추종.
                cell.Target = (uint)k.x < StampLayers.MaxPlayers
                    ? StockPolicy.Target((Commodity)k.y, pop[k.x], newCap) : 0;

                if (cell.Capacity == 0 && cell.Stored == 0)
                    pool.Cells.Remove(k);                  // 빈 셀 정리(맵 비대 방지)
                else
                    pool.Cells[k] = cell;
                cap.Remove(k);                             // 처리 완료 표시(남은 건 신규)
            }
            keys.Dispose();

            // ── 신규 (owner,commodity): Stored=0, Capacity=합, Target=인구 비례 ──
            var newKeys = cap.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < newKeys.Length; i++)
            {
                var k = newKeys[i];
                pool.Cells[k] = new PoolCell
                {
                    Stored   = 0,
                    Capacity = cap[k],
                    Target   = (uint)k.x < StampLayers.MaxPlayers
                        ? StockPolicy.Target((Commodity)k.y, pop[k.x], cap[k]) : 0,
                };
            }
            newKeys.Dispose();

            pop.Dispose();
            cap.Dispose();
        }
    }
}
