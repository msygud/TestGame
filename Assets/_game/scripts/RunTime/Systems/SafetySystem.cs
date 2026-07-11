using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SafetySystem — 치안 욕구(CitizenSafety) 전담: 증가 + 해소 (커버형 v1, 2026-07-12)
    //
    //  커버형 욕구는 방문·추구가 없다 — 집이 오라(경찰서류) 커버 **안**이면 Level 감소
    //  (4배속 회복), **밖**이면 증가. NeedDecision/ServiceSearch/Movement 파이프라인을
    //  전혀 안 탄다(공통 시스템 무수정 — "공통 시스템은 욕구 타입을 모른다" 검증 사례).
    //
    //  판정 위치 = 집(CitizenResidence.Home)의 footprint 원점 셀 — 도시빌더 관례
    //  (치안은 거주지 기준). 무주택 시민은 커버 판정 불가 → 증가만(불안 지속).
    //  owner = 집 소유자 — 오라 맵이 (owner, 셀) 키라 자기 도시의 시설만 진정시킨다.
    //
    //  실행: 실시간 ~1초 게이트(느슨함 — 매 프레임 정밀 불필요) + 게임초 누적 dt.
    //  잡은 front 오라 맵을 [ReadOnly]로 읽음 — 발행측(AuraCoverageSystem)의
    //  GetSingletonRW와 프레임워크 의존성으로 상호 안전.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AuraCoverageSystem))]
    public partial struct SafetySystem : ISystem
    {
        double _nextRealTime;
        double _lastGameSec;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<AuraCoverage>();
            _lastGameSec = -1;
        }

        public void OnUpdate(ref SystemState state)
        {
            double now = SystemAPI.Time.ElapsedTime;
            if (now < _nextRealTime) return;
            _nextRealTime = now + 1.0;

            var clock = SystemAPI.GetSingleton<GameClock>();
            if (_lastGameSec < 0) { _lastGameSec = clock.TotalSeconds; return; }   // 첫 틱 = 기준점만
            float gameDt = (float)(clock.TotalSeconds - _lastGameSec);
            _lastGameSec = clock.TotalSeconds;
            if (gameDt <= 0f) return;   // 일시정지

            var aura = SystemAPI.GetSingleton<AuraCoverage>();
            if (!aura.Map.IsCreated) return;

            state.Dependency = new SafetyTickJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Aura     = aura.Map,
                Dt       = gameDt,
            }.ScheduleParallel(state.Dependency);
        }
    }

    // ── 증가/해소 통합(커버 여부가 부호를 정함) ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SafetyTickJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public NativeHashMap<int3, ulong> Aura;
        public float Dt;   // 게임초(누적)

        const float ReliefFactor = 4f;   // 커버 시 회복 배속 — 튜닝 대상

        void Execute(ref CitizenSafety safety, in CitizenResidence res)
        {
            bool covered = false;
            if (res.Home != Entity.Null && FpLookup.HasComponent(res.Home))
            {
                var fp = FpLookup[res.Home];
                covered = Aura.TryGetValue(
                              new int3(fp.OwnerLocalId, fp.Origin.x, fp.Origin.y), out ulong bits)
                          && (bits & (ulong)NeedType.HighCrime) != 0;
            }

            safety.Level = math.saturate(safety.Level
                + (covered ? -safety.Rate * ReliefFactor : safety.Rate) * Dt);
        }
    }
}
