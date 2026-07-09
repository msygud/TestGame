using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
    //  실행 모델(2026-07-04 잡화 — 프로파일 실측 메인스레드 ~43ms 스파이크 해소):
    //    DayChanged →
    //      ① SnapshotJob(빠름): 레이어를 잡 전용 복사본으로. state.Dependency에 등록 +
    //         GridLayers RW 의도 선언 → 이후 레이어 쓰기 시스템이 '복사 완료'만 대기.
    //      ② GrowthJob(느림): 스냅샷만 읽는 Burst 잡 — 핸들을 state.Dependency에 안 올리고
    //         프라이빗으로 보관 → 여러 프레임에 걸쳐 워커에서 계산(메인 비용 0).
    //      ③ 폴링: IsCompleted 확인(블로킹 Complete 금지) 후 메인에서 명령 엔티티 생성만.
    //    결과가 1~수 프레임 늦어도 무방(게임-일 1회 계획) — 명령은 어차피
    //    RoadSystem/BuildingPlacement가 실제 레이어로 재검증한다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct AiCityGrowthSystem : ISystem
    {
        JobHandle _growthHandle;   // 성장 잡(느림) — state.Dependency 밖에서 폴링
        bool      _running;
        bool      _dayPending;     // 잡 실행 중 DayChanged 도착 → 완료 후 즉시 재스케줄

        GrowthSnap _snap;          // 잡 전용 레이어 복사본(Persistent, 런마다 할당/해제)
        NativeArray<TeamInput>            _teams;
        NativeArray<BuildOption>         _demandOpts;   // 플레이어별 수요 건물(WHAT, Key=0=없음)
        NativeArray<int2>                _demandCells;  // 플레이어별 수요 셀(실셀 중심, STEP 3 WHERE 바이어스)
        NativeList<PlaceRoadCommand>      _roadOut;
        NativeList<PlaceBuildingRequest>  _bldOut;
        NativeList<GrowthLog>             _logOut;
        int _scheduledDay;         // 로그 메시지용

        // ResourceLayer가 미생성일 때 잡에 대신 캡처할 빈 맵(잡 필드는 항상 할당돼야 함).
        NativeHashMap<int2, ResourceCell> _emptyRes;

        // (owner,needBit) → 직전에 처리한 누적 NoCoverage. 세션 지속. 누적 필드에서 '증가분(delta)'만
        //   판단하기 위한 기준선 — 총량 기준이면 커버 후에도 영구 재건축(난사). delta는 커버되면 0.
        NativeHashMap<int2, int> _lastNoCoverage;

        // (owner,수요셀,resId) → 그 수요에 지은 건물 시도 횟수. 상한 도달 = "둘러싸여 닿을 수 없는" 요청으로
        //   판단해 블랙리스트(무한 반복 건설 차단, 2026-07-09). 세션 지속.
        NativeHashMap<int4, int> _demandAttempts;

        // (owner,수요셀,resId) → 철거-후-건설(재개발) 횟수. 인테리어+프런티어가 여러 번 실패한 꽉 찬 도심에서
        //   주택을 철거해 구멍을 낸 횟수. (owner,셀,res)당 RedevelopCap 한도(thrash 방지). 세션 지속.
        NativeHashMap<int4, int> _redevelopAttempts;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<EntranceLookup>();
            state.RequireForUpdate<PrefabMetaLookup>();
            state.RequireForUpdate<CellTypeLookup>();
            _emptyRes = new NativeHashMap<int2, ResourceCell>(1, Allocator.Persistent);
            _lastNoCoverage = new NativeHashMap<int2, int>(64, Allocator.Persistent);
            _demandAttempts = new NativeHashMap<int4, int>(64, Allocator.Persistent);
            _redevelopAttempts = new NativeHashMap<int4, int>(64, Allocator.Persistent);
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_running)
            {
                _growthHandle.Complete();
                DisposeRun();
                _running = false;
            }
            _emptyRes.Dispose();
            if (_lastNoCoverage.IsCreated) _lastNoCoverage.Dispose();
            if (_demandAttempts.IsCreated) _demandAttempts.Dispose();
            if (_redevelopAttempts.IsCreated) _redevelopAttempts.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();

            // ── 완료 폴링: 끝났으면 결과 적용(명령 엔티티 생성 + 로그), 아니면 다음 프레임 ──
            if (_running)
            {
                if (clock.DayChanged) _dayPending = true;   // 놓친 날은 완료 직후 몰아서
                if (!_growthHandle.IsCompleted) return;
                _growthHandle.Complete();                    // IsCompleted 후라 논블로킹
                ApplyResults(ref state);
                DisposeRun();
                _running = false;
            }

            if (!clock.DayChanged && !_dayPending) return;
            _dayPending = false;

            // ── 스케줄: 입력 수집(메인, 소량) → 스냅샷 잡 → 성장 잡 ──────────────
            var entranceLookup = SystemAPI.GetSingleton<EntranceLookup>();
            var metaLookup     = SystemAPI.GetSingleton<PrefabMetaLookup>();
            var cellTypeLookup = SystemAPI.GetSingleton<CellTypeLookup>();
            var cfg = SystemAPI.TryGetSingleton<GrowthConfig>(out var gc) ? gc : GrowthConfig.Default;

            // 영역 게이트는 TerritoryLayer의 '팀 id'와 '내 팀'을 비교 → LocalId→팀 매핑 필요.
            if (!SystemAPI.TryGetSingleton<TeamTable>(out var teams)) teams = TeamTable.Identity;
            // 적 영토 완충(셀) — 국경에 지어 곧바로 잠식·파괴되는 churn 방지(밸런스 config).
            int enemyBuffer = (SystemAPI.TryGetSingleton<TerritoryCaptureConfig>(out var capCfg)
                ? capCfg : TerritoryCaptureConfig.Default).AiEnemyBufferCells;

            // AI 팀 수집 (메인 — 팀 수 ≤ 8).
            var teamList = new NativeList<TeamInput>(8, Allocator.Temp);
            foreach (var (teamRO, gridRO) in
                     SystemAPI.Query<RefRO<TeamInfoData>, RefRO<CityGrid>>())
            {
                if (teamRO.ValueRO.IsPlayer()) continue;
                teamList.Add(new TeamInput { Owner = teamRO.ValueRO.LocalID, Grid = gridRO.ValueRO });
            }
            if (teamList.Length == 0) { teamList.Dispose(); return; }

            // 건물 메타/입구를 메인에서 값으로 해석 → 잡이 룩업 컨테이너를 안 들고 감
            //   (룩업 재빌드와의 수명 충돌 원천 차단).
            var optA = ResolveOption(cfg.BuildingKeyA, in metaLookup, in entranceLookup);
            var optB = ResolveOption(cfg.BuildingKeyB, in metaLookup, in entranceLookup);

            // GridLayers RW 의도 선언(확립 기법) — 이후 프레임에서 레이어를 만지는 모든
            //   시스템이 GetSingleton 시 '스냅샷 복사 잡'의 완료를 대기하게 강제한다.
            //   (복사는 빠름 — 대기해도 사실상 0. 느린 성장 잡은 스냅샷만 읽어 대기 없음.)
            var layers = SystemAPI.GetSingletonRW<GridLayers>().ValueRO;

            _teams   = new NativeArray<TeamInput>(teamList.AsArray(), Allocator.Persistent);
            teamList.Dispose();
            _roadOut = new NativeList<PlaceRoadCommand>(256, Allocator.Persistent);
            _bldOut  = new NativeList<PlaceBuildingRequest>(64, Allocator.Persistent);
            _logOut  = new NativeList<GrowthLog>(16, Allocator.Persistent);
            _snap    = GrowthSnap.Allocate(in layers);
            _scheduledDay = clock.Day;

            // 욕구 주도 배치 STEP 2(WHAT-only): 팀별 최대 NoCoverage 수요 → 해소 건물 해석
            //   (메인, 잡 밖 — L2/DemandField 접근). 수요/매핑 없으면 Key=0 → 잡이 기존
            //   OptA/OptB 확장으로 폴백(회귀 0). WHERE 바이어스는 STEP 3.
            _demandOpts = new NativeArray<BuildOption>(_teams.Length, Allocator.Persistent);
            _demandCells = new NativeArray<int2>(_teams.Length, Allocator.Persistent);
            for (int di = 0; di < _demandOpts.Length; di++) _demandOpts[di] = default;
            if (SystemAPI.TryGetSingleton<DemandField>(out var demandField) && demandField.Stats.IsCreated)
            {
                NeedLookupL2 l2 = default; bool haveL2 = false;
                foreach (var lk in SystemAPI.Query<RefRO<NeedLookupL2>>())
                { l2 = lk.ValueRO; haveL2 = true; break; }

                // case-b 앵커용 창고 셀 수집(owner, footprint 원점). commodity 생산자(제분소·농장)는
                //   소비자 옆이 아니라 가장 가까운 자기 창고 근처로 앵커링(풀 공유 → 창고 커버가 접속점).
                var warehouseCells = new NativeList<int3>(16, Allocator.Temp);
                foreach (var (wh, wfp) in
                         SystemAPI.Query<RefRO<WarehouseTag>, RefRO<BuildingFootprint>>())
                    warehouseCells.Add(new int3(wfp.ValueRO.OwnerLocalId,
                                                wfp.ValueRO.Origin.x, wfp.ValueRO.Origin.y));

                // L2 없어도(재베이크 전) 하드코딩 폴백이 돌도록 항상 호출.
                var redevelopReqs = new NativeList<int3>(8, Allocator.Temp);
                ResolveDemandOptions(in demandField, l2, haveL2, in metaLookup, in entranceLookup,
                    in warehouseCells, ref redevelopReqs);
                warehouseCells.Dispose();

                // 철거-후-건설 실행(메인, SnapshotJob 전 → 같은 틱 스냅샷에 구멍 반영):
                //   요청 target 근처 최근접 **주택**을 철거(셀 즉시 해제 + destroy + StampDirty) → 스냅샷에
                //   구멍이 담겨 GrowthJob 인테리어 배치가 같은 틱에 채운다. thrash 가드는 ResolveDemandOptions.
                //   주택만 철거(ResidenceBuilding 필터) — 서비스/창고는 대상 아님. 철거된 집 거주민은
                //   DeadReferenceReclaim이 재하우징(누수 없음).
                if (redevelopReqs.Length > 0)
                {
                    // 라이브 레이어 쓰기 전 **모든 잡 완료**: AiRoadJanitor의 SnapshotJob이 OccupancyLayer를
                    //   [ReadOnly]로 읽는데, 폴링 백그라운드라 프레임워크의 GridLayers 컴포넌트 의존성 추적을
                    //   벗어나 있어 GetSingletonRW(ValueRW)로도 완료가 강제되지 않는다(실측). 재개발은 저빈도
                    //   (escalation gate)라 이 전체 동기화는 드물게만 발생 → 라이브 쓰기를 확실히 안전하게.
                    state.EntityManager.CompleteAllTrackedJobs();

                    var occ    = layers.OccupancyLayer;
                    bool hasGm = SystemAPI.HasSingleton<GridMap>();
                    var gm     = hasGm ? SystemAPI.GetSingleton<GridMap>() : default;
                    var rdEcb  = new EntityCommandBuffer(Allocator.Temp);

                    for (int r = 0; r < redevelopReqs.Length; r++)
                    {
                        var req    = redevelopReqs[r];
                        int rOwner = req.x;
                        int2 tgt   = new int2(req.y, req.z);

                        Entity best = Entity.Null; int2 bestOrigin = default, bestEff = default;
                        long bestD = long.MaxValue;
                        foreach (var (bfRO, e) in
                                 SystemAPI.Query<RefRO<BuildingFootprint>>()
                                     .WithAll<ResidenceBuilding>().WithEntityAccess())
                        {
                            var bf = bfRO.ValueRO;
                            if (bf.OwnerLocalId != rOwner) continue;
                            int2 eff = EntranceOps.RotateSize(bf.Size, bf.RotSteps);
                            int2 c   = bf.Origin + eff / 2;
                            long dx = c.x - tgt.x, dy = c.y - tgt.y;
                            long d  = dx * dx + dy * dy;
                            if (d < bestD) { bestD = d; best = e; bestOrigin = bf.Origin; bestEff = eff; }
                        }

                        const long RedevelopRangeSq = 32 * 32;   // 너무 먼 주택은 철거해도 헛일 → 스킵
                        if (best == Entity.Null || bestD > RedevelopRangeSq) continue;

                        for (int dx = 0; dx < bestEff.x; dx++)
                        for (int dz = 0; dz < bestEff.y; dz++)
                        {
                            int2 cell = bestOrigin + new int2(dx, dz);
                            occ.Remove(cell);
                            if (hasGm) gm.BuildingCells.Remove(cell);
                        }
                        rdEcb.DestroyEntity(best);
                        var de = rdEcb.CreateEntity();
                        rdEcb.AddComponent(de, new StampDirtyEvent { OwnerLocalId = rOwner });
                    }

                    rdEcb.Playback(state.EntityManager);
                    rdEcb.Dispose();
                }
                redevelopReqs.Dispose();
            }

            var copyH = new SnapshotJob
            {
                SrcTerrain   = layers.TerrainLayer,
                SrcRoad      = layers.RoadLayer,
                SrcOcc       = layers.OccupancyLayer,
                SrcTerr      = layers.TerritoryLayer,
                SrcRes       = layers.ResourceLayer.IsCreated ? layers.ResourceLayer : _emptyRes,
                SrcCellTypes = cellTypeLookup.Table,
                Snap         = _snap,
            }.Schedule(state.Dependency);

            _growthHandle = new GrowthJob
            {
                Snap        = _snap,
                Teams       = _teams,
                DemandOpts  = _demandOpts,
                DemandCells = _demandCells,
                OptA        = optA,
                OptB        = optB,
                Cfg         = cfg,
                TeamsTable  = teams,
                EnemyBuffer = enemyBuffer,
                Day         = clock.Day,
                RoadOut     = _roadOut,
                BldOut      = _bldOut,
                LogOut      = _logOut,
            }.Schedule(copyH);

            state.Dependency = copyH;   // 복사만 등록 — 느린 성장 잡은 의존성 체인 밖(폴링)
            _running = true;
        }

        // 잡 결과 적용 — 명령 엔티티 생성(같은 프레임에 RoadSystem/BuildingPlacement가 처리)
        //   + Burst 밖으로 미뤄둔 로그 출력. 메인스레드 비용은 엔티티 몇십 개 생성뿐.
        void ApplyResults(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            for (int i = 0; i < _roadOut.Length; i++)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, _roadOut[i]);
            }
            for (int i = 0; i < _bldOut.Length; i++)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, _bldOut[i]);
            }
            ecb.Playback(state.EntityManager);
            ecb.Dispose();

            for (int i = 0; i < _logOut.Length; i++)
            {
                var l = _logOut[i];
                switch (l.Kind)
                {
                    case GrowthLog.NoTeamRoad:
                        Debug.LogWarning($"[AiCityGrowth] team{l.Owner}: 팀 도로 없음");
                        break;
                    case GrowthLog.NoSpot:
                        Debug.Log($"[AiCityGrowth] team{l.Owner} day{_scheduledDay}: " +
                                  $"성장 자리 없음 K={l.A} (유효후보={l.B})");
                        break;
                }
            }
        }

        void DisposeRun()
        {
            _snap.Dispose();
            _teams.Dispose();
            _demandOpts.Dispose();
            _demandCells.Dispose();
            _roadOut.Dispose();
            _bldOut.Dispose();
            _logOut.Dispose();
        }

        // 건물 메타/입구 → 값 타입 해석(메인). 무효(미등록/크기 0/8셀 초과)면 Key=0.
        static BuildOption ResolveOption(
            int key, in PrefabMetaLookup metaLookup, in EntranceLookup entranceLookup)
        {
            if (key <= 0) return default;
            if (!metaLookup.TryGetMeta(key, 0, out var meta)) return default;
            if (meta.Size.x <= 0 || meta.Size.y <= 0) return default;
            int k = BlockSizeFor(math.max(meta.Size.x, meta.Size.y));
            if (k == 0)
            {
                Debug.LogWarning($"[AiCityGrowth] 건물 key={key} size={meta.Size} 8셀 초과");
                return default;
            }
            var opt = new BuildOption
            {
                Key = key, Size = meta.Size, BuildableOn = meta.BuildableOn, BlockK = k,
            };
            if (meta.HasEntrance && entranceLookup.TryGet(key, out var ent))
            { opt.HasEnt = true; opt.Ent = ent; }
            return opt;
        }

        // 욕구 주도 배치 STEP 2 — 팀별 최대 NoCoverage 수요 욕구 → 해소 건물 BuildOption(메인).
        //   ResolveOption 패턴 재사용. 수요 없음/매핑 없음/무효 건물 = Key=0(잡이 OptA/OptB로 폴백).
        //   ※ v1: NoCoverage(공급자 없음)만 건물화. Reached(상류 부족)는 집계만 — 공급망 자기조립은 후속.
        //   ※ WHERE(수요셀)는 v1 미사용(WHAT-only). 아무 팩션 L2든 공통 매핑(FactionFlags=0) 보유.
        void ResolveDemandOptions(in DemandField df, NeedLookupL2 l2, bool haveL2,
            in PrefabMetaLookup metaLookup, in EntranceLookup entranceLookup,
            in NativeList<int3> warehouseCells, ref NativeList<int3> redevelopReqs)
        {
            var stats = df.Stats;

            // (owner,resId) → 누적 NoCoverage 합 + 우세(최대) 수요 셀. 누적은 안 줄어드므로 판단은
            //   '지난 처리 대비 증가분(delta)'으로 — 총량 기준이면 커버 후에도 난사.
            var cur = new NativeHashMap<int2, CurAgg>(64, Allocator.Temp);
            foreach (var kv in stats)
            {
                int nc = kv.Value.FailNoCoverage;      // NoCoverage 계열(시민 커버 공백 + 공급자 상류 부족)
                if (nc == 0) continue;
                var okey = new int2(kv.Key.x, kv.Key.w);   // (owner, resId)
                cur.TryGetValue(okey, out var agg);
                agg.Sum += nc;
                if (nc > agg.DomVal) { agg.DomVal = nc; agg.DomCell = new int2(kv.Key.y, kv.Key.z); }
                cur[okey] = agg;
            }

            for (int t = 0; t < _teams.Length; t++)
            {
                int owner = _teams[t].Owner;
                // 의존성 우선(원재료→중간재→최종)으로 데드락 회피 + 블랙리스트 회피. tier 낮을수록(rawer)
                //   우선, 동tier면 delta. 상류가 채워지면 그 수요가 꺼져 자연히 하류로 넘어감.
                int bestRes = -1, bestTier = int.MaxValue, bestDelta = 0, bestCur = 0;
                int2 bestCell = default;
                foreach (var e in cur)
                {
                    if (e.Key.x != owner) continue;
                    int res = e.Key.y;
                    _lastNoCoverage.TryGetValue(new int2(owner, res), out int last);
                    int delta = e.Value.Sum - last;
                    if (delta < DemandActThreshold) continue;      // 임계 미만은 후보 아님

                    // 반복 무효 건설 차단: 이 (owner,우세셀,res)에 상한만큼 지었는데도 수요가 안 꺼지면
                    //   = "둘러싸여 닿을 수 없는" 요청 → 블랙리스트(이 후보만 건너뜀, 다른 후보는 계속 처리).
                    var bkey = new int4(owner, e.Value.DomCell.x, e.Value.DomCell.y, res);
                    _demandAttempts.TryGetValue(bkey, out int attempts);
                    if (attempts >= DemandAttemptCap) continue;

                    int tier = ResourceTier(res);
                    if (tier < bestTier || (tier == bestTier && delta > bestDelta))
                    { bestTier = tier; bestDelta = delta; bestRes = res; bestCur = e.Value.Sum; bestCell = e.Value.DomCell; }
                }
                if (bestRes < 0) continue;   // 임계 넘는 (비블랙리스트) 후보 없음

                // resource id 분기: 창고(routing) / 욕구(<64)=relief / commodity(64~)=producer.
                int mainKey = 0;
                if (DemandResource.IsWarehouse(bestRes))
                {
                    mainKey = 1005;   // ⚠ 임시(검증용) — 창고 stock_h. 능력 파생표로 교체 예정.
                }
                else if (!DemandResource.IsCommodity(bestRes))
                {
                    uint needMask = 1u << bestRes;
                    if (haveL2 && LookupHelper.TryGetMainKey(needMask, l2, out int mk))
                        mainKey = mk;                               // 정식: L2 결정 테이블
                    if (mainKey <= 0)
                        mainKey = HardcodedNeedMainKey(bestRes);    // ⚠ 임시(검증용) — L2 없음/매핑 없음 폴백
                }
                else
                {
                    mainKey = HardcodedCommodityProducer(DemandResource.ToCommodity(bestRes));  // ⚠ 임시(검증용)
                }
                if (mainKey <= 0) continue;
                var opt = ResolveOption(mainKey, in metaLookup, in entranceLookup);
                if (opt.Key <= 0) continue;

                _demandOpts[t] = opt;
                _lastNoCoverage[new int2(owner, bestRes)] = bestCur;   // 기준선 갱신(해소되면 delta→0)

                // WHERE 앵커:
                //   · case a (시민 공급자·창고): 우세 수요 셀(소비자·굶주린 곳) 근처.
                //   · case b (commodity 생산자=제분소·농장): 풀 공유라 소비자 옆이 아니라 가장 가까운
                //     자기 창고 근처로 — push/pull 커버 접속점(제분소 기아 픽스). 창고 없으면 수요 셀
                //     폴백(생산자 Output이 차면 B-1이 창고 자기조립 → 다음 틱부터 앵커 성립).
                int2 ro = DemandGrid.ToRealOrigin(bestCell);
                int2 whereCell = new int2(ro.x + DemandGrid.CellSize / 2, ro.y + DemandGrid.CellSize / 2);
                if (DemandResource.IsCommodity(bestRes)
                    && TryNearestWarehouse(owner, whereCell, in warehouseCells, out int2 whCell))
                    whereCell = whCell;
                _demandCells[t] = whereCell;

                // 이 (owner,셀,res) 시도 기록 — 반복 무효 시 블랙리스트. 해소되면 delta<임계로 재선택 안 돼 멈춤.
                var akey = new int4(owner, bestCell.x, bestCell.y, bestRes);
                _demandAttempts.TryGetValue(akey, out int prev);
                int attemptsNow = prev + 1;
                _demandAttempts[akey] = attemptsNow;

                // 철거-후-건설 에스컬레이션: 인테리어+프런티어를 RedevelopEscalate회 시도해도 수요가 안 꺼지면
                //   = 꽉 찬 도심(수요 근처에 빈 구획 없음) → whereCell 근처 주택 1채 철거 요청. 철거로 생긴
                //   구멍을 같은 틱 GrowthJob 인테리어 배치가 채운다. thrash 가드: (owner,셀,res)당 RedevelopCap
                //   한도 + 철거 시 attempts 리셋(새 구멍에 재배치 기회 → 블랙리스트에 안 걸림).
                _redevelopAttempts.TryGetValue(akey, out int redev);
                if (attemptsNow >= RedevelopEscalate && redev < RedevelopCap)
                {
                    redevelopReqs.Add(new int3(owner, whereCell.x, whereCell.y));
                    _redevelopAttempts[akey] = redev + 1;
                    _demandAttempts[akey]    = 0;
                }
            }

            cur.Dispose();
        }

        // NoCoverage 누적이 이 값을 넘으면 그 욕구의 해소 건물 1개 배치(틱당). 누적이라 실수요는
        //   빠르게 초과 — 너무 낮으면 초기 노이즈에 반응. 튜닝 대상(추후 GrowthConfig 노출).
        const int DemandActThreshold = 20;

        // 반복 무효 건설(둘러싸인 요청자) 차단: 같은 (owner,수요셀,res)에 이만큼 지어도 수요가 안 꺼지면
        //   블랙리스트. 낮으면 정상 성장도 조기 차단, 높으면 난사 허용 — 튜닝 대상.
        const int DemandAttemptCap = 3;

        // 철거-후-건설(재개발) 게이트 — 꽉 찬 도심의 미충족 수요를 주택 철거로 소화.
        //   RedevelopEscalate = 인테리어+프런티어가 이만큼 실패하면 주택 철거로 escalation(hysteresis).
        //   RedevelopCap      = (owner,셀,res)당 최대 철거 횟수(thrash 상한 — 도시 공동화 방지).
        const int RedevelopEscalate = 2;
        const int RedevelopCap      = 2;

        // (owner,resId) 누적 집계 — 합 + 우세(최대) 수요 셀.
        struct CurAgg { public int Sum; public int DomVal; public int2 DomCell; }

        // ⚠ 임시 하드코딩(검증용) — NeedMaps 재베이크/등록창 없이 demand→건물 루프를 확인하는 폴백.
        //   L2(정식 결정 테이블)가 비었을 때만 발동. "프리팹 접근 구도" 확정 시 제거.
        static int HardcodedNeedMainKey(int needBit) => needBit == 0 ? 1002 : 0;   // Hunger → restarant_h_small(1002)

        // ⚠ 임시 하드코딩(검증용) — commodity→producer. 능력(ProductionJob 출력)에서 파생표로 교체 예정.
        static int HardcodedCommodityProducer(Commodity c) => c switch
        {
            Commodity.Flour => 1003,   // 제분소 powder_h
            Commodity.Grain => 1004,   // 농장 farm_h
            _               => 0,
        };

        // case-b 앵커: 이 owner의 창고 중 target에 가장 가까운 것(footprint 원점). 없으면 false.
        //   풀이 공유라 "아무 창고나"면 접속되지만, 최근접이 커버 반경 안에 들 확률이 높다.
        static bool TryNearestWarehouse(
            int owner, int2 target, in NativeList<int3> warehouses, out int2 cell)
        {
            cell = default;
            long best = long.MaxValue;
            for (int i = 0; i < warehouses.Length; i++)
            {
                var w = warehouses[i];
                if (w.x != owner) continue;
                int dx = w.y - target.x, dy = w.z - target.y;
                long d = (long)dx * dx + (long)dy * dy;
                if (d < best) { best = d; cell = new int2(w.y, w.z); }
            }
            return best != long.MaxValue;
        }

        // resource id → 의존성 tier(원재료 0 < 중간재 1 < 최종/욕구 2). 낮을수록 먼저 짓는다.
        //   commodity는 CommodityDefs.TierOf, 욕구(최종 소비)는 Final 취급 → 상류가 하류보다 우선.
        static int ResourceTier(int resId)
            => DemandResource.IsWarehouse(resId) ? -1     // 창고 최우선(없으면 아무것도 안 흐름)
             : DemandResource.IsCommodity(resId) ? (int)CommodityDefs.TierOf(DemandResource.ToCommodity(resId))
             : (int)CommodityTier.Final;

        static int BlockSizeFor(int maxDim) => maxDim <= 4 ? 4 : maxDim <= 6 ? 6 : maxDim <= 8 ? 8 : 0;

        // ══════════════════════════════════════════════════════════════════════
        //  잡 입출력 타입
        // ══════════════════════════════════════════════════════════════════════

        struct TeamInput
        {
            public int      Owner;
            public CityGrid Grid;
        }

        // 성장 후보 건물 — 메타/입구를 메인에서 해석한 값 스냅샷. Key=0 = 없음.
        struct BuildOption
        {
            public int          Key;
            public int2         Size;
            public TerrainMask  BuildableOn;
            public int          BlockK;    // BlockSizeFor(max(Size)) 선계산
            public bool         HasEnt;
            public EntranceInfo Ent;
        }

        // Burst 잡 안에서 못 찍는 로그를 완료 후 메인에서 출력하기 위한 이벤트.
        struct GrowthLog
        {
            public const byte NoTeamRoad = 1;   // A,B 미사용
            public const byte NoSpot     = 2;   // A=K, B=유효후보 수
            public int  Owner;
            public byte Kind;
            public int  A, B;
        }

        // 잡 전용 레이어 복사본 — 필드명을 GridLayers와 맞춰 헬퍼 본문 변경 최소화.
        //   Water/자원은 셀 집합으로 선해석(룩업 체인 제거 + 조회 1회).
        struct GrowthSnap
        {
            public NativeHashMap<int2, TerrainCell>   TerrainLayer;
            public NativeHashMap<int2, RoadCell>      RoadLayer;
            public NativeHashMap<int2, OccupancyCell> OccupancyLayer;
            public NativeHashMap<int2, int>           TerritoryLayer;
            public NativeHashSet<int2>                ResourceBlocked;   // Amount>0 셀
            public NativeHashSet<int2>                WaterCells;        // CellType=Water 셀

            public static GrowthSnap Allocate(in GridLayers src)
            {
                int terrain = math.max(16, src.TerrainLayer.Count);
                return new GrowthSnap
                {
                    TerrainLayer   = new(terrain, Allocator.Persistent),
                    RoadLayer      = new(math.max(16, src.RoadLayer.Count),      Allocator.Persistent),
                    OccupancyLayer = new(math.max(16, src.OccupancyLayer.Count), Allocator.Persistent),
                    TerritoryLayer = new(math.max(16, src.TerritoryLayer.Count), Allocator.Persistent),
                    ResourceBlocked = new(math.max(16,
                        src.ResourceLayer.IsCreated ? src.ResourceLayer.Count : 0), Allocator.Persistent),
                    WaterCells     = new(terrain, Allocator.Persistent),
                };
            }

            public void Dispose()
            {
                TerrainLayer.Dispose();
                RoadLayer.Dispose();
                OccupancyLayer.Dispose();
                TerritoryLayer.Dispose();
                ResourceBlocked.Dispose();
                WaterCells.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ① SnapshotJob — 레이어 → 잡 전용 복사본 (빠름, state.Dependency 등록)
        // ══════════════════════════════════════════════════════════════════════
        [BurstCompile]
        struct SnapshotJob : IJob
        {
            [ReadOnly] public NativeHashMap<int2, TerrainCell>   SrcTerrain;
            [ReadOnly] public NativeHashMap<int2, RoadCell>      SrcRoad;
            [ReadOnly] public NativeHashMap<int2, OccupancyCell> SrcOcc;
            [ReadOnly] public NativeHashMap<int2, int>           SrcTerr;
            [ReadOnly] public NativeHashMap<int2, ResourceCell>  SrcRes;   // 미생성 시 빈 더미
            [ReadOnly] public NativeHashMap<int, CellTypeInfo>   SrcCellTypes;

            public GrowthSnap Snap;

            public void Execute()
            {
                foreach (var kv in SrcTerrain)
                {
                    Snap.TerrainLayer.TryAdd(kv.Key, kv.Value);
                    if (SrcCellTypes.TryGetValue(kv.Value.TypeId, out var ti)
                        && ti.TerrainCategory == TerrainCategory.Water)
                        Snap.WaterCells.Add(kv.Key);
                }
                foreach (var kv in SrcRoad) Snap.RoadLayer.TryAdd(kv.Key, kv.Value);
                foreach (var kv in SrcOcc)  Snap.OccupancyLayer.TryAdd(kv.Key, kv.Value);
                foreach (var kv in SrcTerr) Snap.TerritoryLayer.TryAdd(kv.Key, kv.Value);
                foreach (var kv in SrcRes)
                    if (kv.Value.Amount > 0) Snap.ResourceBlocked.Add(kv.Key);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ② GrowthJob — 팀별 성장 계산 (느림, 스냅샷만 읽음 → 프레임에 걸쳐 실행)
        // ══════════════════════════════════════════════════════════════════════
        [BurstCompile]
        struct GrowthJob : IJob
        {
            [ReadOnly] public GrowthSnap Snap;
            [ReadOnly] public NativeArray<TeamInput> Teams;
            [ReadOnly] public NativeArray<BuildOption> DemandOpts;   // 플레이어별 수요 건물(WHAT, Key=0=없음)
            [ReadOnly] public NativeArray<int2>       DemandCells;   // 플레이어별 수요 셀(STEP 3 WHERE 바이어스)
            public BuildOption  OptA, OptB;
            public GrowthConfig Cfg;
            public TeamTable    TeamsTable;
            public int          EnemyBuffer;
            public int          Day;

            public NativeList<PlaceRoadCommand>     RoadOut;
            public NativeList<PlaceBuildingRequest> BldOut;
            public NativeList<GrowthLog>            LogOut;

            public void Execute()
            {
                for (int t = 0; t < Teams.Length; t++)
                {
                    int owner = Teams[t].Owner;
                    var grid  = Teams[t].Grid;

                    var rng = Unity.Mathematics.Random.CreateFromIndex(
                        math.hash(new int2(Day + 1, owner + 1)) ^ grid.Seed);

                    // 구획 채우기 → 확장: 갇힌 구획을 격자 패킹으로 개발(DevelopParcels),
                    //   개발할 갇힌 구획이 없으면 바깥으로 블록 1개 확장(GrowOneBlock).
                    //   한 틱 건물 상한 = BuildPerTick. claimed로 같은 틱 중복 방지.
                    int budget  = math.max(1, Cfg.BuildPerTick);
                    var claimed = new NativeHashSet<int2>(128, Allocator.Temp);

                    // 도시 '바깥(프런티어)' 영역 = 도로/건물을 벽으로 막고 bbox 테두리에서 flood.
                    //   채우기는 enclosed(바깥 아님) 셀만 → 바깥 열린 땅은 도로 확장용으로 보존.
                    var outside = new NativeHashSet<int2>(512, Allocator.Temp);
                    ComputeEnclosureOutside(in Snap, owner, out int2 encLo, out int2 encHi, ref outside);

                    // "건설은 베이스-연결에서만"(행위 규칙) — 단절된 자기 도로 섬은 확장 앵커/입구로 못 쓴다.
                    //   섬 자체는 포위 상태로 존속(상태 허용) — 파괴/재연결은 janitor·capture 소관.
                    var baseReached = new NativeHashSet<int2>(256, Allocator.Temp);
                    ComputeBaseReached(in Snap, owner, grid.Anchor,
                        math.max(1, Cfg.BaseSeedRadius), ref baseReached);

                    // 0) 욕구 주도 배치(budget A=틱당 수요 건물 1개): STEP 3 WHERE — 수요 셀(굶주린
                    //    소비자 위치) 최근접 프런티어에 배치 → producer를 소비자 근처로 co-locate(물류·노동
                    //    연결). 수요 없음(Key=0)이면 건너뜀 → 아래 기존 로직 그대로(폴백, 회귀 0).
                    var demandOpt = DemandOpts[t];
                    bool builtDemand = false;
                    if (demandOpt.Key > 0)
                    {
                        // 구획 수요-결정(인테리어 우선): 수요 셀(소비자/창고) 최근접 '갇힌 빈 구획'에 먼저
                        //   배치 → 소비자 옆이라 좁은 커버로도 닿음(주택이 선점하기 전에 서비스가 점유,
                        //   전투로 뚫린 내부 구멍도 서비스로 복구). 빈 구획/유효 위치 없으면 프런티어 폴백(기존).
                        builtDemand = TryPlaceDemandInterior(in Snap, in grid, owner, in demandOpt,
                            DemandCells[t], in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                            in baseReached, ref claimed, ref BldOut);
                        if (!builtDemand)
                            builtDemand = GrowOneBlock(in Snap, in grid, owner, in demandOpt,
                                in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer, in baseReached,
                                DemandCells[t], true, ref rng, ref RoadOut, ref BldOut, ref LogOut);
                    }

                    // 1) 갇힌 구획 개발 (D1 구획 파생 + B 격자 패킹).
                    bool worked = DevelopParcels(in Snap, in grid, owner, in OptA, in OptB,
                        in outside, encLo, encHi, in TeamsTable, EnemyBuffer, in baseReached,
                        budget, ref claimed, ref BldOut, ref rng);

                    // 2) 갇힌 구획 없음 & 수요 건물도 안 지었으면 → 바깥 확장 1회(중복 프런티어 방지).
                    if (!worked && !builtDemand)
                    {
                        bool aOk = OptA.Key > 0, bOk = OptB.Key > 0;
                        BuildOption first, second;
                        if (aOk && bOk)
                        {
                            bool pickA = rng.NextBool();
                            first  = pickA ? OptA : OptB;
                            second = pickA ? OptB : OptA;
                        }
                        else { first = aOk ? OptA : OptB; second = default; }

                        bool grew = first.Key > 0 && GrowOneBlock(in Snap, in grid, owner, in first,
                            in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                            in baseReached, default, false, ref rng, ref RoadOut, ref BldOut, ref LogOut);
                        if (!grew && second.Key > 0 && second.Key != first.Key)
                            GrowOneBlock(in Snap, in grid, owner, in second,
                                in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                                in baseReached, default, false, ref rng, ref RoadOut, ref BldOut, ref LogOut);
                    }

                    claimed.Dispose();
                    outside.Dispose();
                    baseReached.Dispose();
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  성장 로직 (전부 Burst 잡 안에서 실행 — 스냅샷 전용, 관리형 API 금지)
        // ══════════════════════════════════════════════════════════════════════

        // 방향 인덱스 0~3 → 오프셋 (N,E,S,W). RoadDirOps.Offsets(managed 배열)의 Burst-안전 판.
        static int2 Dir4(int d) => d switch
        {
            0 => new int2(0, 1),
            1 => new int2(1, 0),
            2 => new int2(0, -1),
            _ => new int2(-1, 0),
        };

        static bool GrowOneBlock(
            in GrowthSnap layers, in CityGrid grid, int owner, in BuildOption opt,
            in GrowthConfig cfg,
            in NativeHashSet<int2> outside, int2 encLo, int2 encHi, in TeamTable teams, int enemyBuffer,
            in NativeHashSet<int2> baseReached,
            int2 biasCell, bool hasBias,
            ref Unity.Mathematics.Random rng,
            ref NativeList<PlaceRoadCommand> roadOut, ref NativeList<PlaceBuildingRequest> bldOut,
            ref NativeList<GrowthLog> logOut)
        {
            int Road = math.max(1, grid.Road);
            int K = opt.BlockK;   // BlockSizeFor(max(Size)) — 무효 키는 Resolve에서 걸러짐

            if (!TeamRoadBounds(in layers, owner, out int2 rmin, out int2 rmax))
            { logOut.Add(new GrowthLog { Owner = owner, Kind = GrowthLog.NoTeamRoad }); return false; }
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
                // 행위 규칙: 확장 앵커는 '베이스-연결' 도로만 — 단절 섬에서 제2 도시 증식 금지.
                if (!baseReached.Contains(kv.Value.FootprintOrigin)) continue;
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
                    if (!BlockValid(in layers, O, K, Road, owner, in teams, enemyBuffer)) continue;
                    if (cfg.RejectParallelSeam && IsParallelSeam(O, K, Road, owner, in layers)) continue;
                    // 바깥-전용 확장: 블록 내부가 '갇힌(enclosed)' 포켓이면 거부(중첩 방지).
                    //   갇힌 빈 구획은 채우기가 담당, 확장은 바깥 프런티어로만.
                    if (!InteriorExterior(O, K, in outside, encLo, encHi)) continue;

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
                        Dist  = hasBias
                            ? math.abs(O.x + K * 0.5f - biasCell.x) + math.abs(O.y + K * 0.5f - biasCell.y)
                            : math.abs(O.x + K * 0.5f - baseC.x)    + math.abs(O.y + K * 0.5f - baseC.y),
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
                {
                    bool better = hasBias
                        ? cands[i].Dist < cands[bi].Dist    // WHERE 바이어스: 수요셀 최근접 우선
                        : Better(cands[i], cands[bi]);
                    if (better) bi = i;
                }
                int2 pick = cands[bi].O;
                cands.RemoveAtSwapBack(bi);
                if (DevelopBlock(in layers, in grid, pick, K, Road, owner, in opt,
                        ref roadOut, ref bldOut, ref rng))
                { grew = true; break; }
            }
            cands.Dispose();
            if (grew) return true;

            logOut.Add(new GrowthLog { Owner = owner, Kind = GrowthLog.NoSpot, A = K, B = nValid });
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
        static bool HasEmptyNeighborFootprint(int2 fo, int rs, in GrowthSnap layers)
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
        static int RoadFootprintMask(int2 fo, int rs, in GrowthSnap layers, int owner)
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

        // 블록 내부(K×K)가 모두 '바깥(exterior)'인가 — 윈도우 밖이거나 outside 집합 소속.
        //   하나라도 enclosed(윈도우 안 & outside 아님)면 false → 확장이 갇힌 포켓에 안 들어감.
        static bool InteriorExterior(int2 O, int K, in NativeHashSet<int2> outside, int2 encLo, int2 encHi)
        {
            if (!outside.IsCreated) return true;   // enclosure 미계산 시 통과(안전)
            for (int dz = 0; dz < K; dz++)
            for (int dx = 0; dx < K; dx++)
            {
                int2 c = O + new int2(dx, dz);
                bool outOfWindow = c.x < encLo.x || c.x > encHi.x || c.y < encLo.y || c.y > encHi.y;
                if (!outOfWindow && !outside.Contains(c)) return false;   // enclosed 포켓
            }
            return true;
        }

        // ── 블록 개발: 도로 링 + 건물 1개 ────────────────────────────────────
        static bool DevelopBlock(
            in GrowthSnap layers, in CityGrid grid, int2 O, int K, int Road,
            int owner, in BuildOption opt,
            ref NativeList<PlaceRoadCommand> roadOut, ref NativeList<PlaceBuildingRequest> bldOut,
            ref Unity.Mathematics.Random rng)
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

            bool placed = TryPlaceBuildingInSpan(in layers, O, K,
                in opt, in plannedRoad, out int2 bOrigin, out float bRot, ref rng);

            if (placed)
            {
                for (int i = 0; i < roadOrigins.Length; i++)
                {
                    roadOut.Add(new PlaceRoadCommand
                    {
                        Cell = roadOrigins[i], OwnerLocalId = owner, LaneCount = 2,
                        FactionId = grid.FactionId, Size = (byte)Road,
                        Axis       = useDrawn ? RoadPlacedAxis.Any : roadAxis[i],
                        Directions = useDrawn ? roadDirs[i] : RoadDir.None,
                    });
                }

                bldOut.Add(new PlaceBuildingRequest
                {
                    MainKey = opt.Key, VariantKey = 0, Cell = bOrigin, RotationY = bRot,
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
            in GrowthSnap layers, int2 O, int K, int Road,
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
            int2 origin, RoadPlacedAxis axis, int Road, in GrowthSnap layers,
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
            in GrowthSnap layers, int2 spanOrigin, int spanSize,
            in BuildOption opt,
            in NativeHashSet<int2> plannedRoad, out int2 bestOrigin, out float bestRot,
            ref Unity.Mathematics.Random rng)
        {
            bestOrigin = default; bestRot = 0f;
            int2 sz = opt.Size;
            int stepCount = opt.HasEnt ? 4 : 1;
            // 유효한 (위치·회전) 후보를 전부 훑어 균등 랜덤 1개 선택(reservoir) — 첫-fit 획일 배치 탈피.
            //   유효성 검사는 기존 그대로(입구 도로닿음·평탄·맵안), 선택만 무작위. 시드 결정적.
            int cnt = 0;
            for (int cz = 0; cz < spanSize; cz++)
            for (int cx = 0; cx < spanSize; cx++)
            {
                int2 origin = spanOrigin + new int2(cx, cz);
                for (int steps = 0; steps < stepCount; steps++)
                {
                    int2 eff = EntranceOps.RotateSize(sz, steps);
                    if (cx + eff.x > spanSize || cz + eff.y > spanSize) continue;
                    if (!FootprintBuildableFlat(origin, eff, opt.BuildableOn, in layers)) continue;
                    if (opt.HasEnt)
                    {
                        int2 erc = EntranceOps.EntranceRoadCell(origin, sz, in opt.Ent, steps);
                        if (!layers.RoadLayer.ContainsKey(erc) && !plannedRoad.Contains(erc)) continue;
                    }
                    cnt++;
                    if (rng.NextInt(cnt) == 0)
                    {
                        bestOrigin = origin;
                        bestRot    = EntranceOps.StepsToRotationY(steps);
                    }
                }
            }
            return cnt > 0;
        }

        // 셀이 적 영역/경합지이거나 그로부터 buffer(셀) 안인가 — AI 확장 완충 게이트.
        //   정확한 팽창(dilation) 대신 8방향 buffer 지점 샘플 근사(저비용, DayChanged 저빈도라 충분).
        static bool NearEnemyOrContested(
            in GrowthSnap layers, int2 c, int owner, in TeamTable teams, int buffer)
        {
            if (TerritoryOps.InEnemyTerritory(in layers.TerritoryLayer, c, owner, in teams)
                || TerritoryOps.IsContested(in layers.TerritoryLayer, c)) return true;
            if (buffer <= 0) return false;

            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int2 s = c + new int2(dx, dz) * buffer;
                if (TerritoryOps.InEnemyTerritory(in layers.TerritoryLayer, s, owner, in teams)
                    || TerritoryOps.IsContested(in layers.TerritoryLayer, s)) return true;
            }
            return false;
        }

        static bool FootprintTouchesTeamRoad(int2 origin, int2 eff, int owner, in GrowthSnap layers)
        {
            for (int dz = 0; dz < eff.y; dz++)
            for (int dx = 0; dx < eff.x; dx++)
            {
                int2 c = origin + new int2(dx, dz);
                for (int d = 0; d < 4; d++)
                    if (IsTeamRoad(c + Dir4(d), in layers, owner)) return true;
            }
            return false;
        }

        // ── 구획 수요-결정(인테리어): 수요 건물 opt를 '갇힌 빈 구획' 중 target 최근접 유효 위치에 1개 배치.
        //   소비자/창고(target) 옆에 직접 지어 좁은 커버로도 닿게 — 빈 구멍(전투 파괴 포함)·미개발 포켓을
        //   주택이 무차별로 선점하기 전에 서비스로 채운다. 빈 구획/유효 위치 없으면 false(→ 프런티어 폴백).
        //   claimed에 등록 → 뒤이은 주택 채우기(DevelopParcels)가 이 셀을 건너뛴다(같은 구획에 서비스+주택 혼합).
        static bool TryPlaceDemandInterior(
            in GrowthSnap layers, in CityGrid grid, int owner, in BuildOption opt, int2 target,
            in NativeHashSet<int2> outside, int2 encLo, int2 encHi, in TeamTable teams, int enemyBuffer,
            in NativeHashSet<int2> baseReached,
            ref NativeHashSet<int2> claimed, ref NativeList<PlaceBuildingRequest> bldOut)
        {
            if (opt.Key <= 0) return false;
            if (encHi.x < encLo.x || encHi.y < encLo.y) return false;

            // 갇힌 빈 셀 수집(DevelopParcels와 동일 기준).
            var enc = new NativeHashSet<int2>(512, Allocator.Temp);
            for (int z = encLo.y; z <= encHi.y; z++)
            for (int x = encLo.x; x <= encHi.x; x++)
            {
                int2 c = new int2(x, z);
                if (outside.Contains(c)) continue;
                if (!CellBuildable(c, in layers)) continue;
                if (NearEnemyOrContested(in layers, c, owner, in teams, enemyBuffer)) continue;
                if (layers.WaterCells.Contains(c)) continue;
                enc.Add(c);
            }
            if (enc.IsEmpty) { enc.Dispose(); return false; }

            int2 sz = opt.Size;
            int stepCount = opt.HasEnt ? 4 : 1;

            bool found = false;
            float bestDist = float.MaxValue;
            int2 bestOrigin = default; int bestSteps = 0; int2 bestEff = default;

            foreach (var origin in enc)
            {
                for (int steps = 0; steps < stepCount; steps++)
                {
                    int2 eff = EntranceOps.RotateSize(sz, steps);

                    // footprint 전체가 enc 안 + 미claimed.
                    bool ok = true;
                    for (int dz = 0; dz < eff.y && ok; dz++)
                    for (int dx = 0; dx < eff.x && ok; dx++)
                    {
                        int2 c = origin + new int2(dx, dz);
                        if (!enc.Contains(c) || claimed.Contains(c)) ok = false;
                    }
                    if (!ok) continue;
                    if (!FootprintBuildableFlat(origin, eff, opt.BuildableOn, in layers)) continue;

                    if (opt.HasEnt)
                    {
                        if (!EntranceOps.IsEntranceOnOwnRoad(origin, sz, in opt.Ent, steps, in layers.RoadLayer, owner)) continue;
                        int2 erc = EntranceOps.EntranceRoadCell(origin, sz, in opt.Ent, steps);
                        if (layers.RoadLayer.TryGetValue(erc, out var ercRc)
                            && !baseReached.Contains(ercRc.FootprintOrigin)) continue;
                    }
                    else if (!FootprintTouchesTeamRoad(origin, eff, owner, in layers)) continue;

                    // target(소비자/창고) 최근접 우선.
                    float cx = origin.x + eff.x * 0.5f, cz = origin.y + eff.y * 0.5f;
                    float d = math.abs(cx - target.x) + math.abs(cz - target.y);
                    if (d < bestDist)
                    { bestDist = d; bestOrigin = origin; bestSteps = steps; bestEff = eff; found = true; }
                }
            }
            enc.Dispose();

            // 너무 먼 구멍은 target(소비자/창고)을 커버 못 함 → 프런티어 폴백이 나음(튜닝 상수).
            const float InteriorMaxDist = 32f;
            if (!found || bestDist > InteriorMaxDist) return false;

            bldOut.Add(new PlaceBuildingRequest
            {
                MainKey = opt.Key, VariantKey = 0, Cell = bestOrigin,
                RotationY = EntranceOps.StepsToRotationY(bestSteps),
                OwnerLocalId = owner, FactionId = grid.FactionId, RequireRoadAccess = true,
            });
            for (int dz = 0; dz < bestEff.y; dz++)
            for (int dx = 0; dx < bestEff.x; dx++)
                claimed.Add(bestOrigin + new int2(dx, dz));
            return true;
        }

        // ── D1+B: 갇힌 빈 구획 개발 ──────────────────────────────────────────
        //  enclosed empty 셀(=outside 아님, buildable, Land)을 4-연결 '구획'으로 묶고(D1):
        //    · B: 구획 bbox 원점 기준 격자로 건물 패킹(큰 plot 먼저, 작은 나중 = 혼합).
        //         → 4×4 구획에 2×2 4개 정확히. 격자 정렬이라 빈틈/낭비 없음.
        //    · (C1 골목 분할은 폐기(2026-07-01) — 도로 안 닿는 깊은 셀은 빈 공터로 남긴다.)
        //  반환: 건물을 하나라도 발행했으면 true.
        static bool DevelopParcels(
            in GrowthSnap layers, in CityGrid grid, int owner,
            in BuildOption optA, in BuildOption optB,
            in NativeHashSet<int2> outside, int2 encLo, int2 encHi, in TeamTable teams, int enemyBuffer,
            in NativeHashSet<int2> baseReached,
            int budget, ref NativeHashSet<int2> claimed, ref NativeList<PlaceBuildingRequest> bldOut,
            ref Unity.Mathematics.Random rng)
        {
            if (encHi.x < encLo.x || encHi.y < encLo.y) return false;   // 도로 없음

            // enclosed empty 셀 수집
            var enc = new NativeHashSet<int2>(512, Allocator.Temp);
            for (int z = encLo.y; z <= encHi.y; z++)
            for (int x = encLo.x; x <= encHi.x; x++)
            {
                int2 c = new int2(x, z);
                if (outside.Contains(c)) continue;
                if (!CellBuildable(c, in layers)) continue;                 // 도로/건물/자원/맵밖 제외
                if (NearEnemyOrContested(in layers, c, owner, in teams, enemyBuffer)) continue;
                if (layers.WaterCells.Contains(c)) continue;
                enc.Add(c);
            }
            if (enc.IsEmpty) { enc.Dispose(); return false; }

            // plot 크게→작게 (혼합): 두 키 중 큰 것 먼저.
            BuildOption optBig = optB, optSmall = optA;
            if (optA.Key > 0 && optB.Key > 0
                && math.max(optA.Size.x, optA.Size.y) > math.max(optB.Size.x, optB.Size.y))
            { optBig = optA; optSmall = optB; }

            bool didAny = false;
            int placed = 0;

            var visited = new NativeHashSet<int2>(512, Allocator.Temp);
            var q = new NativeQueue<int2>(Allocator.Temp);

            foreach (var seed in enc)
            {
                if (!visited.Add(seed)) continue;

                // 구획(연결요소) BFS + bbox
                q.Clear(); q.Enqueue(seed);
                int2 mn = seed, mx = seed;
                while (q.TryDequeue(out int2 cur))
                {
                    mn = math.min(mn, cur); mx = math.max(mx, cur);
                    for (int d = 0; d < 4; d++)
                    {
                        int2 nb = cur + Dir4(d);
                        if (enc.Contains(nb) && visited.Add(nb)) q.Enqueue(nb);
                    }
                }

                // B: 격자 패킹 (큰 plot 먼저, 작은 나중)
                if (placed < budget)
                    placed += PackOnGrid(in optBig, mn, mx, in enc, in layers,
                        in grid, owner, in baseReached, budget - placed, ref claimed, ref bldOut, ref rng);
                if (placed < budget)
                    placed += PackOnGrid(in optSmall, mn, mx, in enc, in layers,
                        in grid, owner, in baseReached, budget - placed, ref claimed, ref bldOut, ref rng);
                if (placed > 0) didAny = true;

                if (placed >= budget) break;
            }

            q.Dispose(); visited.Dispose(); enc.Dispose();
            return didAny;
        }

        // 구획 bbox를 plot(정사각 P) 격자로 채운다 — footprint가 구획(enc) 안 + 미예약 + 입구 도로닿음.
        static int PackOnGrid(
            in BuildOption opt, int2 mn, int2 mx, in NativeHashSet<int2> enc,
            in GrowthSnap layers, in CityGrid grid, int owner,
            in NativeHashSet<int2> baseReached, int budget,
            ref NativeHashSet<int2> claimed, ref NativeList<PlaceBuildingRequest> bldOut,
            ref Unity.Mathematics.Random rng)
        {
            if (opt.Key <= 0 || budget <= 0) return 0;
            int2 sz = opt.Size;
            int P = math.max(sz.x, sz.y);
            int stepCount = opt.HasEnt ? 4 : 1;

            int count = 0;
            for (int gz = mn.y; gz + P - 1 <= mx.y; gz += P)
            for (int gx = mn.x; gx + P - 1 <= mx.x; gx += P)
            {
                if (count >= budget) return count;
                int2 origin = new int2(gx, gz);

                // 격자 위치는 무겹침 위해 고정, 회전만 유효 후보 중 균등 랜덤(reservoir) — 방향 획일성 탈피.
                int chosenSteps = -1; int2 chosenEff = default; int cnt = 0;
                for (int steps = 0; steps < stepCount; steps++)
                {
                    int2 eff = EntranceOps.RotateSize(sz, steps);
                    if (!FootprintBuildableFlat(origin, eff, opt.BuildableOn, in layers))
                        continue;

                    bool ok = true;
                    for (int dz = 0; dz < eff.y && ok; dz++)
                    for (int dx = 0; dx < eff.x && ok; dx++)
                    {
                        int2 c = origin + new int2(dx, dz);
                        if (!enc.Contains(c) || claimed.Contains(c)) ok = false;
                    }
                    if (!ok) continue;

                    if (opt.HasEnt)
                    {
                        if (!EntranceOps.IsEntranceOnOwnRoad(origin, sz, in opt.Ent, steps, in layers.RoadLayer, owner)) continue;
                        // 행위 규칙: 입구 도로는 '베이스-연결'이어야 — 단절 섬 내부 개발 금지(포위 상태 존중).
                        int2 erc = EntranceOps.EntranceRoadCell(origin, sz, in opt.Ent, steps);
                        if (layers.RoadLayer.TryGetValue(erc, out var ercRc)
                            && !baseReached.Contains(ercRc.FootprintOrigin)) continue;
                    }
                    else if (!FootprintTouchesTeamRoad(origin, eff, owner, in layers)) continue;

                    cnt++;
                    if (rng.NextInt(cnt) == 0) { chosenSteps = steps; chosenEff = eff; }
                }
                if (chosenSteps < 0) continue;   // 이 격자 칸에 유효 회전 없음 → 다음 칸

                bldOut.Add(new PlaceBuildingRequest
                {
                    MainKey = opt.Key, VariantKey = 0, Cell = origin,
                    RotationY = EntranceOps.StepsToRotationY(chosenSteps),
                    OwnerLocalId = owner, FactionId = grid.FactionId, RequireRoadAccess = true,
                });
                for (int dz = 0; dz < chosenEff.y; dz++)
                for (int dx = 0; dx < chosenEff.x; dx++)
                    claimed.Add(origin + new int2(dx, dz));
                count++;
            }
            return count;
        }

        // 도시 '바깥(프런티어)' 셀 집합 계산 — 팀 도로/건물을 벽으로 막고 bbox 테두리에서 flood.
        //   outside에 안 든 (윈도우 안) 셀 = enclosed(도로로 둘러싸인 안쪽). 채우기는 그쪽만.
        //   → 바깥 열린 땅은 도로 확장(DevelopBlock)용으로 비워둬 자기-포위(stop)를 막는다.
        static void ComputeEnclosureOutside(
            in GrowthSnap layers, int owner, out int2 lo, out int2 hi, ref NativeHashSet<int2> outside)
        {
            lo = int2.zero; hi = int2.zero;
            if (!TeamRoadBounds(in layers, owner, out int2 mn, out int2 mx)) return;
            const int M = 8;                 // 큰 건물(stock 5) footprint + 여유
            lo = mn - M; hi = mx + M;

            var q = new NativeQueue<int2>(Allocator.Temp);

            for (int x = lo.x; x <= hi.x; x++)
            {
                TrySeedOutside(in layers, owner, new int2(x, lo.y), ref outside, ref q);
                TrySeedOutside(in layers, owner, new int2(x, hi.y), ref outside, ref q);
            }
            for (int y = lo.y; y <= hi.y; y++)
            {
                TrySeedOutside(in layers, owner, new int2(lo.x, y), ref outside, ref q);
                TrySeedOutside(in layers, owner, new int2(hi.x, y), ref outside, ref q);
            }

            while (q.TryDequeue(out int2 c))
                for (int d = 0; d < 4; d++)
                {
                    int2 n = c + Dir4(d);
                    if (n.x < lo.x || n.x > hi.x || n.y < lo.y || n.y > hi.y) continue;
                    TrySeedOutside(in layers, owner, n, ref outside, ref q);
                }

            q.Dispose();
        }

        // 벽(팀 도로/건물)이 아니면 outside에 추가하고 큐에 넣는다(이미 있으면 무시).
        static void TrySeedOutside(in GrowthSnap layers, int owner, int2 s,
            ref NativeHashSet<int2> outside, ref NativeQueue<int2> q)
        {
            if (IsTeamRoad(s, in layers, owner) || IsTeamBuilding(s, in layers, owner)) return;
            if (outside.Add(s)) q.Enqueue(s);
        }

        // ── 유효성 / 모서리 판정 ─────────────────────────────────────────────

        // footprint(내부 K + 도로 링 Road) 전체 유효? 맵 안·같은 높이·Land·내부 빈·링 빈/팀도로.
        static bool BlockValid(
            in GrowthSnap layers, int2 O, int K, int Road, int owner,
            in TeamTable teams, int enemyBuffer)
        {
            int2 ro = O - new int2(Road, Road);
            int  rs = K + 2 * Road;

            bool first = true; byte baseH = 0;
            for (int dz = 0; dz < rs; dz++)
            for (int dx = 0; dx < rs; dx++)
            {
                int2 c = ro + new int2(dx, dz);
                if (!layers.TerrainLayer.TryGetValue(c, out var tc)) return false;        // 맵 밖
                if (layers.WaterCells.Contains(c)) return false;                           // 물
                if (first) { baseH = tc.Height; first = false; }
                else if (tc.Height != baseH) return false;                                 // 단차

                bool inInterior = dx >= Road && dx < Road + K && dz >= Road && dz < Road + K;

                // Territory 게이트 — 적 영역만 피한다(자기 영역엔 자유롭게 채우고 확장).
                //   ※ 예전 '내부는 어떤 영역이라도 거부'는 자기 베이스가 영역을 만들면
                //     자기 영역 위에서 확장이 막혀 정지하는 버그가 있었음 → 적 영역 전용으로 완화.
                //   완충(enemyBuffer): 국경 바로 옆에 지으면 다음 잠식에 파괴(churn) → 여유 유지.
                if (NearEnemyOrContested(in layers, c, owner, in teams, enemyBuffer)) return false;

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
        static int SideMassMask(int2 O, int K, int Road, int owner, in GrowthSnap layers)
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
        static int RingShareScore(int2 O, int K, int Road, int owner, in GrowthSnap layers)
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
        static bool IsParallelSeam(int2 O, int K, int Road, int owner, in GrowthSnap layers)
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

        static bool LineMass(int2 start, int2 step, int count, int owner, in GrowthSnap layers)
        {
            for (int i = 0; i < count; i++)
                if (IsMass(start + step * i, in layers, owner)) return true;
            return false;
        }

        static bool IsMass(int2 c, in GrowthSnap layers, int owner)
            => IsTeamRoad(c, in layers, owner) || IsTeamBuilding(c, in layers, owner);

        // 베이스(Anchor 반경 8)에서 도로로 도달 가능한 자기 도로 footprint 집합.
        //   "건설은 베이스-연결에서만"(행위 규칙) — 전투/점령으로 단절된 자기 도로 섬은
        //   확장 앵커/입구로 쓰지 않는다(섬은 포위 '상태'로 존속 — 상태는 허용).
        static void ComputeBaseReached(
            in GrowthSnap layers, int owner, int2 anchor, int seedR, ref NativeHashSet<int2> reached)
        {
            var visited = new NativeHashSet<int2>(256, Allocator.Temp);
            var q       = new NativeQueue<int2>(Allocator.Temp);

            for (int dz = -seedR; dz <= seedR; dz++)
            for (int dx = -seedR; dx <= seedR; dx++)
            {
                int2 c = anchor + new int2(dx, dz);
                if (layers.RoadLayer.TryGetValue(c, out var rc) && rc.OwnerLocalId == owner
                    && visited.Add(c))
                { q.Enqueue(c); reached.Add(rc.FootprintOrigin); }
            }
            while (q.TryDequeue(out int2 cur))
                for (int d = 0; d < 4; d++)
                {
                    int2 n = cur + Dir4(d);
                    if (layers.RoadLayer.TryGetValue(n, out var rc) && rc.OwnerLocalId == owner
                        && visited.Add(n))
                    { q.Enqueue(n); reached.Add(rc.FootprintOrigin); }
                }

            visited.Dispose(); q.Dispose();
        }

        static bool TeamRoadBounds(in GrowthSnap layers, int owner, out int2 mn, out int2 mx)
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

        static bool IsTeamRoad(int2 c, in GrowthSnap layers, int owner)
            => layers.RoadLayer.TryGetValue(c, out var rc) && rc.OwnerLocalId == owner;

        static bool IsTeamBuilding(int2 c, in GrowthSnap layers, int owner)
            => layers.OccupancyLayer.TryGetValue(c, out var occ)
            && occ.Type == OccupantType.Building && occ.OwnerLocalId == owner;

        static bool CellBuildable(int2 c, in GrowthSnap layers)
        {
            if (!layers.TerrainLayer.ContainsKey(c)) return false;
            if (layers.RoadLayer.ContainsKey(c))     return false;
            if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                && occ.Type != OccupantType.Environment) return false;
            if (layers.ResourceBlocked.Contains(c)) return false;
            return true;
        }

        static bool FootprintBuildableFlat(
            int2 origin, int2 sz, TerrainMask buildableOn,
            in GrowthSnap layers)
        {
            bool first = true; byte baseH = 0;
            for (int dx = 0; dx < sz.x; dx++)
            for (int dz = 0; dz < sz.y; dz++)
            {
                int2 c = origin + new int2(dx, dz);
                if (!layers.TerrainLayer.TryGetValue(c, out var tc)) return false;
                if (!CellBuildable(c, in layers)) return false;
                var mask = layers.WaterCells.Contains(c) ? TerrainMask.Water : TerrainMask.Land;
                if ((buildableOn & mask) == 0) return false;
                if (first) { baseH = tc.Height; first = false; }
                else if (tc.Height != baseH) return false;
            }
            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  GrowthConfig — AI 성장 밸런스 (싱글톤, 없으면 Default)
    //  Test.cs가 인스펙터 값을 매 프레임 push(통합 밸런스 패널) — 변경 즉시 반영.
    // ══════════════════════════════════════════════════════════════════════════
    public struct GrowthConfig : IComponentData
    {
        public int BuildingKeyA;
        public int BuildingKeyB;

        // 확장 편향 데드밴드(셀): Anchor 기준 |extX-extZ|가 이 값 이하이면 균형으로 보고
        //   편향 교정을 끄고 품질(오목/공유)로 채운다. 블록 한 변(~4-8)보다 커야 좌우 떨림 방지.
        public int BalanceDeadband;

        // 평행 seam 거부: 기존 도로와 공유 없이 한 칸 평행으로 깔리는 블록을 차단.
        public bool RejectParallelSeam;

        // 한 틱(DayChanged)에 팀별 최대 배치 수. 스코어러가 공유(cohesion) 자리를 먼저
        //   채우므로, 클수록 현재 지구가 빠르게 빽빽이 차고 막히면 바깥으로 확장된다.
        public int BuildPerTick;

        // "베이스-연결"(행위 규칙) 판정의 시드 반경(셀, Chebyshev — Anchor 주변 이 반경 안
        //   자기 도로에서 flood 시작). 성장 앵커/입구 게이트와 janitor(트림·섬·지선)가 공유
        //   — 두 시스템이 같은 값을 봐야 "연결" 판정이 어긋나지 않는다.
        public int BaseSeedRadius;

        public static GrowthConfig Default => new GrowthConfig
        {
            BuildingKeyA        = 1004,
            BuildingKeyB        = 1005,
            BalanceDeadband     = 8,
            RejectParallelSeam  = true,
            BuildPerTick        = 6,
            BaseSeedRadius      = 8,
        };
    }
}
