using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DemandAggregationSystem — 시도 실패 → 수요 필드 (2026-07-08 재정의)
    //  (2026-07-10 계획 P: 생산자 신호는 물류 미스 창(LogisticsMissLog) 드레인으로 교체 —
    //   이 시스템의 자체 수집은 시민 채널만. stamp 접근 0.)
    // ──────────────────────────────────────────────────────────────────────────
    //  ~1초마다 샘플. "미충족"(추구 중인데 목적지 없음, Idle/AtHome) 시민을 **현재
    //  건물 셀**(시도 위치) + 추구 욕구 비트 + **실패 사유**(ServiceTarget.LastOutcome)로
    //  집계 → DemandField에 **누적**(존재하는 동안, Clear 없음).
    //
    //  실행 모델("느슨함=백그라운드 잡" + "누적·즉시성 불필요"):
    //    ① CollectDemandJob(병렬, 라이브 읽기 → state.Dependency 등록):
    //       미충족 시민마다 (key(owner,dx,dy,needBit), 사유)를 큐에 넣음.
    //    ② TallyDemandJob(단일): 큐를 누적 back 맵에 접음(fold — Clear 없음).
    //    ③ 폴링: IsCompleted 후 back → front(DemandField.Stats) **복사 발행** + Version++.
    //       front은 메인만 쓰므로 독자(히트맵/배치)는 항상 안전한 스냅샷을 읽는다.
    //    집계는 저빈도라 체인이 dependency에 있어도 부담 없음(라이브 읽기 안전 확보).
    //
    //  ※ 사유는 ServiceSearchJob이 매 프레임 ServiceTarget.LastOutcome에 기록(계측 ~0).
    //     여기선 1초에 한 번 그 값을 읽어 누적할 뿐(카운팅 = 저빈도 게이트).
    //  ※ 요청/비율(성공 계측)은 v1 미포함 — 실패 강도만. 후속 슬라이스에서 추가.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct DemandAggregationSystem : ISystem
    {
        double    _next;
        JobHandle _handle;
        bool      _running;
        NativeHashMap<int4, DemandStat> _back;   // 누적 truth(Persistent) — 잡이 fold
        NativeQueue<DemandSample>       _keys;   // 런별(TempJob)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenTag>();
            _next = 0;
            _back = new NativeHashMap<int4, DemandStat>(1024, Allocator.Persistent);

            if (!SystemAPI.HasSingleton<DemandField>())
            {
                var e = state.EntityManager.CreateEntity(typeof(DemandField));
                state.EntityManager.SetComponentData(e, new DemandField
                {
                    Stats   = new NativeHashMap<int4, DemandStat>(1024, Allocator.Persistent),
                    Version = 0,
                });
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_running) { _handle.Complete(); _keys.Dispose(); _running = false; }
            if (_back.IsCreated) _back.Dispose();
            if (SystemAPI.HasSingleton<DemandField>())
            {
                var df = SystemAPI.GetSingleton<DemandField>();
                if (df.Stats.IsCreated) df.Stats.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 완료 폴링: 누적 back → front 복사 발행 + 버전 ──
            if (_running)
            {
                if (!_handle.IsCompleted) return;
                _handle.Complete();

                ref var df = ref SystemAPI.GetSingletonRW<DemandField>().ValueRW;
                df.Stats.Clear();
                var kv = _back.GetKeyValueArrays(Allocator.Temp);
                for (int i = 0; i < kv.Keys.Length; i++)
                    df.Stats[kv.Keys[i]] = kv.Values[i];
                kv.Dispose();
                df.Version++;

                _keys.Dispose();
                _running = false;
            }

            double now = SystemAPI.Time.ElapsedTime;
            if (now < _next) return;
            _next = now + 1.0;

            // 영업시간(현재 게임 시각) — 영업시간 외(일시적) 미충족은 집계에서 제외.
            int hour = SystemAPI.TryGetSingleton<GameClock>(out var clock) ? clock.Hour : 12;

            // ── 스케줄 ──
            //  ① 생산자/창고 신호(계획 P, 2026-07-10): 구 "굶은 생산자 상태 스캔"(outputFull·연결수요)은
            //     은퇴 — LogisticsPull/Push가 기록한 **실제 미스 창**(LogisticsMissLog, 메인 전용)을
            //     드레인해 샘플로 변환. 플래그 창이라 셀당 1샘플/초(시민 채널과 동등 가중). 이로써 이
            //     시스템은 stamp를 전혀 읽지 않는다(구 2b 메인스레드 우회 코드 삭제).
            //  ② 시민 수집(병렬, 라이브 읽기 → dependency) → ③ tally(단일, 누적 fold).
            _keys = new NativeQueue<DemandSample>(Allocator.TempJob);

            if (SystemAPI.TryGetSingleton<LogisticsMissLog>(out var missLog) && missLog.Window.IsCreated)
            {
                foreach (var kv in missLog.Window)
                    _keys.Enqueue(new DemandSample { Key = kv.Key, Cause = 0 });
                missLog.Window.Clear();
            }

            // 오라 과부하 감사 창(3안, 2026-07-16): AuraCoverageSystem이 시간당 기록한 지속
            //   과부하(시설 셀, relief) → 초과 인원. **Cause=Full(만석=증설)**로 변환 — d<1 불평
            //   채널이 겹침(pm≥적정) 탓에 침묵하는 사각지대(시설 파괴·후발 밀도 과부하) 보완.
            //   Full 수요는 실측(2026-07-16)대로 커버 안에 착지(증설)하고, 폐쇄 도심은 재개발
            //   escalation이 자리를 낸다. 초과 인원만큼 큐잉 = 시민 채널의 재실 비례와 동등 가중.
            if (SystemAPI.TryGetSingleton<AuraOverloadLog>(out var overLog) && overLog.Window.IsCreated)
            {
                foreach (var kv in overLog.Window)
                    for (int i = 0; i < kv.Value; i++)
                        _keys.Enqueue(new DemandSample { Key = kv.Key, Cause = 1 });
                overLog.Window.Clear();
            }

            var collectH = new CollectDemandJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Hour     = hour,
                Samples  = _keys.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);

            // 커버형(관리형 오라) 수요 채널(d<1 불평 2026-07-16, 제네릭화 — 치안+헬스케어): 추구/방문
            //   파이프라인을 안 타므로 위 CollectDemandJob(미충족=Pursuing 기반)이 못 본다 — **현재 셀의
            //   서비스 품질 d가 적정 미만인 시민**을 그 셀에 직접 샘플(present-based 자연 중화). 커버
            //   좋으면(d≥ServiceAdequate) 무샘플 → AI가 안 지음(품질·반경·근무자 올리면 건설 수↓).
            //   ※ 헬스케어 합류(2026-07-16 유저 실측 픽스): 병원 전멸/구멍 시 헬스케어=0인데 아무 수요도
            //   없던 비대칭(치안만 배선) 제거 — 이제 병원도 경찰처럼 커버 구멍이 재건을 부른다(재개발 포함).
            //   오라 front 맵 [ReadOnly] 캡처(발행측 CompleteAllTrackedJobs로 안전 — AuraCoverageSystem 참조).
            if (SystemAPI.TryGetSingleton<AuraCoverage>(out var aura) && aura.Map.IsCreated)
                collectH = new CollectAuraDemandJob
                {
                    FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                    Aura     = aura.Map,
                    Samples  = _keys.AsParallelWriter(),
                }.ScheduleParallel(collectH);

            _handle = new TallyDemandJob { Samples = _keys, Back = _back }.Schedule(collectH);
            state.Dependency = _handle;   // 라이브 시민 컴포넌트 읽기 안전(프레임워크 추적)
            _running = true;
        }

        // (구 SupplyHasWarehouseCoverage는 계획 P에서 은퇴 — 커버 판정은 LogisticsPull/Push가
        //  이미 수행하며 미스를 LogisticsMissLog에 기록. 이 시스템은 stamp를 읽지 않는다.)
    }

    /// <summary>집계 샘플 1건 — 시도 위치 key + 실패 사유(0=NoCoverage, 1=Reached).</summary>
    public struct DemandSample
    {
        public int4 Key;    // (owner, dx, dy, needBit)
        public byte Cause;  // 0=NoCoverage / 1=Full / 2=NoGoods / 3=Unstaffed (2026-07-14 세분)
    }

    // ── ① 미충족 시민 → (시도위치 key, 사유) 수집(병렬) ─────────────────────────
    //   미충족 = 추구 욕구 있음 + 목적지 없음 + Idle/AtHome + 사유 확정(LastOutcome≠None).
    //   위치 = **현재 건물**의 수요셀(추구를 시작한 자리 = 노출된 실수요). CurrentBuilding
    //   없으면(이동 중 등) 제외. 영업시간 외는 제외(구조적 부족만 남김).
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct CollectDemandJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        public int Hour;                          // 현재 게임 시각(0~23)
        public NativeQueue<DemandSample>.ParallelWriter Samples;

        void Execute(in CitizenNeeds needs, in ServiceTarget target, in CitizenState st)
        {
            if (needs.Pursuing == NeedType.None) return;
            if (target.Has) return;
            if (st.Activity != CitizenActivity.Idle && st.Activity != CitizenActivity.AtHome) return;

            // 사유 미확정(추구 전이 직후 아직 미검색) → 다음 초에 잡힘.
            var outcome = target.LastOutcome;
            if (outcome == ServiceOutcome.None) return;

            int bit = math.tzcnt((ulong)needs.Pursuing);      // 욕구 비트 인덱스(제네릭)
            if (!NeedServiceHours.IsOpen(bit, Hour)) return;   // 영업시간 외 → 일시적 노이즈 제외

            Entity cur = st.CurrentBuilding;
            if (cur == Entity.Null || !FpLookup.HasComponent(cur)) return;
            var fp = FpLookup[cur];

            int2 dcell = DemandGrid.ToCell(fp.Origin);
            byte cause = outcome switch
            {
                ServiceOutcome.Full      => 1,
                ServiceOutcome.NoGoods   => 2,
                ServiceOutcome.Unstaffed => 3,
                _                        => 0,   // NoCoverage
            };
            Samples.Enqueue(new DemandSample
            {
                Key   = new int4(fp.OwnerLocalId, dcell.x, dcell.y, bit),
                Cause = cause,
            });
        }
    }

    // (구 CollectSupplyDemandJob은 2b에서 메인스레드 수집으로 이관 — OnUpdate 인라인 + SupplyHasWarehouseCoverage.
    //  이유: 연결 수요가 stamp를 읽는데 백그라운드 폴링 잡이 라이브 stamp를 들면 StampRebuild 쓰기와 충돌.)

    // ── ①-b 커버형(관리형 오라) 수요 수집 — "현재 위치 서비스 미달" 시민 → NoCoverage 샘플 ──
    //   판정 위치 = 현재 건물(2026-07-12 유저 재설계, SafetySystem과 동일 기준): 집 고정
    //   판정은 커버 수요를 주거지로만 쏠리게 함 — 직장에서 미달인 시민은 **직장 셀**에
    //   수요를 남겨 시설이 상업·산업 지구에도 선다. 커버면 샘플 없음(해소 진행 중).
    //   이동 중(CurrentBuilding=Null)은 위치 특정 불가라 제외.
    //   셀당 1샘플/초 가중 = 시민·물류 채널과 동등. 임계/블랙리스트/재기준선 기계 재사용.
    //   제네릭(2026-07-16): 치안 하드코딩 → 관리형 서비스 비트 목록. 새 관리형 서비스 =
    //   아래 한 줄 추가. 게이트 WithAll(CitizenCivic)은 "휴먼 시민" 대용(2026-07-17 공무불만
    //   통합 — 진단은 시민 값이 아니라 셀의 비트별 지도에서 파생, 시민은 재실 가중치만).
    [BurstCompile]
    [WithAll(typeof(CitizenTag), typeof(CitizenCivic))]
    public partial struct CollectAuraDemandJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public NativeHashMap<int4, int> Aura;   // int4(owner,x,y,reliefBit)→품질 permille
        public NativeQueue<DemandSample>.ParallelWriter Samples;

        // 서비스 적정 임계(permille) — 현재 셀 d가 이 값 **미만**이면 불평(수요 샘플). ★건설 밀도 마스터
        //   손잡이★: 1 = 커버되면 만족(최소 건설·겹침 없음, 품질이 서비스 수준을 정함) / 1000 = full 요구
        //   (degraded도 증설). 품질·반경·근무자를 올리면 d↑ → 미달 셀↓ → AI 건설 수↓(밸런스가 직접 좌우).
        const int ServiceAdequate = 1;

        void Execute(in CitizenState st)
        {
            // d<1 불평(유저 모델): 지금 있는 건물 셀의 서비스 품질 d가 적정 미만이면 그 셀에 샘플.
            //   present-based(재실 인원 비례 = 자연 중화). d≥적정이면 무샘플 → **커버된 셀은 수요 0**
            //   (구 IsActive 게이트의 팬텀 — 커버 셀 도착 시 잔여 불안이 샘플되던 문제 — 제거).
            //   WHERE = 시민 현재 셀, 자기종결(증설→커버↑→미달 셀↓→수요↓).
            Entity at = st.CurrentBuilding;
            if (at == Entity.Null || !FpLookup.HasComponent(at)) return;
            var fp = FpLookup[at];

            // 관리형 서비스 비트 목록 — 새 서비스 = 한 줄(비트가 L2 자동 파생으로 해석 가능해야:
            //   프리팹 AuraSupplier.Relief에 그 비트가 있으면 자동).
            SampleIfInadequate(in fp, NeedType.HighCrime);          // 치안(경찰)
            SampleIfInadequate(in fp, NeedType.PoorHealthcare);     // 헬스케어(병원 오라, 2026-07-16 합류)
            SampleIfInadequate(in fp, NeedType.PoorSanitation);     // 환경(청소국, 2026-07-17 합류)
            SampleIfInadequate(in fp, NeedType.PoorAdministration); // 행정(관공서, 2026-07-17 합류)
            SampleIfInadequate(in fp, NeedType.Fire);               // 소방(소방서, 2026-07-17 합류)
        }

        void SampleIfInadequate(in BuildingFootprint fp, NeedType service)
        {
            int bit = math.tzcnt((ulong)service);
            bool present = Aura.TryGetValue(
                new int4(fp.OwnerLocalId, fp.Origin.x, fp.Origin.y, bit), out int pm);
            if (pm >= ServiceAdequate) return;   // 적정 이상 커버 → 불평 없음(팬텀 방지·밸런스 반영)

            // 사유 분기(2026-07-17, 무근무 병원 폭주 픽스): 엔트리 존재(잠재 커버 = 시설이 반경
            //   안에 있음) + 미달 = **Unstaffed**(무근무·무품질 — remedy는 고용, 건설 아님 →
            //   빌드 트리거 제외, F12 가시화만). 엔트리 부재 = 진짜 시설 없음 = NoCoverage(신설).
            //   구분 없으면 무근무 시설 옆에 같은 시설이 무한 증설된다(신설도 무근무 → 재귀).
            int2 dcell = DemandGrid.ToCell(fp.Origin);
            Samples.Enqueue(new DemandSample
            {
                Key   = new int4(fp.OwnerLocalId, dcell.x, dcell.y, bit),
                Cause = (byte)(present ? 3 : 0),   // 3=Unstaffed(비건설) / 0=NoCoverage(신설)
            });
        }
    }

    // 욕구별 공급자 영업시간(needBit 정적 스위치) — 영업시간 외 미충족은 집계 제외.
    //   ⚠ 값은 실제 공급자 staffing(JobSchedule.Profile)과 일치해야 함(불일치 시 드리프트).
    //   Hunger=식당(Merchant 8~24)과 일치. 미래 욕구는 여기 case 한 줄(제네릭 유지).
    //   2번째 욕구 도입 시 JobSchedule.Profile 파생으로 이관해 드리프트 제거 검토.
    public static class NeedServiceHours
    {
        public static bool IsOpen(int needBit, int hour) => needBit switch
        {
            0 => hour >= 8 && hour < 24,   // Hunger — 식당 영업 8~24
            5 => hour >= 8 && hour < 24,   // LowEducation — 학교 8~24(Teacher 근무창 일치, 2026-07-18 야간 확장:
                                            //   구 8~16은 주간 근로자 근무창과 완전 겹침 → 취업 시민의 수요 샘플이
                                            //   전량 폐기돼 학교가 영영 안 지어지는 구조적 결함이었음)
            _ => true,                      // 기타(상시 — 서비스 창 도입 시 case 추가)
        };
    }

    // ── ② 큐 → 누적 back 맵 fold(단일, Clear 없음 — 존재하는 동안 누적) ──────────
    [BurstCompile]
    public struct TallyDemandJob : IJob
    {
        public NativeQueue<DemandSample>       Samples;
        public NativeHashMap<int4, DemandStat> Back;

        public void Execute()
        {
            while (Samples.TryDequeue(out var s))
            {
                Back.TryGetValue(s.Key, out var stat);
                switch (s.Cause)
                {
                    case 1:  stat.FailFull++;      break;
                    case 2:  stat.FailNoGoods++;   break;
                    case 3:  stat.FailUnstaffed++; break;
                    default: stat.FailNoCoverage++; break;
                }
                Back[s.Key] = stat;
            }
        }
    }
}
