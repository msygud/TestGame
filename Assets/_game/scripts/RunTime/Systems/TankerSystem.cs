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
        NativeHashSet<Entity> _warnedPorts;   // 물 없는 항만 경고 = 엔티티당 1회(스팸 방지)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<TankerPrefabSingleton>();
            state.RequireForUpdate<LogisticsPool>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridSettings>();
            state.RequireForUpdate<CellTypeLookup>();
            _warnedPorts = new NativeHashSet<Entity>(8, Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_warnedPorts.IsCreated) _warnedPorts.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (SystemAPI.TryGetSingleton<GameClock>(out var clk))
            {
                if (clk.TotalSeconds < _nextGameSec) return;
                _nextGameSec = clk.TotalSeconds + clk.SecondsPerDay / 48f;
            }

            // 메인스레드 룩업(LocalTransform 등 — 이동 잡이 쓰는 타입) 접근 전 완료.
            //   저빈도 게이트(25게임분) 뒤라 동기화 비용 무시 가능.
            state.CompleteDependency();

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
                if (wt.ValueRO.SeaRange <= 0) continue;   // SeaRange>0 = 항만 표식(운항 반경 아님)
                ports.Add(new PortInfo
                {
                    Entity = e, Owner = wt.ValueRO.OwnerLocalId,
                    Pos = tf.ValueRO.Position, Origin = fp.ValueRO.Origin,
                    SeaRange = wt.ValueRO.SeaRange,
                });
            }
            if (ports.Length == 0) { ports.Dispose(); ecb.Playback(state.EntityManager); ecb.Dispose(); return; }

            var portHasTanker = new NativeHashMap<Entity, byte>(ports.Length, Allocator.Temp);
            foreach (var tanker in SystemAPI.Query<RefRO<OilTanker>>())
                portHasTanker.TryAdd(tanker.ValueRO.HomePort, 1);

            // 스폰 게이트(2026-07-19 유저 관찰 반영): 시추(해상 공급자)가 하나도 없는 소유자의
            //   항만엔 배를 띄우지 않는다 — 석유 산업 없는 항만의 유령선 노이즈 제거.
            //   첫 시추가 서면 다음 게이트에 자동 스폰(구조 변경 없음, 값 조건만).
            var ownersWithRigs = new NativeHashSet<int>(8, Allocator.Temp);
            foreach (var fp in SystemAPI.Query<RefRO<BuildingFootprint>>().WithAll<OffshoreSupplier>())
                ownersWithRigs.Add(fp.ValueRO.OwnerLocalId);

            // ── ① 스폰: 배 없는 항만 + 소유자 시추 보유 → 인근 물 셀에 1척 ──────
            for (int i = 0; i < ports.Length; i++)
            {
                if (portHasTanker.ContainsKey(ports[i].Entity)) continue;
                if (!ownersWithRigs.Contains(ports[i].Owner)) continue;   // 시추 0 = 스폰 보류
                if (!TryFindNearbyWaterCell(ports[i].Origin, 12, in layers, cellType, out int2 waterCell))
                {
                    // 물 없는 "항만" = 데이터 이상(스테일 베이크·과거 배치 잔재 등) — 엔티티당
                    //   1회만 경고(스팸 방지). 좌표로 현장 확인(F11 클릭 = Port 표시) 후 Alt+클릭
                    //   철거가 정리 경로.
                    if (_warnedPorts.Add(ports[i].Entity))
                        UnityEngine.Debug.LogWarning(
                            $"[Tanker] P{ports[i].Owner} 항만 @{ports[i].Origin} (SeaRange={ports[i].SeaRange}) " +
                            "인근 12셀에 물 없음 — 유조선 스폰 불가. 내륙 항만 = 데이터 이상: 현장 F11 확인 요망");
                    continue;
                }

                var t = ecb.Instantiate(prefab.Prefab);
                float3 pos = settings.CellCenter(waterCell.x, waterCell.y);
                UnityEngine.Debug.Log($"[Tanker] P{ports[i].Owner} 유조선 스폰 @{waterCell} (항만 {ports[i].Origin})");
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
                        // 일감: 자기 소유 시추 중 Output > Discharge — 최근접 우선.
                        //   ⚠ 반경 제한 없음(2026-07-19 유저 확정): 배는 거리=왕복 시간이라는
                        //   연속 비용을 이미 지불한다 — 하드 컷오프는 이중 규제 + 조용한 정지 함정.
                        //   SeaRange는 항만 표식·텔레포트 폴백 반경으로만 남는다.
                        int seen = 0, owned = 0, ready = 0;
                        Entity bestRig = Entity.Null; float bestD = float.MaxValue; float3 bestPos = default;
                        foreach (var (fp, rigTf, rigE) in
                                 SystemAPI.Query<RefRO<BuildingFootprint>, RefRO<LocalTransform>>()
                                     .WithAll<OffshoreSupplier>().WithEntityAccess())
                        {
                            seen++;
                            if (fp.ValueRO.OwnerLocalId != ports[portIdx].Owner) continue;
                            owned++;
                            if (!stockLookup.HasBuffer(rigE)) continue;
                            bool hasJob = false;
                            var stock = stockLookup[rigE];
                            for (int s = 0; s < stock.Length; s++)
                                if (stock[s].Role == StockRole.Output && stock[s].Current > stock[s].Discharge)
                                { hasJob = true; break; }
                            if (!hasJob) continue;
                            ready++;
                            float dSq = math.distancesq(rigTf.ValueRO.Position, ports[portIdx].Pos);
                            if (dSq >= bestD) continue;
                            bestRig = rigE; bestD = dSq; bestPos = rigTf.ValueRO.Position;
                        }
                        if (bestRig == Entity.Null)
                        {
                            UnityEngine.Debug.Log(
                                $"[Tanker] P{ports[portIdx].Owner} Docked 일감 없음 — 시추 {seen} / 내소유 {owned} / 출하대기 {ready}");
                            break;
                        }

                        tk.TargetRig = bestRig; tk.Phase = TankerPhase.ToRig;
                        UnityEngine.Debug.Log(
                            $"[Tanker] P{ports[portIdx].Owner} 적재 항해 시작 → 시추 @{bestPos.xz} (거리 {math.sqrt(bestD):F0})");
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
                        UnityEngine.Debug.Log($"[Tanker] 적재 {tk.Cargo}/{tk.Capacity} ({tk.CargoCommodity}) → 항만 복귀");
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
                        if (tk.Cargo <= 0)
                        {
                            UnityEngine.Debug.Log($"[Tanker] 하역 완료 → Docked");
                            tk.Cargo = 0; tk.CargoCommodity = Commodity.None; tk.Phase = TankerPhase.Docked;
                        }
                        break;
                    }
                }
            }

            ownersWithRigs.Dispose();
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
            public int2   Origin;
            public int    SeaRange;   // 진단용(유령 항만 = 스테일 베이크 쓰레기 값 판별)
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
