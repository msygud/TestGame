using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Game.Unit;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AiCityGrowthSystem — AI 팀 도시 성장 (모서리 앵커 / 가변 블록)
    //
    //  핵심: 새 블록은 기존 팀 도로 셀(모서리)을 '시작점'으로 그 지점부터 셀을 채운다.
    //    · 시작 모서리에 닿는 링이 기존 도로와 정확히 일치 → 어긋남 없는 삼거리/사거리.
    //    · 블록 크기는 건물에서 {4,6,8} 자동. 이웃과 크기/변 길이 달라도 됨
    //      (공유변은 겹치는 만큼만 공유, 나머지는 새 도로).
    //    · 확장 편향(덜 자란 축) 최우선 → 한쪽 쏠림 방지. 동률 시 오목/노치 우선(빈틈 메움).
    //    · footprint(내부 K + 도로 링) 전체를 평탄·Land·맵안 검증 → 해변/단차엔 안 깖.
    //
    //  배치 후보: 각 팀 도로 셀 R의 4개 사분면(NE/NW/SE/SW)에 블록을 앵커.
    //    그 블록의 두 근접 링(코너에서 만나는)이 R의 도로와 일치 → 정렬.
    //
    //  시스템 순서: AiCityGrowth → RoadSystem → BuildingPlacement (같은 프레임).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct AiCityGrowthSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<EntranceLookup>();
            state.RequireForUpdate<PrefabMetaLookup>();
            state.RequireForUpdate<CellTypeLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!clock.DayChanged) return;

            var layers         = SystemAPI.GetSingleton<GridLayers>();
            var entranceLookup = SystemAPI.GetSingleton<EntranceLookup>();
            var metaLookup     = SystemAPI.GetSingleton<PrefabMetaLookup>();
            var cellTypeLookup = SystemAPI.GetSingleton<CellTypeLookup>();
            var cfg            = GrowthConfig.Default;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (teamRO, gridRO) in
                     SystemAPI.Query<RefRO<TeamInfoData>, RefRO<CityGrid>>())
            {
                var team = teamRO.ValueRO;
                if (team.IsPlayer()) continue;

                int owner = team.LocalID;
                var grid  = gridRO.ValueRO;

                var rng = Unity.Mathematics.Random.CreateFromIndex(
                    math.hash(new int2(clock.Day + 1, owner + 1)) ^ grid.Seed);
                int chosenKey = PickBuilding(in cfg, ref rng);
                if (chosenKey <= 0) continue;

                bool grew = GrowOneBlock(in layers, in entranceLookup, in metaLookup, in cellTypeLookup,
                    in grid, owner, chosenKey, clock.Day, in cfg, ref rng, ref ecb);

                // ② 건물 크기 폴백 — 선택한 건물이 안 들어가면 다른(보통 더 작은) 건물로 한 번 더.
                //    최악 지형은 큰 블록만 막히는 경우가 많아 작은 블록이면 들어감 → 교착 완화.
                if (!grew)
                {
                    int altKey = chosenKey == cfg.BuildingKeyA ? cfg.BuildingKeyB : cfg.BuildingKeyA;
                    if (altKey > 0 && altKey != chosenKey)
                        GrowOneBlock(in layers, in entranceLookup, in metaLookup, in cellTypeLookup,
                            in grid, owner, altKey, clock.Day, in cfg, ref rng, ref ecb);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static bool GrowOneBlock(
            in GridLayers layers, in EntranceLookup entranceLookup, in PrefabMetaLookup metaLookup,
            in CellTypeLookup cellTypeLookup, in CityGrid grid, int owner, int chosenKey, int day,
            in GrowthConfig cfg, ref Unity.Mathematics.Random rng, ref EntityCommandBuffer ecb)
        {
            if (!metaLookup.TryGetMeta(chosenKey, 0, out var meta)) return false;
            int2 sz = meta.Size;
            if (sz.x <= 0 || sz.y <= 0) return false;

            int Road = math.max(1, grid.Road);
            int K = BlockSizeFor(math.max(sz.x, sz.y));
            if (K == 0)
            { Debug.LogWarning($"[AiCityGrowth] team{owner}: 건물 key={chosenKey} size={sz} 8셀 초과"); return false; }

            bool hasEnt = meta.HasEntrance && entranceLookup.TryGet(chosenKey, out _);
            EntranceInfo ent = default;
            if (hasEnt) entranceLookup.TryGet(chosenKey, out ent);

            if (!TeamRoadBounds(in layers, owner, out int2 rmin, out int2 rmax))
            { Debug.LogWarning($"[AiCityGrowth] team{owner}: 팀 도로 없음"); return false; }
            float2 baseC = new float2((rmin.x + rmax.x) * 0.5f, (rmin.y + rmax.y) * 0.5f);

            // 확장 편향(expansion bias) — 고정 Anchor 기준 축별 확장량. 덜 자란 축을 최우선으로
            //   키워 한쪽 쏠림을 능동 교정한다(오목→오목 끌개 문제도 같이 해소). 이동 centroid가
            //   아니라 고정 Anchor라 양의 피드백에 안 휘말림. |extX-extZ|가 데드밴드 이하이면
            //   균형으로 보고 편향 교정을 끄고 품질(오목/공유)로 채운다.
            int extX = rmax.x - grid.Anchor.x;
            int extZ = rmax.y - grid.Anchor.y;
            int diff = extX - extZ;
            bool balanceActive = math.abs(diff) > cfg.BalanceDeadband;
            bool lagIsX  = diff < 0;              // extX < extZ → X가 덜 자람(키워야 함)
            int  leadExt = lagIsX ? extZ : extX;  // 앞선 축 확장량(여기까지 따라잡으면 균형)

            // 모든 유효 후보를 한 풀에 모아 단일 비교자(Better)로 랭크.
            //   우선순위: ① 편향(밀린 축) ② 카테고리(오목>코너볼록>직선T) ③ 링 share ④ 닿는 변 ⑤ 근접.
            //   오목을 '버킷 하드 우선'에서 카테고리 2차 키로 강등 → 평탄 프런티어도 편향이 요구하면 발전.
            var seen  = new NativeHashSet<int2>(256, Allocator.Temp);
            var cands = new NativeList<Cand>(256, Allocator.Temp);
            int nValid = 0;

            // 앵커는 도로 footprint(roadSize×roadSize) '원점' 기준 — 셀 하나로 잡으면
            // footprint 안에서 셀이 밀린 만큼(최대 roadSize-1칸) 어긋난다(=한 칸 밀림).
            var seenRoad = new NativeHashSet<int2>(256, Allocator.Temp);
            foreach (var kv in layers.RoadLayer)
            {
                if (kv.Value.OwnerLocalId != owner) continue;
                int2 fo = kv.Value.FootprintOrigin;                    // 도로 footprint 원점
                int  rs = kv.Value.Size <= 1 ? 1 : kv.Value.Size;
                if (!seenRoad.Add(fo)) continue;                       // footprint당 1회
                int rmask = RoadFootprintMask(fo, rs, in layers, owner);
                if (!HasEmptyNeighborFootprint(fo, rs, in layers)) continue;   // 프런티어만
                bool straight = IsStraightThrough(rmask);              // 직선 통과
                bool junction = math.countbits(rmask) >= 3;             // 삼거리/사거리 → 볼록 불가, 오목만

                for (int q = 0; q < 4; q++)
                {
                    int2 O = QuadrantOrigin(fo, q, K, Road);
                    if (!seen.Add(O)) continue;
                    if (!BlockValid(in layers, in cellTypeLookup, O, K, Road, owner)) continue;
                    if (cfg.RejectParallelSeam && IsParallelSeam(O, K, Road, owner, in layers)) continue;

                    // 연결성은 앵커 도로 footprint(fo)가 링에 포함되어 보장됨 → 별도 게이트 불필요.
                    int massMask = SideMassMask(O, K, Road, owner, in layers);

                    // 카테고리: 오목(2) > 코너볼록(1) > 직선 T분기(0). 삼거리/사거리는 볼록 불가 → 오목만.
                    int category;
                    if (IsConcave(massMask)) category = 2;
                    else if (junction)       continue;
                    else if (straight)       category = 0;
                    else                     category = 1;

                    nValid++;
                    int reach = lagIsX ? (O.x + K - grid.Anchor.x) : (O.y + K - grid.Anchor.y);
                    cands.Add(new Cand
                    {
                        O     = O,
                        Bal   = balanceActive ? math.min(reach, leadExt) : 0,
                        Cat   = category,
                        Share = RingShareScore(O, K, Road, owner, in layers),
                        Sides = math.countbits(massMask),
                        Dist  = math.abs(O.x + K * 0.5f - baseC.x) + math.abs(O.y + K * 0.5f - baseC.y),
                    });
                }
            }
            seen.Dispose();
            seenRoad.Dispose();

            // 랭크 순으로 시도 — DevelopBlock(건물 입구-도로 정렬 등)이 실패하면 차선 후보로 폴백.
            bool grew = false;
            while (cands.Length > 0)
            {
                int bi = 0;
                for (int i = 1; i < cands.Length; i++)
                    if (Better(cands[i], cands[bi])) bi = i;
                int2 pick = cands[bi].O;
                cands.RemoveAtSwapBack(bi);
                if (DevelopBlock(in layers, in grid, pick, K, Road, owner, chosenKey,
                        sz, hasEnt, in ent, meta.BuildableOn, in cellTypeLookup, ref ecb))
                { grew = true; break; }
            }
            cands.Dispose();
            if (grew) return true;

            Debug.Log($"[AiCityGrowth] team{owner} day{day}: 성장 자리 없음 K={K} (유효후보={nValid})");
            return false;
        }

        // 후보 — 한 풀에서 단일 비교자로 랭크.
        struct Cand
        {
            public int2  O;
            public int   Bal, Cat, Share, Sides;
            public float Dist;
        }

        // 후보 비교(우선순위): ① 편향(밀린 축 확장) ② 카테고리(오목>코너볼록>직선T)
        //   ③ 링 share(기존 도로 재사용=정렬) ④ 닿는 변(조밀) ⑤ 베이스 근접.
        //   앞 항목이 클수록(거리는 작을수록) 우수.
        static bool Better(in Cand a, in Cand b)
            => a.Bal   != b.Bal   ? a.Bal   > b.Bal
             : a.Cat   != b.Cat   ? a.Cat   > b.Cat
             : a.Share != b.Share ? a.Share > b.Share
             : a.Sides != b.Sides ? a.Sides > b.Sides
             : a.Dist  < b.Dist;

        static int BlockSizeFor(int maxDim) => maxDim <= 4 ? 4 : maxDim <= 6 ? 6 : maxDim <= 8 ? 8 : 0;

        // 도로 footprint 원점 fo를 코너로, 사분면 q에 K×K 블록(내부 원점).
        //   두 근접 링(roadSize=Road 폭)이 fo의 도로 footprint와 정확히 일치 → 밀림 없음.
        //   q: 0=NE,1=NW,2=SE,3=SW. (fo = 블록 쪽 코너의 도로 footprint 원점)
        static int2 QuadrantOrigin(int2 fo, int q, int K, int Road) => q switch
        {
            0 => new int2(fo.x + Road, fo.y + Road),   // NE: 서·남 링이 fo 열·행 도로와 일치
            1 => new int2(fo.x - K,    fo.y + Road),   // NW: 동·남 링
            2 => new int2(fo.x + Road, fo.y - K),      // SE: 서·북 링
            _ => new int2(fo.x - K,    fo.y - K),      // SW: 동·북 링
        };

        // 도로 footprint의 4변 바깥에 빈 셀이 하나라도 있나(프런티어 판정).
        static bool HasEmptyNeighborFootprint(int2 fo, int rs, in GridLayers layers)
        {
            for (int i = 0; i < rs; i++)
            {
                if (CellBuildable(new int2(fo.x + i, fo.y + rs), in layers)) return true;  // N
                if (CellBuildable(new int2(fo.x + rs, fo.y + i), in layers)) return true;  // E
                if (CellBuildable(new int2(fo.x + i, fo.y - 1),  in layers)) return true;  // S
                if (CellBuildable(new int2(fo.x - 1,  fo.y + i), in layers)) return true;  // W
            }
            return false;
        }

        // 도로 footprint 매크로 4-이웃 비트마스크 (bit0=N,1=E,2=S,3=W).
        //   footprint 변 너머에 같은 팀 도로가 있으면 그 방향 비트 set.
        static int RoadFootprintMask(int2 fo, int rs, in GridLayers layers, int owner)
        {
            int m = 0;
            for (int i = 0; i < rs; i++)
            {
                if (IsTeamRoad(new int2(fo.x + i, fo.y + rs), in layers, owner)) m |= 1; // N
                if (IsTeamRoad(new int2(fo.x + rs, fo.y + i), in layers, owner)) m |= 2; // E
                if (IsTeamRoad(new int2(fo.x + i, fo.y - 1),  in layers, owner)) m |= 4; // S
                if (IsTeamRoad(new int2(fo.x - 1,  fo.y + i), in layers, owner)) m |= 8; // W
            }
            return m;
        }

        // 직선 통과(마주보는 2연결)인가 — 특이점 아님.
        static bool IsStraightThrough(int rmask)
            => math.countbits(rmask) == 2 && (rmask == 0b0101 || rmask == 0b1010);


        // 오목 = 도시에 닿는 변이 2개 이상(노치/통로/크룩). 1개 이하 = 볼록 외곽 확장.
        //   변 판정은 코너 돌출을 뺀 '내부 폭'만 스캔하므로(SideMassMask), 코너만 살짝
        //   닿는 볼록 확장이 오목으로 오판되지 않는다.
        static bool IsConcave(int massMask) => math.countbits(massMask) >= 2;

        // ── 블록 개발: 도로 링 + 건물 1개 ────────────────────────────────────
        static bool DevelopBlock(
            in GridLayers layers, in CityGrid grid, int2 O, int K, int Road, int owner, int chosenKey,
            int2 sz, bool hasEnt, in EntranceInfo ent, TerrainMask buildableOn,
            in CellTypeLookup cellTypeLookup, ref EntityCommandBuffer ecb)
        {
            // Road==1(기본 DefaultSize)은 그린-방향(연속 루프) 발행 → 겹치는 모든
            //   셀이 OR 병합돼 교차로가 아닌 이음매도 연결(유저 드래그와 같은 모델).
            //   Road>1(멀티셀)은 명시 분기가 1×1만 다루므로 기존 축 모델로 폴백.
            bool useDrawn = Road == 1;

            var roadOrigins = new NativeList<int2>(64, Allocator.Temp);
            var roadDirs    = new NativeList<RoadDir>(64, Allocator.Temp);          // 그린 모델
            var roadAxis    = new NativeList<RoadPlacedAxis>(64, Allocator.Temp);   // 축 폴백
            var plannedRoad = new NativeHashSet<int2>(128, Allocator.Temp);

            if (useDrawn)
                CollectRingRoadsDrawn(O, K, ref roadOrigins, ref roadDirs, ref plannedRoad);
            else
                CollectRingRoads(in layers, O, K, Road, ref roadOrigins, ref roadAxis, ref plannedRoad);

            bool placed = TryPlaceBuildingInSpan(in layers, in cellTypeLookup, O, K,
                sz, hasEnt, in ent, buildableOn, in plannedRoad, out int2 bOrigin, out float bRot);

            if (placed)
            {
                for (int i = 0; i < roadOrigins.Length; i++)
                {
                    var e = ecb.CreateEntity();
                    ecb.AddComponent(e, new PlaceRoadCommand
                    {
                        Cell = roadOrigins[i], OwnerLocalId = owner, LaneCount = 2,
                        FactionId = grid.FactionId, Size = (byte)Road,
                        Axis       = useDrawn ? RoadPlacedAxis.Any : roadAxis[i],
                        Directions = useDrawn ? roadDirs[i] : RoadDir.None,
                    });
                }
                var be = ecb.CreateEntity();
                ecb.AddComponent(be, new PlaceBuildingRequest
                {
                    MainKey = chosenKey, VariantKey = 0, Cell = bOrigin, RotationY = bRot,
                    OwnerLocalId = owner, FactionId = grid.FactionId, RequireRoadAccess = true,
                });
            }

            roadOrigins.Dispose(); roadDirs.Dispose(); roadAxis.Dispose(); plannedRoad.Dispose();
            return placed;
        }

        // 블록 도로 링을 '그린-방향 연속 루프'로 수집 (Road==1 전용).
        //   각 링 셀의 Directions = 같은 링의 4-이웃을 향한 비트(코너=수직 2비트, 변=직선 2비트).
        //   링 전체(새 셀 + 기존 공유 셀)를 모두 발행 → RoadSystem이 새 셀 set / 기존 OR.
        //     · 모든 링 셀이 루프 이웃을 향한 비트를 가져 상호 비트 불변식 성립(완전 연결 루프).
        //     · 기존 팀 도로(이웃 블록·베이스)와 겹치는 셀은 OR 병합 → 교차로 아닌 곳도 연결.
        static void CollectRingRoadsDrawn(
            int2 O, int K,
            ref NativeList<int2> outOrigins, ref NativeList<RoadDir> outDirs,
            ref NativeHashSet<int2> outCells)
        {
            int xa = O.x - 1, xb = O.x + K, za = O.y - 1, zb = O.y + K;

            var ring = new NativeHashSet<int2>(128, Allocator.Temp);
            for (int z = za; z <= zb; z++)
            for (int x = xa; x <= xb; x++)
            {
                bool onCol = x == xa || x == xb;
                bool onRow = z == za || z == zb;
                if (!onCol && !onRow) continue;   // 내부(건물 영역) 스킵
                var c = new int2(x, z);
                ring.Add(c);
                outCells.Add(c);
            }

            foreach (var c in ring)
            {
                RoadDir bits = RoadDir.None;
                if (ring.Contains(c + new int2(0,  1))) bits |= RoadDir.N;
                if (ring.Contains(c + new int2(1,  0))) bits |= RoadDir.E;
                if (ring.Contains(c + new int2(0, -1))) bits |= RoadDir.S;
                if (ring.Contains(c + new int2(-1, 0))) bits |= RoadDir.W;
                outOrigins.Add(c);
                outDirs.Add(bits);
            }
            ring.Dispose();
        }

        // 블록 도로 링 수집 + 셀별 배치 축 결정.
        //   변별 축: 위/아래 행 = EW, 좌/우 열 = NS, 네 코너 = Any(양축 회전 허용).
        //   → 평행하게 1칸 옆에 깔린 도로(예: seam)는 축이 안 맞아 자동 연결되지 않는다
        //     (사거리 떡칠 방지). 실제로 변이 공유되면 같은 셀이라 정상 연결.
        static void CollectRingRoads(
            in GridLayers layers, int2 O, int K, int Road,
            ref NativeList<int2> outOrigins, ref NativeList<RoadPlacedAxis> outAxis,
            ref NativeHashSet<int2> outCells)
        {
            int xa = O.x - Road, xb = O.x + K, za = O.y - Road, zb = O.y + K;
            for (int z = za; z <= zb; z += Road)
            for (int x = xa; x <= xb; x += Road)
            {
                bool onCol = x == xa || x == xb;   // 좌/우 열 (NS)
                bool onRow = z == za || z == zb;   // 위/아래 행 (EW)
                if (!onCol && !onRow) continue;    // 내부(건물 영역) 스킵
                RoadPlacedAxis axis = onCol && onRow ? RoadPlacedAxis.Any   // 코너
                                    : onCol          ? RoadPlacedAxis.NS
                                                     : RoadPlacedAxis.EW;
                AddRoadFootprint(new int2(x, z), axis, Road, in layers,
                    ref outOrigins, ref outAxis, ref outCells);
            }
        }

        static void AddRoadFootprint(
            int2 origin, RoadPlacedAxis axis, int Road, in GridLayers layers,
            ref NativeList<int2> outOrigins, ref NativeList<RoadPlacedAxis> outAxis,
            ref NativeHashSet<int2> outCells)
        {
            bool alreadyRoad = true;
            for (int dx = 0; dx < Road; dx++)
            for (int dz = 0; dz < Road; dz++)
            {
                int2 c = origin + new int2(dx, dz);
                outCells.Add(c);
                if (!layers.RoadLayer.ContainsKey(c)) alreadyRoad = false;
            }
            if (!alreadyRoad && !layers.RoadLayer.ContainsKey(origin))
            { outOrigins.Add(origin); outAxis.Add(axis); }
        }

        static bool TryPlaceBuildingInSpan(
            in GridLayers layers, in CellTypeLookup cellTypeLookup, int2 spanOrigin, int spanSize,
            int2 sz, bool hasEnt, in EntranceInfo ent, TerrainMask buildableOn,
            in NativeHashSet<int2> plannedRoad, out int2 bestOrigin, out float bestRot)
        {
            bestOrigin = default; bestRot = 0f;
            int stepCount = hasEnt ? 4 : 1;
            for (int cz = 0; cz < spanSize; cz++)
            for (int cx = 0; cx < spanSize; cx++)
            {
                int2 origin = spanOrigin + new int2(cx, cz);
                for (int steps = 0; steps < stepCount; steps++)
                {
                    int2 eff = EntranceOps.RotateSize(sz, steps);
                    if (cx + eff.x > spanSize || cz + eff.y > spanSize) continue;
                    if (!FootprintBuildableFlat(origin, eff, buildableOn, in layers, in cellTypeLookup)) continue;
                    if (hasEnt)
                    {
                        int2 erc = EntranceOps.EntranceRoadCell(origin, sz, in ent, steps);
                        if (!layers.RoadLayer.ContainsKey(erc) && !plannedRoad.Contains(erc)) continue;
                    }
                    bestOrigin = origin;
                    bestRot = EntranceOps.StepsToRotationY(steps);
                    return true;
                }
            }
            return false;
        }

        // ── 유효성 / 모서리 판정 ─────────────────────────────────────────────

        // footprint(내부 K + 도로 링 Road) 전체 유효? 맵 안·같은 높이·Land·내부 빈·링 빈/팀도로.
        static bool BlockValid(
            in GridLayers layers, in CellTypeLookup cellTypeLookup, int2 O, int K, int Road, int owner)
        {
            int2 ro = O - new int2(Road, Road);
            int  rs = K + 2 * Road;
            bool first = true; byte baseH = 0;
            for (int dz = 0; dz < rs; dz++)
            for (int dx = 0; dx < rs; dx++)
            {
                int2 c = ro + new int2(dx, dz);
                if (!layers.TerrainLayer.TryGetValue(c, out var tc)) return false;        // 맵 밖
                if (cellTypeLookup.TryGet(tc.TypeId, out var ti)
                    && ti.TerrainCategory == TerrainCategory.Water) return false;          // 물
                if (first) { baseH = tc.Height; first = false; }
                else if (tc.Height != baseH) return false;                                 // 단차

                bool inInterior = dx >= Road && dx < Road + K && dz >= Road && dz < Road + K;
                if (inInterior)
                {
                    if (!CellBuildable(c, in layers)) return false;
                }
                else
                {
                    if (!CellBuildable(c, in layers) && !IsTeamRoad(c, in layers, owner)) return false;
                }
            }
            return true;
        }

        // 블록 4변이 도시(팀 도로/건물)에 닿는가 — bit0=N,1=E,2=S,3=W.
        //   ⚠ 코너 돌출을 빼고 '내부 폭 K'만 스캔(변 바로 바깥 라인). 그래야 코너에서만
        //   살짝 닿는 볼록 확장이 한 변 전체로 오판되지 않는다(오목/볼록 정확 구분).
        static int SideMassMask(int2 O, int K, int Road, int owner, in GridLayers layers)
        {
            int mask = 0;
            if (LineMass(new int2(O.x, O.y + K + Road), new int2(1, 0), K, owner, in layers)) mask |= 1; // N
            if (LineMass(new int2(O.x + K + Road, O.y), new int2(0, 1), K, owner, in layers)) mask |= 2; // E
            if (LineMass(new int2(O.x, O.y - Road - 1), new int2(1, 0), K, owner, in layers)) mask |= 4; // S
            if (LineMass(new int2(O.x - Road - 1, O.y), new int2(0, 1), K, owner, in layers)) mask |= 8; // W
            return mask;
        }

        // 블록 도로 링 중 이미 '팀 도로'인 셀 수.
        //   많을수록 기존 도로를 재사용 = 어긋남 없이 맞물림(삼거리/사거리).
        //   적으면 새 도로가 기존 도로 옆에 평행하게 깔려 '밀림'으로 보임 → 비선호.
        static int RingShareScore(int2 O, int K, int Road, int owner, in GridLayers layers)
        {
            int2 ro = O - new int2(Road, Road);
            int  rs = K + 2 * Road;
            int  share = 0;
            for (int dz = 0; dz < rs; dz++)
            for (int dx = 0; dx < rs; dx++)
            {
                bool interior = dx >= Road && dx < Road + K && dz >= Road && dz < Road + K;
                if (interior) continue;                         // 링(둘레)만
                if (IsTeamRoad(ro + new int2(dx, dz), in layers, owner)) share++;
            }
            return share;
        }

        // 평행 seam: 블록의 '새' 도로 변 바로 바깥에 같은 팀 도로가 평행하게 붙어 있으면 true
        //   (= 공유 없이 한 칸 어긋나 나란히 깔리는 도로). → 그런 후보를 거부.
        //   변이 이미 기존 도로(공유)면 '새 변'이 아니므로 제외 → 정상 삼거리/사거리는 통과.
        //   바깥이 빈 땅인 프런티어 확장도 통과(편향 교정과 양립). 코너 오판 방지로 내부 폭 K만 스캔.
        static bool IsParallelSeam(int2 O, int K, int Road, int owner, in GridLayers layers)
        {
            for (int i = 0; i < K; i++)
            {
                // S: 새 도로 최외곽 행 z=O.y-Road, 그 바로 바깥 z=O.y-Road-1
                if (IsTeamRoad(new int2(O.x + i, O.y - Road - 1), in layers, owner)
                    && !IsTeamRoad(new int2(O.x + i, O.y - Road), in layers, owner)) return true;
                // N
                if (IsTeamRoad(new int2(O.x + i, O.y + K + Road), in layers, owner)
                    && !IsTeamRoad(new int2(O.x + i, O.y + K + Road - 1), in layers, owner)) return true;
                // W
                if (IsTeamRoad(new int2(O.x - Road - 1, O.y + i), in layers, owner)
                    && !IsTeamRoad(new int2(O.x - Road, O.y + i), in layers, owner)) return true;
                // E
                if (IsTeamRoad(new int2(O.x + K + Road, O.y + i), in layers, owner)
                    && !IsTeamRoad(new int2(O.x + K + Road - 1, O.y + i), in layers, owner)) return true;
            }
            return false;
        }

        static bool LineMass(int2 start, int2 step, int count, int owner, in GridLayers layers)
        {
            for (int i = 0; i < count; i++)
                if (IsMass(start + step * i, in layers, owner)) return true;
            return false;
        }

        static bool IsMass(int2 c, in GridLayers layers, int owner)
            => IsTeamRoad(c, in layers, owner) || IsTeamBuilding(c, in layers, owner);

        static bool TeamRoadBounds(in GridLayers layers, int owner, out int2 mn, out int2 mx)
        {
            mn = new int2(int.MaxValue, int.MaxValue); mx = new int2(int.MinValue, int.MinValue);
            bool any = false;
            foreach (var kv in layers.RoadLayer)
            {
                if (kv.Value.OwnerLocalId != owner) continue;
                mn = math.min(mn, kv.Key); mx = math.max(mx, kv.Key); any = true;
            }
            return any;
        }

        // ── 공용 셀 판정 ─────────────────────────────────────────────────────

        static bool IsTeamRoad(int2 c, in GridLayers layers, int owner)
            => layers.RoadLayer.TryGetValue(c, out var rc) && rc.OwnerLocalId == owner;

        static bool IsTeamBuilding(int2 c, in GridLayers layers, int owner)
            => layers.OccupancyLayer.TryGetValue(c, out var occ)
            && occ.Type == OccupantType.Building && occ.OwnerLocalId == owner;

        static bool CellBuildable(int2 c, in GridLayers layers)
        {
            if (!layers.TerrainLayer.ContainsKey(c)) return false;
            if (layers.RoadLayer.ContainsKey(c))     return false;
            if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                && occ.Type != OccupantType.Environment) return false;
            if (layers.ResourceLayer.IsCreated
                && layers.ResourceLayer.TryGetValue(c, out var res) && res.Amount > 0) return false;
            return true;
        }

        static bool FootprintBuildableFlat(
            int2 origin, int2 sz, TerrainMask buildableOn,
            in GridLayers layers, in CellTypeLookup cellTypeLookup)
        {
            bool first = true; byte baseH = 0;
            for (int dx = 0; dx < sz.x; dx++)
            for (int dz = 0; dz < sz.y; dz++)
            {
                int2 c = origin + new int2(dx, dz);
                if (!layers.TerrainLayer.TryGetValue(c, out var tc)) return false;
                if (!CellBuildable(c, in layers)) return false;
                if (cellTypeLookup.TryGet(tc.TypeId, out var ti))
                {
                    var mask = ti.TerrainCategory == TerrainCategory.Water ? TerrainMask.Water : TerrainMask.Land;
                    if ((buildableOn & mask) == 0) return false;
                }
                if (first) { baseH = tc.Height; first = false; }
                else if (tc.Height != baseH) return false;
            }
            return true;
        }

        static int PickBuilding(in GrowthConfig cfg, ref Unity.Mathematics.Random rng)
        {
            bool aOk = cfg.BuildingKeyA > 0;
            bool bOk = cfg.BuildingKeyB > 0;
            if (aOk && bOk) return rng.NextBool() ? cfg.BuildingKeyA : cfg.BuildingKeyB;
            if (aOk) return cfg.BuildingKeyA;
            if (bOk) return cfg.BuildingKeyB;
            return 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GrowthConfig — 성장 건물 키 (블록 = 건물 크기 {4,6,8}, 모서리 앵커로 정렬)
    // ══════════════════════════════════════════════════════════════════════════
    public struct GrowthConfig
    {
        public int BuildingKeyA;
        public int BuildingKeyB;

        // 확장 편향 데드밴드(셀): Anchor 기준 |extX-extZ|가 이 값 이하이면 균형으로 보고
        //   편향 교정을 끄고 품질(오목/공유)로 채운다. 블록 한 변(~4-8)보다 커야 좌우 떨림 방지.
        public int BalanceDeadband;

        // 평행 seam 거부: 기존 도로와 공유 없이 한 칸 평행으로 깔리는 블록을 차단.
        public bool RejectParallelSeam;

        public static GrowthConfig Default => new GrowthConfig
        {
            BuildingKeyA       = 1004,
            BuildingKeyB       = 1005,
            BalanceDeadband    = 8,
            RejectParallelSeam = true,
        };
    }
}
