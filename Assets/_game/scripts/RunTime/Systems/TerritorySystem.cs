using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
    //   빌드 게이트는 TeamTable(LocalId→팀, TeamTableSystem이 유지)로 셀의 '팀 id'를 풀어
    //   '셀 팀 ≠ 내 팀'을 비교 → 동맹(team≠localId)·내 영역에서도 정확(TerritoryOps).
    //
    //  실행 모델(2026-07-04 잡화 — 메인스레드 스파이크 해소, 더블 버퍼):
    //    1초 게이트 →
    //      ① 입력 수집(메인, 소량): 거주지·영향력·config. 지형 키 캐시는 최초/맵 변경 시만
    //         TerrainKeysJob으로 재빌드(state.Dependency 등록 — 라이브 레이어를 읽는 유일 잡).
    //      ② ComputeJob(느림): 체임퍼 DT + 버킷 선별 + 경합 해소 전부 — **백 버퍼**에만 쓰는
    //         Burst 잡. 핸들은 state.Dependency 밖(프라이빗) → 여러 프레임에 걸쳐 워커에서.
    //      ③ 폴링: IsCompleted 후 메인에서 **핸들 스왑 O(1)**(백↔프런트) + TerritoryVersion +1.
    //    다독자 계약: 프런트(TerritoryLayer)는 스왑 사이 불변 — 독자는 매 업데이트 싱글톤에서
    //    새로 받아 읽기만. 스왑 직전 GetSingletonRW(쓰기 선언)로 등록된 독자 잡과 순서 강제.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct TerritorySystem : ISystem
    {
        const int MP = StampLayers.MaxPlayers;   // 8

        double    _nextRecompute;
        JobHandle _handle;         // ComputeJob — state.Dependency 밖에서 폴링
        bool      _running;

        NativeHashMap<int2, int> _back;         // 백 버퍼(스왑 상대, Persistent — 상시 유지)
        NativeHashSet<int2>      _terrainKeys;  // 지형 키 캐시(맵 불변 — Count 변화 시만 재빌드)
        int                      _terrainKeyCount;

        // 런별 입력(Persistent — 잡 수명 동안 유지, 완료 후 해제)
        NativeList<ResInfo> _residences;
        NativeArray<float>  _pInf;
        NativeArray<int>    _pTeam;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            _nextRecompute   = 0;
            _terrainKeyCount = -1;
            _back = new NativeHashMap<int2, int>(2048, Allocator.Persistent);

            // 렌더 캐시 무효화용 버전 싱글톤(재계산마다 +1).
            if (!SystemAPI.HasSingleton<TerritoryVersion>())
                state.EntityManager.CreateEntity(typeof(TerritoryVersion));
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_running)
            {
                _handle.Complete();
                DisposeRun();
                _running = false;
            }
            if (_back.IsCreated) _back.Dispose();
            if (_terrainKeys.IsCreated) _terrainKeys.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 완료 폴링: 끝났으면 스왑(O(1)) + 버전 +1, 아니면 다음 프레임 ──────
            if (_running)
            {
                if (!_handle.IsCompleted) return;
                _handle.Complete();   // IsCompleted 후라 논블로킹

                // 스왑 직전 쓰기 선언 — TerritoryLayer를 읽는 등록된 잡이 있으면 먼저 완료됨.
                //   스왑 후 옛 프런트는 프라이빗 백 버퍼가 되므로 다음 계산이 안전하게 덮어씀.
                ref var gl = ref SystemAPI.GetSingletonRW<GridLayers>().ValueRW;
                (gl.TerritoryLayer, _back) = (_back, gl.TerritoryLayer);

                if (SystemAPI.TryGetSingleton<TerritoryVersion>(out var ver))
                    SystemAPI.SetSingleton(new TerritoryVersion { Value = ver.Value + 1 });

                DisposeRun();
                _running = false;
            }

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
            _pInf  = new NativeArray<float>(MP, Allocator.Persistent);
            _pTeam = new NativeArray<int>(MP, Allocator.Persistent);
            for (int i = 0; i < MP; i++) { _pInf[i] = 1f; _pTeam[i] = i; }
            if (SystemAPI.TryGetSingletonEntity<PlayerInfluenceConfig>(out var cfgE)
                && state.EntityManager.HasBuffer<PlayerInfluenceElement>(cfgE))
            {
                var buf = state.EntityManager.GetBuffer<PlayerInfluenceElement>(cfgE);
                for (int i = 0; i < MP && i < buf.Length; i++)
                {
                    _pInf[i]  = buf[i].Influence;
                    int t = buf[i].Team;
                    _pTeam[i] = (uint)t < MP ? t : i;
                }
            }

            // ── 거주건물 수집 (메인 — 수백 개, 저비용) ─────────────────────────
            _residences = new NativeList<ResInfo>(64, Allocator.Persistent);
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
                _residences.Add(new ResInfo { Owner = owner, Center = center, Cells = cells });
            }

            // ── 지형 키 캐시 — 맵 로드 후 불변이라 최초/변경 시 1회만 재빌드 ──────
            //   (라이브 TerrainLayer를 읽는 잡은 이것뿐 — state.Dependency 등록 +
            //    GridLayers 쓰기 선언으로 이후 접근자와 순서 강제. 빠른 잡이라 대기 ~0.)
            JobHandle dep = state.Dependency;
            int tc = layers.TerrainLayer.Count;
            if (tc != _terrainKeyCount)
            {
                if (_terrainKeys.IsCreated) _terrainKeys.Dispose();
                _terrainKeys = new NativeHashSet<int2>(math.max(16, tc), Allocator.Persistent);
                var layersDecl = SystemAPI.GetSingletonRW<GridLayers>().ValueRO;   // 쓰기 선언
                dep = new TerrainKeysJob
                {
                    Src = layersDecl.TerrainLayer,
                    Dst = _terrainKeys,
                }.Schedule(dep);
                state.Dependency = dep;
                _terrainKeyCount = tc;
            }

            // ── 본 계산 잡 — 백 버퍼에만 쓰므로 체인 밖(폴링) ────────────────────
            _handle = new ComputeJob
            {
                TerrainKeys = _terrainKeys,
                Residences  = _residences,
                PInf        = _pInf,
                PTeam       = _pTeam,
                MaxRadius   = maxRadius,
                Back        = _back,
            }.Schedule(dep);
            _running = true;
        }

        void DisposeRun()
        {
            _residences.Dispose();
            _pInf.Dispose();
            _pTeam.Dispose();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  잡
        // ══════════════════════════════════════════════════════════════════════

        // 지형 키 집합 재빌드(맵 로드/변경 시 1회) — ComputeJob이 라이브 레이어 대신 이걸 읽음.
        [BurstCompile]
        struct TerrainKeysJob : IJob
        {
            [ReadOnly] public NativeHashMap<int2, TerrainCell> Src;
            public NativeHashSet<int2> Dst;

            public void Execute()
            {
                foreach (var kv in Src) Dst.Add(kv.Key);
            }
        }

        // reach(체임퍼 DT + 버킷 선별) + 경합 해소 전부 — 백 버퍼에 완성본을 쓴다.
        [BurstCompile]
        struct ComputeJob : IJob
        {
            [ReadOnly] public NativeHashSet<int2> TerrainKeys;
            [ReadOnly] public NativeList<ResInfo> Residences;
            [ReadOnly] public NativeArray<float>  PInf;
            [ReadOnly] public NativeArray<int>    PTeam;
            public int MaxRadius;

            public NativeHashMap<int2, int> Back;   // 출력: 셀 → 팀 id / Contested(-2)

            public void Execute()
            {
                Back.Clear();

                // ── 팀 영향력 합 (멤버 플레이어 1회씩) ───────────────────────
                var teamInf   = new NativeArray<float>(MP, Allocator.Temp);
                var ownerSeen = new NativeHashSet<int>(MP, Allocator.Temp);
                for (int i = 0; i < Residences.Length; i++)
                {
                    int o = Residences[i].Owner;
                    if (ownerSeen.Add(o)) teamInf[PTeam[o]] += PInf[o];
                }
                ownerSeen.Dispose();

                // ── reach: 소유자별 nearest-N → 셀별 '팀 비트마스크' ──────────
                //   셀×거주지 브루트포스 O(W×R)는 전맵 정복 시 1.5초 스파이크
                //   → 체임퍼(chamfer 3×3) 거리변환 O(W) + 거리 버킷 선별 O(W).
                //   거리 근사(팔각형, 오차 ~4%)라 외곽이 미세하게 각질 수 있음(수용).
                var reach = new NativeHashMap<int2, int>(2048, Allocator.Temp);
                for (int owner = 0; owner < MP; owner++)
                {
                    int budget = 0;
                    int2 bbMin = default, bbMax = default;
                    bool any = false;
                    for (int i = 0; i < Residences.Length; i++)
                    {
                        if (Residences[i].Owner != owner) continue;
                        budget += Residences[i].Cells;
                        int2 c = Residences[i].Center;
                        if (!any) { bbMin = c; bbMax = c; any = true; }
                        else { bbMin = math.min(bbMin, c); bbMax = math.max(bbMax, c); }
                    }
                    if (!any || budget <= 0) continue;

                    int margin = math.min(MaxRadius, (int)math.ceil(math.sqrt(budget / math.PI)) + 2);
                    int2 lo = bbMin - margin, hi = bbMax + margin;
                    int W = hi.x - lo.x + 1, H = hi.y - lo.y + 1;

                    // 거리장 초기화(INF) + 거주지 시드(0).
                    const float INF = 1e9f, D1 = 1f, D2 = 1.4142135f;
                    var dist = new NativeArray<float>(W * H, Allocator.Temp,
                        NativeArrayOptions.UninitializedMemory);
                    for (int i = 0; i < dist.Length; i++) dist[i] = INF;
                    for (int i = 0; i < Residences.Length; i++)
                    {
                        if (Residences[i].Owner != owner) continue;
                        int2 c = Residences[i].Center - lo;
                        if (c.x >= 0 && c.x < W && c.y >= 0 && c.y < H) dist[c.y * W + c.x] = 0f;
                    }

                    // 체임퍼 2패스 — 최근접 거주지까지의 근사 유클리드 거리.
                    for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        int idx = z * W + x; float d = dist[idx];
                        if (x > 0)     d = math.min(d, dist[idx - 1] + D1);
                        if (z > 0)
                        {
                            d = math.min(d, dist[idx - W] + D1);
                            if (x > 0)     d = math.min(d, dist[idx - W - 1] + D2);
                            if (x < W - 1) d = math.min(d, dist[idx - W + 1] + D2);
                        }
                        dist[idx] = d;
                    }
                    for (int z = H - 1; z >= 0; z--)
                    for (int x = W - 1; x >= 0; x--)
                    {
                        int idx = z * W + x; float d = dist[idx];
                        if (x < W - 1) d = math.min(d, dist[idx + 1] + D1);
                        if (z < H - 1)
                        {
                            d = math.min(d, dist[idx + W] + D1);
                            if (x < W - 1) d = math.min(d, dist[idx + W + 1] + D2);
                            if (x > 0)     d = math.min(d, dist[idx + W - 1] + D2);
                        }
                        dist[idx] = d;
                    }

                    // 거리 버킷(0.25 양자화) 카운팅 → budget 컷오프 버킷 산출.
                    //   마지막 버킷 = 오버플로(그 안의 순서는 스캔 순 — budget이 컷 반경을
                    //   넘는 극단에서만 근사, 결정적).
                    int nb = margin * 8 + 16;
                    var counts = new NativeArray<int>(nb, Allocator.Temp);
                    for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        float d = dist[z * W + x];
                        if (d >= INF) continue;
                        if (!TerrainKeys.Contains(lo + new int2(x, z))) continue;
                        counts[math.min((int)(d * 4f), nb - 1)]++;
                    }
                    int cutBucket = nb - 1, cum = 0, remainInCut = 0;
                    for (int b = 0; b < nb; b++)
                    {
                        if (cum + counts[b] >= budget) { cutBucket = b; remainInCut = budget - cum; break; }
                        cum += counts[b];
                        if (b == nb - 1) remainInCut = counts[b];   // budget > 전체 — 다 가짐
                    }

                    // 수집: 컷오프 미만 전부 + 컷오프 버킷은 스캔 순서로 잔여분(결정적).
                    int bit = 1 << PTeam[owner];
                    for (int z = 0; z < H; z++)
                    for (int x = 0; x < W; x++)
                    {
                        float d = dist[z * W + x];
                        if (d >= INF) continue;
                        int2 cell = lo + new int2(x, z);
                        if (!TerrainKeys.Contains(cell)) continue;
                        int q = math.min((int)(d * 4f), nb - 1);
                        if (q > cutBucket) continue;
                        if (q == cutBucket) { if (remainInCut <= 0) continue; remainInCut--; }
                        reach.TryGetValue(cell, out int m);
                        reach[cell] = m | bit;
                    }

                    counts.Dispose();
                    dist.Dispose();
                }

                // ── 해소: 단독 팀 셀 = 소유, 경합 셀(2팀+) = 모아서 구역 비례 배분 ──
                var contested = new NativeHashSet<int2>(512, Allocator.Temp);
                foreach (var kv in reach)
                {
                    int m = kv.Value;
                    if (math.countbits(m) == 1) Back[kv.Key] = math.tzcnt(m);
                    else                        contested.Add(kv.Key);
                }

                AllocateContested(in contested, in reach, in Residences, in PTeam, in teamInf,
                    new ClaimComparer(), ref Back);

                teamInf.Dispose();
                reach.Dispose();
                contested.Dispose();
            }
        }

        // 방향 인덱스 0~3 → 오프셋 (N,E,S,W). RoadDirOps.Offsets(managed 배열)의 Burst-안전 판.
        static int2 Dir4(int d) => d switch
        {
            0 => new int2(0, 1),
            1 => new int2(1, 0),
            2 => new int2(0, -1),
            _ => new int2(-1, 0),
        };

        // 경합 구역(연결요소)별: 승자팀 K = floor(T×(승자−2등)/승자) 칸을 승자 거주지 가까운 순으로.
        static void AllocateContested(
            in NativeHashSet<int2> contested, in NativeHashMap<int2, int> reach,
            in NativeList<ResInfo> residences, in NativeArray<int> pTeam, in NativeArray<float> teamInf,
            ClaimComparer cmp, ref NativeHashMap<int2, int> back)
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
                        int2 nb = cur + Dir4(d);
                        if (contested.Contains(nb) && visited.Add(nb)) q.Enqueue(nb);
                    }
                }

                // 구역 전체를 '경합지(-2, 잠김)'로 먼저 마킹 — 승자 K칸만 이후 덮어씀.
                //   → 미배분 경합 칸은 중립(열림)이 아니라 경합지(누구도 불가)로 남는다.
                for (int i = 0; i < comp.Length; i++)
                    back[comp[i]] = TerritoryOps.Contested;

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
                    for (int i = 0; i < kk; i++) back[ra[i].Cell] = win;
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
