using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritorySystem — 인구 기반 영역 재계산 + 영역 침범물 파괴(capture)
    // ──────────────────────────────────────────────────────────────────────────
    //  ~1초마다(실시간) 전체 재계산(기존 결정 셀도 매번 재결정):
    //    ① 거주건물(ResidenceBuilding)마다 영역 셀 수 = 인구 / PopPerCell.
    //       소유자별로 모든 거주지의 셀 수를 합산한 예산(budget)만큼,
    //       거주지 중심에서 '가장 가까운 셀'을 채운다(다중소스 nearest-N).
    //       → 거주지가 겹치면 예산이 합산돼 경계가 바깥으로 밀려난다(중첩 전파).
    //       셀 소유 경합(다른 팀)은 더 가까운 쪽이 가진다(net by proximity).
    //       결과를 GridLayers.TerritoryLayer(int2→LocalId)에 기록(클리어 후 재작성).
    //    ② capture — 영역이 적을 덮으면(셀 소유자 ≠ 건물/도로 소유자, 둘 다 실제 팀):
    //         · 적 건물 footprint → RazeAreaCommand   (RazeSystem이 파괴)
    //         · 적 도로 셀        → RemoveRoadCommand{Forced=1} (RoadSystem이 철거)
    //
    //  메인스레드·저빈도(1초). TerritoryLayer의 유일한 writer.
    //  시스템 순서: RazeSystem·RoadSystem 전 → capture 명령이 같은 프레임에 실행.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RazeSystem))]
    public partial struct TerritorySystem : ISystem
    {
        double _nextRecompute;   // 다음 재계산 실시각(초)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            _nextRecompute = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 초단위 게이트 ───────────────────────────────────────────────
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextRecompute) return;
            _nextRecompute = now + 1.0;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;

            int popPerCell = TerritoryConfig.Default.PopPerCell;
            int maxRadius  = TerritoryConfig.Default.MaxRadius;
            if (SystemAPI.TryGetSingleton<TerritoryConfig>(out var cfg))
            {
                popPerCell = math.max(1, cfg.PopPerCell);
                maxRadius  = math.max(1, cfg.MaxRadius);
            }

            // ── 거주건물 수집 ──────────────────────────────────────────────
            var residences = new NativeList<ResInfo>(64, Allocator.Temp);
            foreach (var (occ, bf) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                              .WithAll<ResidenceBuilding>())
            {
                int owner = bf.ValueRO.OwnerLocalId;
                if ((uint)owner >= StampLayers.MaxPlayers) continue;

                int pop = occ.ValueRO.Current > 0 ? occ.ValueRO.Current : occ.ValueRO.Capacity;
                if (pop <= 0) continue;

                int2 eff    = EntranceOps.RotateSize(bf.ValueRO.Size, bf.ValueRO.RotSteps);
                int2 center = bf.ValueRO.Origin + eff / 2;

                residences.Add(new ResInfo
                {
                    Owner  = owner,
                    Center = center,
                    Cells  = math.max(1, pop / popPerCell),
                });
            }

            // ── 소유자별 nearest-N 배정 → 경합은 거리로 해소 ────────────────
            var best = new NativeHashMap<int2, OwnerDist>(2048, Allocator.Temp);
            var cmp  = new ClaimComparer();

            for (int owner = 0; owner < StampLayers.MaxPlayers; owner++)
            {
                // 이 소유자의 예산 + 경계 박스
                int budget = 0;
                int2 bbMin = default, bbMax = default;
                bool any = false;
                for (int i = 0; i < residences.Length; i++)
                {
                    if (residences[i].Owner != owner) continue;
                    budget += residences[i].Cells;
                    int2 c = residences[i].Center;
                    if (!any) { bbMin = c; bbMax = c; any = true; }
                    else { bbMin = math.min(bbMin, c); bbMax = math.max(bbMax, c); }
                }
                if (!any || budget <= 0) continue;

                int margin = math.min(maxRadius, (int)math.ceil(math.sqrt(budget / math.PI)) + 2);

                // 윈도우 내 후보 셀 + 가장 가까운 거주지까지 거리
                var cand = new NativeList<Claim>(256, Allocator.Temp);
                for (int z = bbMin.y - margin; z <= bbMax.y + margin; z++)
                for (int x = bbMin.x - margin; x <= bbMax.x + margin; x++)
                {
                    int2 cell = new int2(x, z);
                    if (!layers.TerrainLayer.ContainsKey(cell)) continue;   // 맵 안만

                    float md = float.MaxValue;
                    for (int i = 0; i < residences.Length; i++)
                    {
                        if (residences[i].Owner != owner) continue;
                        float d = math.distance((float2)cell, (float2)residences[i].Center);
                        if (d < md) md = d;
                    }
                    if (md < float.MaxValue) cand.Add(new Claim { Dist = md, Cell = cell });
                }

                // 가까운 순으로 예산만큼만 배정
                var arr = cand.AsArray();
                arr.Sort(cmp);
                int take = math.min(budget, arr.Length);
                for (int k = 0; k < take; k++)
                {
                    var c = arr[k];
                    if (!best.TryGetValue(c.Cell, out var b) || c.Dist < b.Dist)
                        best[c.Cell] = new OwnerDist { Owner = owner, Dist = c.Dist };
                }
                cand.Dispose();
            }

            // ── TerritoryLayer 재작성 ───────────────────────────────────────
            layers.TerritoryLayer.Clear();
            foreach (var kv in best)
                layers.TerritoryLayer[kv.Key] = kv.Value.Owner;

            residences.Dispose();
            best.Dispose();

            // ── capture — 적 영역에 든 적 건물/도로 강제 파괴 ───────────────
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var bf in SystemAPI.Query<RefRO<BuildingFootprint>>())
            {
                int2 eff = EntranceOps.RotateSize(bf.ValueRO.Size, bf.ValueRO.RotSteps);
                int2 org = bf.ValueRO.Origin;
                if (!TerritoryOps.FootprintInEnemyTerritory(
                        in layers.TerritoryLayer, org, eff, bf.ValueRO.OwnerLocalId))
                    continue;

                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new RazeAreaCommand { Min = org, Max = org + eff - 1 });
            }

            foreach (var kv in layers.RoadLayer)
            {
                var rc = kv.Value;
                if (!TerritoryOps.InEnemyTerritory(in layers.TerritoryLayer, kv.Key, rc.OwnerLocalId))
                    continue;

                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new RemoveRoadCommand
                {
                    Cell = kv.Key, OwnerLocalId = rc.OwnerLocalId, Forced = 1,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        struct ResInfo { public int Owner; public int2 Center; public int Cells; }
        struct OwnerDist { public int Owner; public float Dist; }
        struct Claim { public float Dist; public int2 Cell; }

        // 거리 오름차순, 동률은 셀 좌표(결정적).
        struct ClaimComparer : IComparer<Claim>
        {
            public int Compare(Claim a, Claim b)
            {
                if (a.Dist < b.Dist) return -1;
                if (a.Dist > b.Dist) return 1;
                if (a.Cell.x != b.Cell.x) return a.Cell.x - b.Cell.x;
                return a.Cell.y - b.Cell.y;
            }
        }
    }
}
