using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TerritorySystem — 인구 기반 영역(reach) + 팀 영향력 경합 (파괴 없음·표시 전용)
    // ──────────────────────────────────────────────────────────────────────────
    //  ~1초마다 전체 재계산. 두 개념 분리:
    //
    //   [영역 reach] 물리 범위 = 거주건물 인구. 셀 수 = floor(인구/PopPerCell),
    //     소유자별 예산만큼 거주지 최근접 셀(nearest-N). 각 셀을 그 플레이어의 '팀'으로 태깅.
    //     → 한 셀에 여러 팀이 닿으면 경합. 같은 팀끼리는 경합 아님(동맹은 영역 공유).
    //
    //   [영향력 influence] 경합 해소용 '힘' = 플레이어별 스칼라(입력). 같은 팀은 합산(동맹 연합).
    //     경합 구역(연결요소, T칸)에서: 승자팀=영향력 1등, 2등팀과의 차로 차지 칸수
    //       K = floor(T × (승자 − 2등) / 승자)  (동률이면 K=0 → 전부 중립).
    //     K칸은 승자팀 거주지에 가까운 순으로 차지(연속·결정적), 나머지는 중립.
    //     ※ "많은 팀 경합 시 이득↓"은 2등이 세질수록 K↓로 자연 반영(연합 가정 없음).
    //
    //   결과 → GridLayers.TerritoryLayer(int2 → 팀 id, 없으면 중립).
    //   ※ capture(파괴) 없음. 영향력 스칼라는 추후 행복도/팩션으로 대체(여기선 입력 placeholder).
    //   ⚠ TerritoryLayer가 '팀 id'라 빌드 게이트는 team=localId 기본에서만 정확(동맹 게이트 후속).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TerritorySystem : ISystem
    {
        const int MP = StampLayers.MaxPlayers;   // 8

        double _nextRecompute;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            _nextRecompute = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextRecompute) return;
            _nextRecompute = now + 1.0;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerritoryLayer.IsCreated) return;

            float popPerCell = TerritoryConfig.Default.PopPerCell;
            int   maxRadius  = TerritoryConfig.Default.MaxRadius;
            if (SystemAPI.TryGetSingleton<TerritoryConfig>(out var cfg))
            {
                popPerCell = cfg.PopPerCell > 0f ? cfg.PopPerCell : TerritoryConfig.Default.PopPerCell;
                maxRadius  = math.max(1, cfg.MaxRadius);
            }

            // ── 플레이어 영향력/팀 입력 (없으면 team=localId, influence=1) ──────
            var pInf  = new NativeArray<float>(MP, Allocator.Temp);
            var pTeam = new NativeArray<int>(MP, Allocator.Temp);
            for (int i = 0; i < MP; i++) { pInf[i] = 1f; pTeam[i] = i; }
            if (SystemAPI.TryGetSingletonEntity<PlayerInfluenceConfig>(out var cfgE)
                && state.EntityManager.HasBuffer<PlayerInfluenceElement>(cfgE))
            {
                var buf = state.EntityManager.GetBuffer<PlayerInfluenceElement>(cfgE);
                for (int i = 0; i < MP && i < buf.Length; i++)
                {
                    pInf[i]  = buf[i].Influence;
                    int t = buf[i].Team;
                    pTeam[i] = (uint)t < MP ? t : i;
                }
            }

            // ── 거주건물 수집 ──────────────────────────────────────────────
            var residences = new NativeList<ResInfo>(64, Allocator.Temp);
            foreach (var (occ, bf) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                              .WithAll<ResidenceBuilding>())
            {
                int owner = bf.ValueRO.OwnerLocalId;
                if ((uint)owner >= MP) continue;
                int pop = occ.ValueRO.Current > 0 ? occ.ValueRO.Current : occ.ValueRO.Capacity;
                if (pop <= 0) continue;
                int cells = (int)math.floor(pop / popPerCell);
                if (cells <= 0) continue;

                int2 eff    = EntranceOps.RotateSize(bf.ValueRO.Size, bf.ValueRO.RotSteps);
                int2 center = bf.ValueRO.Origin + eff / 2;
                residences.Add(new ResInfo { Owner = owner, Center = center, Cells = cells });
            }

            // ── 팀 영향력 합 (멤버 플레이어 1회씩) ───────────────────────────
            var teamInf   = new NativeArray<float>(MP, Allocator.Temp);
            var ownerSeen = new NativeHashSet<int>(MP, Allocator.Temp);
            for (int i = 0; i < residences.Length; i++)
            {
                int o = residences[i].Owner;
                if (ownerSeen.Add(o)) teamInf[pTeam[o]] += pInf[o];
            }
            ownerSeen.Dispose();

            // ── reach: 소유자별 nearest-N → 셀별 '팀 비트마스크' ──────────────
            var reach = new NativeHashMap<int2, int>(2048, Allocator.Temp);
            var cmp   = new ClaimComparer();
            for (int owner = 0; owner < MP; owner++)
            {
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

                var cand = new NativeList<Claim>(256, Allocator.Temp);
                for (int z = bbMin.y - margin; z <= bbMax.y + margin; z++)
                for (int x = bbMin.x - margin; x <= bbMax.x + margin; x++)
                {
                    int2 cell = new int2(x, z);
                    if (!layers.TerrainLayer.ContainsKey(cell)) continue;
                    float md = float.MaxValue;
                    for (int i = 0; i < residences.Length; i++)
                    {
                        if (residences[i].Owner != owner) continue;
                        float d = math.distance((float2)cell, (float2)residences[i].Center);
                        if (d < md) md = d;
                    }
                    if (md < float.MaxValue) cand.Add(new Claim { Dist = md, Cell = cell });
                }

                var arr = cand.AsArray();
                arr.Sort(cmp);
                int take = math.min(budget, arr.Length);
                int bit  = 1 << pTeam[owner];
                for (int k = 0; k < take; k++)
                {
                    int2 cell = arr[k].Cell;
                    reach.TryGetValue(cell, out int m);
                    reach[cell] = m | bit;
                }
                cand.Dispose();
            }

            // ── 해소: 단독 팀 셀 = 소유, 경합 셀(2팀+) = 모아서 구역 비례 배분 ──
            layers.TerritoryLayer.Clear();
            var contested = new NativeHashSet<int2>(512, Allocator.Temp);
            foreach (var kv in reach)
            {
                int m = kv.Value;
                if (math.countbits(m) == 1) layers.TerritoryLayer[kv.Key] = math.tzcnt(m);
                else                        contested.Add(kv.Key);
            }

            AllocateContested(in contested, in reach, in residences, in pTeam, in teamInf, cmp, ref layers);

            pInf.Dispose(); pTeam.Dispose(); teamInf.Dispose();
            residences.Dispose(); reach.Dispose(); contested.Dispose();
        }

        // 경합 구역(연결요소)별: 승자팀 K = floor(T×(승자−2등)/승자) 칸을 승자 거주지 가까운 순으로.
        static void AllocateContested(
            in NativeHashSet<int2> contested, in NativeHashMap<int2, int> reach,
            in NativeList<ResInfo> residences, in NativeArray<int> pTeam, in NativeArray<float> teamInf,
            ClaimComparer cmp, ref GridLayers layers)
        {
            if (contested.IsEmpty) return;

            var visited = new NativeHashSet<int2>(512, Allocator.Temp);
            var q       = new NativeQueue<int2>(Allocator.Temp);
            var comp    = new NativeList<int2>(128, Allocator.Temp);

            foreach (var seed in contested)
            {
                if (!visited.Add(seed)) continue;

                // 연결요소 수집 + 등장 팀 union
                comp.Clear(); q.Clear(); q.Enqueue(seed);
                int teamsMask = 0;
                while (q.TryDequeue(out int2 cur))
                {
                    comp.Add(cur);
                    if (reach.TryGetValue(cur, out int m)) teamsMask |= m;
                    for (int d = 0; d < 4; d++)
                    {
                        int2 nb = cur + RoadDirOps.Offsets[d];
                        if (contested.Contains(nb) && visited.Add(nb)) q.Enqueue(nb);
                    }
                }

                // 구역 전체를 '경합지(-2, 잠김)'로 먼저 마킹 — 승자 K칸만 이후 덮어씀.
                //   → 미배분 경합 칸은 중립(열림)이 아니라 경합지(누구도 불가)로 남는다.
                for (int i = 0; i < comp.Length; i++)
                    layers.TerritoryLayer[comp[i]] = TerritoryOps.Contested;

                // 승자/2등 팀 (영향력)
                int win = -1, second = -1;
                for (int t = 0; t < MP; t++)
                {
                    if ((teamsMask & (1 << t)) == 0) continue;
                    if (win < 0 || teamInf[t] > teamInf[win]) { second = win; win = t; }
                    else if (second < 0 || teamInf[t] > teamInf[second]) second = t;
                }
                if (win < 0) continue;

                // ★승자는 '자기가 닿은(reach)' 경합 칸만 차지한다 — 안 닿은 칸(다른 팀만 닿음)을
                //   가져가면 reach(인구) 초과 + 남의 땅 침탈. 후보 = 승자 비트가 있는 comp 칸.
                int winBit = 1 << win;
                var rank = new NativeList<Claim>(comp.Length, Allocator.Temp);
                for (int i = 0; i < comp.Length; i++)
                {
                    int2 c = comp[i];
                    if (!reach.TryGetValue(c, out int rm) || (rm & winBit) == 0) continue;  // 승자 미도달 칸 제외
                    float md = float.MaxValue;
                    for (int r = 0; r < residences.Length; r++)
                    {
                        if (pTeam[residences[r].Owner] != win) continue;
                        float d = math.distance((float2)c, (float2)residences[r].Center);
                        if (d < md) md = d;
                    }
                    rank.Add(new Claim { Dist = md, Cell = c });
                }

                float wi = teamInf[win];
                float si = second >= 0 ? teamInf[second] : 0f;
                int   Tw = rank.Length;                       // 승자가 닿은 경합 칸 수
                int   K  = wi > 0f ? (int)math.floor(Tw * (wi - si) / wi) : 0;
                if (K > 0)
                {
                    var ra = rank.AsArray();
                    ra.Sort(cmp);
                    int kk = math.min(K, ra.Length);
                    for (int i = 0; i < kk; i++) layers.TerritoryLayer[ra[i].Cell] = win;
                }
                rank.Dispose();
            }

            comp.Dispose(); q.Dispose(); visited.Dispose();
        }

        struct ResInfo { public int Owner; public int2 Center; public int Cells; }
        struct Claim   { public float Dist; public int2 Cell; }

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
