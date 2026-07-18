using Game.Unit;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TankerSystem — 유조선 운항: 시추 → 항만 실체 운송 (2026-07-19)
    //
    //  루프(항만당 1척, 저빈도 게이트 — push와 동일 주기):
    //    ① 스폰: 항만(WarehouseTag.SeaRange>0)에 배가 없으면 인근 물 셀에 1척 스폰.
    //    ② Docked: 반경 내 자기 시추 중 Output이 discharge를 넘은 곳 → ToRig + 이동 명령.
    //    ③ ToRig 도착: 시추 Output에서 실제 차감해 적재(용량까지) → ToPort + 이동 명령.
    //    ④ ToPort 도착: 공유 풀에 하역(여유만큼, RecordDeposit 계측). 풀 만석이면 잔량
    //       유지한 채 다음 게이트에 재시도. 다 내리면 Docked.
    //
    //  이동 = 유닛 골격 위임: MoveOrderRequest만 발행(NavalUnit이 물 도메인 A*를 탄다).
    //  항해 중엔 게이트마다 이동 명령을 재발행(경로 실패·스턱 자연 복구 — 저빈도라 무부담).
    //  도착 판정 = 거리(느슨, ArriveSlack) — 프레임 정밀 불필요("결과 지연 허용").
    //  화물은 실체: 배가 없으면(격침 등) 화물도 없다 — 보급선 습격의 물질 기반.
    //  ※ 메인스레드: 건물 StockEntry buffer 접근(Pull/Push와 동일 사유) + 소수 엔티티.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TankerSystem : ISystem
    {
        const float ArriveSlack   = 6f;   // 도착 판정 반경(월드) — StopDistance보다 여유
        const float StopDistRig   = 4f;   // 시추 앞 정지 거리
        const float StopDistPort  = 4f;   // 항만 앞 정지 거리

        double _nextGameSec;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TankerPrefabSingleton>();
            state.RequireForUpdate<LogisticsPool>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridSettings>();
            state.RequireForUpdate<CellTypeLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton<GameClock>(out var clk))
            {
                if (clk.TotalSeconds < _nextGameSec) return;
                _nextGameSec = clk.TotalSeconds + clk.SecondsPerDay / 48f;
            }

            var prefab   = SystemAPI.GetSingleton<TankerPrefabSingleton>();
            var pool     = SystemAPI.GetSingleton<LogisticsPool>();
            var layers   = SystemAPI.GetSingleton<GridLayers>();
            var settings = SystemAPI.GetSingleton<GridSettings>();
            var cellType = SystemAPI.GetSingleton<CellTypeLookup>();
            var ecb      = new EntityCommandBuffer(Allocator.Temp);

            // ── 항만 수집 + 항만별 배 유무 ──────────────────────────────────
            var ports = new NativeList<PortInfo>(8, Allocator.Temp);
            foreach (var (wt, fp, tf, e) in
                     SystemAPI.Query<RefRO<WarehouseTag>, RefRO<BuildingFootprint>,
                                     RefRO<LocalTransform>>().WithEntityAccess())
            {
                if (wt.ValueRO.SeaRange <= 0) continue;
                ports.Add(new PortInfo
                {
                    Entity = e, Owner = wt.ValueRO.OwnerLocalId,
                    Pos = tf.ValueRO.Position, RangeSq = (float)wt.ValueRO.SeaRange * wt.ValueRO.SeaRange
                                                          * settings.CellSize * settings.CellSize,
                    Origin = fp.ValueRO.Origin,
                });
            }
            if (ports.Length == 0) { ports.Dispose(); ecb.Playback(state.EntityManager); ecb.Dispose(); return; }

            var portHasTanker = new NativeHashMap<Entity, byte>(ports.Length, Allocator.Temp);
            foreach (var tanker in SystemAPI.Query<RefRO<OilTanker>>())
                portHasTanker.TryAdd(tanker.ValueRO.HomePort, 1);

            // ── ① 스폰: 배 없는 항만 → 인근 물 셀에 1척 ──────────────────────
            for (int i = 0; i < ports.Length; i++)
            {
                if (portHasTanker.ContainsKey(ports[i].Entity)) continue;
                if (!TryFindNearbyWaterCell(ports[i].Origin, 12, in layers, cellType, out int2 waterCell))
                    continue;   // 물이 안 닿는 창고 = 항만 아님(설정 실수) — 조용히 패스

                var t = ecb.Instantiate(prefab.Prefab);
                float3 pos = settings.CellCenter(waterCell.x, waterCell.y);
                ecb.SetComponent(t, LocalTransform.FromPosition(pos));
                ecb.AddComponent(t, new OilTanker
                {
                    HomePort = ports[i].Entity, TargetRig = Entity.Null,
                    Phase = TankerPhase.Docked, CargoCommodity = Commodity.None,
                    Cargo = 0, Capacity = prefab.Capacity,
                });
                ecb.AddSharedComponent(t, new OwnerShared(ports[i].Owner));
                portHasTanker.TryAdd(ports[i].Entity, 1);
            }

            // ── ②③④ 운항 상태 기계 ─────────────────────────────────────────
            var tfLookup    = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var fpLookup    = SystemAPI.GetComponentLookup<BuildingFootprint>(true);
            var stockLookup = SystemAPI.GetBufferLookup<StockEntry>(false);

            foreach (var (tankerRW, tf, tankerEntity) in
                     SystemAPI.Query<RefRW<OilTanker>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                ref var tk = ref tankerRW.ValueRW;

                // 소속 항만 소실(철거·점령) → 배는 남되 대기(다음 게이트에 새 항만 스폰 로직과 별개).
                if (!state.EntityManager.Exists(tk.HomePort) || !tfLookup.HasComponent(tk.HomePort))
                    continue;
                float3 portPos = tfLookup[tk.HomePort].Position;
                int    portIdx = IndexOfPort(in ports, tk.HomePort);
                if (portIdx < 0) continue;

                switch (tk.Phase)
                {
                    case TankerPhase.Docked:
                    {
                        // 일감: 자기 소유·항만 반경 내 시추 중 Output > Discharge.
                        Entity bestRig = Entity.Null; float bestD = float.MaxValue; float3 bestPos = default;
                        foreach (var (fp, rigTf, rigE) in
                                 SystemAPI.Query<RefRO<BuildingFootprint>, RefRO<LocalTransform>>()
                                     .WithAll<OffshoreSupplier>().WithEntityAccess())
                        {
                            if (fp.ValueRO.OwnerLocalId != ports[portIdx].Owner) continue;
                            if (!stockLookup.HasBuffer(rigE)) continue;
                            float dSq = math.distancesq(rigTf.ValueRO.Position, ports[portIdx].Pos);
                            if (dSq > ports[portIdx].RangeSq || dSq >= bestD) continue;
                            var stock = stockLookup[rigE];
                            for (int s = 0; s < stock.Length; s++)
                                if (stock[s].Role == StockRole.Output && stock[s].Current > stock[s].Discharge)
                                { bestRig = rigE; bestD = dSq; bestPos = rigTf.ValueRO.Position; break; }
                        }
                        if (bestRig == Entity.Null) break;

                        tk.TargetRig = bestRig; tk.Phase = TankerPhase.ToRig;
                        EmitMove(ecb, tankerEntity, bestPos, StopDistRig);
                        break;
                    }

                    case TankerPhase.ToRig:
                    {
                        // 시추 소실 → 복귀.
                        if (!state.EntityManager.Exists(tk.TargetRig) || !tfLookup.HasComponent(tk.TargetRig))
                        { tk.TargetRig = Entity.Null; tk.Phase = TankerPhase.Docked; break; }

                        float3 rigPos = tfLookup[tk.TargetRig].Position;
                        if (math.distance(tf.ValueRO.Position, rigPos) > StopDistRig + ArriveSlack)
                        { EmitMove(ecb, tankerEntity, rigPos, StopDistRig); break; }   // 재발행 = 스턱 복구

                        // 적재: 시추 Output → 화물(실체 이전).
                        if (stockLookup.HasBuffer(tk.TargetRig))
                        {
                            var stock = stockLookup[tk.TargetRig];
                            for (int s = 0; s < stock.Length && tk.Cargo < tk.Capacity; s++)
                            {
                                var entry = stock[s];
                                if (entry.Role != StockRole.Output || entry.Current <= 0) continue;
                                if (tk.CargoCommodity != Commodity.None && tk.CargoCommodity != entry.Commodity) continue;
                                int take = math.min(entry.Current, tk.Capacity - tk.Cargo);
                                entry.Current -= take; stock[s] = entry;
                                tk.CargoCommodity = entry.Commodity; tk.Cargo += take;
                            }
                        }
                        tk.TargetRig = Entity.Null; tk.Phase = TankerPhase.ToPort;
                        EmitMove(ecb, tankerEntity, portPos, StopDistPort);
                        break;
                    }

                    case TankerPhase.ToPort:
                    {
                        if (math.distance(tf.ValueRO.Position, portPos) > StopDistPort + ArriveSlack)
                        { EmitMove(ecb, tankerEntity, portPos, StopDistPort); break; }

                        // 하역: 공유 풀에 여유만큼(만석 = 잔량 유지, 다음 게이트 재시도).
                        if (tk.Cargo > 0 && tk.CargoCommodity != Commodity.None)
                        {
                            var key = LogisticsPool.Key(ports[portIdx].Owner, tk.CargoCommodity);
                            pool.Cells.TryGetValue(key, out var cell);
                            int free = cell.Capacity - cell.Stored;
                            int put  = math.min(tk.Cargo, math.max(0, free));
                            if (put > 0)
                            {
                                cell.Stored += put; pool.Cells[key] = cell;
                                pool.RecordDeposit(key, put);
                                tk.Cargo -= put;
                            }
                        }
                        if (tk.Cargo <= 0) { tk.Cargo = 0; tk.CargoCommodity = Commodity.None; tk.Phase = TankerPhase.Docked; }
                        break;
                    }
                }
            }

            portHasTanker.Dispose();
            ports.Dispose();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        struct PortInfo
        {
            public Entity Entity;
            public int    Owner;
            public float3 Pos;
            public float  RangeSq;   // 월드 거리²(SeaRange 셀 × CellSize)
            public int2   Origin;
        }

        static int IndexOfPort(in NativeList<PortInfo> ports, Entity e)
        {
            for (int i = 0; i < ports.Length; i++)
                if (ports[i].Entity == e) return i;
            return -1;
        }

        static void EmitMove(EntityCommandBuffer ecb, Entity unit, float3 target, float stopDistance)
        {
            var req = ecb.CreateEntity();
            ecb.AddComponent(req, new MoveOrderRequest
            {
                Unit = unit, Target = target, StopDistance = stopDistance,
                RepathCount = 0, CommandKind = UnitCommandKind.ForceMove, SkipPathfinding = 0,
            });
        }

        /// <summary>footprint 주변 나선 탐색으로 가장 가까운 물 셀(도시 그리드). 메인 읽기 전용.</summary>
        static bool TryFindNearbyWaterCell(int2 origin, int maxRadius,
            in GridLayers layers, CellTypeLookup cellType, out int2 result)
        {
            for (int r = 1; r <= maxRadius; r++)
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (math.max(math.abs(dx), math.abs(dz)) != r) continue;   // 링 외곽만
                var cell = origin + new int2(dx, dz);
                if (layers.TerrainLayer.TryGetValue(cell, out var terrain)
                    && cellType.TryGet(terrain.TypeId, out var info)
                    && info.TerrainCategory == TerrainCategory.Water)
                { result = cell; return true; }
            }
            result = default;
            return false;
        }
    }
}
