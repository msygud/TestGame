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

            var collectH = new CollectDemandJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Hour     = hour,
                Samples  = _keys.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);

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
        public byte Cause;  // 0 = NoCoverage / 1 = Reached
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
            Samples.Enqueue(new DemandSample
            {
                Key   = new int4(fp.OwnerLocalId, dcell.x, dcell.y, bit),
                Cause = (byte)(outcome == ServiceOutcome.Reached ? 1 : 0),
            });
        }
    }

    // (구 CollectSupplyDemandJob은 2b에서 메인스레드 수집으로 이관 — OnUpdate 인라인 + SupplyHasWarehouseCoverage.
    //  이유: 연결 수요가 stamp를 읽는데 백그라운드 폴링 잡이 라이브 stamp를 들면 StampRebuild 쓰기와 충돌.)

    // 욕구별 공급자 영업시간(needBit 정적 스위치) — 영업시간 외 미충족은 집계 제외.
    //   ⚠ 값은 실제 공급자 staffing(JobSchedule.Profile)과 일치해야 함(불일치 시 드리프트).
    //   Hunger=식당(Merchant 8~24)과 일치. 미래 욕구는 여기 case 한 줄(제네릭 유지).
    //   2번째 욕구 도입 시 JobSchedule.Profile 파생으로 이관해 드리프트 제거 검토.
    public static class NeedServiceHours
    {
        public static bool IsOpen(int needBit, int hour) => needBit switch
        {
            0 => hour >= 8 && hour < 24,   // Hunger — 식당 영업 8~24
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
                if (s.Cause == 1) stat.FailReached++;
                else              stat.FailNoCoverage++;
                Back[s.Key] = stat;
            }
        }
    }
}
