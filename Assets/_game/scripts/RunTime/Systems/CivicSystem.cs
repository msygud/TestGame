using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CivicSystem — 공무불만(CitizenCivic) 전담: 관리형 오라 서비스 통합 경험 축
    //  (2026-07-17 유저 확정 — 구 SafetySystem(치안 단독) + 오라 서비스별 시스템을 대체)
    //
    //  커버형 규약은 그대로: 방문·추구 없음, **지금 있는 건물** 셀의 서비스별 커버
    //  vᵢ(permille/1000)를 **가중합** V = Σ wᵢ·vᵢ → 목표 Level = 1−V.
    //  개선(목표 ≤ 현재) = 즉시 / 악화 = Rate 점진(비대칭 — 수요 폭풍 방지, 치안 계승).
    //
    //  ★역할 분리(유저 논의 2026-07-17)★
    //    · 진단("무엇이 부족한가") = CollectAuraDemandJob이 셀의 **비트별** 지도를 직접
    //      읽어 서비스별 수요 발행 — 시민의 이 값은 진단에 안 쓰인다(재실 가중치만).
    //    · 이 값 = 결과 축: 컨디션 투영(CitizenConditions.Safety = 1−Level) → 사기·
    //      생산성·(미래) 이주. 서비스가 늘어도 시민 컴포넌트는 이거 하나.
    //  새 오라 서비스 추가 = 아래 가중치 한 줄 + CollectAuraDemandJob 비트 한 줄.
    //
    //  실행: 실시간 ~1초 게이트 + 게임초 누적 dt. front 오라 맵 [ReadOnly].
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AuraCoverageSystem))]
    public partial struct CivicSystem : ISystem
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

            state.Dependency = new CivicTickJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Aura     = aura.Map,
                Dt       = gameDt,
            }.ScheduleParallel(state.Dependency);
        }
    }

    // ── 증가/해소 통합: 현재 위치의 서비스별 커버 가중합이 목표를 정함 ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct CivicTickJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public NativeHashMap<int4, int> Aura;   // int4(owner,x,y,reliefBit)→품질 permille
        public float Dt;   // 게임초(누적)

        // ⚠ 서비스 가중치(v1 밸런스 — 합 1 유지): 치안이 체감 최대, 행정 최소.
        //   새 오라 서비스 = 여기 한 줄(기존 비중 재배분).
        const float WCrime      = 0.40f;   // NeedType.HighCrime          (경찰)
        const float WFire       = 0.25f;   // NeedType.Fire               (소방서)
        const float WSanitation = 0.20f;   // NeedType.PoorSanitation     (청소국)
        const float WAdmin      = 0.15f;   // NeedType.PoorAdministration (관공서)

        // ⚠ 테스트 과장 배속(구 SafetySystem 계승 — 커버 차이 즉시 체감용). 밸런싱 #1에서
        //   1로 복원하고 Rate로 조정.
        const float TestRateScale = 20f;

        void Execute(ref CitizenCivic civic, in CitizenState st)
        {
            // 판정 위치 = 지금 있는 건물(집/직장/방문지). 없으면(이동·노숙) V=0(거리의 불안).
            float v = 0f;
            Entity at = st.CurrentBuilding;
            if (at != Entity.Null && FpLookup.HasComponent(at))
            {
                var fp = FpLookup[at];
                v = WCrime      * Cov(in fp, NeedType.HighCrime)
                  + WFire       * Cov(in fp, NeedType.Fire)
                  + WSanitation * Cov(in fp, NeedType.PoorSanitation)
                  + WAdmin      * Cov(in fp, NeedType.PoorAdministration);
            }

            // 비대칭 접근(치안 계승): 개선 = 즉시 안심 / 악화 = 점진 누적.
            float target = 1f - v;
            civic.Level = target <= civic.Level
                ? target
                : math.min(target, civic.Level + civic.Rate * TestRateScale * Dt);
        }

        // 이 건물 셀에 닿는 해당 서비스 품질(0~1). 엔트리 부재 = 0(미커버).
        float Cov(in BuildingFootprint fp, NeedType service)
        {
            int bit = math.tzcnt((ulong)service);
            return Aura.TryGetValue(
                new int4(fp.OwnerLocalId, fp.Origin.x, fp.Origin.y, bit), out int pm)
                ? pm * 0.001f : 0f;
        }
    }
}
