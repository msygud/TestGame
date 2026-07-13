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
        NativeArray<BuildOption>         _whDemandOpts; // 플레이어별 **창고 전용 레인**(2026-07-11 — 수요 슬롯과
        NativeArray<int2>                _whDemandCells;//   경쟁 없이 병행 배치: 확장 추격이 식당·생산자에 안 밀림)
        NativeHashMap<int3, int>         _whCovered;    // (owner,x,y) 창고 커버 도로셀 → **잔여 깊이**(MaxDist−Dist,
                                                        //   셀별 최대) — 계획 C 배치 검증 + 신설 도로 커버 근사가
                                                        //   "잔여 ≥ 우회 거리"를 요구(경계 셀 옆 오통과 픽스,
                                                        //   2026-07-10 미커버 농장 실측). 런마다 메인에서 복사.
        // 지구 그리드(2026-07-11 배치 그리드 + 전략 평가) — 런별 파생(지속 부기 없음 = drift 없음).
        NativeHashMap<int2, DistrictStat> _districts;    // DistrictSurveyJob 산출(지구 테이블 back)
        NativeHashSet<int3>               _whDistricts;  // (owner,dx,dy) 창고 보유 지구
        NativeHashSet<int3>               _supDistricts; // (owner,dx,dy) 욕구 공급자(StampSupplier) 보유 지구
        NativeHashSet<int3>               _auraDistricts;// (owner,dx,dy) 오라 공급자(AuraSupplier) 보유 지구
        NativeArray<int3>                 _targetOut;    // 팀별 확장 목표 지구(x,y,score). x=int.MinValue=없음

        // owner → 예약된 창고 건설 앵커(계획 B 확장): 생산자가 "창고 커버 안 유효 후보 없음"으로 배치
        //   실패하면 다음 틱은 **반드시 창고 먼저**(커버 확장) — 유저 합의 "예약하고 다음 건설". 세션 지속.
        NativeHashMap<int, int2> _pendingWarehouse;
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

        // (owner,수요셀,resId) → 채널 재기준선 시점의 셀별 누적 NoCoverage 스냅샷(2026-07-12).
        //   셀 선택을 "누적 최대"가 아니라 **"재기준선 이후 신규 미스 최대"**로 — 누적은 감쇠가
        //   없어서 이미 해소(커버)된 셀의 역사적 더미가 우세를 영구 독점, 커버된 자리 옆에
        //   반복 건설(유저 실측: 경찰서 인접 3개 = attempts 지문, 주거 붐이 더미를 증폭).
        //   갱신 시점 = 채널 재기준선(쿨다운 만료)과 동일 — 합계·셀 기준선의 의미 일치. 세션 지속.
        NativeHashMap<int4, int> _cellSeen;

        // (owner,수요셀,resId) → 철거-후-건설(재개발) 횟수. 인테리어+프런티어가 여러 번 실패한 꽉 찬 도심에서
        //   주택을 철거해 구멍을 낸 횟수. (owner,셀,res)당 RedevelopCap 한도(thrash 방지). 세션 지속.
        NativeHashMap<int4, int> _redevelopAttempts;

        // (owner,resId) → 풀 경로(commodity) 증설 쿨다운 만료 게임시각(P v2). 새 생산자의 효과
        //   (고용→생산→풀 유입)가 흐름 창에 반영되기까지 재증설 금지 — 기준선 개념이 없는 풀 전용.
        NativeHashMap<int2, double> _poolCooldown;

        // 지구(dx,dy) → 전일 도로 owner 비트마스크. 세션 지속 — 서베이가 차분으로
        //   FrontOwners(확장 프런트 = 어제 없던 도로 owner)를 계산(전략 경쟁항 입력).
        NativeHashMap<int2, byte> _prevRoadOwners;

        // (owner,resId) → 기준선 재설정 예정 게임시각(TotalSeconds). 배치 쿨다운(2026-07-10):
        //   건설 → stamp 재빌드(HourChanged 라운드로빈)·풀 용량 재계산(HourChanged) 반영까지의 공백 동안
        //   쌓인 샘플이 다음 틱 delta를 다시 임계 위로 밀어 올려 같은 수요를 재건설(관찰: 창고 6연접).
        //   반영 창 동안 그 (owner,res) 후보 제외 → 만료 첫 틱에 기준선을 현재 합으로 재설정(lag 샘플
        //   폐기) → 이후 '반영 후' 새로 쌓인 수요만 판단. 세션 지속.
        NativeHashMap<int2, double> _demandRebaselineAt;

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
            _cellSeen = new NativeHashMap<int4, int>(256, Allocator.Persistent);
            _redevelopAttempts = new NativeHashMap<int4, int>(64, Allocator.Persistent);
            _demandRebaselineAt = new NativeHashMap<int2, double>(64, Allocator.Persistent);
            _poolCooldown = new NativeHashMap<int2, double>(64, Allocator.Persistent);
            _pendingWarehouse = new NativeHashMap<int, int2>(8, Allocator.Persistent);
            _prevRoadOwners = new NativeHashMap<int2, byte>(256, Allocator.Persistent);

            // 지구 테이블 front 싱글톤(수명주기 패턴) — 서베이 잡 완료 후 발행, 오버레이/앵커가 읽음.
            if (!SystemAPI.HasSingleton<DistrictTable>())
            {
                var e = state.EntityManager.CreateEntity(typeof(DistrictTable));
                state.EntityManager.SetComponentData(e, new DistrictTable
                {
                    Stats   = new NativeHashMap<int2, DistrictStat>(256, Allocator.Persistent),
                    Targets = new NativeHashMap<int, int2>(8, Allocator.Persistent),
                    Version = 0,
                });
            }
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
            if (_cellSeen.IsCreated) _cellSeen.Dispose();
            if (_redevelopAttempts.IsCreated) _redevelopAttempts.Dispose();
            if (_demandRebaselineAt.IsCreated) _demandRebaselineAt.Dispose();
            if (_poolCooldown.IsCreated) _poolCooldown.Dispose();
            if (_pendingWarehouse.IsCreated) _pendingWarehouse.Dispose();
            if (_prevRoadOwners.IsCreated) _prevRoadOwners.Dispose();
            if (SystemAPI.HasSingleton<DistrictTable>())
            {
                var dt = SystemAPI.GetSingleton<DistrictTable>();
                if (dt.Stats.IsCreated) dt.Stats.Dispose();
                if (dt.Targets.IsCreated) dt.Targets.Dispose();
            }
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

            // 계획 E(주택 점유율 게이트, 2026-07-11): "하루 구역 신설 + 주택 다채움"(초기 확장 테스트용)
            //   은퇴 — 주택 채움·주택용 확장은 거주 점유율이 임계 이상일 때만(수요 주도). 주택 fallback
            //   지위는 유지(수요장 기계 추가 없음), '무조건'만 제거. 빈 주택 양산은 영역에도 무익(Current 기반).
            var hCur = new NativeArray<int>(8, Allocator.Temp);
            var hCap = new NativeArray<int>(8, Allocator.Temp);
            foreach (var (occRO, fpRO) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                         .WithAll<ResidenceBuilding>().WithNone<WorkplaceBuilding>())
            {
                int ho = fpRO.ValueRO.OwnerLocalId;
                if ((uint)ho < 8) { hCur[ho] += occRO.ValueRO.Current; hCap[ho] += occRO.ValueRO.Capacity; }
            }

            // AI 팀 수집 (메인 — 팀 수 ≤ 8).
            var teamList = new NativeList<TeamInput>(8, Allocator.Temp);
            foreach (var (teamRO, gridRO) in
                     SystemAPI.Query<RefRO<TeamInfoData>, RefRO<CityGrid>>())
            {
                if (teamRO.ValueRO.IsPlayer()) continue;
                int lid = teamRO.ValueRO.LocalID;
                // 게이트: 주택 0채(부트스트랩) 또는 점유율 ≥ HousingGatePct → 주택 필요.
                bool wantHousing = (uint)lid >= 8 || hCap[lid] == 0
                                   || hCur[lid] * 100 >= hCap[lid] * HousingGatePct;
                teamList.Add(new TeamInput
                { Owner = lid, Grid = gridRO.ValueRO, WantHousing = wantHousing });
            }
            hCur.Dispose(); hCap.Dispose();
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
            _whDemandOpts = new NativeArray<BuildOption>(_teams.Length, Allocator.Persistent);
            _whDemandCells = new NativeArray<int2>(_teams.Length, Allocator.Persistent);
            for (int di = 0; di < _demandOpts.Length; di++) _demandOpts[di] = default;

            // 계획 C: AI 팀별 창고 stamp 커버 도로셀을 (owner,x,y) 평면 키로 복사(중첩 컨테이너 금지 우회).
            //   메인에서 복사 — 백그라운드 잡이 라이브 stamp를 캡처하면 StampRebuild(메인 쓰기)와 충돌하는
            //   확립된 함정 준수. 게임-일 1회 × stamp 엔트리 수라 저렴.
            _whCovered = new NativeHashMap<int3, int>(1024, Allocator.Persistent);
            if (SystemAPI.TryGetSingleton<StampLayers>(out var stampL))
            {
                for (int ti = 0; ti < _teams.Length; ti++)
                {
                    int wOwner = _teams[ti].Owner;
                    if ((uint)wOwner >= StampLayers.MaxPlayers) continue;
                    var map = stampL[wOwner];
                    if (!map.IsCreated) continue;
                    var kv = map.GetKeyValueArrays(Allocator.Temp);
                    for (int i = 0; i < kv.Keys.Length; i++)
                    {
                        if (kv.Values[i].Kind != StampKind.Warehouse) continue;
                        var k3 = new int3(wOwner, kv.Keys[i].x, kv.Keys[i].y);
                        int remain = SpawnSystem.WarehouseStampMaxDist - kv.Values[i].Dist;
                        if (remain < 0) remain = 0;
                        if (!_whCovered.TryGetValue(k3, out int cur) || remain > cur)
                            _whCovered[k3] = remain;   // 셀별 최대 잔여(여러 창고 중 가장 여유 있는 것)
                    }
                    kv.Dispose();
                }
            }
            // 창고 footprint 원점 수집(owner,x,y) — case-b 앵커(수요 해석, 메인 전용).
            var warehouseCells = new NativeList<int3>(16, Allocator.Temp);
            foreach (var (wh, wfp) in
                     SystemAPI.Query<RefRO<WarehouseTag>, RefRO<BuildingFootprint>>())
                warehouseCells.Add(new int3(wfp.ValueRO.OwnerLocalId,
                                            wfp.ValueRO.Origin.x, wfp.ValueRO.Origin.y));

            // 지구 슬롯 상태(2026-07-11 배치 그리드): (owner, 지구)별 인프라 보유 — 매 런 파생
            //   (지속 부기 없음 = drift 없음). 슬롯 유보(주택)·슬롯 선호(창고/서비스)·앵커 스냅 입력.
            _whDistricts   = new NativeHashSet<int3>(64, Allocator.Persistent);
            _supDistricts  = new NativeHashSet<int3>(64, Allocator.Persistent);
            _auraDistricts = new NativeHashSet<int3>(64, Allocator.Persistent);
            for (int wi = 0; wi < warehouseCells.Length; wi++)
            {
                var w = warehouseCells[wi];
                int2 wd = DistrictGrid.ToDistrict(new int2(w.y, w.z));
                _whDistricts.Add(new int3(w.x, wd.x, wd.y));
            }
            foreach (var (sup, sfp) in
                     SystemAPI.Query<RefRO<StampSupplier>, RefRO<BuildingFootprint>>())
            {
                int2 sd = DistrictGrid.ToDistrict(sfp.ValueRO.Origin);
                _supDistricts.Add(new int3(sfp.ValueRO.OwnerLocalId, sd.x, sd.y));
            }
            foreach (var (auraSup, afp) in
                     SystemAPI.Query<RefRO<AuraSupplier>, RefRO<BuildingFootprint>>())
            {
                int2 ad = DistrictGrid.ToDistrict(afp.ValueRO.Origin);
                _auraDistricts.Add(new int3(afp.ValueRO.OwnerLocalId, ad.x, ad.y));
            }

            // 지구 테이블 front(전일 발행분) — 창고 확장 앵커(슬롯 스냅)의 입력. 첫날은 비어 폴백.
            bool haveDistricts = SystemAPI.TryGetSingleton<DistrictTable>(out var dtable)
                                 && dtable.Stats.IsCreated;

            if (SystemAPI.TryGetSingleton<DemandField>(out var demandField) && demandField.Stats.IsCreated)
            {
                NeedLookupL2 l2 = default; bool haveL2 = false;
                foreach (var lk in SystemAPI.Query<RefRO<NeedLookupL2>>())
                { l2 = lk.ValueRO; haveL2 = true; break; }

                // L2 없어도(재베이크 전) 하드코딩 폴백이 돌도록 항상 호출.
                var redevelopReqs = new NativeList<int3>(8, Allocator.Temp);
                bool havePool = SystemAPI.TryGetSingleton<LogisticsPool>(out var lpool);
                // 생산/창고 파생 결정 테이블(다지기 ③·④) — 없으면(초기화 전) 하드코딩 폴백.
                bool haveProducer = SystemAPI.TryGetSingleton<ProducerLookup>(out var prodLookup)
                                    && prodLookup.Table.IsCreated;
                ResolveDemandOptions(in demandField, l2, haveL2, in metaLookup, in entranceLookup,
                    in warehouseCells, ref redevelopReqs,
                    clock.TotalSeconds, clock.SecondsPerDay / 24f, havePool, lpool,
                    haveProducer, prodLookup,
                    in dtable, haveDistricts, in teams);

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
                        // WithNone<WorkplaceBuilding>(2026-07-12): 거주+고용 동시 보유는 정상 경로에
                        //   없음(authoring이 경고) — 과거 맨 우클릭 오태그(Test.cs)로 ResidenceBuilding이
                        //   붙은 창고·식당·생산자를 재개발이 "주택"으로 오인해 철거하던 구멍 봉인.
                        //   이미 오염된 세이브도 이 필터로 보호된다.
                        foreach (var (bfRO, e) in
                                 SystemAPI.Query<RefRO<BuildingFootprint>>()
                                     .WithAll<ResidenceBuilding>().WithNone<WorkplaceBuilding>()
                                     .WithEntityAccess())
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
                        // 파괴 원인 추적(2026-07-11): 재개발은 주택(ResidenceBuilding)만 — 창고가 사라지면
                        //   이 로그가 아니라 [Capture]/[Raze] 쪽이다.
                        Debug.Log($"[CityAI] P{rOwner} 재개발 철거(주택) origin={bestOrigin} target={tgt}");
                    }

                    rdEcb.Playback(state.EntityManager);
                    rdEcb.Dispose();
                }
                redevelopReqs.Dispose();
            }

            warehouseCells.Dispose();

            _districts = new NativeHashMap<int2, DistrictStat>(256, Allocator.Persistent);
            _targetOut = new NativeArray<int3>(_teams.Length, Allocator.Persistent);

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

            // 지구 서베이(2026-07-11): 스냅샷만 읽어 지구 테이블을 채움 — 같은 틱 GrowthJob이
            //   소비(전략 목표·슬롯). _prevRoadOwners는 이 시스템 전용 + 완료 폴링 후에만 메인이
            //   만지므로 [ReadOnly] 캡처 안전(두 번째 스냅샷 복사 불필요).
            var surveyH = new DistrictSurveyJob
            {
                Snap           = _snap,
                PrevRoadOwners = _prevRoadOwners,
                Out            = _districts,
            }.Schedule(copyH);

            _growthHandle = new GrowthJob
            {
                Snap         = _snap,
                Teams        = _teams,
                DemandOpts   = _demandOpts,
                DemandCells  = _demandCells,
                WhOpts       = _whDemandOpts,
                WhCells      = _whDemandCells,
                WhCovered    = _whCovered,
                Districts    = _districts,
                WhDistricts  = _whDistricts,
                SupDistricts = _supDistricts,
                AuraDistricts= _auraDistricts,
                TargetOut    = _targetOut,
                OptA         = optA,
                OptB         = optB,
                Cfg          = cfg,
                TeamsTable   = teams,
                EnemyBuffer  = enemyBuffer,
                Day          = clock.Day,
                RoadOut      = _roadOut,
                BldOut       = _bldOut,
                LogOut       = _logOut,
            }.Schedule(surveyH);

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

            // 지구 테이블 발행(back → front 복사 + Version) + 확장 프런트 기준선 갱신 + 목표 발행.
            if (SystemAPI.HasSingleton<DistrictTable>())
            {
                ref var dt = ref SystemAPI.GetSingletonRW<DistrictTable>().ValueRW;
                dt.Stats.Clear();
                _prevRoadOwners.Clear();
                foreach (var kv in _districts)
                {
                    dt.Stats[kv.Key] = kv.Value;
                    if (kv.Value.RoadOwners != 0) _prevRoadOwners[kv.Key] = kv.Value.RoadOwners;
                }
                dt.Targets.Clear();
                for (int i = 0; i < _targetOut.Length; i++)
                    if (_targetOut[i].x != int.MinValue)
                        dt.Targets[_teams[i].Owner] = new int2(_targetOut[i].x, _targetOut[i].y);
                dt.Version++;
            }
            // (확장목표 일일 로그 은퇴 2026-07-12 — F5 오버레이가 목표 지구를 상시 표시.
            //   로그 정리 원칙: 정상 동작 로그 삭제, 이상·파괴·경고만 존치.)

            for (int i = 0; i < _logOut.Length; i++)
            {
                var l = _logOut[i];
                switch (l.Kind)
                {
                    case GrowthLog.NoTeamRoad:
                        Debug.LogWarning($"[AiCityGrowth] team{l.Owner}: 팀 도로 없음");
                        break;
                    // NoSpot(주택 성장 포화)·DemandInterior/Frontier(배치 성공)는 로그 은퇴
                    //   (2026-07-12 정리 — 정상 동작·포화 상태. DemandNoSpot 경고가 이상 경로 전담).
                    case GrowthLog.DemandNoSpot:
                        // 생산자(NeedsWarehouse)의 커버 후보 부재 → 다음 틱 창고 예약(계획 B "예약 후 건설").
                        if (l.B == 1) _pendingWarehouse[l.Owner] = l.Cell;
                        Debug.LogWarning($"[CityAI] P{l.Owner} day{_scheduledDay}: 수요 건물 key={l.A} 자리 없음"
                            + (l.B == 1 ? $" — 커버 안 후보 없음 → 다음 틱 창고 예약(앵커 {l.Cell})" : ""));
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
            _whDemandOpts.Dispose();
            _whDemandCells.Dispose();
            _whCovered.Dispose();
            _districts.Dispose();
            _whDistricts.Dispose();
            _supDistricts.Dispose();
            _auraDistricts.Dispose();
            _targetOut.Dispose();
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
            // 데이터 오류 tripwire(2026-07-12): BuildableOn=None이면 FootprintBuildableFlat이
            //   전 후보를 거부 → 증상이 "자리 없음"으로만 보임(레지스트리 항목 미설정 흔적).
            if (meta.BuildableOn == TerrainMask.None)
                Debug.LogWarning($"[AiCityGrowth] 건물 key={key} BuildableOn=None — 레지스트리 항목의 " +
                                 "배치 가능 지형 미설정(모든 배치가 실패함). Land/Water/Any 지정 필요.");
            var opt = new BuildOption
            {
                Key = key, Size = meta.Size, BuildableOn = meta.BuildableOn, BlockK = k,
            };
            if (meta.HasEntrance && entranceLookup.TryGet(key, out var ent))
            { opt.HasEnt = true; opt.Ent = ent; }
            else if (meta.HasEntrance)
                // 입구 미정의 폴백(FootprintTouchesTeamRoad)은 정상 경로지만, 새 건물 등록 시
                //   입구를 깜빡한 경우를 가시화(배치 실패 진단 보조 — 경고 아님).
                Debug.Log($"[AiCityGrowth] 건물 key={key}: 입구 정의 없음 — footprint 인접 도로 판정으로 배치");
            return opt;
        }

        // 욕구 주도 배치 STEP 2 — 팀별 최대 NoCoverage 수요 욕구 → 해소 건물 BuildOption(메인).
        //   ResolveOption 패턴 재사용. 수요 없음/매핑 없음/무효 건물 = Key=0(잡이 OptA/OptB로 폴백).
        //   ※ v1: NoCoverage(공급자 없음)만 건물화. Reached(상류 부족)는 집계만 — 공급망 자기조립은 후속.
        //   ※ WHERE(수요셀)는 v1 미사용(WHAT-only). 아무 팩션 L2든 공통 매핑(FactionFlags=0) 보유.
        void ResolveDemandOptions(in DemandField df, NeedLookupL2 l2, bool haveL2,
            in PrefabMetaLookup metaLookup, in EntranceLookup entranceLookup,
            in NativeList<int3> warehouseCells, ref NativeList<int3> redevelopReqs,
            double now, float hourSeconds, bool havePool, LogisticsPool lpool,
            bool haveProducer, ProducerLookup prodLookup,
            in DistrictTable dtable, bool haveDistricts, in TeamTable teamTable)
        {
            var stats = df.Stats;

            // 창고 MainKey — 파생 테이블(WarehouseTag 보유 프리팹, 다지기 ④) 우선,
            //   미파생(프리팹 미베이크)이면 구 하드코딩 폴백.
            int whMainKey = haveProducer && prodLookup.WarehouseMainKey > 0
                ? prodLookup.WarehouseMainKey : 1005;

            // (owner,resId) → 누적 NoCoverage 합 + 우세(최대) 수요 셀. 누적은 안 줄어드므로 판단은
            //   '지난 처리 대비 증가분(delta)'으로 — 총량 기준이면 커버 후에도 난사.
            var cur = new NativeHashMap<int2, CurAgg>(64, Allocator.Temp);
            foreach (var kv in stats)
            {
                // 건설 트리거 = NoCoverage(신설) + Full(만석→증설, 슬라이스 2 2026-07-14). 둘 다
                //   "같은 종류 건물을 시민 위치 근처에 하나 더"로 해결됨. NoGoods/Unstaffed는 제외
                //   (상류·고용 소관 — 복제하면 굶는 건물만 늘어남). Full 정교화(per-capita 비율·사이징)는 후속.
                int nc = kv.Value.FailNoCoverage + kv.Value.FailFull;
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

                // 창고 전용 레인 준비(2026-07-11): 예약(생산자 자리 없음 — 우선) 또는 수요장 WarehouseId
                //   후보(아래 루프에서 분리 수집). 창고는 수요 슬롯과 경쟁하지 않는다 — 유저 실측 "창고
                //   없이 넓은 확장": 팀당 하루 1건 슬롯을 식당·생산자와 나누면 확장기에 창고 추격이 밀림
                //   (추격 사슬 자체도 시민→식당→기아→창고 2단계 지연). 주택(예산)/창고(전용)/수요(슬롯) 3레인.
                bool whPending  = false;
                bool whSlotMode = false;   // 앵커가 "창고 없는 지구" 중심인가 → 배치에 지구당 1채 게이트
                int2 whAnchor   = default;
                if (_pendingWarehouse.TryGetValue(owner, out int2 pendAnchor))
                {
                    _pendingWarehouse.Remove(owner);
                    whPending = true;
                    // 앵커 체인(2026-07-11 연접 픽스): 구 폴백(pendAnchor = 실패한 생산자의 case-b 앵커
                    //   = **최근접 창고 셀**)이 연접의 원흉 — 확장 앵커가 실패하면 기존 창고 옆에
                    //   그대로 착지했다. ① 확장 앵커(내 도로 보유 + 창고 없는 지구) ② 실패 지점 지구의
                    //   이웃 창고-없는 지구 ③ 전략 목표 지구(창고 없으면) ④ 최후에만 pendAnchor
                    //   (게이트 없음 — 로그로 가시화).
                    if (TryFindExpansionAnchor(in dtable, haveDistricts, owner, pendAnchor,
                            out int2 expAnchor, in teamTable))
                    { whAnchor = expAnchor; whSlotMode = true; }
                    else if (TryNeighborWarehouselessDistrict(in dtable, haveDistricts, owner,
                                 DistrictGrid.ToDistrict(pendAnchor), in teamTable, out int2 nbAnchor))
                    { whAnchor = nbAnchor; whSlotMode = true; }
                    else if (haveDistricts && dtable.Targets.IsCreated
                             && dtable.Targets.TryGetValue(owner, out int2 tgtD)
                             && !_whDistricts.Contains(new int3(owner, tgtD.x, tgtD.y)))
                    { whAnchor = DistrictGrid.Center(tgtD); whSlotMode = true; }
                    else
                        whAnchor = pendAnchor;
                }
                bool whSpatial = false;
                int2 whDomCell = default; int whSum = 0, whDelta = 0;

                // 의존성 우선(원재료→중간재→최종)으로 데드락 회피 + 블랙리스트 회피. tier 낮을수록(rawer)
                //   우선, 동tier면 delta. 상류가 채워지면 그 수요가 꺼져 자연히 하류로 넘어감.
                int bestRes = -1, bestTier = int.MaxValue, bestDelta = 0, bestCur = 0;
                int2 bestCell = default;
                foreach (var e in cur)
                {
                    if (e.Key.x != owner) continue;
                    int res = e.Key.y;

                    // 배치 쿨다운(반영 lag 흡수): 반영 창 동안 후보 제외 → 만료 첫 틱에 기준선을
                    //   현재 합으로 재설정(lag 구간 샘플 폐기)하고 이번 틱은 측정 시작만. attempts·
                    //   재개발 escalation도 이 앞에서 걸러져 lag로 오염되지 않는다.
                    var ckey = new int2(owner, res);
                    if (_demandRebaselineAt.TryGetValue(ckey, out double until))
                    {
                        if (now < until) continue;                 // stamp/풀 반영 대기 중
                        _lastNoCoverage[ckey] = e.Value.Sum;       // 재기준선 — lag 샘플 폐기(로그 은퇴)
                        // 셀별 기준선도 동시 스냅샷(2026-07-12): 셀 선택이 "재기준선 이후 신규
                        //   미스"만 보게 — 해소된 셀의 역사적 누적 더미가 우세를 영구 독점해
                        //   커버된 자리 옆에 반복 건설되는 결함(경찰서 인접 3개) 차단.
                        foreach (var sc in stats)
                            if (sc.Key.x == owner && sc.Key.w == res)
                                _cellSeen[sc.Key] = sc.Value.FailNoCoverage + sc.Value.FailFull;
                        _demandRebaselineAt.Remove(ckey);
                        continue;                                  // 다음 틱부터 '반영 후' 수요만 판단
                    }

                    _lastNoCoverage.TryGetValue(ckey, out int last);
                    int delta = e.Value.Sum - last;
                    if (delta < DemandActThreshold) continue;      // 임계 미만은 후보 아님

                    // 양적 미스(commodity) 후보 억제: 풀 재고비율 ≥80%면 결핍 신호는 잔상 → 건너뜀.
                    if (havePool && DemandResource.IsCommodity(res)
                        && lpool.Cells.TryGetValue(new int2(owner, (int)DemandResource.ToCommodity(res)), out var pc)
                        && pc.Capacity > 0 && pc.Stored * 5 >= pc.Capacity * 4)
                        continue;

                    // 창고는 전용 레인으로 분리 — 수요 슬롯 tier 경쟁에서 제외(2026-07-11).
                    //   셀 선택 = **비블랙리스트 셀 중 최대 미스**(우세 셀 단독이 아님, 2026-07-11 유저 실측):
                    //   좁은 회랑 셀이 미스 누적 최대로 우세를 독점 + 배치 불가로 블랙리스트되면 채널
                    //   전체가 침묵 → 회랑 너머 신도시에 창고가 영영 안 감. 셀 단위로 걸러야 수요가
                    //   자연히 다음 셀(신도시)로 넘어간다.
                    if (DemandResource.IsWarehouse(res))
                    {
                        if (!whPending)   // 예약이 이미 레인을 점유했으면 이번 틱은 예약 우선(수요는 다음 틱)
                        {
                            int bestNc = 0; int2 cellPick = default;
                            foreach (var s in stats)
                            {
                                if (s.Key.x != owner || s.Key.w != DemandResource.WarehouseId) continue;
                                // 신규 미스(재기준선 이후)만 — 누적 더미의 우세 독점 차단(2026-07-12).
                                _cellSeen.TryGetValue(s.Key, out int seenNc);
                                int nc = s.Value.FailNoCoverage - seenNc;
                                if (nc <= bestNc) continue;
                                var k = new int4(owner, s.Key.y, s.Key.z, DemandResource.WarehouseId);
                                _demandAttempts.TryGetValue(k, out int at);
                                if (at >= DemandAttemptCap) continue;   // 이 셀만 제외 — 채널은 유지
                                bestNc = nc; cellPick = new int2(s.Key.y, s.Key.z);
                            }
                            if (bestNc > 0)
                            { whSpatial = true; whDomCell = cellPick; whSum = e.Value.Sum; whDelta = delta; }
                            // (전 셀 블랙리스트 침묵 로그 은퇴 — 정상 수렴 상태, 2026-07-12)
                        }
                        continue;
                    }

                    // 욕구 채널도 **셀 단위 선택**(2026-07-11 — 동형 결함 3번째: 우세 셀 블랙리스트가
                    //   채널 전체를 침묵 → 식당이 영영 안 서고 그 팀은 주택만 확장, 유저 실측). 비블랙
                    //   리스트 셀 중 최대 미스를 고름 — 실패 지역은 셀 단위로 포기되고 수요는 다음
                    //   지역으로 이월. 물량(commodity)은 블랙리스트 자체 면제(안 꺼짐 = 더 필요, 2026-07-10).
                    int2 domCell = e.Value.DomCell;
                    if (!DemandResource.IsCommodity(res))
                    {
                        // 해소 건물이 해석 불가능한 욕구는 후보 제외(2026-07-12): 새 비트(치안 등)의
                        //   프리팹이 아직 미등록이면 mainKey=0 → 뒤에서 continue인데, 그 전에 tier
                        //   경쟁에서 이겨 팀의 수요 슬롯을 영구 점유(식당·생산자 수요 아사)할 수
                        //   있다. 프리팹 등록(L2 자동 파생) 즉시 자동 활성.
                        uint nm = 1u << res;
                        bool resolvable = (haveL2 && LookupHelper.TryGetMainKey(nm, l2, out int rk) && rk > 0)
                                          || HardcodedNeedMainKey(res) > 0;
                        if (!resolvable) continue;

                        int bestNc2 = 0; int2 pick2 = default;
                        foreach (var s in stats)
                        {
                            if (s.Key.x != owner || s.Key.w != res) continue;
                            // 신규 미스(재기준선 이후)만 — 누적 더미의 우세 독점 차단(2026-07-12).
                            //   NoCoverage + Full(만석) — 셀 선택도 트리거와 동일 기준(슬라이스 2).
                            _cellSeen.TryGetValue(s.Key, out int seen2);
                            int nc2 = s.Value.FailNoCoverage + s.Value.FailFull - seen2;
                            if (nc2 <= bestNc2) continue;
                            var k2 = new int4(owner, s.Key.y, s.Key.z, res);
                            _demandAttempts.TryGetValue(k2, out int at2);
                            if (at2 >= DemandAttemptCap) continue;   // 이 셀만 제외 — 채널 유지
                            bestNc2 = nc2; pick2 = new int2(s.Key.y, s.Key.z);
                        }
                        if (bestNc2 <= 0) continue;   // 전 셀 블랙리스트 — 침묵(로그 은퇴 2026-07-12)
                        domCell = pick2;
                    }

                    int tier = ResourceTier(res);
                    if (tier < bestTier || (tier == bestTier && delta > bestDelta))
                    { bestTier = tier; bestDelta = delta; bestRes = res; bestCur = e.Value.Sum; bestCell = domCell; }
                }
                // ── 창고 전용 레인 확정(수요 슬롯과 독립 — 이후 continue에 영향받지 않게 먼저) ──
                if (whPending || whSpatial)
                {
                    var wopt = ResolveOption(whMainKey, in metaLookup, in entranceLookup);
                    if (wopt.Key > 0)
                    {
                        wopt.IsWarehouse = true;
                        if (!whPending)
                        {
                            int2 wro = DemandGrid.ToRealOrigin(whDomCell);
                            whAnchor = new int2(wro.x + DemandGrid.CellSize / 2, wro.y + DemandGrid.CellSize / 2);
                            // 지구 슬롯 스냅(2026-07-11): ① 미스 셀 지구에 내 창고 없음 → 앵커 = 지구 중심
                            //   (피치=커버 반경 유도라 중심 창고 1채가 지구 전체 커버) ② 이미 있음(포화) →
                            //   이웃 창고-없는 지구로 이월(지구 단위 타일링 — 기존 창고 옆 재착지 차단)
                            //   ③ 이웃도 없으면 미스 셀 유지(진짜 커버 구멍 수선 — 게이트 없는 유일 경로).
                            int2 whDd = DistrictGrid.ToDistrict(whAnchor);
                            if (!_whDistricts.Contains(new int3(owner, whDd.x, whDd.y)))
                            { whAnchor = DistrictGrid.Center(whDd); whSlotMode = true; }
                            else if (TryNeighborWarehouselessDistrict(in dtable, haveDistricts, owner,
                                         whDd, in teamTable, out int2 nbAnchor2))
                            { whAnchor = nbAnchor2; whSlotMode = true; }
                            // 공간 창고 수요 부기(기존 수요 슬롯과 동일 규약): 기준선/재기준선 + attempts/재개발.
                            _lastNoCoverage[new int2(owner, DemandResource.WarehouseId)] = whSum;
                            _demandRebaselineAt[new int2(owner, DemandResource.WarehouseId)]
                                = now + DemandCooldownHours * hourSeconds;
                            var wakey = new int4(owner, whDomCell.x, whDomCell.y, DemandResource.WarehouseId);
                            _demandAttempts.TryGetValue(wakey, out int wprev);
                            int wattempts = wprev + 1;
                            _demandAttempts[wakey] = wattempts;
                            _redevelopAttempts.TryGetValue(wakey, out int wredev);
                            if (wattempts >= RedevelopEscalate && wredev < RedevelopCap)
                            {
                                redevelopReqs.Add(new int3(owner, whAnchor.x, whAnchor.y));
                                _redevelopAttempts[wakey] = wredev + 1;
                                _demandAttempts[wakey]    = 0;
                            }
                            // 정상 경로(지구 슬롯)는 무로그 — 게이트 면제 경로(미스셀 폴백)만 관찰
                            //   (잔여 연접 1곳의 식별 도구, 2026-07-12 로그 정리).
                            if (!whSlotMode)
                                Debug.Log($"[CityAI] P{owner} 창고건설(전용 레인): delta={whDelta}"
                                    + $" 수요셀={whDomCell} 앵커={whAnchor} (미스셀 폴백 — 구멍 수선)");
                        }
                        else if (!whSlotMode)
                            Debug.Log($"[CityAI] P{owner} 창고 예약 건설 앵커={whAnchor}"
                                + " (⚠ 폴백: 실패지점=최근접창고 — 연접 위험)");

                        wopt.SlotAnchored = whSlotMode;   // 배치 게이트: 슬롯 모드 = 창고 보유 지구 착지 금지
                        _whDemandOpts[t]  = wopt;
                        _whDemandCells[t] = whAnchor;
                    }
                }

                // ── 풀 흐름 후보(P v3 — 층 분리: 풀은 **물리 흐름만**, 결핍은 수요층) ─────────
                //   판단 재료 전부 풀 로컬·물리적: Stored/Capacity/Out(실유출)/In(실유입).
                //   유입 < 유출(공급이 소비를 못 따라감) + 재고 < 유출×계수(목표재고 미달)면 재고가
                //   남아 있어도 **고갈 전 선제 증설** — 정상 가동 체인의 스케일링 전담. 흐름 0(부트
                //   스트랩·절대 결핍)은 이 층이 못 보고 안 봄 — 수요층(미스→수요장)이 전담.
                //   창(window) = 지난 성장 틱 이후 누적(팀 전체 처리 후 Clear).
                int poolRes = -1, poolTier = int.MaxValue, poolD = 0, poolIn = 0, poolStored = 0;
                if (havePool && lpool.Flow.IsCreated)
                {
                    foreach (var f in lpool.Flow)
                    {
                        if (f.Key.x != owner) continue;
                        int d = f.Value.Out;                       // 물리 유출만(결핍 미포함)
                        if (d <= 0) continue;
                        if (f.Value.In >= d) continue;             // 공급이 소비를 따라감 → 증설 불필요
                        lpool.Cells.TryGetValue(f.Key, out var pcell);
                        if (pcell.Stored >= d * PoolTargetFactor) continue;   // 목표재고(버퍼) 충분
                        if (pcell.Capacity > 0 && pcell.Stored * 5 >= pcell.Capacity * 4) continue; // ≥80%
                        int fres = DemandResource.ForCommodity((Commodity)f.Key.y);
                        if (_poolCooldown.TryGetValue(new int2(owner, fres), out double pu) && now < pu)
                            continue;                              // 직전 증설 효과 반영 대기
                        int ftier = ResourceTier(fres);
                        if (ftier < poolTier || (ftier == poolTier && d - f.Value.In > poolD - poolIn))
                        { poolTier = ftier; poolRes = fres; poolD = d; poolIn = f.Value.In; poolStored = pcell.Stored; }
                    }
                }

                // 공간(수요장: 욕구·창고·**결핍 commodity**) 후보 vs 풀(흐름 commodity) 후보 —
                //   tier 낮은 쪽 승리(창고 -1 > 원재료 0 > 중간재 1 > 욕구 2). **동tier(같은 commodity
                //   가능)면 공간 우선** — 결핍(절대 부족)이 추세보다 급하다.
                bool usePool = poolRes >= 0 && (bestRes < 0 || poolTier < bestTier);
                if (bestRes < 0 && !usePool) continue;   // 두 채널 다 후보 없음
                if (usePool)
                {
                    bestRes   = poolRes;
                    bestDelta = poolD - poolIn;                             // 로그용(공급 갭)
                    bestCell  = DemandGrid.ToCell(_teams[t].Grid.Anchor);   // 창고-먼저 폴백 앵커(베이스)
                }

                // WHERE 앵커 기본값 = 우세 수요 셀 중심(case a: 시민 공급자·창고).
                int2 ro = DemandGrid.ToRealOrigin(bestCell);
                int2 whereCell = new int2(ro.x + DemandGrid.CellSize / 2, ro.y + DemandGrid.CellSize / 2);

                // resource id 분기: 욕구(<64)=relief / commodity(64~)=producer.
                //   (WarehouseId는 전용 레인에서 이미 처리 — bestRes로 올 수 없음.)
                int mainKey = 0;
                bool needsWarehouse = false;   // 계획 C: 생산자는 입구가 창고 커버 안일 때만 배치 유효
                bool warehouseFirst = false;   // 계획 C: 창고 0채 → 이 틱은 창고 먼저, 생산자는 다음 틱
                if (!DemandResource.IsCommodity(bestRes))
                {
                    uint needMask = 1u << bestRes;
                    if (haveL2 && LookupHelper.TryGetMainKey(needMask, l2, out int mk))
                        mainKey = mk;                               // 정식: L2 결정 테이블
                    if (mainKey <= 0)
                        mainKey = HardcodedNeedMainKey(bestRes);    // ⚠ 임시(검증용) — L2 없음/매핑 없음 폴백
                }
                else if (TryNearestWarehouse(owner, whereCell, in warehouseCells, out int2 whCell))
                {
                    // case b (commodity 생산자=제분소·농장): 풀 공유라 소비자 옆이 아니라 가장 가까운
                    //   자기 창고 근처로(push/pull 커버 접속점) + 커버 검증 강제(계획 C — 근처 '편향'만으론
                    //   반경 밖에 떨어져 미커버 생산자→연결수요→창고 연접 나선의 연료가 됨).
                    //   commodity→생산자 = 파생 결정 테이블(능력 원천, 다지기 ③) 우선, 미파생 폴백.
                    var pc = DemandResource.ToCommodity(bestRes);
                    if (!haveProducer || !prodLookup.Table.TryGetValue((int)pc, out mainKey))
                        mainKey = HardcodedCommodityProducer(pc);   // ⚠ 미베이크 폴백
                    whereCell = whCell;
                    needsWarehouse = true;
                }
                else
                {
                    // 계획 C "창고 먼저": 창고 0채면 생산자는 어디에도 커버될 수 없음 → 이 틱은 창고를
                    //   짓고, 생산자는 수요 지속(쿨다운 미개시)으로 다음 틱 재선택된다.
                    mainKey = whMainKey;
                    warehouseFirst = true;
                }
                if (mainKey <= 0) continue;
                var opt = ResolveOption(mainKey, in metaLookup, in entranceLookup);
                if (opt.Key <= 0) continue;
                opt.NeedsWarehouse = needsWarehouse;
                opt.IsWarehouse    = mainKey == whMainKey;   // 창고 = 타일링 선호(계획 B) — 모든 창고 경로 공통
                // 커버형 v1: 오라 공급자(경찰서류) — 지구 슬롯 선호(오라 없는 지구), 창고 커버 무관.
                if (haveProducer && prodLookup.AuraKeys.IsCreated
                    && prodLookup.AuraKeys.TryGetValue(mainKey, out int auraR))
                { opt.IsAura = true; opt.AuraRadius = auraR; }
                // 계획 D: 방문형 욕구 공급자(식당 등) = 창고 커버 선호(태어날 때 풀 연결 — 입력 pull).
                //   오라형은 재고가 없으므로 제외.
                opt.PrefersCovered = !DemandResource.IsWarehouse(bestRes) && !DemandResource.IsCommodity(bestRes)
                                     && !opt.IsAura;

                // 오라 앵커 지구 스냅(2026-07-12 연접 픽스 — 창고 앵커 스냅과 동형): 수요 셀 지구에
                //   내 오라가 없으면 앵커 = 지구 중심(반경 20 ≥ 지구 스팬 → 중심 1채가 지구 전체
                //   커버 = 인접 셀 수요까지 일괄 소화). 이미 있으면 미스 셀 유지(경계 구멍 수선).
                if (opt.IsAura)
                {
                    int2 adx = DistrictGrid.ToDistrict(whereCell);
                    if (!_auraDistricts.Contains(new int3(owner, adx.x, adx.y)))
                    {
                        whereCell = DistrictGrid.Center(adx);
                        // 지구당 1채 하드 게이트(2026-07-12, 창고 연접 픽스와 동형): 슬롯 모드
                        //   오라는 내 오라 보유 지구에 착지 금지 — 나쁜 지형에서 목표 지구에
                        //   못 서고 옆 지구 회랑에 연쇄 착지(연속 N채)하던 경로의 구조적 차단.
                        //   미스셀(지구에 오라 있음 — 진짜 커버 구멍 수선)만 게이트 면제.
                        opt.SlotAnchored = true;
                    }
                }

                _demandOpts[t] = opt;
                _demandCells[t] = whereCell;
                // 부기 — 채널 간 조율: commodity 건설은 어느 채널이 지었든 **양쪽 쿨다운 동시 개시**
                //   (풀 쿨다운 = 증설 효과(고용→생산→첫 push) 반영 대기 / 수요장 재기준선 = 그동안
                //   쌓일 미스 lag 폐기 — 없으면 두 채널이 같은 부족에 중복 증설). 창고-먼저는 둘 다
                //   미개시(생산자 신호 유지 → 다음 틱 생산자).
                if (!warehouseFirst)
                {
                    if (DemandResource.IsCommodity(bestRes))
                        _poolCooldown[new int2(owner, bestRes)] = now + PoolCooldownHours * hourSeconds;
                    _demandRebaselineAt[new int2(owner, bestRes)] = now + DemandCooldownHours * hourSeconds;
                }
                if (usePool)
                    bestCell = DemandGrid.ToCell(whereCell);   // attempts/재개발 부기용 셀(앵커 기준)
                else
                    _lastNoCoverage[new int2(owner, bestRes)] = bestCur;   // 기준선 갱신(해소되면 delta→0)

                // (수요건설/풀결정 일일 로그 은퇴 2026-07-12 — 정상 결정 경로. 실패는
                //   DemandNoSpot 경고가, 상태는 F10/F12 HUD가 전담.)

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

            // 흐름 창 소비 완료 → 다음 창 시작(전 팀 처리 후 일괄 Clear).
            if (havePool && lpool.Flow.IsCreated) lpool.Flow.Clear();

            cur.Dispose();
        }

        // NoCoverage 누적이 이 값을 넘으면 그 욕구의 해소 건물 1개 배치(틱당). 누적이라 실수요는
        //   빠르게 초과 — 너무 낮으면 초기 노이즈에 반응. 튜닝 대상(추후 GrowthConfig 노출).
        const int DemandActThreshold = 20;

        // 반복 무효 건설(둘러싸인 요청자) 차단: 같은 (owner,수요셀,res)에 이만큼 지어도 수요가 안 꺼지면
        //   블랙리스트. 낮으면 정상 성장도 조기 차단, 높으면 난사 허용 — 튜닝 대상.
        //   ⚠ **공간 수요(욕구·창고) 전용** — commodity(물량)는 면제(2026-07-10, 위 후보 루프 참조).
        const int DemandAttemptCap = 3;

        // 배치 쿨다운(게임시간) — 건설 후 stamp 재빌드(HourChanged 라운드로빈 1팀/시간, 8팀 최악 ~8h) +
        //   풀 용량 재계산(1h)이 반영될 여유. 결정 틱이 DayChanged라 24h 이하 값은 "다음 틱에 재기준선"과
        //   동치 — 틱 주기가 바뀌어도 안전하도록 시간으로 표현. 1시간=SecondsPerDay/24(3600 아님).
        const float DemandCooldownHours = 12f;

        // 풀 경로(P v2) 상수 — 목표재고 = 창(지난 틱 이후) 수요 × 계수(선제 버퍼: 재고가 이 밑이고
        //   유입<수요면 고갈 전에 증설). 쿨다운 48h = 생산자 건설 → 고용 배정 → 생산 → 첫 push까지의
        //   반영 지연 흡수. 둘 다 튜닝 대상.
        const int   PoolTargetFactor  = 2;
        const float PoolCooldownHours = 48f;

        // 철거-후-건설(재개발) 게이트 — 꽉 찬 도심의 미충족 수요를 주택 철거로 소화.
        //   RedevelopEscalate = 인테리어+프런티어가 이만큼 실패하면 주택 철거로 escalation(hysteresis).
        //   RedevelopCap      = (owner,셀,res)당 최대 철거 횟수(thrash 상한 — 도시 공동화 방지).
        const int RedevelopEscalate = 2;
        const int RedevelopCap      = 2;

        // 지구 전략 평가(2026-07-11) — 확장 목표 지구 점수 = Room(개발 여지) + 자원·인접 가중
        //   ± 경쟁항(이웃 지구의 적 확장 프런트 × 가중). 가중 음수 = 회피(기본, 국경 churn 억제),
        //   양수 = 선점 — 팩션 성향 도입 시 FactionConfig 오버라이드가 확장점. 전부 튜닝 대상.
        const int TargetMinRoom         = 48;    // 지구(24²=576셀)의 ~8% 미만 = 목표 제외(회랑·포화 도심 탈락)
        const int TargetResourceWeight  = 2;
        const int TargetAdjacencyWeight = 16;    // 내 도로 보유 이웃 지구 수(0~8) — 연결 비용 근사
        const int TargetContestWeight   = -64;   // 기본 회피: 적 확장 프런트 인접 지구 감점

        // 확장 목표 지구 선정(Burst 잡 안, 팀당 1회/일): 후보 = 내 도로 보유 지구 ∪ 그 8-이웃
        //   (테이블 존재분), 적(타 팀) 도로·건물·영토 지구와 Room 임계 미달 제외. 점수 최대 지구의
        //   중심이 주택/폴백 확장(GrowOneBlock)의 소프트 bias가 된다. 동점 = (y,x) 사전순(결정적).
        static bool SelectTargetDistrict(int owner, in NativeHashMap<int2, DistrictStat> districts,
            in TeamTable teams, out int2 best, out int bestScore)
        {
            best = default; bestScore = int.MinValue;
            int myTeam = teams.Get(owner);
            int myBit  = 1 << owner;
            foreach (var kv in districts)
            {
                var s = kv.Value;
                if (s.Room < TargetMinRoom) continue;

                // 내 도시 인접성: 자기 지구이거나 8-이웃에 내 도로 지구가 있어야 후보.
                bool mine = (s.RoadOwners & myBit) != 0;
                int adjMine = 0;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (districts.TryGetValue(kv.Key + new int2(dx, dz), out var nb)
                        && (nb.RoadOwners & myBit) != 0) adjMine++;
                }
                if (!mine && adjMine == 0) continue;

                // 적 존재 지구 제외(배치 게이트와 일관 — 목표로 삼아도 지을 수 없음).
                if (HasEnemyPresence(in s, owner, myTeam, in teams)) continue;

                // 경쟁항: 이웃 지구의 적 확장 프런트(전일 도로 차분) 수.
                int enemyFront = 0;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (!districts.TryGetValue(kv.Key + new int2(dx, dz), out var nb)) continue;
                    for (int o = 0; o < 8; o++)
                        if (o != owner && (nb.FrontOwners & (1 << o)) != 0 && teams.Get(o) != myTeam)
                        { enemyFront++; break; }
                }

                int score = s.Room + s.Resource * TargetResourceWeight
                          + adjMine * TargetAdjacencyWeight
                          + enemyFront * TargetContestWeight;
                if (score > bestScore
                    || (score == bestScore && (kv.Key.y < best.y || (kv.Key.y == best.y && kv.Key.x < best.x))))
                { bestScore = score; best = kv.Key; }
            }
            return bestScore != int.MinValue;
        }

        // 지구에 적(타 팀)의 도로·건물·영토가 있는가 — 목표/앵커 공용 게이트.
        static bool HasEnemyPresence(in DistrictStat s, int owner, int myTeam, in TeamTable teams)
        {
            int pres = s.RoadOwners | s.BldOwners;
            for (int o = 0; o < 8; o++)
                if (o != owner && (pres & (1 << o)) != 0 && teams.Get(o) != myTeam) return true;
            if (s.TerrTeams != 0)
            {
                int myMask = (uint)myTeam < 8 ? 1 << myTeam : 0;
                if ((s.TerrTeams & ~myMask) != 0) return true;
            }
            return false;
        }

        // (owner,resId) 누적 집계 — 합 + 우세(최대) 수요 셀.
        struct CurAgg { public int Sum; public int DomVal; public int2 DomCell; }

        // 확장 창고 앵커 v2(지구 테이블, 2026-07-11): 구 "도로셀 전수 × 7×7 빈땅 스캔"(¼ 샘플링
        //   후에도 CPU 스파이크 용의자) 은퇴 — 전일 발행 지구 테이블에서 **내 도로 보유 + 내 창고
        //   없음 + 적 없음 + Room 임계 이상** 지구 중 Room 최대를 골라 그 **중심(슬롯)**을 앵커로.
        //   피치=커버 반경 유도(24 ≤ BFS 30)라 슬롯 중심 창고 1채 = 지구 온전 커버. 동점은 near
        //   (실패 지점) 근접. 테이블 없음(첫날)/후보 없음 → false(호출부가 pendAnchor 유지).
        //   Room 임계가 회랑 지구를 걸러 "회랑 셀 우세 독점" 문제의 거시 해법을 겸한다.
        bool TryFindExpansionAnchor(in DistrictTable table, bool haveTable, int owner, int2 near,
            out int2 anchor, in TeamTable teamTable)
        {
            anchor = near;
            if (!haveTable) return false;
            int myTeam = teamTable.Get(owner);
            int myBit  = 1 << owner;
            int bestRoom = 0; long bestD = long.MaxValue; bool found = false;
            foreach (var kv in table.Stats)
            {
                var s = kv.Value;
                if ((s.RoadOwners & myBit) == 0) continue;                    // 내 도로 없음 = 닿을 수 없음
                if (s.Room < TargetMinRoom) continue;                         // 여지 없음(회랑·포화) 제외
                if (_whDistricts.Contains(new int3(owner, kv.Key.x, kv.Key.y))) continue;   // 이미 창고
                // 적 존재 지구 제외(국경 churn 방지 — 구 앵커의 적영토 제외 계승).
                if (HasEnemyPresence(in s, owner, myTeam, in teamTable)) continue;

                int2 c = DistrictGrid.Center(kv.Key);
                long ddx = c.x - near.x, ddz = c.y - near.y;
                long d = ddx * ddx + ddz * ddz;
                if (s.Room > bestRoom || (s.Room == bestRoom && d < bestD))
                { bestRoom = s.Room; bestD = d; anchor = c; found = true; }
            }
            return found;
        }

        // 이웃 창고-없는 지구 리다이렉트(2026-07-11 연접 픽스): from 지구가 이미 내 창고를 보유하면
        //   8-이웃 중 "창고 없음 + Room≥임계 + 적 없음" 지구의 중심으로 앵커 이월 — 창고 수요가
        //   포화 지구 안(=기존 창고 옆)이 아니라 다음 지구로 확산(지구 단위 타일링). 이웃 중심은
        //   from 지구 도로에서 최대 ~피치 1.5배라 reach guard(20) 안팎 — 안 닿으면 NoSpot으로
        //   정직하게 실패하고, 도로가 그쪽으로 자란 뒤(전략 bias) 재시도된다. 이웃에 내 도로가
        //   없어도 됨(경계 도로에서 candidates가 닿음).
        bool TryNeighborWarehouselessDistrict(in DistrictTable table, bool haveTable, int owner,
            int2 fromDistrict, in TeamTable teamTable, out int2 anchor)
        {
            anchor = default;
            if (!haveTable) return false;
            int myTeam = teamTable.Get(owner);
            int bestRoom = 0; bool found = false;
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                int2 d = fromDistrict + new int2(dx, dz);
                if (!table.Stats.TryGetValue(d, out var s)) continue;
                if (s.Room < TargetMinRoom) continue;
                if (_whDistricts.Contains(new int3(owner, d.x, d.y))) continue;
                if (HasEnemyPresence(in s, owner, myTeam, in teamTable)) continue;
                if (s.Room > bestRoom) { bestRoom = s.Room; anchor = DistrictGrid.Center(d); found = true; }
            }
            return found;
        }

        // 수요 resource id → 로그용 이름(메인 전용 — managed string).
        static string ResName(int resId)
            => DemandResource.IsWarehouse(resId) ? "Warehouse"
             : DemandResource.IsCommodity(resId) ? DemandResource.ToCommodity(resId).ToString()
             : $"Need#{resId}";

        // ⚠ 미베이크 폴백(다지기 ② 이후) — L2는 프리팹 능력에서 자동 파생(LookupBuildSystem).
        //   프리팹에 BuildingAuthoring Relief가 채워지면 L2 행이 자동 생성돼 이 폴백은 침묵.
        //   전 건물 베이크 확인 후 삭제 예정.
        static int HardcodedNeedMainKey(int needBit) => needBit == 0 ? 1002 : 0;   // Hunger → restarant_h_small(1002)

        // ⚠ 미베이크 폴백(다지기 ③ 이후) — 정식 경로 = ProducerLookup(능력 ProductionJob.RecipeOutput
        //   파생). 프리팹 베이크 확인 후 삭제 예정.
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
            public bool     WantHousing;   // 계획 E: 거주 점유율 게이트 통과(주택 채움·주택 확장 허용)
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
            public bool         NeedsWarehouse;   // 계획 C: commodity 생산자 — 입구가 창고 커버 안일 때만 배치 유효
            public bool         IsWarehouse;      // 계획 B: 창고 — 입구가 기존 커버 **밖**인 후보 선호(타일링,
                                                  //   약간 중첩 허용 = 밖 후보 없으면 안쪽 폴백)
            public bool         PrefersCovered;   // 계획 D: 시민 공급자(식당 등) — 입구가 창고 커버 **안**인
                                                  //   후보 선호(태어날 때 풀 연결 — Flour 등 입력 pull).
                                                  //   소프트: 커버 후보 없으면 미커버 폴백(시민 근접이 우선,
                                                  //   창고는 커버미스로 뒤따라옴)
            public bool         SlotAnchored;     // 연접 픽스(2026-07-11): 앵커가 "창고 없는 지구" 중심 —
                                                  //   배치 후보가 **내 창고 보유 지구에 못 앉음**(지구당 1채
                                                  //   하드 게이트). false(미스셀/최후 폴백)만 게이트 면제
                                                  //   (진짜 커버 구멍 수선 경로).
            public bool         IsAura;           // 커버형 v1(2026-07-12): 오라 공급자(경찰서류) —
                                                  //   "오라 없는 지구"의 중앙 슬롯 선호(창고/공급자와 동형).
            public int          AuraRadius;       // 오라 반경(셀) — **도달 가드**: 앵커(수요/지구 중심)에서
                                                  //   맨해튼 반경 밖 후보 제외(못 덮는 배치 = 수요 안 꺼짐 →
                                                  //   attempts 3회 연접 루프의 원인, 2026-07-12 유저 실측
                                                  //   "경찰서 인접 3개" = DemandAttemptCap 지문).
        }

        // Burst 잡 안에서 못 찍는 로그를 완료 후 메인에서 출력하기 위한 이벤트.
        struct GrowthLog
        {
            public const byte NoTeamRoad     = 1;   // A,B 미사용
            public const byte NoSpot         = 2;   // A=K, B=유효후보 수
            public const byte DemandInterior = 3;   // A=key — 수요 건물 인테리어 배치
            public const byte DemandFrontier = 4;   // A=key — 수요 건물 프런티어 배치
            public const byte DemandNoSpot   = 5;   // A=key, B=NeedsWarehouse(1/0), Cell=앵커 — 자리 없음
            public int  Owner;
            public byte Kind;
            public int  A, B;
            public int2 Cell;   // DemandNoSpot: 실패한 수요 건물의 앵커(창고 예약 건설 위치)
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
        //  ①-b DistrictSurveyJob — 스냅샷 → 지구 테이블 (2026-07-11 지구 그리드)
        //    잡 체인: SnapshotJob → 여기 → GrowthJob(같은 틱 소비). 완료 후 메인이
        //    front(DistrictTable 싱글톤)로 복사 발행 — 오버레이/다음 틱 창고 앵커용.
        // ══════════════════════════════════════════════════════════════════════
        [BurstCompile]
        struct DistrictSurveyJob : IJob
        {
            [ReadOnly] public GrowthSnap Snap;
            [ReadOnly] public NativeHashMap<int2, byte> PrevRoadOwners;   // 전일 도로 owner 마스크
            public NativeHashMap<int2, DistrictStat> Out;

            public void Execute()
            {
                foreach (var kv in Snap.TerrainLayer)
                {
                    int2 d = DistrictGrid.ToDistrict(kv.Key);
                    Out.TryGetValue(d, out var s);
                    if (CellBuildable(kv.Key, in Snap) && !Snap.WaterCells.Contains(kv.Key)) s.Room++;
                    if (Snap.ResourceBlocked.Contains(kv.Key)) s.Resource++;
                    if (Snap.TerritoryLayer.TryGetValue(kv.Key, out int team) && (uint)team < 8)
                        s.TerrTeams |= (byte)(1 << team);
                    Out[d] = s;
                }
                foreach (var kv in Snap.RoadLayer)
                {
                    int o = kv.Value.OwnerLocalId;
                    if ((uint)o >= 8) continue;
                    int2 d = DistrictGrid.ToDistrict(kv.Key);
                    Out.TryGetValue(d, out var s);
                    s.RoadOwners |= (byte)(1 << o);
                    Out[d] = s;
                }
                foreach (var kv in Snap.OccupancyLayer)
                {
                    if (kv.Value.Type != OccupantType.Building) continue;
                    int o = kv.Value.OwnerLocalId;
                    if ((uint)o >= 8) continue;
                    int2 d = DistrictGrid.ToDistrict(kv.Key);
                    Out.TryGetValue(d, out var s);
                    s.BldOwners |= (byte)(1 << o);
                    Out[d] = s;
                }
                // 확장 프런트 = 어제 없던 도로 owner 비트(경쟁항 입력).
                var keys = Out.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    var s = Out[keys[i]];
                    PrevRoadOwners.TryGetValue(keys[i], out byte prev);
                    s.FrontOwners = (byte)(s.RoadOwners & ~prev);
                    Out[keys[i]] = s;
                }
                keys.Dispose();
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
            [ReadOnly] public NativeArray<BuildOption> WhOpts;       // 플레이어별 창고 전용 레인(Key=0=없음)
            [ReadOnly] public NativeArray<int2>       WhCells;       // 창고 레인 앵커
            [ReadOnly] public NativeHashMap<int3, int> WhCovered;    // (owner,x,y)→잔여 깊이(계획 C·커버 근사)
            [ReadOnly] public NativeHashMap<int2, DistrictStat> Districts;   // 지구 테이블(서베이, 같은 틱)
            [ReadOnly] public NativeHashSet<int3>     WhDistricts;  // (owner,dx,dy) 창고 보유 지구(슬롯 판정)
            [ReadOnly] public NativeHashSet<int3>     SupDistricts; // (owner,dx,dy) 공급자 보유 지구(슬롯 판정)
            [ReadOnly] public NativeHashSet<int3>     AuraDistricts;// (owner,dx,dy) 오라 보유 지구(슬롯 판정)
            public NativeArray<int3> TargetOut;                      // 팀별 확장 목표 지구(x,y,score) — 발행/로그용
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
                // 같은 틱 블록 계획 원장(2026-07-12): 레인들(창고/수요/주택)과 팀들이 전부 같은
                //   스냅샷으로 검증해 서로의 계획을 못 봄 → 8×8 링이 같은 틱 4×4 내부를 가르는
                //   교차-레인 침범의 원흉. 성공한 프런티어 블록 (O.x, O.y, K, Road)를 기록하고
                //   이후 후보는 내부-침범 겹침이면 거부(링-링 공유 타일링은 허용).
                var plannedBlocks = new NativeList<int4>(8, Allocator.Temp);

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

                    // 전략 확장 목표(2026-07-11 지구 평가): 지구 테이블에서 목표 지구 1개 선정 →
                    //   주택/폴백 확장(아래 2)의 **소프트 bias**(품질 키 유지 + 거리 기준점만 교체,
                    //   축 균형 Bal은 목표가 대신함). 수요·창고 레인(하드 bias)은 무관.
                    bool hasTarget = SelectTargetDistrict(owner, in Districts, in TeamsTable,
                        out int2 targetD, out int targetScore);
                    int2 strategicCell = hasTarget ? DistrictGrid.Center(targetD) : default;
                    TargetOut[t] = hasTarget ? new int3(targetD.x, targetD.y, targetScore)
                                             : new int3(int.MinValue, 0, 0);

                    // 0) 욕구 주도 배치(budget A=틱당 수요 건물 1개): STEP 3 WHERE — 수요 셀(굶주린
                    //    소비자 위치) 최근접 프런티어에 배치 → producer를 소비자 근처로 co-locate(물류·노동
                    //    연결). 수요 없음(Key=0)이면 건너뜀 → 아래 기존 로직 그대로(폴백, 회귀 0).
                    // 0-a) 창고 전용 레인(2026-07-11): 수요 슬롯과 병행 배치 — 확장 추격이 식당·생산자
                    //      수요와 슬롯을 다투지 않는다. claimed 공유로 같은 틱 중복 점유는 방지.
                    var whOpt = WhOpts[t];
                    if (whOpt.Key > 0)
                    {
                        bool builtWh = TryPlaceDemandInterior(in Snap, in grid, owner, in whOpt,
                            WhCells[t], in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                            in baseReached, in WhCovered, in WhDistricts, in SupDistricts, in AuraDistricts,
                            ref claimed, ref BldOut);
                        if (builtWh)
                            LogOut.Add(new GrowthLog
                            { Owner = owner, Kind = GrowthLog.DemandInterior, A = whOpt.Key });
                        else
                        {
                            builtWh = GrowOneBlock(in Snap, in grid, owner, in whOpt,
                                in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer, in baseReached,
                                in WhCovered, in WhDistricts, in SupDistricts, in AuraDistricts,
                                WhCells[t], true, true, ref rng, ref plannedBlocks,
                                ref RoadOut, ref BldOut, ref LogOut);
                            LogOut.Add(new GrowthLog
                            {
                                Owner = owner,
                                Kind  = builtWh ? GrowthLog.DemandFrontier : GrowthLog.DemandNoSpot,
                                A     = whOpt.Key, B = 0, Cell = WhCells[t],
                            });
                        }
                    }

                    var demandOpt = DemandOpts[t];
                    bool builtDemand = false;
                    if (demandOpt.Key > 0)
                    {
                        // 구획 수요-결정(인테리어 우선): 수요 셀(소비자/창고) 최근접 '갇힌 빈 구획'에 먼저
                        //   배치 → 소비자 옆이라 좁은 커버로도 닿음(주택이 선점하기 전에 서비스가 점유,
                        //   전투로 뚫린 내부 구멍도 서비스로 복구). 빈 구획/유효 위치 없으면 프런티어 폴백(기존).
                        builtDemand = TryPlaceDemandInterior(in Snap, in grid, owner, in demandOpt,
                            DemandCells[t], in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                            in baseReached, in WhCovered, in WhDistricts, in SupDistricts, in AuraDistricts,
                            ref claimed, ref BldOut);
                        if (builtDemand)
                            LogOut.Add(new GrowthLog
                            { Owner = owner, Kind = GrowthLog.DemandInterior, A = demandOpt.Key });
                        else
                        {
                            builtDemand = GrowOneBlock(in Snap, in grid, owner, in demandOpt,
                                in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer, in baseReached,
                                in WhCovered, in WhDistricts, in SupDistricts, in AuraDistricts,
                                DemandCells[t], true, true, ref rng, ref plannedBlocks,
                                ref RoadOut, ref BldOut, ref LogOut);
                            LogOut.Add(new GrowthLog
                            {
                                Owner = owner,
                                Kind  = builtDemand ? GrowthLog.DemandFrontier : GrowthLog.DemandNoSpot,
                                A     = demandOpt.Key,
                                B     = demandOpt.NeedsWarehouse ? 1 : 0,
                                Cell  = DemandCells[t],
                            });
                        }
                    }

                    // 1) 갇힌 구획 개발 (D1 구획 파생 + B 격자 패킹) — 계획 E: 주택은 점유율 게이트
                    //    통과 시에만(수요 주도). 게이트 닫힘 = 빈 슬롯이 서비스·생산자·창고의 경쟁장으로 보존.
                    bool wantHousing = Teams[t].WantHousing;
                    bool worked = wantHousing && DevelopParcels(in Snap, in grid, owner, in OptA, in OptB,
                        in outside, encLo, encHi, in TeamsTable, EnemyBuffer, in baseReached,
                        in WhDistricts, in SupDistricts,
                        budget, ref claimed, ref BldOut, ref rng);

                    // 2) 갇힌 구획 없음 & 수요 건물도 안 지었으면 → 바깥 확장 1회(중복 프런티어 방지).
                    //    주택용 확장도 게이트 대상(계획 E) — 수요 건물의 프런티어 확장(위 0)은 게이트 무관.
                    if (!worked && !builtDemand && wantHousing)
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

                        // 전략 목표가 있으면 소프트 bias(hardBias=false): 품질 랭킹 유지 + 거리
                        //   기준점 = 목표 지구 중심, Bal(축 균형)은 끔(거시 자리를 목표가 대신).
                        bool grew = first.Key > 0 && GrowOneBlock(in Snap, in grid, owner, in first,
                            in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                            in baseReached, in WhCovered, in WhDistricts, in SupDistricts, in AuraDistricts,
                            strategicCell, hasTarget, false, ref rng, ref plannedBlocks,
                            ref RoadOut, ref BldOut, ref LogOut);
                        if (!grew && second.Key > 0 && second.Key != first.Key)
                            GrowOneBlock(in Snap, in grid, owner, in second,
                                in Cfg, in outside, encLo, encHi, in TeamsTable, EnemyBuffer,
                                in baseReached, in WhCovered, in WhDistricts, in SupDistricts, in AuraDistricts,
                                strategicCell, hasTarget, false, ref rng, ref plannedBlocks,
                            ref RoadOut, ref BldOut, ref LogOut);
                    }

                    claimed.Dispose();
                    outside.Dispose();
                    baseReached.Dispose();
                }
                plannedBlocks.Dispose();
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
            in NativeHashSet<int2> baseReached, in NativeHashMap<int3, int> whCov,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts,
            in NativeHashSet<int3> auraDistricts,
            int2 biasCell, bool hasBias, bool hardBias,
            ref Unity.Mathematics.Random rng, ref NativeList<int4> plannedBlocks,
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
            // 소프트 bias(전략 목표, 2026-07-11)일 때 축 균형은 끔 — 균형은 "거시 목표 부재 시의
            //   둥근 도시 근사"였고 목표가 그 자리를 대신한다(1차 키라 공존 시 목표를 사실상 무시).
            bool balanceActive = math.abs(diff) > cfg.BalanceDeadband && !(hasBias && !hardBias);
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

                for (int q = 0; q < 4; q++)
                {
                    int2 O = QuadrantOrigin(fo, q, K, Road);
                    if (!seen.Add(O)) continue;
                    if (!BlockValid(in layers, O, K, Road, owner, in teams, enemyBuffer)) continue;
                    if (cfg.RejectParallelSeam && IsParallelSeam(O, K, Road, owner, in layers)) continue;
                    // 바깥-전용 확장: 블록 내부가 '갇힌(enclosed)' 포켓이면 거부(중첩 방지).
                    //   갇힌 빈 구획은 채우기가 담당, 확장은 바깥 프런티어로만.
                    //   링(둘레 도로)도 검사(2026-07-12) — 새 링이 이웃 구획의 갇힌 내부를
                    //   관통하는 후보 거부(구획 침범 차단). 기존 자기 도로(공유 변)는 허용.
                    if (!InteriorExterior(O, K, Road, owner, in layers, in outside, encLo, encHi)) continue;
                    // 같은 틱 원장 충돌(2026-07-12): 이 틱에 이미 계획된 블록은 스냅샷에 없어
                    //   BlockValid/InteriorExterior가 못 본다 — 원장으로 내부 침범 거부.
                    if (OverlapsPlanned(O, K, Road, in plannedBlocks)) continue;

                    // 연결성은 앵커 도로 footprint(fo)가 링에 포함되어 보장됨 → 별도 게이트 불필요.
                    int massMask = SideMassMask(O, K, Road, owner, in layers);

                    // 카테고리: 오목(2) > 코너볼록·교차로(1) > 직선 T분기(0).
                    //   교차로(삼/사거리) 앵커 복권(2026-07-12): 블록 격자의 이음 지점이 바로
                    //   교차로인데 제외하면 차선 앵커(한 칸 옆 변 중간 셀)가 이겨 같은 크기
                    //   블록도 계단식으로 밀렸다. 새 도로는 교차로에서 시작하는 게 정렬의 기본.
                    int category;
                    if (IsConcave(massMask)) category = 2;
                    else if (straight)       category = 0;
                    else                     category = 1;

                    float candDist = hasBias
                        ? math.abs(O.x + K * 0.5f - biasCell.x) + math.abs(O.y + K * 0.5f - biasCell.y)
                        : math.abs(O.x + K * 0.5f - baseC.x)    + math.abs(O.y + K * 0.5f - baseC.y);
                    // 창고 도달 가드(2026-07-10): 앵커(요청 셀)에 실제로 닿을 수 없는 먼 블록은 후보 제외 —
                    //   못 닿는 창고를 지으면 요청이 안 꺼져 "창고 옆 창고" 연쇄가 됨. 후보 0 = 정직한 NoSpot.
                    if (hasBias && opt.IsWarehouse && candDist > WarehouseReachGuard) continue;
                    // 오라 도달 가드(2026-07-12, 동형): 앵커에서 오라 반경(맨해튼 ≤ 반경 ⇒ 유클리드 ≤ 반경,
                    //   보수적) 밖 블록 제외 — 못 덮는 경찰서는 수요가 안 꺼져 attempts 연접 루프가 됨.
                    if (hasBias && opt.IsAura && opt.AuraRadius > 0 && candDist > opt.AuraRadius) continue;

                    nValid++;
                    int reach = lagIsX ? (O.x + K - grid.Anchor.x) : (O.y + K - grid.Anchor.y);
                    cands.Add(new Cand
                    {
                        O     = O,
                        Bal   = balanceActive ? math.min(reach, leadExt) : 0,
                        Cat   = category,
                        Share = RingShareScore(O, K, Road, owner, in layers),
                        Sides = math.countbits(massMask),
                        Dist  = candDist,
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
                    // 하드 bias(수요·창고 레인) = 앵커 최근접 단독. 소프트 bias(전략 목표) =
                    //   품질 랭킹 유지(Bal=0, Dist 기준점만 목표 지구 중심 — 마지막 키).
                    bool better = hasBias && hardBias
                        ? cands[i].Dist < cands[bi].Dist
                        : Better(cands[i], cands[bi]);
                    if (better) bi = i;
                }
                int2 pick = cands[bi].O;
                cands.RemoveAtSwapBack(bi);
                if (DevelopBlock(in layers, in grid, pick, K, Road, owner, in opt, in whCov,
                        in whDistricts, in supDistricts, in auraDistricts,
                        ref roadOut, ref bldOut, ref rng))
                { plannedBlocks.Add(new int4(pick.x, pick.y, K, Road)); grew = true; break; }
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

        // 블록 내부(K×K)+링(둘레 도로)이 모두 '바깥(exterior)'인가 — 윈도우 밖이거나 outside 소속.
        //   내부가 enclosed(윈도우 안 & outside 아님)면 false → 확장이 갇힌 포켓에 안 들어감.
        //   링 확장(2026-07-12): 새로 깔릴 링 셀이 enclosed면 false — 링 한 변이 이웃 구획의
        //   갇힌 빈 내부를 관통해 "남의 구획 안에 도로"가 그어지는 침범 차단. 기존 자기 도로는
        //   벽이라 outside에 없지만 공유 변(정상 맞물림)이므로 IsTeamRoad로 명시 허용.
        //   (BlockValid가 먼저 돌므로 이 시점 링 셀 = buildable 또는 자기 도로뿐.)
        static bool InteriorExterior(int2 O, int K, int Road, int owner, in GrowthSnap layers,
            in NativeHashSet<int2> outside, int2 encLo, int2 encHi)
        {
            if (!outside.IsCreated) return true;   // enclosure 미계산 시 통과(안전)
            int2 ro = O - new int2(Road, Road);
            int  rs = K + 2 * Road;
            for (int dz = 0; dz < rs; dz++)
            for (int dx = 0; dx < rs; dx++)
            {
                int2 c = ro + new int2(dx, dz);
                bool outOfWindow = c.x < encLo.x || c.x > encHi.x || c.y < encLo.y || c.y > encHi.y;
                if (outOfWindow || outside.Contains(c)) continue;         // 바깥 = 통과
                bool interior = dx >= Road && dx < Road + K && dz >= Road && dz < Road + K;
                if (interior) return false;                               // 내부가 enclosed 포켓
                if (!IsTeamRoad(c, in layers, owner)) return false;       // 새 링이 구획 내부 관통
            }
            return true;
        }

        // 같은 틱 계획 원장 겹침(2026-07-12): 후보 (O,K)가 이 틱에 이미 계획된 블록과
        //   '내부 침범' 관계면 true — 내 내부 vs 상대 윈도우(링 포함), 상대 내부 vs 내 윈도우.
        //   링-링만 겹치는 변 맞물림(정상 타일링, 도로 OR 병합)은 허용.
        static bool OverlapsPlanned(int2 O, int K, int Road, in NativeList<int4> planned)
        {
            for (int i = 0; i < planned.Length; i++)
            {
                int2 Op = planned[i].xy;
                int  Kp = planned[i].z, Rp = planned[i].w;
                if (RectOverlap(O, O + K, Op - Rp, Op + Kp + Rp)) return true;
                if (RectOverlap(Op, Op + Kp, O - Road, O + K + Road)) return true;
            }
            return false;
        }

        // [lo, hiEx) 반개구간 사각 겹침.
        static bool RectOverlap(int2 aLo, int2 aHiEx, int2 bLo, int2 bHiEx)
            => aLo.x < bHiEx.x && bLo.x < aHiEx.x && aLo.y < bHiEx.y && bLo.y < aHiEx.y;

        // ── 블록 개발: 도로 링 + 건물 1개 ────────────────────────────────────
        static bool DevelopBlock(
            in GrowthSnap layers, in CityGrid grid, int2 O, int K, int Road,
            int owner, in BuildOption opt, in NativeHashMap<int3, int> whCov,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts,
            in NativeHashSet<int3> auraDistricts,
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
                in opt, owner, in whCov, in whDistricts, in supDistricts, in auraDistricts,
                in plannedRoad, out int2 bOrigin, out float bRot, ref rng);

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
            in BuildOption opt, int owner, in NativeHashMap<int3, int> whCov,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts,
            in NativeHashSet<int3> auraDistricts,
            in NativeHashSet<int2> plannedRoad, out int2 bestOrigin, out float bestRot,
            ref Unity.Mathematics.Random rng)
        {
            bestOrigin = default; bestRot = 0f;
            int2 sz = opt.Size;
            int stepCount = opt.HasEnt ? 4 : 1;
            // 유효한 (위치·회전) 후보를 전부 훑어 균등 랜덤 1개 선택(reservoir) — 첫-fit 획일 배치 탈피.
            //   유효성 검사는 기존 그대로(입구 도로닿음·평탄·맵안), 선택만 무작위. 시드 결정적.
            //   계획 B(창고 타일링): "입구 미커버" 후보용 선호 reservoir를 따로 유지 — 있으면 그쪽 사용,
            //   없으면 전체 reservoir 폴백(약간 중첩 허용). 비창고는 추가 rng 소비 없음(결정성 보존).
            //   지구 슬롯(2026-07-11): 인프라(창고/공급자)는 "자기 인프라 없는 지구의 중앙 슬롯" 후보를
            //   최상위 reservoir로 — 슬롯 > 커버/타일링 선호 > 전체(전부 소프트 폴백).
            int cnt = 0, cntPref = 0, cntSlot = 0;
            int2 prefOrigin = default; float prefRot = 0f;
            int2 slotOrigin = default; float slotRot = 0f;
            for (int cz = 0; cz < spanSize; cz++)
            for (int cx = 0; cx < spanSize; cx++)
            {
                int2 origin = spanOrigin + new int2(cx, cz);
                // 지구당 1채 게이트(슬롯 앵커 전용) — 창고(2026-07-11)·오라(2026-07-12 확장).
                if (opt.SlotAnchored && (opt.IsWarehouse || opt.IsAura))
                {
                    int2 od = DistrictGrid.ToDistrict(origin);
                    var k3 = new int3(owner, od.x, od.y);
                    if (opt.IsWarehouse ? whDistricts.Contains(k3) : auraDistricts.Contains(k3))
                        continue;
                }
                for (int steps = 0; steps < stepCount; steps++)
                {
                    int2 eff = EntranceOps.RotateSize(sz, steps);
                    if (cx + eff.x > spanSize || cz + eff.y > spanSize) continue;
                    if (!FootprintBuildableFlat(origin, eff, opt.BuildableOn, in layers)) continue;
                    bool ercCovered = false;
                    if (opt.HasEnt)
                    {
                        int2 erc = EntranceOps.EntranceRoadCell(origin, sz, in opt.Ent, steps);
                        bool ercExists = layers.RoadLayer.ContainsKey(erc);
                        if (!ercExists && !plannedRoad.Contains(erc)) continue;
                        // 계획 C: commodity 생산자는 입구 도로셀이 창고 커버 안이어야(풀 접속 보장).
                        if (opt.NeedsWarehouse && !WarehouseCoverOk(erc, ercExists, owner, in whCov)) continue;
                        ercCovered = whCov.ContainsKey(new int3(owner, erc.x, erc.y));
                    }
                    cnt++;
                    if (rng.NextInt(cnt) == 0)
                    {
                        bestOrigin = origin;
                        bestRot    = EntranceOps.StepsToRotationY(steps);
                    }
                    // 선호 reservoir: 창고=미커버(타일링, 계획 B) / 시민 공급자=커버(풀 연결, 계획 D).
                    if ((opt.IsWarehouse && !ercCovered) || (opt.PrefersCovered && ercCovered))
                    {
                        cntPref++;
                        if (rng.NextInt(cntPref) == 0)
                        {
                            prefOrigin = origin;
                            prefRot    = EntranceOps.StepsToRotationY(steps);
                        }
                    }
                    // 슬롯 reservoir(최상위): 인프라 미충족 지구의 중앙 슬롯 후보.
                    if (InInfraSlot(origin, owner, in opt, in whDistricts, in supDistricts, in auraDistricts))
                    {
                        cntSlot++;
                        if (rng.NextInt(cntSlot) == 0)
                        {
                            slotOrigin = origin;
                            slotRot    = EntranceOps.StepsToRotationY(steps);
                        }
                    }
                }
            }
            if      (cntSlot > 0) { bestOrigin = slotOrigin; bestRot = slotRot; }
            else if (cntPref > 0) { bestOrigin = prefOrigin; bestRot = prefRot; }
            return cnt > 0;
        }

        // 지구 슬롯 판정(2026-07-11): 이 origin이 "그 인프라가 없는 지구"의 중앙 슬롯 안인가.
        //   창고 = 창고 없는 지구 / 방문형 공급자 = 공급자 없는 지구 / 오라형 = 오라 없는 지구.
        //   그 외 건물은 항상 false(rng 소비 없음 = 결정성 보존). origin 셀 기준 —
        //   주거 유보(InReservedSlot)와 동일 해상도.
        static bool InInfraSlot(int2 origin, int owner, in BuildOption opt,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts,
            in NativeHashSet<int3> auraDistricts)
        {
            if (!opt.IsWarehouse && !opt.PrefersCovered && !opt.IsAura) return false;
            if (!DistrictGrid.InSlot(origin)) return false;
            int2 d = DistrictGrid.ToDistrict(origin);
            var k = new int3(owner, d.x, d.y);
            return opt.IsWarehouse ? !whDistricts.Contains(k)
                 : opt.IsAura      ? !auraDistricts.Contains(k)
                 : !supDistricts.Contains(k);
        }

        // 창고 도달 가드(맨해튼) — 창고 배치 후보는 앵커(요청 셀)에서 이 거리 안이어야 함. 반경(30,
        //   도로 BFS)보다 확실히 안쪽 값으로 잡아 "지었는데 못 닿음"을 차단. 튜닝 대상.
        const float WarehouseReachGuard = 20f;

        // 계획 E: 주택 채움·주택용 확장 허용 점유율(%) — 거주 점유가 이 이상이어야 새 주택. 낮으면
        //   과잉 주택(구 "무조건 다채움" 왜곡), 높으면 이민 유입이 병목. 튜닝 대상.
        const int HousingGatePct = 85;

        // 지구 슬롯 유보(구 계획 F의 일반화, 2026-07-11): 셀이 "인프라(창고·공급자) 미충족 지구"의
        //   중앙 슬롯 안인가 — 주거 채움 후순위(소프트 예약). 구 F는 *기존* 창고 주변만 지켰지만
        //   슬롯 유보는 *앞으로 올* 인프라 자리를 지킨다(주택 선점 → 생산자 밀림 → 연접 나선의
        //   선제 차단). 창고·공급자 둘 다 있는 지구의 슬롯만 해제.
        static bool InReservedSlot(int2 c, int owner,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts)
        {
            if (!DistrictGrid.InSlot(c)) return false;
            int2 d = DistrictGrid.ToDistrict(c);
            var k = new int3(owner, d.x, d.y);
            return !whDistricts.Contains(k) || !supDistricts.Contains(k);
        }

        // ── 계획 C: 창고 커버 판정 ──────────────────────────────────────────
        //   기존 도로셀 = stamp 스냅샷 정확 판정. 이번 틱 신설(planned) 도로셀은 stamp가 아직 없으므로
        //   근사 — 근처(체비쇼프 NewRoadCoverSlack) 커버 셀의 **잔여 깊이**(MaxDist−Dist)가 그 셀→erc
        //   우회 거리(체비쇼프 + 링 마진)를 감당해야 재빌드 후 실제 커버됨. (구 "존재만 확인" 근사는
        //   경계 셀(잔여 0) 옆 확장을 오통과 → 미커버 농장 → 못 닿는 창고 연쇄, 2026-07-10 실측.)
        const int NewRoadCoverSlack   = 8;
        const int NewRoadDetourMargin = 4;   // 링 우회(직선 아님) 여유 — 튜닝
        static bool WarehouseCoverOk(int2 erc, bool ercExists, int owner, in NativeHashMap<int3, int> whCov)
        {
            if (whCov.ContainsKey(new int3(owner, erc.x, erc.y))) return true;
            if (ercExists) return false;   // 기존 도로인데 stamp 밖 = 확정 미커버
            for (int dz = -NewRoadCoverSlack; dz <= NewRoadCoverSlack; dz++)
            for (int dx = -NewRoadCoverSlack; dx <= NewRoadCoverSlack; dx++)
            {
                if (whCov.TryGetValue(new int3(owner, erc.x + dx, erc.y + dz), out int remain)
                    && remain >= math.max(math.abs(dx), math.abs(dz)) + NewRoadDetourMargin)
                    return true;
            }
            return false;
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
            in NativeHashSet<int2> baseReached, in NativeHashMap<int3, int> whCov,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts,
            in NativeHashSet<int3> auraDistricts,
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
            int bestCls = int.MaxValue;   // 계획 B: 0=선호(창고 입구 미커버=타일링) / 1=폴백(중첩 허용)
            int2 bestOrigin = default; int bestSteps = 0; int2 bestEff = default;

            foreach (var origin in enc)
            {
                // 지구당 1채 게이트(슬롯 앵커 전용): 창고(2026-07-11)·오라(2026-07-12 확장) —
                //   슬롯 모드는 해당 인프라 보유 지구에 못 앉는다(연접·연쇄 착지의 구조적 차단).
                //   미스셀 폴백(SlotAnchored=false)만 면제(진짜 커버 구멍 수선).
                if (opt.SlotAnchored && (opt.IsWarehouse || opt.IsAura))
                {
                    int2 od = DistrictGrid.ToDistrict(origin);
                    var k3 = new int3(owner, od.x, od.y);
                    if (opt.IsWarehouse ? whDistricts.Contains(k3) : auraDistricts.Contains(k3))
                        continue;
                }

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

                    bool ercCovered = false;
                    if (opt.HasEnt)
                    {
                        if (!EntranceOps.IsEntranceOnOwnRoad(origin, sz, in opt.Ent, steps, in layers.RoadLayer, owner)) continue;
                        int2 erc = EntranceOps.EntranceRoadCell(origin, sz, in opt.Ent, steps);
                        if (layers.RoadLayer.TryGetValue(erc, out var ercRc)
                            && !baseReached.Contains(ercRc.FootprintOrigin)) continue;
                        ercCovered = whCov.ContainsKey(new int3(owner, erc.x, erc.y));
                        // 계획 C: commodity 생산자는 입구 도로셀이 창고 커버 안이어야(풀 접속 보장).
                        //   인테리어 경로는 기존 도로만 쓰므로 stamp 스냅샷 정확 판정.
                        if (opt.NeedsWarehouse && !ercCovered) continue;
                    }
                    else if (!FootprintTouchesTeamRoad(origin, eff, owner, in layers)) continue;

                    // target(소비자/창고) 최근접 우선. 클래스가 거리보다 우선:
                    //   · 창고(계획 B): "입구 미커버" 선호(커버 경계 밖으로 확장 = 타일링).
                    //   · 시민 공급자(계획 D): "입구 커버" 선호(태어날 때 풀 연결). 둘 다 소프트 폴백.
                    //   · 지구 슬롯(2026-07-11, 상위 키 +2): 인프라 미충족 지구의 중앙 슬롯 = 0/1,
                    //     그 외 = 2/3 — 슬롯 후보 없으면 기존 커버 클래스→거리 비교로 자연 폴백.
                    float cx = origin.x + eff.x * 0.5f, cz = origin.y + eff.y * 0.5f;
                    float d = math.abs(cx - target.x) + math.abs(cz - target.y);
                    int cls = (opt.IsWarehouse && ercCovered) || (opt.PrefersCovered && !ercCovered) ? 1 : 0;
                    if ((opt.IsWarehouse || opt.PrefersCovered || opt.IsAura)
                        && !InInfraSlot(origin, owner, in opt, in whDistricts, in supDistricts, in auraDistricts))
                        cls += 2;
                    if (cls < bestCls || (cls == bestCls && d < bestDist))
                    { bestCls = cls; bestDist = d; bestOrigin = origin; bestSteps = steps; bestEff = eff; found = true; }
                }
            }
            enc.Dispose();

            // 너무 먼 구멍은 target(소비자/창고)을 커버 못 함 → 프런티어 폴백이 나음(튜닝 상수).
            //   창고는 더 엄격(WarehouseReachGuard): 반경(30)보다 확실히 안쪽이어야 요청 셀에 실제로
            //   닿음 — 못 닿는 창고는 짓지 않는다(미커버 요청자 → 못 닿는 창고 연쇄 차단, 2026-07-10).
            //   오라(경찰서류)도 동형(2026-07-12): 자기 반경 안이어야 앵커(수요지)를 실제로 덮음.
            const float InteriorMaxDist = 32f;
            float guard = opt.IsWarehouse                  ? WarehouseReachGuard
                        : opt.IsAura && opt.AuraRadius > 0 ? opt.AuraRadius
                        : InteriorMaxDist;
            if (!found || bestDist > guard) return false;

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
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts,
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
                        in grid, owner, in baseReached, in whDistricts, in supDistricts,
                        budget - placed, ref claimed, ref bldOut, ref rng);
                if (placed < budget)
                    placed += PackOnGrid(in optSmall, mn, mx, in enc, in layers,
                        in grid, owner, in baseReached, in whDistricts, in supDistricts,
                        budget - placed, ref claimed, ref bldOut, ref rng);
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
            in NativeHashSet<int2> baseReached,
            in NativeHashSet<int3> whDistricts, in NativeHashSet<int3> supDistricts, int budget,
            ref NativeHashSet<int2> claimed, ref NativeList<PlaceBuildingRequest> bldOut,
            ref Unity.Mathematics.Random rng)
        {
            if (opt.Key <= 0 || budget <= 0) return 0;
            int2 sz = opt.Size;
            int P = math.max(sz.x, sz.y);
            int stepCount = opt.HasEnt ? 4 : 1;

            int count = 0;
            // 지구 슬롯 유보(구 계획 F 일반화, 소프트): 1차 패스는 인프라 미충족 지구의 중앙 슬롯을
            //   유보(창고·서비스 자리 보존) — 다른 슬롯이 소진되고 예산이 남을 때만 2차 패스가 채움.
            //   하드 마스크 아님("강압적이지 않게" 합의): 주거 수요가 압박하면 결국 채워진다.
            for (int pass = 0; pass < 2; pass++)
            for (int gz = mn.y; gz + P - 1 <= mx.y; gz += P)
            for (int gx = mn.x; gx + P - 1 <= mx.x; gx += P)
            {
                if (count >= budget) return count;
                int2 origin = new int2(gx, gz);
                if ((pass == 0) == InReservedSlot(origin, owner, in whDistricts, in supDistricts)) continue;

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
