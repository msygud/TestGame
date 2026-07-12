using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SafetySystem — 치안 욕구(CitizenSafety) 전담: 증가 + 해소 (커버형 v1, 2026-07-12)
    //
    //  커버형 욕구는 방문·추구가 없다 — **지금 있는 곳**이 오라(경찰서류) 커버 **안**이면
    //  Level 감소(배속 회복), **밖**이면 증가. NeedDecision/ServiceSearch/Movement 파이프
    //  라인을 전혀 안 탄다(공통 시스템 무수정).
    //
    //  판정 위치 = **현재 건물(CitizenState.CurrentBuilding)** — 2026-07-12 유저 재설계:
    //  구 "거주지 고정" 판정은 모든 커버 수요를 주거지로 쏠리게 함(직장 지구가 영원히
    //  미커버). 범위형은 "내가 지금 있는 자리"가 좌우한다: AtHome=집 / AtWork=직장 /
    //  AtDestination=방문지. 이동 중·노숙(CurrentBuilding=Null) = 미커버 취급(거리의 불안).
    //  owner = 그 건물 소유자 — 오라 맵이 (owner, 셀) 키라 자기 도시의 시설만 진정시킨다.
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

    // ── 증가/해소 통합(현재 위치의 커버 여부가 부호를 정함) ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SafetyTickJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public NativeHashMap<int3, ulong> Aura;
        public float Dt;   // 게임초(누적)

        // ⚠ 테스트 과장 배속(2026-07-12 유저 요청 "크게 차이 나도록") — 미커버 증가에 적용.
        //   기본 Rate 0.0005/게임초는 커버 차이가 며칠 뒤에야 보임 → ×20이면 미커버
        //   ~1.5게임시간에 임계 도달(즉시 체감). 밸런싱 #1에서 1로 되돌리고 Rate로 조정.
        const float TestRateScale = 20f;

        void Execute(ref CitizenSafety safety, in CitizenState st)
        {
            // 판정 위치 = 지금 있는 건물(집/직장/방문지). 없으면(이동·노숙) 미커버.
            bool covered = false;
            Entity at = st.CurrentBuilding;
            if (at != Entity.Null && FpLookup.HasComponent(at))
            {
                var fp = FpLookup[at];
                covered = Aura.TryGetValue(
                              new int3(fp.OwnerLocalId, fp.Origin.x, fp.Origin.y), out ulong bits)
                          && (bits & (ulong)NeedType.HighCrime) != 0;
            }

            // 비대칭 일괄(2026-07-12 유저 결정): 커버 = **즉시 안심(Level=0, 일괄 해소)** /
            //   미커버 = 점진 누적(불안은 서서히 — 수요도 "지속 노출된 곳"에만 쌓여 폭풍 방지).
            //   구 대칭 적분(커버 시 ×4 배속 회복)은 톱니(집↔직장 왕복)로 staff 샘플링이
            //   출렁이던 원인 — 은퇴.
            safety.Level = covered
                ? 0f
                : math.saturate(safety.Level + safety.Rate * TestRateScale * Dt);
        }
    }
}
