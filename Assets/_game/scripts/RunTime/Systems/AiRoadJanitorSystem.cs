using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AiRoadJanitorSystem — AI 도로 잔해 정리 (반파 복구의 전제)
    // ──────────────────────────────────────────────────────────────────────────
    //  영토 전환 파괴/전투로 AI 도시가 반파되면 잘린 도로 팔·고립 조각이 남아
    //  격자 재확장(BlockValid)이 '내부에 도로 있음'으로 반복 거부 → 버려진 구역이 된다.
    //  AI 도로 불변식("항상 닫힌 사각형")을 이용해 잔해를 결정적으로 식별·제거:
    //
    //    ① dead-end trim — 이웃 footprint가 1개 이하인 자기 도로(막다른 끝)를 반복 제거
    //       → 잘린 팔이 교차점까지 침식. 정상 AI 도로엔 막다른 길이 없으므로 오검출 없음.
    //    ② 베이스-단절 잔해 섬 — 베이스(Anchor 근방)에서 도로로 도달 불가 + 자기 건물이
    //       하나도 안 붙은 '순수 도로 조각' 제거. (건물 딸린 섬은 유지 — 추후 재연결 후보.)
    //
    //    ③ 지선(RoadSpur) 관리 — 온전한 지선(타겟 인접 도로가 베이스 연결)은 끝 footprint를
    //       트림에서 보호(끝만 보호하면 라인 전체가 degree 2로 안전). 끊긴 지선은
    //       RoadPathRequest 재발행(자가 수리) — 옛 조각은 섬 제거로 청소 후 새 경로.
    //
    //  가드: 자기 건물 4-인접(입구 접근로) + 지선 끝. Explicit 가드는 폐기 —
    //        AI 링도 그린-모델(Explicit)이라 남기면 아무것도 못 다듬는다(지선은 ③이 보호).
    //        degree는 footprint 단위(멀티셀 안전).
    //  AI 팀 전용(인간 도로는 막다른 길이 정당). DayChanged 저빈도. 제거는 Forced
    //  RemoveRoadCommand → RoadSystem이 일괄 처리([UpdateBefore]).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct AiRoadJanitorSystem : ISystem
    {
        const int BaseSeedRadius = 8;   // Anchor 이 반경(Chebyshev) 안 도로 = 베이스 연결 시드

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<CellTypeLookup>();   // 섬 재연결 경로 탐색용
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!clock.DayChanged) return;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.RoadLayer.IsCreated) return;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 활성 지선 등록 목록 — 온전한 지선 보호 + 끊긴 지선 자가수리.
            var spurs = new NativeList<RoadSpur>(8, Allocator.Temp);
            foreach (var spur in SystemAPI.Query<RefRO<RoadSpur>>())
                spurs.Add(spur.ValueRO);

            var cellTypeLookup = SystemAPI.GetSingleton<CellTypeLookup>();
            if (!SystemAPI.TryGetSingleton<TeamTable>(out var teams)) teams = TeamTable.Identity;

            var aiFaction = new NativeHashMap<int, int>(8, Allocator.Temp);   // AI localId → factionId
            foreach (var (teamRO, gridRO) in
                     SystemAPI.Query<RefRO<Game.Unit.TeamInfoData>, RefRO<CityGrid>>())
            {
                if (teamRO.ValueRO.IsPlayer()) continue;
                aiFaction.TryAdd(teamRO.ValueRO.LocalID, gridRO.ValueRO.FactionId);
                Janitor(teamRO.ValueRO.LocalID, gridRO.ValueRO.FactionId, gridRO.ValueRO.Anchor,
                    in layers, in cellTypeLookup, in teams, in spurs, ref ecb);
            }

            // ── 입구 도로 복구 — 입구 도로 셀에 '자기 도로'가 없는 AI 건물은 죽은 자산
            //   (시민 경로·공급 stamp 단절 = 이진 고장) → RoadPathRequest로 본망에서 그 셀까지
            //   재부설(RegisterSpur=0: 일회성 — 건물 소멸 시 함께 잊힘). owner당 하루 N개 스로틀.
            const int MaxEntranceRepairsPerDay = 2;
            var repairCount = new NativeHashMap<int, int>(8, Allocator.Temp);
            foreach (var (bfRO, entRO) in
                     SystemAPI.Query<RefRO<BuildingFootprint>, RefRO<BuildingEntrance>>()
                              .WithNone<Game.Unit.CombatDeadTag>())
            {
                var bf = bfRO.ValueRO;
                if (!aiFaction.TryGetValue(bf.OwnerLocalId, out int fid)) continue;

                int2 erc = EntranceOps.EntranceRoadCell(
                    bf.Origin, bf.Size, in entRO.ValueRO.Entrance, bf.RotSteps);
                if (layers.RoadLayer.TryGetValue(erc, out var rc)
                    && rc.OwnerLocalId == bf.OwnerLocalId) continue;   // 입구 도로 살아있음

                repairCount.TryGetValue(bf.OwnerLocalId, out int n);
                if (n >= MaxEntranceRepairsPerDay) continue;
                repairCount[bf.OwnerLocalId] = n + 1;

                var re = ecb.CreateEntity();
                ecb.AddComponent(re, new RoadPathRequest
                {
                    Target = erc, OwnerLocalId = bf.OwnerLocalId, FactionId = fid,
                    StopAdjacent = 0, RegisterSpur = 0,
                });
            }
            repairCount.Dispose(); aiFaction.Dispose();

            spurs.Dispose();
            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        struct FpInfo { public byte Size; public byte AdjBuilding; }

        static void Janitor(int owner, int factionId, int2 anchor, in GridLayers layers,
            in CellTypeLookup cellTypeLookup, in TeamTable teams, in NativeList<RoadSpur> spurs,
            ref EntityCommandBuffer ecb)
        {
            // ── footprint 수집 (origin 단위) + 건물 인접 캐시 ────────────────
            var fps = new NativeHashMap<int2, FpInfo>(256, Allocator.Temp);
            foreach (var kv in layers.RoadLayer)
            {
                if (kv.Value.OwnerLocalId != owner) continue;
                int2 o = kv.Value.FootprintOrigin;
                fps.TryGetValue(o, out var info);
                info.Size = (byte)math.max(1, (int)kv.Value.Size);
                fps[o] = info;
            }
            if (fps.IsEmpty) { fps.Dispose(); return; }

            var origins = fps.GetKeyArray(Allocator.Temp);
            for (int i = 0; i < origins.Length; i++)
            {
                var info = fps[origins[i]];
                info.AdjBuilding = AdjacentOwnBuilding(origins[i], info.Size, owner, in layers) ? (byte)1 : (byte)0;
                fps[origins[i]] = info;
            }

            var removed   = new NativeHashSet<int2>(64, Allocator.Temp);
            var neighbors = new NativeList<int2>(8, Allocator.Temp);

            // ── 베이스 연결(reached) — 트림 전에 계산(지선 보호 판정에 필요) ──
            var reached = new NativeHashSet<int2>(origins.Length, Allocator.Temp);
            var q       = new NativeQueue<int2>(Allocator.Temp);
            for (int i = 0; i < origins.Length; i++)
            {
                int2 o = origins[i];
                int2 hi = o + fps[o].Size - 1;
                int dx = math.max(0, math.max(anchor.x - hi.x, o.x - anchor.x));
                int dz = math.max(0, math.max(anchor.y - hi.y, o.y - anchor.y));
                if (math.max(dx, dz) <= BaseSeedRadius && reached.Add(o)) q.Enqueue(o);
            }
            while (q.TryDequeue(out int2 cur))
            {
                CollectNeighborOrigins(cur, fps[cur].Size, owner, in layers, in removed, ref neighbors);
                for (int i = 0; i < neighbors.Length; i++)
                    if (reached.Add(neighbors[i])) q.Enqueue(neighbors[i]);
            }

            // ── 지선(RoadSpur) 보호 + 자가수리 ──────────────────────────────
            //   온전한 지선(타겟 인접 자기 도로가 베이스에 연결) → 그 끝 footprint 보호(트림 면제).
            //   끊긴 지선(인접 도로 없음/단절) → RoadPathRequest 재발행(다음 프레임 재부설).
            //     단절된 옛 조각은 보호 안 함 → 섬 제거로 청소된 뒤 새 경로가 깔린다.
            var spurProtected = new NativeHashSet<int2>(8, Allocator.Temp);
            for (int s = 0; s < spurs.Length; s++)
            {
                if (spurs[s].OwnerLocalId != owner) continue;
                bool intact = false;
                for (int d = 0; d < 4 && !intact; d++)
                {
                    int2 n = spurs[s].Target + RoadDirOps.Offsets[d];
                    if (layers.RoadLayer.TryGetValue(n, out var rc) && rc.OwnerLocalId == owner
                        && reached.Contains(rc.FootprintOrigin))
                    { spurProtected.Add(rc.FootprintOrigin); intact = true; }
                }
                // 타겟 셀 자체가 도로인 경우(StopAdjacent=0 지선)도 확인.
                if (!intact && layers.RoadLayer.TryGetValue(spurs[s].Target, out var tc)
                    && tc.OwnerLocalId == owner && reached.Contains(tc.FootprintOrigin))
                { spurProtected.Add(tc.FootprintOrigin); intact = true; }

                if (!intact)
                {
                    var re = ecb.CreateEntity();
                    ecb.AddComponent(re, new RoadPathRequest
                    {
                        Target = spurs[s].Target, OwnerLocalId = owner,
                        FactionId = spurs[s].FactionId, StopAdjacent = spurs[s].StopAdjacent,
                    });
                }
            }

            // ── ① dead-end trim (반복 침식) ─────────────────────────────────
            //   가드 = 건물 인접(입구 접근로) / 지선 끝. Explicit 가드는 폐기 —
            //   AI 링도 그린-모델(Explicit)이라 남겨두면 아무것도 못 다듬는다.
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < origins.Length; i++)
                {
                    int2 o = origins[i];
                    if (removed.Contains(o)) continue;
                    var info = fps[o];
                    if (info.AdjBuilding != 0 || spurProtected.Contains(o)) continue;

                    CollectNeighborOrigins(o, info.Size, owner, in layers, in removed, ref neighbors);
                    if (neighbors.Length <= 1) { removed.Add(o); changed = true; }
                }
            }

            // ── ② 베이스-단절 잔해 — '연결요소(섬)' 단위 판정 ────────────────
            //   섬에 자기 건물이 하나라도 붙어 있으면 통째로 유지(재연결 후보),
            //   전혀 없으면 통째로 제거. ※ footprint 단위로 지우면 건물 딸린 섬에서
            //   건물 안 붙은 교차로/코너(건물과 대각 관계라 4-인접 아님)만 뽑혀 나가
            //   도시가 벌집이 된다 — 실측 버그(2026-07-01).
            var visited = new NativeHashSet<int2>(64, Allocator.Temp);
            var comp    = new NativeList<int2>(64, Allocator.Temp);
            bool reconnected = false;   // 하루 1개 섬만 실제 연결(병행 과제) — 실패한 섬은 다음 섬 시도
            for (int i = 0; i < origins.Length; i++)
            {
                int2 seed = origins[i];
                if (removed.Contains(seed) || reached.Contains(seed) || visited.Contains(seed)) continue;

                comp.Clear();
                visited.Add(seed); q.Enqueue(seed);
                bool hasBuilding = false;
                while (q.TryDequeue(out int2 cur))
                {
                    comp.Add(cur);
                    if (fps[cur].AdjBuilding != 0) hasBuilding = true;
                    CollectNeighborOrigins(cur, fps[cur].Size, owner, in layers, in removed, ref neighbors);
                    for (int n = 0; n < neighbors.Length; n++)
                        if (!reached.Contains(neighbors[n]) && visited.Add(neighbors[n]))
                            q.Enqueue(neighbors[n]);
                }

                if (!hasBuilding)
                    for (int c = 0; c < comp.Length; c++) removed.Add(comp[c]);
                else if (!reconnected)
                    reconnected = TryReconnectIsland(owner, factionId, in comp, in layers,
                        in cellTypeLookup, in teams, in reached, ref ecb);
            }
            visited.Dispose(); comp.Dispose();

            // ── 제거 발행 ───────────────────────────────────────────────────
            foreach (var o in removed)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new RemoveRoadCommand { Cell = o, OwnerLocalId = owner, Forced = 1 });
            }

            spurProtected.Dispose(); neighbors.Dispose(); q.Dispose(); reached.Dispose();
            removed.Dispose(); origins.Dispose(); fps.Dispose();
        }

        // 건물 딸린 고립 섬을 본망에 재연결 — FindRoadPathToIsland(베이스-연결 시드, 섬의 아무
        //   1×1 도로 셀 인접이 목표 = 최근접 접점 자동, 통과성에 영토 게이트 포함 = 배치 거부와
        //   일치해 '깔고 실패' 없음). 성공 시 그린 경로 발행 + 접점 상호 비트. 실패 = no-op(포위 지속).
        static bool TryReconnectIsland(int owner, int factionId, in NativeList<int2> islandComp,
            in GridLayers layers, in CellTypeLookup cellTypeLookup, in TeamTable teams,
            in NativeHashSet<int2> reached, ref EntityCommandBuffer ecb)
        {
            var islandOrigins = new NativeHashSet<int2>(islandComp.Length, Allocator.Temp);
            for (int i = 0; i < islandComp.Length; i++) islandOrigins.Add(islandComp[i]);

            var path = new NativeList<int2>(64, Allocator.Temp);
            bool ok = BlockOps.FindRoadPathToIsland(
                in layers.RoadLayer, in layers.OccupancyLayer, in layers.TerrainLayer,
                in layers.ResourceLayer, in cellTypeLookup, in layers.TerritoryLayer, in teams,
                owner, in reached, in islandOrigins, 8192, ref path, out int2 islandCell);

            if (ok && path.Length >= 1)
            {
                // path[0]=소스 도로 셀 … 마지막=접점 직전. 새 셀이 있을 때만 경로 발행
                //   (길이 1 = 소스가 이미 섬에 인접 → 비트 병합만으로 연결).
                if (path.Length >= 2)
                    RoadPathSystem.EmitDrawnPath(in path, owner, factionId, ref ecb);

                int2 last  = path[path.Length - 1];
                int2 delta = islandCell - last;
                for (int d = 0; d < 4; d++)
                {
                    if (!RoadDirOps.Offsets[d].Equals(delta)) continue;
                    EmitBit(last, (RoadDir)(1 << d), owner, factionId, ref ecb);              // 끝→섬
                    EmitBit(islandCell, (RoadDir)(1 << ((d + 2) & 3)), owner, factionId, ref ecb); // 섬→끝
                    break;
                }
            }
            path.Dispose(); islandOrigins.Dispose();
            return ok;
        }

        static void EmitBit(int2 cell, RoadDir bit, int owner, int factionId, ref EntityCommandBuffer ecb)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new PlaceRoadCommand
            {
                Cell = cell, OwnerLocalId = owner, LaneCount = 2, FactionId = factionId,
                Size = 1, Axis = RoadPlacedAxis.Any, Directions = bit,
            });
        }

        // footprint에 4-인접한 '다른' 자기 도로 footprint origin들(removed 제외, 중복 제거).
        static void CollectNeighborOrigins(
            int2 origin, int size, int owner, in GridLayers layers,
            in NativeHashSet<int2> removed, ref NativeList<int2> result)
        {
            result.Clear();
            for (int dz = 0; dz < size; dz++)
            for (int dx = 0; dx < size; dx++)
            {
                int2 c = origin + new int2(dx, dz);
                for (int d = 0; d < 4; d++)
                {
                    int2 n = c + RoadDirOps.Offsets[d];
                    // footprint 내부 셀은 이웃 아님.
                    if (n.x >= origin.x && n.x < origin.x + size &&
                        n.y >= origin.y && n.y < origin.y + size) continue;
                    if (!layers.RoadLayer.TryGetValue(n, out var rc) || rc.OwnerLocalId != owner) continue;
                    int2 no = rc.FootprintOrigin;
                    if (no.Equals(origin) || removed.Contains(no)) continue;
                    bool dup = false;
                    for (int i = 0; i < result.Length && !dup; i++) dup = result[i].Equals(no);
                    if (!dup) result.Add(no);
                }
            }
        }

        // footprint에 4-인접한 자기 건물 셀이 있나 (입구 접근로 보호).
        static bool AdjacentOwnBuilding(int2 origin, int size, int owner, in GridLayers layers)
        {
            for (int dz = 0; dz < size; dz++)
            for (int dx = 0; dx < size; dx++)
            {
                int2 c = origin + new int2(dx, dz);
                for (int d = 0; d < 4; d++)
                {
                    int2 n = c + RoadDirOps.Offsets[d];
                    if (layers.OccupancyLayer.TryGetValue(n, out var occ)
                        && occ.Type == OccupantType.Building
                        && occ.OwnerLocalId == owner) return true;
                }
            }
            return false;
        }
    }
}
