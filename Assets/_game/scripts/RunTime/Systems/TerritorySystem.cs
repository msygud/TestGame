using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritorySystem — 인구 기반 영역 재계산 + 영역 침범물 파괴(capture)
    // ──────────────────────────────────────────────────────────────────────────
    //  매 게임시간(HourChanged)마다:
    //    ① 거주건물(ResidenceBuilding)마다 인구→영역 셀 수→원형 영향력을 누적.
    //       같은 셀의 플레이어별 영향력을 합산(8슬롯) → 순(net) 최대 팀이 셀 소유.
    //       결과를 GridLayers.TerritoryLayer(int2→LocalId)에 기록(매번 클리어 후 재작성).
    //    ② capture — 영역이 새로 적을 덮으면(셀 소유자 ≠ 건물/도로 소유자, 둘 다 실제 팀):
    //         · 적 건물 footprint → RazeAreaCommand (RazeSystem이 파괴 + 점유 해제)
    //         · 적 도로 셀        → RemoveRoadCommand{Forced=1} (RoadSystem이 철거)
    //       → "유일한 강제 파괴 = 남의 영역 안의 도로·건설물" (설계 점6).
    //
    //  메인스레드·저빈도(HourChanged). TerritoryLayer의 유일한 writer.
    //  시스템 순서: RazeSystem·RoadSystem 전 → 발행한 명령이 같은 프레임에 실행된다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RazeSystem))]
    public partial struct TerritorySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!clock.HourChanged) return;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;
            var cfg = TerritoryConfig.Default;

            // ── ① 영향력 누적 ───────────────────────────────────────────────
            var accum = new NativeHashMap<int2, CellAccum>(2048, Allocator.Temp);

            foreach (var (occ, bf) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                              .WithAll<ResidenceBuilding>())
            {
                int owner = bf.ValueRO.OwnerLocalId;
                if ((uint)owner >= StampLayers.MaxPlayers) continue;

                // 인구: 입주자(Current) 우선, 아직 비었으면 정원(Capacity)을 잠재 인구로.
                //   (시민 스폰이 붙으면 Current가 진짜 인구가 된다 — 공식은 동일.)
                int pop = occ.ValueRO.Current > 0 ? occ.ValueRO.Current : occ.ValueRO.Capacity;
                if (pop <= 0) continue;

                int cellCount = math.max(1, pop / math.max(1, cfg.PopPerCell));
                // 원형 전파: 디스크 넓이 ≈ 셀 수 → r = √(N/π).
                int r = (int)math.ceil(math.sqrt(cellCount / math.PI));
                r = math.clamp(r, 1, cfg.MaxRadius);

                int2 eff    = EntranceOps.RotateSize(bf.ValueRO.Size, bf.ValueRO.RotSteps);
                int2 center = bf.ValueRO.Origin + eff / 2;

                for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    float dist = math.sqrt((float)(dx * dx + dz * dz));
                    if (dist > r) continue;                       // 디스크 밖
                    int2 cell = center + new int2(dx, dz);
                    if (!layers.TerrainLayer.ContainsKey(cell)) continue;  // 맵 밖만 제외

                    int w = math.max(1, (int)math.round(r - dist + 1));    // 중심일수록 큼
                    accum.TryGetValue(cell, out var ca);
                    ca.Add(owner, w);
                    accum[cell] = ca;
                }
            }

            // ── ② TerritoryLayer 재작성(클리어 후) — 순 영향력 최대 팀이 셀 소유 ──
            layers.TerritoryLayer.Clear();
            foreach (var kv in accum)
            {
                int owner = kv.Value.Best();
                if (owner >= 0) layers.TerritoryLayer[kv.Key] = owner;
            }
            accum.Dispose();

            // ── ③ capture — 적 영역에 들어간 적 건물/도로 강제 파괴 ───────────
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 적 건물 → RazeAreaCommand (footprint 사각형)
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

            // 적 도로 셀 → RemoveRoadCommand{Forced}
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

        // ── 셀별 플레이어 영향력 누적(8슬롯, 중첩 컨테이너 회피 — StampLayers 패턴) ──
        struct CellAccum
        {
            public int A0, A1, A2, A3, A4, A5, A6, A7;

            public void Add(int owner, int amt)
            {
                switch (owner)
                {
                    case 0: A0 += amt; break;
                    case 1: A1 += amt; break;
                    case 2: A2 += amt; break;
                    case 3: A3 += amt; break;
                    case 4: A4 += amt; break;
                    case 5: A5 += amt; break;
                    case 6: A6 += amt; break;
                    case 7: A7 += amt; break;
                }
            }

            /// <summary>순 영향력 최대 팀 LocalId(전부 0이면 -1). 동률은 낮은 id 우선(결정적).</summary>
            public int Best()
            {
                int best = 0, bestOwner = -1;
                Consider(A0, 0, ref best, ref bestOwner);
                Consider(A1, 1, ref best, ref bestOwner);
                Consider(A2, 2, ref best, ref bestOwner);
                Consider(A3, 3, ref best, ref bestOwner);
                Consider(A4, 4, ref best, ref bestOwner);
                Consider(A5, 5, ref best, ref bestOwner);
                Consider(A6, 6, ref best, ref bestOwner);
                Consider(A7, 7, ref best, ref bestOwner);
                return bestOwner;
            }

            static void Consider(int inf, int owner, ref int best, ref int bestOwner)
            {
                if (inf > best) { best = inf; bestOwner = owner; }
            }
        }
    }
}
