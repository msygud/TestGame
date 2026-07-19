using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  OffshorePushSystem — 해상 공급자 출력 → 공유 풀 (항만 반경, 2026-07-19)
    //
    //  해상 건물(OffshoreSupplier — 시추 등)은 도로 입구가 없어 stamp 커버리지
    //  (LogisticsPushSystem의 풀 접속 판정)를 원리적으로 얻을 수 없다. 대신:
    //    · 풀 접속 = 자기 소유 **항만 창고**(WarehouseTag.SeaRange > 0)가 유클리드
    //      반경 안에 하나라도 있는가(이진 — 육상 stamp 커버리지와 동형 계약).
    //    · 접속 시 Output 재고를 기존 push 규약 그대로 풀에 배출(discharge 초과 시
    //      floor 0까지, 풀 여유만큼, RecordDeposit 계측).
    //    · 미접속 + 배출 대기 출력 = LogisticsMissLog에 창고 수요(WarehouseId) 기록 —
    //      F12 가시화. AI의 항만 자동 건설(해안 착지)은 2단계 과제라 지금은 신호만.
    //
    //  전역 풀 = 1단계 합의(캐리어 없는 논리 텔레포트). 3단계(군집 분리·실체 호송)에서
    //  이 시스템이 "항만 하역" 물리 경로로 진화한다.
    //  ※ 메인스레드·저빈도 게이트 — LogisticsPushSystem과 동일 계약(버퍼 alias 회피).
    //    항만 후보는 창고 수십 채 수준이라 전수 거리 비교로 충분(공간 인덱스 불필요).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct OffshorePushSystem : ISystem
    {
        double _nextGameSec;   // 게임초 게이트 — Push와 동일 주기(SecondsPerDay/48)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<OffshoreSupplier>();
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

            var pool = SystemAPI.GetSingleton<LogisticsPool>();
            var miss = SystemAPI.GetSingleton<LogisticsMissLog>();

            // 유조선 인프라 존재 = 실체 운송이 담당(2026-07-19) — 이 시스템의 텔레포트 배출은
            //   **레거시 폴백**으로 강등(프리팹 미제작 시에만). 미접속 miss 신호는 계속 이 시스템 소관.
            bool tankerMode = SystemAPI.HasSingleton<TankerPrefabSingleton>();

            // ── 항만 수집: owner별 (중심셀, 반경²) — 창고 수는 소수라 Temp 전수 스캔 ──
            var ports = new NativeList<int4>(16, Allocator.Temp);   // (owner, cx, cy, range²)
            foreach (var (wt, wfp) in
                     SystemAPI.Query<RefRO<WarehouseTag>, RefRO<BuildingFootprint>>())
            {
                int range = wt.ValueRO.SeaRange;
                if (range <= 0) continue;
                int2 c = Center(wfp.ValueRO);
                ports.Add(new int4(wt.ValueRO.OwnerLocalId, c.x, c.y, range * range));
            }

            foreach (var (footprint, entity) in
                     SystemAPI.Query<RefRO<BuildingFootprint>>()
                         .WithAll<OffshoreSupplier>()
                         .WithEntityAccess())
            {
                if (!SystemAPI.HasBuffer<StockEntry>(entity)) continue;

                int owner = footprint.ValueRO.OwnerLocalId;
                if ((uint)owner >= StampLayers.MaxPlayers) continue;

                var output = SystemAPI.GetBuffer<StockEntry>(entity);

                // 풀 접속(2026-07-19 유저 확정 — 반경은 폴백 전용):
                //   · 유조선 모드 = 자기 항만이 **하나라도 있으면** 접속(거리 무관 — 배가 거리를
                //     왕복 시간으로 지불하므로 하드 반경은 이중 규제).
                //   · 텔레포트 폴백 = 반경 필요(즉시 이전이라 반경 없으면 마법이 됨).
                bool served = tankerMode
                    ? HasAnyPort(in ports, owner)
                    : InPortRange(in ports, owner, Center(footprint.ValueRO));
                if (!served)
                {
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

                // 유조선 모드: 항만 반경 안 = 배가 실어 나른다 — 텔레포트 배출 안 함.
                if (tankerMode) continue;

                // ── 배출(레거시 폴백 — LogisticsPushSystem과 동일 규약) ──
                for (int i = 0; i < output.Length; i++)
                {
                    var e = output[i];
                    if (e.Role != StockRole.Output) continue;
                    if (e.Current <= e.Discharge) continue;

                    const int floor = 0;
                    int want = e.Current - floor;
                    if (want <= 0) continue;

                    var key = LogisticsPool.Key(owner, e.Commodity);
                    pool.Cells.TryGetValue(key, out var cell);
                    int free = cell.Capacity - cell.Stored;
                    if (free <= 0) continue;   // 풀 만석 = 과잉생산 — 수요로 답하지 않음(계획 P)

                    int put = want < free ? want : free;
                    cell.Stored     += put;
                    pool.Cells[key]  = cell;
                    pool.RecordDeposit(key, put);
                    e.Current -= put;
                    output[i]  = e;
                }
            }

            ports.Dispose();
        }

        /// <summary>footprint 중심 셀(회전 반영).</summary>
        static int2 Center(in BuildingFootprint fp)
        {
            int2 size = EntranceOps.RotateSize(fp.Size, fp.RotSteps);
            return fp.Origin + size / 2;
        }

        static bool InPortRange(in NativeList<int4> ports, int owner, int2 pos)
        {
            for (int i = 0; i < ports.Length; i++)
            {
                if (ports[i].x != owner) continue;
                int dx = ports[i].y - pos.x, dy = ports[i].z - pos.y;
                if (dx * dx + dy * dy <= ports[i].w) return true;
            }
            return false;
        }

        /// <summary>이 소유자의 항만이 하나라도 있는가(유조선 모드 접속 판정 — 거리 무관).</summary>
        static bool HasAnyPort(in NativeList<int4> ports, int owner)
        {
            for (int i = 0; i < ports.Length; i++)
                if (ports[i].x == owner) return true;
            return false;
        }
    }
}
