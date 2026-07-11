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
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AuraCoverageSystem : ISystem
    {
        struct AuraSrc
        {
            public int   Owner;
            public int2  Origin;
            public int2  Eff;      // 회전 반영 크기
            public ulong Relief;
            public int   Radius;
        }

        NativeHashMap<int3, ulong> _back;   // 잡이 채우는 백 버퍼(Persistent)
        NativeList<AuraSrc>        _srcs;   // 런별 소스 스냅샷
        JobHandle _handle;
        bool      _running;
        bool      _everBuilt;

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
        }

        public void OnDestroy(ref SystemState state)
        {
            if (_running) { _handle.Complete(); _srcs.Dispose(); _running = false; }
            if (_back.IsCreated) _back.Dispose();
            if (SystemAPI.HasSingleton<AuraCoverage>())
            {
                var ac = SystemAPI.GetSingleton<AuraCoverage>();
                if (ac.Map.IsCreated) ac.Map.Dispose();
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

                _srcs.Dispose();
                _running = false;
            }

            // ── 게이트: 게임 시간당 1회(+최초). 무조건 재그리기 — dirty 추적 불필요한 규모.
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (_everBuilt && !clock.HourChanged) return;
            _everBuilt = true;

            // 소스 스냅샷(메인, 값 복사 — 잡이 라이브 컴포넌트를 안 듦).
            _srcs = new NativeList<AuraSrc>(32, Allocator.Persistent);
            foreach (var (aura, fp) in
                     SystemAPI.Query<RefRO<AuraSupplier>, RefRO<BuildingFootprint>>())
            {
                if (aura.ValueRO.Radius <= 0 || aura.ValueRO.Relief == NeedType.None) continue;
                _srcs.Add(new AuraSrc
                {
                    Owner  = fp.ValueRO.OwnerLocalId,
                    Origin = fp.ValueRO.Origin,
                    Eff    = EntranceOps.RotateSize(fp.ValueRO.Size, fp.ValueRO.RotSteps),
                    Relief = (ulong)aura.ValueRO.Relief,
                    Radius = aura.ValueRO.Radius,
                });
            }

            _handle  = new RebuildAuraJob { Srcs = _srcs, Back = _back }.Schedule(state.Dependency);
            state.Dependency = _handle;
            _running = true;
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
