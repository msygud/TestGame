using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AuraCoverageSystem — 오라 커버 맵 재구축 (커버형 욕구 v1, 2026-07-12)
    // ──────────────────────────────────────────────────────────────────────────
    //  오라형 공급자(AuraSupplier — 경찰서·관공서·광장류)는 도로 BFS(stamp)가 아니라
    //  **단순 반경**으로 커버한다(이동이 없으므로 도로 거리를 흉내 낼 이유 없음, 유저 합의).
    //    · 판정 = footprint 사각형 최근접 거리의 유클리드 제곱(dx²+dz² ≤ R²) —
    //      정수 연산(√/float 없음, 결정적). 모양 = 모서리 둥근 사각(작은 건물 ≈ 원).
    //    · 맵 = (owner, 실셀) → relief 비트합. 소유자별 독립(자기 시민만 진정).
    //
    //  실행 모델("느슨함=백그라운드 잡"): 게임 시간당 1회(+최초) 소스를 메인에서 값
    //  복사 → Burst 잡이 back 맵을 Clear 후 전체 재그리기(무효화 회피 — stamp 독트린)
    //  → IsCompleted 폴링 → front(AuraCoverage 싱글톤)로 복사 발행 + Version++.
    //  독자(SafetySystem·DemandAggregation·배치 프리뷰)는 front만 읽는다(GetSingleton RO
    //  → 발행측 GetSingletonRW와 프레임워크 의존성으로 상호 안전).
    //  비용: 시설 수 × 원 면적(반경 20 ≈ 1,300셀) — 시간당 1회라 사실상 0.
    //
    //  v1.5 과밀 신호(2026-07-12): 같은 틱에 시설별 커버 인구도 집계(CountAuraLoadJob,
    //  최근접 귀속) → 커버 인구 > 정원(AuraSupplier.Capacity)이면 이웃 지구 증설 수요를
    //  AuraOverfullLog(지속 플래그, 시간당 재작성)로 발행 — DemandAggregation이 매초 샘플.
    //  해소(SafetySystem)는 무수정: 정원은 해소 게이트가 아니라 수요 신호다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AuraCoverageSystem : ISystem
    {
        struct AuraSrc
        {
            public Entity Ent;     // 시설 엔티티(부하 발행 키 — AuraLoadMap)
            public int   Owner;
            public int2  Origin;
            public int2  Eff;      // 회전 반영 크기
            public ulong Relief;
            public int   Radius;
            public int   Capacity; // 정원(v1.5 과밀 신호). 0 이하 = 무제한(신호 없음)
        }

        NativeHashMap<int3, ulong> _back;   // 잡이 채우는 백 버퍼(Persistent)
        NativeList<AuraSrc>        _srcs;   // 런별 소스 스냅샷
        NativeArray<int>           _counts; // 런별 시설 커버 인구(최근접 귀속, v1.5)
        JobHandle _handle;
        bool      _running;
        bool      _everBuilt;

        // 과밀 목표 지구 최소 개발 여지(셀) — 물·단차로 개발 불가한 '나쁜 지형' 지구를
        // 증설 목표에서 배제(목표로 삼으면 시설이 영원히 못 서고 신호가 안 꺼진다).
        const int AuraTargetMinRoom = 24;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            _back = new NativeHashMap<int3, ulong>(1024, Allocator.Persistent);

            if (!SystemAPI.HasSingleton<AuraCoverage>())
            {
                var e = state.EntityManager.CreateEntity(typeof(AuraCoverage));
                state.EntityManager.SetComponentData(e, new AuraCoverage
                {
                    Map     = new NativeHashMap<int3, ulong>(1024, Allocator.Persistent),
                    Version = 0,
                });
            }
            if (!SystemAPI.HasSingleton<AuraOverfullLog>())
            {
                var e = state.EntityManager.CreateEntity(typeof(AuraOverfullLog));
                state.EntityManager.SetComponentData(e, new AuraOverfullLog
                {
                    Window = new NativeHashMap<int4, byte>(32, Allocator.Persistent),
                });
            }
            if (!SystemAPI.HasSingleton<AuraLoadMap>())
            {
                var e = state.EntityManager.CreateEntity(typeof(AuraLoadMap));
                state.EntityManager.SetComponentData(e, new AuraLoadMap
                {
                    Map     = new NativeHashMap<Entity, int2>(64, Allocator.Persistent),
                    Version = 0,
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_running) { _handle.Complete(); _srcs.Dispose(); _counts.Dispose(); _running = false; }
            if (_back.IsCreated) _back.Dispose();
            if (SystemAPI.HasSingleton<AuraCoverage>())
            {
                var ac = SystemAPI.GetSingleton<AuraCoverage>();
                if (ac.Map.IsCreated) ac.Map.Dispose();
            }
            if (SystemAPI.HasSingleton<AuraOverfullLog>())
            {
                var log = SystemAPI.GetSingleton<AuraOverfullLog>();
                if (log.Window.IsCreated) log.Window.Dispose();
            }
            if (SystemAPI.HasSingleton<AuraLoadMap>())
            {
                var lm = SystemAPI.GetSingleton<AuraLoadMap>();
                if (lm.Map.IsCreated) lm.Map.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 완료 폴링: back → front 복사 발행 ──
            if (_running)
            {
                if (!_handle.IsCompleted) return;
                _handle.Complete();

                // 발행 전 전체 잡 동기화(확립 패턴 — 재개발과 동일 사유): front 맵을 **원시
                //   컨테이너로 캡처**한 리더 잡(SafetyTickJob·CollectSafetyDemandJob)은 잡 쿼리에
                //   AuraCoverage 타입이 없어 컴포넌트 의존성 추적을 벗어난다 → GetSingletonRW로도
                //   완료가 강제되지 않음(실측 InvalidOperationException, 2026-07-12). 발행은
                //   게임 시간당 1회라 전체 동기화 비용은 무시 가능.
                state.EntityManager.CompleteAllTrackedJobs();

                ref var ac = ref SystemAPI.GetSingletonRW<AuraCoverage>().ValueRW;
                ac.Map.Clear();
                var kv = _back.GetKeyValueArrays(Allocator.Temp);
                for (int i = 0; i < kv.Keys.Length; i++)
                    ac.Map[kv.Keys[i]] = kv.Values[i];
                kv.Dispose();
                ac.Version++;

                // ── v1.5 부하 발행(표시용): 시설 → (커버 인구, 정원). AuraLoadHud(F6)가 읽음.
                ref var loadMap = ref SystemAPI.GetSingletonRW<AuraLoadMap>().ValueRW;
                loadMap.Map.Clear();
                for (int i = 0; i < _srcs.Length; i++)
                    loadMap.Map[_srcs[i].Ent] = new int2(_counts[i], _srcs[i].Capacity);
                loadMap.Version++;

                // ── v1.5 과밀 신호(2026-07-12): 커버 인구 > 정원 → 이웃 지구 증설 수요 ──
                //   해소는 커버만으로 성립(불변) — 정원은 해소 게이트가 아니라 **수요 신호**
                //   ("수용량을 해소가 아니라 수요로 쓴다"). 부하 귀속 = 최근접 시설 1곳(좌석
                //   모델, CountAuraLoadJob): 이웃 증설이 경계 시민을 흡수해 부하가 정원 밑으로
                //   내려가면 신호가 자연 소멸(자기 종결 피드백). 목표 = 8-이웃 중 내 오라 없는
                //   지구(1패스 = 내 도로 보유 지구 우선, 2패스 = 아무 오라-없는 이웃).
                //   전 이웃 보유 = 포화 → 무신호(정직 — Capacity가 밸런싱 손잡이).
                ref var ofLog = ref SystemAPI.GetSingletonRW<AuraOverfullLog>().ValueRW;
                // 로그는 '신규 진입'만(2026-07-12 로그 정리) — 지속 과밀의 시간당 반복 로그 방지.
                var prevOverfull = new NativeHashSet<int4>(32, Allocator.Temp);
                foreach (var pk in ofLog.Window) prevOverfull.Add(pk.Key);
                ofLog.Window.Clear();
                if (_srcs.Length > 0)
                {
                    var auraD = new NativeHashSet<int3>(_srcs.Length * 2, Allocator.Temp);
                    for (int i = 0; i < _srcs.Length; i++)
                    {
                        int2 sd = DistrictGrid.ToDistrict(_srcs[i].Origin);
                        auraD.Add(new int3(_srcs[i].Owner, sd.x, sd.y));
                    }
                    bool haveTable = SystemAPI.TryGetSingleton<DistrictTable>(out var dtable)
                                     && dtable.Stats.IsCreated;
                    int hcBit = math.tzcnt((ulong)NeedType.HighCrime);

                    for (int i = 0; i < _srcs.Length; i++)
                    {
                        var src = _srcs[i];
                        if (src.Capacity <= 0 || _counts[i] <= src.Capacity) continue;

                        int2 d0 = DistrictGrid.ToDistrict(src.Origin);
                        bool found = false; int2 target = default;
                        for (int pass = 0; pass < 2 && !found; pass++)
                        for (int n = 0; n < 8; n++)
                        {
                            int2 nd = d0 + Neigh8(n);
                            if (auraD.Contains(new int3(src.Owner, nd.x, nd.y))) continue;

                            // 중심이 이미 내 오라 커버 = 실질 서비스 존재(옆 지구에 선 시설이
                            //   여길 덮는 경우). 지구 귀속(auraD)만 보면 이를 못 보고 같은 목표를
                            //   영원히 재지목한다 — 나쁜 지형 연쇄 착지(연속 N채)의 한 축.
                            int2 nc = DistrictGrid.Center(nd);
                            if (ac.Map.TryGetValue(new int3(src.Owner, nc.x, nc.y), out ulong cbits)
                                && (cbits & (ulong)NeedType.HighCrime) != 0) continue;

                            // 개발 여지 없는 지구(물·단차 = 나쁜 지형) 제외 — 목표로 삼으면
                            //   시설이 영원히 못 서고 신호가 안 꺼져 옆 회랑에 연쇄 착지한다.
                            DistrictStat dstat = default;
                            bool hasStat = haveTable && dtable.Stats.TryGetValue(nd, out dstat);
                            if (haveTable && (!hasStat || dstat.Room < AuraTargetMinRoom)) continue;
                            if (pass == 0 && (!hasStat
                                || (dstat.RoadOwners & (1 << src.Owner)) == 0)) continue;

                            target = nd; found = true; break;
                        }
                        if (!found) continue;   // 유효 이웃 없음 = 포화/지형 한계 — 무신호(정직)

                        int2 dc = DemandGrid.ToCell(DistrictGrid.Center(target));
                        var okey = new int4(src.Owner, dc.x, dc.y, hcBit);
                        if (ofLog.Window.TryAdd(okey, 1) && !prevOverfull.Contains(okey))
                            UnityEngine.Debug.Log(
                                $"[Aura] P{src.Owner} 과밀 {_counts[i]}/{src.Capacity} " +
                                $"지구=({d0.x},{d0.y}) → 증설 목표=({target.x},{target.y})");
                    }
                    auraD.Dispose();
                }
                prevOverfull.Dispose();

                _srcs.Dispose();
                _counts.Dispose();
                _running = false;
            }

            // ── 게이트: 게임 시간당 1회(+최초). 무조건 재그리기 — dirty 추적 불필요한 규모.
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (_everBuilt && !clock.HourChanged) return;
            _everBuilt = true;

            // 소스 스냅샷(메인, 값 복사 — 잡이 라이브 컴포넌트를 안 듦).
            _srcs = new NativeList<AuraSrc>(32, Allocator.Persistent);
            foreach (var (aura, fp, ent) in
                     SystemAPI.Query<RefRO<AuraSupplier>, RefRO<BuildingFootprint>>()
                              .WithEntityAccess())
            {
                if (aura.ValueRO.Radius <= 0 || aura.ValueRO.Relief == NeedType.None) continue;
                _srcs.Add(new AuraSrc
                {
                    Ent      = ent,
                    Owner    = fp.ValueRO.OwnerLocalId,
                    Origin   = fp.ValueRO.Origin,
                    Eff      = EntranceOps.RotateSize(fp.ValueRO.Size, fp.ValueRO.RotSteps),
                    Relief   = (ulong)aura.ValueRO.Relief,
                    Radius   = aura.ValueRO.Radius,
                    Capacity = aura.ValueRO.Capacity,
                });
            }

            // 커버 맵 재그리기 + 시설별 부하 집계(v1.5) — 서로 독립(둘 다 _srcs RO), 병렬.
            _counts = new NativeArray<int>(_srcs.Length, Allocator.Persistent);
            var hRebuild = new RebuildAuraJob { Srcs = _srcs, Back = _back }.Schedule(state.Dependency);
            var hCount = _srcs.Length > 0
                ? new CountAuraLoadJob
                  {
                      FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                      Srcs     = _srcs,
                      Counts   = _counts,
                  }.Schedule(state.Dependency)
                : default;
            _handle = JobHandle.CombineDependencies(hRebuild, hCount);
            state.Dependency = _handle;
            _running = true;
        }

        // 8-이웃 오프셋(결정적 순서 — 십자 먼저, 대각 나중).
        static int2 Neigh8(int i) => i switch
        {
            0 => new int2(0, 1), 1 => new int2(1, 0), 2 => new int2(0, -1), 3 => new int2(-1, 0),
            4 => new int2(1, 1), 5 => new int2(-1, 1), 6 => new int2(1, -1), _ => new int2(-1, -1),
        };

        // ── v1.5: 시설별 커버 인구 집계(최근접 귀속 = 좌석 모델, 시간당 1회) ──
        //   치안 대상(CitizenSafety 보유) 시민의 집 셀이 닿는 시설 중 **최근접 1곳**에만 +1.
        //   독립 카운트(닿는 곳 전부 +1)로 하면 이웃 증설이 부하를 못 덜어 신호가 영구화된다.
        //   시민 ~2만 × 시설 수십 = 시간당 1회 단일 스레드로 충분(워커에서 실행).
        [BurstCompile]
        [WithAll(typeof(CitizenTag), typeof(CitizenSafety))]
        partial struct CountAuraLoadJob : IJobEntity
        {
            [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
            [ReadOnly] public NativeList<AuraSrc> Srcs;
            public NativeArray<int> Counts;

            void Execute(in CitizenResidence res)
            {
                if (res.Home == Entity.Null || !FpLookup.HasComponent(res.Home)) return;
                var fp = FpLookup[res.Home];

                int best = -1, bestD = int.MaxValue;
                for (int s = 0; s < Srcs.Length; s++)
                {
                    var src = Srcs[s];
                    if (src.Owner != fp.OwnerLocalId) continue;
                    // 집 셀 → 시설 footprint 최근접점 거리²(RebuildAuraJob과 동일 판정, 역방향).
                    int nx = math.clamp(fp.Origin.x, src.Origin.x, src.Origin.x + src.Eff.x - 1);
                    int ny = math.clamp(fp.Origin.y, src.Origin.y, src.Origin.y + src.Eff.y - 1);
                    int dx = fp.Origin.x - nx, dy = fp.Origin.y - ny;
                    int d2 = dx * dx + dy * dy;
                    if (d2 > src.Radius * src.Radius || d2 >= bestD) continue;
                    bestD = d2; best = s;
                }
                if (best >= 0) Counts[best]++;
            }
        }

        // ── 백 버퍼 재그리기: 시설별 footprint-클램프 원형 채우기 ──
        [BurstCompile]
        struct RebuildAuraJob : IJob
        {
            [ReadOnly] public NativeList<AuraSrc> Srcs;
            public NativeHashMap<int3, ulong> Back;

            public void Execute()
            {
                Back.Clear();
                for (int s = 0; s < Srcs.Length; s++)
                {
                    var src = Srcs[s];
                    int r  = src.Radius;
                    int r2 = r * r;
                    int2 lo = src.Origin - new int2(r, r);
                    int2 hi = src.Origin + src.Eff - 1 + new int2(r, r);
                    for (int y = lo.y; y <= hi.y; y++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        // footprint 사각형 최근접점까지의 거리(클램프) — 짝수 크기 중심 애매함 없음.
                        int nx = math.clamp(x, src.Origin.x, src.Origin.x + src.Eff.x - 1);
                        int ny = math.clamp(y, src.Origin.y, src.Origin.y + src.Eff.y - 1);
                        int dx = x - nx, dy = y - ny;
                        if (dx * dx + dy * dy > r2) continue;

                        var key = new int3(src.Owner, x, y);
                        Back.TryGetValue(key, out ulong bits);
                        Back[key] = bits | src.Relief;
                    }
                }
            }
        }
    }
}
