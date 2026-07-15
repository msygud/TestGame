using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SicknessSystem — 질병 상태 전담: 발병 체크 + 회복(자연·입원) (상태화 v2, 2026-07-13)
    //
    //  질병 = **상태**(DiseasedTag)로 재설계(유저): 욕구 긴급도 경쟁이 아니라 "일반 시민과
    //  다른 생활로직" 모드. 발병 = enable → 별도 쿼리(NeedDecisionSystem.DiseaseRouteJob)가
    //  병원 직행 라우팅. 완치 = disable → 정상 복귀. 여기(전담 시스템)는 세 가지만:
    //    ① 발병 체크(DiseaseCheckJob) — **게임 3시간마다**(위상 분산: index%3 == Hour%3),
    //       비질병 시민만. 저항 = base + **헬스케어**(현재 건물 병원 오라 커버) + 쾌적함(미구현 0)
    //       − 나이 불리. 시민별 난수(질병값) ≥ 저항 → **패배 = Level 상승 + DiseasedTag enable**.
    //       (구 체력·인내 저항 항은 유저의 새 3항 공식으로 대체 — 필요 시 재도입.)
    //    ② 자연 회복(SicknessTickJob) — 앓는 동안 극저속. Level≤0 → DiseasedTag disable(완치).
    //    ③ 입원 회복(SicknessReliefJob) — 병원 방문(Disease relief) 체류 적분(배속). 완치 시
    //       타이머 당김(조기 퇴원) + DiseasedTag disable.
    //
    //  헬스케어(CitizenHealthcare): "건물이 보유한 값"(오라 맵)을 **현재 건물 커버로 통째 교체**
    //  (머티리얼라이즈 — 가감 아님, 드리프트 없음). 유일 용도 = 이 저항 입력(컨디션 무연결).
    //
    //  파이프라인: [이 시스템] → NeedDecision(DiseaseRoute) → ServiceSearch → Movement.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AuraCoverageSystem))]
    [UpdateBefore(typeof(NeedDecisionSystem))]
    public partial struct SicknessSystem : ISystem
    {
        int _lastCheckHour;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<CitizenTag>();
            _lastCheckHour = -1;
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();

            // ── 회복(자연 + 입원) — 게임초 dt, 앓는(DiseasedTag) 시민만 처리 ──
            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지

            state.Dependency = new SicknessTickJob   { Dt = gameDt }
                .ScheduleParallel(state.Dependency);
            state.Dependency = new SicknessReliefJob { Dt = gameDt, Now = clock.TotalSeconds }
                .ScheduleParallel(state.Dependency);

            // ── 발병 체크 — 시간이 바뀔 때 1회. 위상 분산(index%3 == Hour%3)으로 각 시민이
            //    게임 3시간에 정확히 한 번 체크 = CPU 균등 + 결정적 주기. ──
            if (clock.Hour == _lastCheckHour) return;
            _lastCheckHour = clock.Hour;

            bool haveAura = SystemAPI.TryGetSingleton<AuraCoverage>(out var aura)
                            && aura.Map.IsCreated;
            if (!haveAura) return;   // 오라 front 준비 전(첫 틱)엔 체크 생략

            int day = (int)math.floor(clock.TotalSeconds / math.max(1f, clock.SecondsPerDay));
            state.Dependency = new DiseaseCheckJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Aura     = aura.Map,
                Hour     = clock.Hour,
                CheckSalt = day * 24 + clock.Hour,   // 체크 시점별 결정적 난수 salt
            }.ScheduleParallel(state.Dependency);
        }
    }

    // ── 자연 회복(느림) — 앓는 시민(DiseasedTag)만. Level≤0 → 완치(DiseasedTag disable). ──
    //   ⚠ 입원 중(AtDestination)은 SicknessReliefJob이 회복+조기 퇴원(좌석 반납)을 전담한다.
    //   여기서 자연 회복이 먼저 완치시켜 DiseasedTag를 끄면, Relief가 [WithAll(DiseasedTag)]에서
    //   같은 프레임 제외돼 ActionEndTime 당김(조기 퇴원)이 스킵 → 완치자가 좌석을 dwell 만료까지
    //   붙잡는다(병상 누수). 병원 밖 앓는 시민만 자연 회복(좌석 없으니 퇴원 처리 불요).
    [BurstCompile]
    [WithAll(typeof(CitizenTag), typeof(DiseasedTag))]
    public partial struct SicknessTickJob : IJobEntity
    {
        public float Dt;

        void Execute(ref CitizenSickness s, in CitizenState st, EnabledRefRW<DiseasedTag> diseased)
        {
            if (st.Activity == CitizenActivity.AtDestination) return;   // 입원 = Relief 전담
            if (s.Rate <= 0f) s.Rate = 0.0005f;   // 구세이브 zero-init 부트스트랩
            s.Level = math.max(0f, s.Level - s.Rate * Dt);
            if (s.Level <= 0f) diseased.ValueRW = false;   // 완치 → 상태 해제
        }
    }

    // ── 입원 회복(체류 적분, 공원 동형) — 완치 시 타이머 당김(조기 퇴원) + 상태 해제. ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag), typeof(DiseasedTag))]
    public partial struct SicknessReliefJob : IJobEntity
    {
        // 입원 회복률(게임초당) — 자연 회복의 6배(병세 0.8 ≈ 4.4게임시간 입원).
        //   ⚠ 병원 질(StaffEffect) 반영은 후속 — ServiceTarget 효과 스냅샷 복사 때.
        const float HospitalReliefRate = 0.003f;

        public float  Dt;
        public double Now;

        void Execute(ref CitizenSickness s, ref CitizenState st, in ServiceTarget target,
                     EnabledRefRW<DiseasedTag> diseased)
        {
            if (st.Activity != CitizenActivity.AtDestination) return;
            if ((target.Relief & NeedType.Disease) == NeedType.None) return;

            s.Level = math.max(0f, s.Level - HospitalReliefRate * Dt);

            if (s.Level <= 0f)
            {
                diseased.ValueRW = false;                       // 완치 → 상태 해제
                if (Now < st.ActionEndTime) st.ActionEndTime = Now;   // 조기 퇴원(자리 반납은 Movement)
            }
        }
    }

    // ── 발병 체크 — 비질병(WithDisabled)만 위상 분산(index%3==Hour%3) 체크. 헬스케어
    //    머티리얼라이즈 + 저항 vs 시민별 난수(질병값). 패배 = Level 상승 + DiseasedTag enable.
    //    ([WithDisabled] + EnabledRefRW = "비활성 순회 후 활성화" 표준 패턴 — 멤버십 명시적.) ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    [WithDisabled(typeof(DiseasedTag))]
    public partial struct DiseaseCheckJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public NativeHashMap<int4, int> Aura;   // int4(owner,x,y,reliefBit)→품질 permille
        public int Hour;
        public int CheckSalt;

        // ⚠ 밸런싱 #1 대상 — 3게임시간 주기(하루 8체크)에 맞춘 임시 상수(과속 방지).
        const float BaseResist   = 0.98f;   // 기본 저항(젊고 미커버 ≈ 2%/체크 발병)
        const float HealthcareMax = 0.02f;   // 헬스케어 커버 시 가산(현재 CitizenHealthcare.Value 상한)
        const float AgePenaltyMax = 0.08f;   // 나이 불리(40세 0 → 90세 최대)
        const float SickLevel    = 0.80f;   // 패배 시 병세(일괄)

        void Execute(Entity e, ref CitizenSickness s, ref CitizenHealthcare hc,
                     in CitizenState st, in CitizenAge age, EnabledRefRW<DiseasedTag> diseased)
        {
            if (((uint)e.Index % 3u) != (uint)(Hour % 3)) return;   // 내 위상만(3시간 주기)

            // 헬스케어 머티리얼라이즈 — 현재 건물 셀의 병원 오라(PoorHealthcare) 서비스 품질로 교체.
            //   가감이 아니라 통째 replace(드리프트 없음). 미커버/이동 중 = 0.
            //   관리형 모델(2026-07-15): 비례 — healthcare = HealthcareMax × v(품질 permille/1000).
            //   극단 동형: v=1 → HealthcareMax(구 '커버') / v=0 → 0(구 '미커버').
            float healthcare = 0f;
            Entity at = st.CurrentBuilding;
            if (at != Entity.Null && FpLookup.HasComponent(at))
            {
                var fp = FpLookup[at];
                int bit = math.tzcnt((ulong)NeedType.PoorHealthcare);
                if (Aura.TryGetValue(new int4(fp.OwnerLocalId, fp.Origin.x, fp.Origin.y, bit), out int pm))
                    healthcare = HealthcareMax * (pm * 0.001f);
            }
            hc.Value = healthcare;

            // 저항 = base + 헬스케어 + 쾌적함(미구현 0) − 나이 불리. 질병값(난수) ≥ 저항 → 발병.
            float agePen = math.saturate((age.Years - 40f) / 50f);
            float comfort = 0f;   // TODO: 쾌적함 욕구 구현 시 여기 가산.
            float resist = math.saturate(BaseResist + healthcare + comfort - AgePenaltyMax * agePen);

            var rng = Unity.Mathematics.Random.CreateFromIndex(
                math.hash(new int2(e.Index, CheckSalt)) | 1u);
            if (rng.NextFloat() >= resist)
            {
                s.Level = math.max(s.Level, SickLevel);   // 패배 — 앓아누움(일괄)
                diseased.ValueRW = true;                   // 질병 상태 진입
            }
        }
    }
}
