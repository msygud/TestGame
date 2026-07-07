using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  DemandAggregationSystem — 미충족 욕구 → 수요 필드 (2026-07-07)
    // ──────────────────────────────────────────────────────────────────────────
    //  ~1초마다 재계산. "미충족" 시민(추구 중인데 목적지 없음, Idle/AtHome)을 집의
    //  수요셀 + 추구 욕구 비트로 집계 → DemandField(더블 버퍼) back에 쓰고 스왑.
    //
    //  실행 모델("느슨함=백그라운드 잡" 원칙):
    //    ① CollectDemandJob(병렬, 라이브 시민 컴포넌트 읽음 → state.Dependency 등록):
    //       미충족 시민마다 key(owner,dx,dy,needBit)를 큐에 넣음.
    //    ② TallyDemandJob(단일): 큐를 back 맵에 tally(Clear 후 누적).
    //    ③ 폴링: IsCompleted 후 front↔back 스왑 + Version++.
    //    집계는 저빈도라 체인이 dependency에 있어도 부담 없음(라이브 읽기 안전 확보).
    //    독자(히트맵/배치)는 스왑 사이 불변 front만 읽는다.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct DemandAggregationSystem : ISystem
    {
        double    _next;
        JobHandle _handle;
        bool      _running;
        NativeHashMap<int4, int> _back;   // 스왑 상대(Persistent)
        NativeQueue<int4>        _keys;   // 런별(TempJob)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenTag>();
            _next = 0;
            _back = new NativeHashMap<int4, int>(1024, Allocator.Persistent);

            if (!SystemAPI.HasSingleton<DemandField>())
            {
                var e = state.EntityManager.CreateEntity(typeof(DemandField));
                state.EntityManager.SetComponentData(e, new DemandField
                {
                    Counts  = new NativeHashMap<int4, int>(1024, Allocator.Persistent),
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
                if (df.Counts.IsCreated) df.Counts.Dispose();
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // ── 완료 폴링: front↔back 스왑 + 버전 ──
            if (_running)
            {
                if (!_handle.IsCompleted) return;
                _handle.Complete();
                ref var df = ref SystemAPI.GetSingletonRW<DemandField>().ValueRW;
                (df.Counts, _back) = (_back, df.Counts);   // 새 데이터가 front로
                df.Version++;
                _keys.Dispose();
                _running = false;
            }

            double now = SystemAPI.Time.ElapsedTime;
            if (now < _next) return;
            _next = now + 1.0;

            // 영업시간(현재 게임 시각) — 구조적/일시적 수요 분리용(STEP 1, 2026-07-07).
            int hour = SystemAPI.TryGetSingleton<GameClock>(out var clock) ? clock.Hour : 12;

            // ── 스케줄: 수집(병렬, 라이브 읽기 → dependency) → tally(단일) ──
            _keys = new NativeQueue<int4>(Allocator.TempJob);
            var collectH = new CollectDemandJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Hour     = hour,
                Keys     = _keys.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);

            _handle = new TallyDemandJob { Keys = _keys, Back = _back }.Schedule(collectH);
            state.Dependency = _handle;   // 라이브 시민 컴포넌트 읽기 안전(프레임워크 추적)
            _running = true;
        }
    }

    // ── ① 미충족 시민 → key 수집(병렬) ─────────────────────────────────────────
    //   미충족 = 추구 욕구 있음(Pursuing) + 목적지 없음(못 찾음/못 감) + Idle/AtHome.
    //   위치 = 집의 수요셀(사는 곳 근처에 서비스가 필요). 노숙(Home==Null)은 제외.
    //   ★영업시간 게이트(STEP 1): 그 욕구의 공급자가 영업하는 시간대 밖이면 집계 제외 —
    //     구조적 부족(영업 중에도 못 감)만 남기고 일시적(심야 폐점) 노이즈 제거. 안 하면
    //     심야에 전 시민이 미충족으로 잡혀 맵 전체가 맥동 → 배치가 식당을 난사(설계 검증).
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct CollectDemandJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        public int Hour;                          // 현재 게임 시각(0~23)
        public NativeQueue<int4>.ParallelWriter Keys;

        void Execute(in CitizenNeeds needs, in ServiceTarget target,
                     in CitizenState st, in CitizenResidence res)
        {
            if (needs.Pursuing == NeedType.None) return;
            if (target.Has) return;
            if (st.Activity != CitizenActivity.Idle && st.Activity != CitizenActivity.AtHome) return;

            int bit = math.tzcnt((ulong)needs.Pursuing);   // 욕구 비트 인덱스(제네릭)
            if (!NeedServiceHours.IsOpen(bit, Hour)) return;   // 폐점 시간대 → 일시적 노이즈 제외

            Entity home = res.Home;
            if (home == Entity.Null || !FpLookup.HasComponent(home)) return;
            var fp = FpLookup[home];

            int2 dcell = DemandGrid.ToCell(fp.Origin);
            Keys.Enqueue(new int4(fp.OwnerLocalId, dcell.x, dcell.y, bit));
        }
    }

    // 욕구별 공급자 영업시간(STEP 1, D1 권장안 A — needBit 정적 스위치).
    //   ⚠ 값은 실제 공급자 staffing(JobSchedule.Profile)과 일치해야 함(불일치 시 드리프트).
    //   Hunger=식당(Merchant 8~24)과 일치. 미래 욕구는 여기 case 한 줄(제네릭 유지).
    //   2번째 욕구 도입 시 JobSchedule.Profile 파생(D1 (B))으로 이관해 드리프트 제거 검토.
    public static class NeedServiceHours
    {
        public static bool IsOpen(int needBit, int hour) => needBit switch
        {
            0 => hour >= 8 && hour < 24,   // Hunger — 식당 영업 8~24
            _ => true,                      // 기타(상시 — 서비스 창 도입 시 case 추가)
        };
    }

    // ── ② 큐 → back 맵 tally(단일) ──────────────────────────────────────────────
    [BurstCompile]
    public struct TallyDemandJob : IJob
    {
        public NativeQueue<int4>        Keys;
        public NativeHashMap<int4, int> Back;

        public void Execute()
        {
            Back.Clear();
            while (Keys.TryDequeue(out var k))
            {
                Back.TryGetValue(k, out int c);
                Back[k] = c + 1;
            }
        }
    }
}
