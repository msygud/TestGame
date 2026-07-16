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
    //  관리형 모델(2026-07-15~16): 셀값 = 서비스 품질 permille. d = Quality × min(1, a/b),
    //  a = 배정 근무자수 × 담당인원(PerWorkerCoverage), b = 범위 내 건물 캐퍼 합. 발행:
    //    · AuraCoverage(셀→품질 permille, 동종 합산) — SafetySystem 등이 비례 완화.
    //    · AuraLoadMap(시설→(감당중 b, 감당가능 a)) — AuraLoadHud(F6) 표시.
    //    · AuraUtilization(시설→가동률 min(1,b/a)) — CitizenMovementSystem 오라직 숙련 성장률.
    //  증설 수요는 시설 과밀 신호가 아니라 **d<1 불평 시민**(DemandAggregation.CollectAuraDemandJob)
    //  이 담당(2026-07-16 통일) — 구 AuraOverfullLog/CountAuraLoadJob/정원 은퇴.
    //    + **과부하 감사(3안, 2026-07-16)**: 겹침이 pm≥적정을 유지해 불평이 침묵하는 사각지대
    //      (시설 파괴·후발 밀도 과부하)만 b/a 지속 초과 → AuraOverloadLog(Cause=Full 증설)로 보완.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AuraCoverageSystem : ISystem
    {
        struct AuraSrc
        {
            public Entity Ent;         // 시설 엔티티(발행 키 — AuraLoadMap·AuraUtilization)
            public int    Owner;
            public int2   Origin;
            public int2   Eff;         // 회전 반영 크기
            public ulong  Relief;
            public int    Radius;
            public int    PerWorker;   // 근무자당 담당 인원(관리형 캐퍼 a = WorkerCount × PerWorker).
            public int    WorkerCount; // 배정 근무자수(BuildingOccupancy.Current) — 순수 인원.
            public int    Quality;     // 서비스 품질 c(0~100, authored) — d = c × min(1,a/b).
        }

        // 관리형 부하(b) 입력 — 범위 내 건물의 캐퍼 합(주거+근무+방문). 2026-07-15.
        struct BldCap
        {
            public int  Owner;
            public int2 Origin;
            public int2 Eff;    // 회전 반영 크기
            public int  Load;   // 주거+근무+방문 캐퍼 합
        }

        NativeHashMap<int4, int> _back;    // 잡이 채우는 백 버퍼(Persistent) — int4(owner,x,y,reliefBit)→품질 permille
        NativeList<AuraSrc>          _srcs;   // 런별 소스 스냅샷
        NativeList<BldCap>           _bld;    // 런별 건물 캐퍼 스냅샷(관리형 부하 b 입력)
        NativeHashMap<Entity, float> _util;   // 백 버퍼: 오라 건물 → 가동률 min(1,b/a)(숙련 성장률)
        NativeHashMap<Entity, int>   _load;   // 백 버퍼: 오라 건물 → 감당중 b(범위 내 건물 캐퍼 합)
        NativeHashMap<Entity, int>   _overHours; // 과부하 감사: 시설 → 연속 과부하 발행 횟수(≈시간)
        JobHandle _handle;
        bool      _running;
        bool      _everBuilt;

        // ⚠ 밸런스 상수(과부하 감사 3안 v1, 2026-07-16):
        //   과부하 = b/a > Num/Den(=1.5배)가 Sustain회 발행(≈게임시간) 연속 — 교대·배치 직후
        //   과도기 노이즈 흡수(hysteresis). 초과 인원 상한 = 시설·시간당 200 —
        //   AiCityGrowth.DemandActThreshold(20 델타) 대비 첫 발행에 트리거 가능한 규모.
        const int OverloadNum = 3, OverloadDen = 2;   // b·Den > a·Num ⇔ b/a > 1.5
        const int OverloadSustainHours = 3;
        const int OverloadSampleCap    = 200;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            _back = new NativeHashMap<int4, int>(1024, Allocator.Persistent);
            _util = new NativeHashMap<Entity, float>(64, Allocator.Persistent);
            _load = new NativeHashMap<Entity, int>(64, Allocator.Persistent);
            _overHours = new NativeHashMap<Entity, int>(64, Allocator.Persistent);

            if (!SystemAPI.HasSingleton<AuraCoverage>())
            {
                var e = state.EntityManager.CreateEntity(typeof(AuraCoverage));
                state.EntityManager.SetComponentData(e, new AuraCoverage
                {
                    Map     = new NativeHashMap<int4, int>(1024, Allocator.Persistent),
                    Version = 0,
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
            if (!SystemAPI.HasSingleton<AuraUtilization>())
            {
                var e = state.EntityManager.CreateEntity(typeof(AuraUtilization));
                state.EntityManager.SetComponentData(e, new AuraUtilization
                {
                    Map     = new NativeHashMap<Entity, float>(64, Allocator.Persistent),
                    Version = 0,
                });
            }
            if (!SystemAPI.HasSingleton<AuraOverloadLog>())
            {
                var e = state.EntityManager.CreateEntity(typeof(AuraOverloadLog));
                state.EntityManager.SetComponentData(e, new AuraOverloadLog
                {
                    Window = new NativeHashMap<int4, int>(32, Allocator.Persistent),
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_running)
            {
                _handle.Complete();
                _srcs.Dispose(); _bld.Dispose();
                _running = false;
            }
            if (_back.IsCreated) _back.Dispose();
            if (_util.IsCreated) _util.Dispose();
            if (_load.IsCreated) _load.Dispose();
            if (_overHours.IsCreated) _overHours.Dispose();
            if (SystemAPI.HasSingleton<AuraCoverage>())
            {
                var ac = SystemAPI.GetSingleton<AuraCoverage>();
                if (ac.Map.IsCreated) ac.Map.Dispose();
            }
            if (SystemAPI.HasSingleton<AuraLoadMap>())
            {
                var lm = SystemAPI.GetSingleton<AuraLoadMap>();
                if (lm.Map.IsCreated) lm.Map.Dispose();
            }
            if (SystemAPI.HasSingleton<AuraUtilization>())
            {
                var u = SystemAPI.GetSingleton<AuraUtilization>();
                if (u.Map.IsCreated) u.Map.Dispose();
            }
            if (SystemAPI.HasSingleton<AuraOverloadLog>())
            {
                var ol = SystemAPI.GetSingleton<AuraOverloadLog>();
                if (ol.Window.IsCreated) ol.Window.Dispose();
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
                //   컨테이너로 캡처**한 리더 잡(SafetyTickJob·CollectAuraDemandJob)은 잡 쿼리에
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

                // ── 부하/캐퍼 발행(관리형, 2026-07-16): 시설 → (감당중 b, 감당가능 a). AuraLoadHud(F6)가 읽음.
                //   b = 범위 내 건물 캐퍼 합(_load), a = 배정 근무자수 × 담당인원.
                ref var loadMap = ref SystemAPI.GetSingletonRW<AuraLoadMap>().ValueRW;
                loadMap.Map.Clear();
                for (int i = 0; i < _srcs.Length; i++)
                {
                    var src = _srcs[i];
                    _load.TryGetValue(src.Ent, out int bLoad);
                    loadMap.Map[src.Ent] = new int2(bLoad, src.WorkerCount * src.PerWorker);
                }
                loadMap.Version++;

                // ── 가동률 발행(관리형 숙련 성장, 2026-07-15): 오라 건물 → min(1,b/a). 백→프런트 복사.
                //   CitizenMovementSystem(퇴근 분기)이 오라직 숙련 성장률 배수로 읽음.
                ref var utilMap = ref SystemAPI.GetSingletonRW<AuraUtilization>().ValueRW;
                utilMap.Map.Clear();
                var ukv = _util.GetKeyValueArrays(Allocator.Temp);
                for (int i = 0; i < ukv.Keys.Length; i++)
                    utilMap.Map[ukv.Keys[i]] = ukv.Values[i];
                ukv.Dispose();
                utilMap.Version++;

                // ── 과부하 감사(3안, 2026-07-16): 지속 과부하 시설 → 증설 수요 로그 ──
                //   겹침이 pm≥ServiceAdequate를 유지하면 d<1 불평(시민 채널)이 침묵하는 사각지대
                //   (파괴로 이웃 부하 폭증 / 밀도 성장의 후발 과부하)를 부하 b/a로 직접 감지.
                //   b/a > 임계가 Sustain회(≈게임시간) 연속이면 초과 인원(b−a)을 AuraOverloadLog에
                //   기록 → DemandAggregation이 Cause=Full(증설)로 드레인. a=0(무근무)은 제외 —
                //   커버 자체가 없어(pm=0) 시민 NoCoverage 채널이 직접 잡는다.
                //   ★자기종결 = 셀 d★: b는 기하 부하(반경 내 캐퍼 합, 백업 모델 = 비분담)라 증설로
                //   안 줄어든다 — b/a만 보면 영구 발화(난립). 그래서 origin 셀의 **합산 d가 이 시설의
                //   authored 품질에 이미 도달**(이웃 오라가 열화를 흡수)이면 발화 억제. 신설 1채가
                //   셀 d를 회복시켜 감사를 멈춘다(attempts/재개발 캡은 최후 백스톱).
                ref var overLog = ref SystemAPI.GetSingletonRW<AuraOverloadLog>().ValueRW;
                for (int i = 0; i < _srcs.Length; i++)
                {
                    var src = _srcs[i];
                    long a = (long)src.WorkerCount * src.PerWorker;
                    _load.TryGetValue(src.Ent, out int bLoad);
                    bool over = a > 0 && (long)bLoad * OverloadDen > a * OverloadNum;
                    if (!over) { _overHours.Remove(src.Ent); continue; }

                    _overHours.TryGetValue(src.Ent, out int h);
                    _overHours[src.Ent] = ++h;
                    if (h < OverloadSustainHours) continue;   // hysteresis — 과도기 노이즈 흡수

                    // 자기종결: origin 셀 합산 d ≥ 이 시설의 authored 품질 → 증설 불필요(억제).
                    int reliefBit = math.tzcnt(src.Relief);
                    _back.TryGetValue(new int4(src.Owner, src.Origin.x, src.Origin.y, reliefBit),
                                      out int pmHere);
                    if (pmHere >= math.min(1000, src.Quality * 10)) continue;

                    int2 dc   = DemandGrid.ToCell(src.Origin);
                    var  okey = new int4(src.Owner, dc.x, dc.y, reliefBit);
                    int excess = (int)math.min(bLoad - a, OverloadSampleCap);
                    overLog.Window.TryGetValue(okey, out int curN);
                    overLog.Window[okey] = math.min(curN + excess, OverloadSampleCap);
                }
                // 사라진 시설(파괴·점령)의 잔존 카운터 청소 — 엔티티 재사용 오염 방지.
                if (_overHours.Count > 0)
                {
                    var alive = new NativeHashSet<Entity>(_srcs.Length, Allocator.Temp);
                    for (int i = 0; i < _srcs.Length; i++) alive.Add(_srcs[i].Ent);
                    var hk = _overHours.GetKeyArray(Allocator.Temp);
                    for (int i = 0; i < hk.Length; i++)
                        if (!alive.Contains(hk[i])) _overHours.Remove(hk[i]);
                    hk.Dispose(); alive.Dispose();
                }

                _srcs.Dispose();
                _bld.Dispose();
                _running = false;
            }

            // ── 게이트: 게임 시간당 1회(+최초). 무조건 재그리기 — dirty 추적 불필요한 규모.
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (_everBuilt && !clock.HourChanged) return;
            _everBuilt = true;

            // 소스 스냅샷(메인, 값 복사 — 잡이 라이브 컴포넌트를 안 듦).
            //   WorkerCount = 오라 건물 BuildingOccupancy.Current(배정 근무자수, 무직장=0).
            var srcOccLk = SystemAPI.GetComponentLookup<BuildingOccupancy>(true);
            _srcs = new NativeList<AuraSrc>(32, Allocator.Persistent);
            foreach (var (aura, fp, ent) in
                     SystemAPI.Query<RefRO<AuraSupplier>, RefRO<BuildingFootprint>>()
                              .WithEntityAccess())
            {
                if (aura.ValueRO.Radius <= 0 || aura.ValueRO.Relief == NeedType.None) continue;
                _srcs.Add(new AuraSrc
                {
                    Ent         = ent,
                    Owner       = fp.ValueRO.OwnerLocalId,
                    Origin      = fp.ValueRO.Origin,
                    Eff         = EntranceOps.RotateSize(fp.ValueRO.Size, fp.ValueRO.RotSteps),
                    Relief      = (ulong)aura.ValueRO.Relief,
                    Radius      = aura.ValueRO.Radius,
                    PerWorker   = aura.ValueRO.PerWorkerCoverage,
                    WorkerCount = srcOccLk.HasComponent(ent) ? srcOccLk[ent].Current : 0,
                    Quality     = aura.ValueRO.Quality,
                });
            }

            // ── 관리형 부하 b: 범위 내 건물 캐퍼 스냅샷(주거+근무+방문). 2026-07-15 ──
            //   태그로 BuildingOccupancy.Capacity를 주거(ResidenceBuilding)/근무(WorkplaceBuilding)
            //   로 해석 + VisitorOccupancy 좌석. 부하 0(캐퍼 없음) 건물은 제외. 시간당 1회
            //   메인 스냅샷(값 복사 — 잡이 라이브 컴포넌트를 안 듦).
            _bld = new NativeList<BldCap>(64, Allocator.Persistent);
            {
                var occLk = SystemAPI.GetComponentLookup<BuildingOccupancy>(true);
                var visLk = SystemAPI.GetComponentLookup<VisitorOccupancy>(true);
                foreach (var (fp, ent) in
                         SystemAPI.Query<RefRO<BuildingFootprint>>().WithEntityAccess())
                {
                    // 부하 = 정원(BuildingOccupancy = 주거 침상 XOR 근무 데스크, 태그 무관 통합)
                    //   + 방문 좌석(VisitorOccupancy). Stage1은 세 채널을 합산만 하므로 주거/근무
                    //   구분 불필요 — 태그 없는 정원도 누락 없이 계산(부하 과소 → 품질 과대 방지).
                    int load = 0;
                    if (occLk.HasComponent(ent)) load += occLk[ent].Capacity;
                    if (visLk.HasComponent(ent)) load += visLk[ent].Capacity;
                    if (load <= 0) continue;
                    _bld.Add(new BldCap
                    {
                        Owner  = fp.ValueRO.OwnerLocalId,
                        Origin = fp.ValueRO.Origin,
                        Eff    = EntranceOps.RotateSize(fp.ValueRO.Size, fp.ValueRO.RotSteps),
                        Load   = load,
                    });
                }
            }

            // ── 잡: RebuildAuraJob(단일) — 부하 b + 캐퍼 a(WorkerCount×PerWorker) → d=c×min(1,a/b)
            //   셀별 품질 permille 동종 합산 채움 + 가동률 min(1,b/a)를 _util, 감당중 b를 _load에 발행.
            //   시간당 1회·시설 수십이라 단일 스레드로 충분.
            _handle = new RebuildAuraJob
            {
                Srcs = _srcs,
                Blds = _bld,
                Back = _back,
                Util = _util,
                Load = _load,
            }.Schedule(state.Dependency);
            state.Dependency = _handle;
            _running = true;
        }

        // ── 백 버퍼 재그리기: 시설별 부하 b·캐퍼 a 산출 → 품질 d를 원형에 채우고 동종 합산 ──
        //   부하 b = 범위 내 건물 캐퍼 합(주거+근무+방문, 같은 owner). 판정 = 건물 origin 셀이 시설
        //     footprint-클램프 반경 내(릴리프 read와 동일 기준). 중첩 시 공유 건물이 각 시설 부하에
        //     양쪽 계산 = 백업 모델(각자 부하를 지되 품질 합산으로 full 회복).
        //   캐퍼 a = WorkerCount × PerWorkerCoverage (배정 근무자수 × 담당인원, 순수 인원, 2026-07-15).
        //   품질 d = Quality × min(1, a/b) — permille = Quality×10×min. WorkerCount·PerWorker·Quality 중
        //     하나라도 0 → 무커버("반드시 근무자"·authoring). 부하 없음(b=0) → full(Quality). 셀별 동종
        //     합산 상한 1000(=100). + 가동률 min(1,b/a)를 Util에 발행(오라직 숙련 성장률 배수).
        //   시간당 1회·시설 수십이라 단일 IJob(부하 스캔 + 채움)으로 충분.
        [BurstCompile]
        struct RebuildAuraJob : IJob
        {
            [ReadOnly] public NativeList<AuraSrc> Srcs;
            [ReadOnly] public NativeList<BldCap>  Blds;
            public NativeHashMap<int4, int>     Back;   // int4(owner,x,y,reliefBit) → 품질 permille
            public NativeHashMap<Entity, float> Util;   // 오라 건물 → 가동률 min(1,b/a)
            public NativeHashMap<Entity, int>   Load;   // 오라 건물 → 감당중 b(범위 내 건물 캐퍼 합)

            public void Execute()
            {
                Back.Clear();
                Util.Clear();
                Load.Clear();
                for (int s = 0; s < Srcs.Length; s++)
                {
                    var src = Srcs[s];
                    int r  = src.Radius;
                    int r2 = r * r;

                    // 부하 b = 범위 내 건물 캐퍼 합.
                    long load = 0;
                    for (int i = 0; i < Blds.Length; i++)
                    {
                        var bld = Blds[i];
                        if (bld.Owner != src.Owner) continue;
                        int bnx = math.clamp(bld.Origin.x, src.Origin.x, src.Origin.x + src.Eff.x - 1);
                        int bny = math.clamp(bld.Origin.y, src.Origin.y, src.Origin.y + src.Eff.y - 1);
                        int bdx = bld.Origin.x - bnx, bdy = bld.Origin.y - bny;
                        if (bdx * bdx + bdy * bdy > r2) continue;
                        load += bld.Load;
                    }

                    // 캐퍼 a = 배정 근무자수 × 담당인원.
                    long a = (long)src.WorkerCount * src.PerWorker;

                    // 가동률(오라직 숙련 성장률 배수) = min(1, b/a). a=0·b=0 → 0(할 일 없음).
                    Util[src.Ent] = (a > 0 && load > 0) ? math.min(1f, (float)load / a) : 0f;
                    Load[src.Ent] = (int)math.min(load, int.MaxValue);   // 감당중 b(HUD 표시)

                    // 품질 d = Quality × min(1, a/b) — permille(Quality×10×min). 셀 합산 상한 1000.
                    int dp;
                    if (a <= 0 || src.Quality <= 0) dp = 0;                        // 무근무·미authoring → 무커버
                    else if (load <= 0) dp = math.min(1000, src.Quality * 10);     // 부하 없음 → full(Quality)
                    else dp = (int)math.clamp(src.Quality * 10f * math.min(1f, (float)a / load), 0f, 1000f);
                    // dp=0(무근무·품질0)도 **값 0 엔트리로 원형을 남긴다**(잠재 커버, 2026-07-17):
                    //   시민 수요 채널이 "시설 없음(NoCoverage=신설)"과 "시설 있는데 무서비스
                    //   (Unstaffed=고용 소관, 비건설)"를 구분하는 근거 — 무근무 병원 옆에 병원을
                    //   무한 증설하던 폭주 차단. 병합식(cur+0)이 기존 양수를 보존하고 부재만 0으로
                    //   채우므로 소비자(비례 완화·HUD pm>0 게이트)는 무영향.
                    int reliefBit = math.tzcnt(src.Relief);

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

                        var key = new int4(src.Owner, x, y, reliefBit);
                        Back.TryGetValue(key, out int cur);
                        Back[key] = math.min(1000, cur + dp);
                    }
                }
            }
        }
    }
}
