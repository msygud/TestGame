using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  SicknessSystem — 질병 욕구 전담: 발병 롤 + 자연 회복 + 입원 치료
    //  (생애주기 v1, 2026-07-12 — 치안(현재 위치·일괄)과 공원(체류 적분)의 합성)
    //
    //  발병(DiseaseFightJob): 시민당 게임 1일 1회(위상 분산 — entity 해시 % 24 == 현재 시).
    //    "질병과 싸워 이겨야 함": 승률 = 기본 + 저항(체력·인내) − 나이 불리
    //    + 병원 근처 보정(**현재 위치**가 병원 오라(Disease 비트) 커버 안 — 치안과 동일 판정).
    //    패배 = Level 일괄 0.8(앓아누움 — 일괄 증감 철학). 이미 앓는 중이면 롤 없음.
    //    질병 위험은 모든 건물에 존재(위치 무관 롤, 커버가 보정만).
    //
    //  회복 2경로:
    //    · 자연: Rate(0.0005/게임초 ≈ 27게임시간) — 병원 없는 초기 도시 붕괴 방지(불사:
    //      죽지 않고 오래 앓을 뿐). SicknessTickJob.
    //    · 입원: 병원 방문(stamp 탐색 — Disease 비트) 후 체류 적분(HospitalReliefRate
    //      0.003/게임초 ≈ 4.4게임시간), 완치 시 타이머 당김 = **조기 퇴원**(공원 동형).
    //      병원 질(StaffEffect→회복 배속)은 후속(ServiceTarget 효과 스냅샷 복사 때).
    //
    //  노동: 병세 중 결근은 WorkforceProductivity가 cond.Health(투영)로 게이트 —
    //  공통 시스템은 질병을 모른다. 수요: 병원 미발견 병자 → Disease 비트 NoCoverage
    //  샘플(공통 CollectDemandJob — 병원 프리팹 등록 전엔 resolvable 가드가 침묵).
    //
    //  파이프라인: [이 시스템] → NeedDecision → ServiceSearch → Movement.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(AuraCoverageSystem))]
    [UpdateBefore(typeof(NeedDecisionSystem))]
    public partial struct SicknessSystem : ISystem
    {
        double _lastGameSec;
        int    _lastRollHour;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<CitizenTag>();
            _lastGameSec = -1;
            _lastRollHour = -1;
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();

            // ── 회복(자연 + 입원) — 게임초 누적 dt, ~매 프레임(값 변경뿐이라 저비용) ──
            float gameDt = SystemAPI.Time.DeltaTime * clock.TimeScale;
            if (gameDt <= 0f) return;   // 일시정지

            state.Dependency = new SicknessTickJob   { Dt = gameDt }
                .ScheduleParallel(state.Dependency);
            state.Dependency = new SicknessReliefJob { Dt = gameDt, Now = clock.TotalSeconds }
                .ScheduleParallel(state.Dependency);

            // ── 발병 롤 — 시간이 바뀔 때 1회, 그 시각을 배정받은 시민만(하루 1회/인) ──
            if (clock.Hour == _lastRollHour) return;
            _lastRollHour = clock.Hour;

            bool haveAura = SystemAPI.TryGetSingleton<AuraCoverage>(out var aura)
                            && aura.Map.IsCreated;
            if (!haveAura) return;   // 오라 front 준비 전(첫 틱)엔 롤 생략

            int day = (int)math.floor(clock.TotalSeconds / math.max(1f, clock.SecondsPerDay));
            state.Dependency = new DiseaseFightJob
            {
                FpLookup = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                Aura     = aura.Map,
                Hour     = clock.Hour,
                Day      = day,
            }.ScheduleParallel(state.Dependency);
        }
    }

    // ── 자연 회복(느림) — 앓는 동안 서서히. 구세이브 zero-init Threshold 부트스트랩. ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SicknessTickJob : IJobEntity
    {
        public float Dt;

        void Execute(ref CitizenSickness s)
        {
            if (s.Threshold <= 0f) { s.Threshold = 0.3f; if (s.Rate <= 0f) s.Rate = 0.0005f; }
            if (s.Level > 0f)
                s.Level = math.max(0f, s.Level - s.Rate * Dt);
        }
    }

    // ── 입원 치료(체류 적분, 공원 동형) — 완치 시 타이머 당김 = 조기 퇴원. ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct SicknessReliefJob : IJobEntity
    {
        // 입원 회복률(게임초당) — 자연 회복의 6배(병세 0.8 ≈ 4.4게임시간 입원).
        //   ⚠ 병원 질(StaffEffect) 반영은 후속 — ServiceTarget 효과 스냅샷 복사 때.
        const float HospitalReliefRate = 0.003f;

        public float  Dt;
        public double Now;

        void Execute(ref CitizenSickness s, ref CitizenState st, in ServiceTarget target)
        {
            if (st.Activity != CitizenActivity.AtDestination) return;
            if ((target.Relief & NeedType.Disease) == NeedType.None) return;

            s.Level = math.max(0f, s.Level - HospitalReliefRate * Dt);

            if (s.Level <= 0f && Now < st.ActionEndTime)
                st.ActionEndTime = Now;   // 완치 → 조기 퇴원(자리 반납은 Movement가 처리)
        }
    }

    // ── 발병 롤 — 배정 시각(entity 해시 % 24)의 시민만, 하루 1회. 전부 시민 로컬 +
    //    오라 front RO(치안 판정과 동일 계약). 결정적 rng(시민×일 시드). ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct DiseaseFightJob : IJobEntity
    {
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public NativeHashMap<int3, ulong> Aura;
        public int Hour;
        public int Day;

        // ⚠ 테스트 성향 상수(체감 우선) — 밸런싱 #1 대상.
        const float BaseWin     = 0.85f;   // 기본 승률(젊고 건강 ≈ 92%)
        const float ResistGain  = 0.15f;   // 저항(체력·인내) 기여
        const float AgePenalty  = 0.50f;   // 나이 불리(40세 0 → 90세 최대)
        const float HospitalAid = 0.15f;   // 현재 위치가 병원 오라 커버면 가산
        const float SickLevel   = 0.80f;   // 패배 시 병세(일괄)

        void Execute(Entity e, ref CitizenSickness s, in CitizenState st,
                     in CitizenAge age, in CitizenAttributes attr)
        {
            if (((uint)e.Index % 24u) != (uint)Hour) return;   // 내 배정 시각만(위상 분산)
            if (s.IsActive) return;                             // 이미 앓는 중 — 롤 없음

            // 병원 근처 보정 — 현재 위치의 오라에 Disease 비트(병원)가 있는가.
            bool nearHospital = false;
            Entity at = st.CurrentBuilding;
            if (at != Entity.Null && FpLookup.HasComponent(at))
            {
                var fp = FpLookup[at];
                nearHospital = Aura.TryGetValue(
                                   new int3(fp.OwnerLocalId, fp.Origin.x, fp.Origin.y), out ulong bits)
                               && (bits & (ulong)NeedType.Disease) != 0;
            }

            float resist = 0.5f * attr.PhysiqueN + 0.5f * attr.ResilienceN;
            float agePen = math.saturate((age.Years - 40f) / 50f);
            float win    = math.saturate(BaseWin + ResistGain * resist
                                         - AgePenalty * agePen
                                         + (nearHospital ? HospitalAid : 0f));

            var rng = Unity.Mathematics.Random.CreateFromIndex(
                math.hash(new int2(e.Index, Day)) | 1u);
            if (rng.NextFloat() >= win)
                s.Level = math.max(s.Level, SickLevel);   // 패배 — 앓아누움(일괄)
        }
    }
}
