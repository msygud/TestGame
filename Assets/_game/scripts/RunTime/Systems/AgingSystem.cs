using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AgingSystem — 나이 증가 (생애주기 v1, 2026-07-12)
    //
    //  불사 세계의 마찰 축: 시민은 죽지 않으므로(영구 원칙) 무한 숙련의 루즈함을
    //  나이가 "관리 비용"으로 상쇄한다 — 늙을수록 피로가 빨리 쌓이고(EnergyTickJob
    //  가중) 질병 싸움에 불리(DiseaseFightSystem). 사망 없음: 최악도 병원행일 뿐.
    //
    //  증가: DayChanged마다 YearsPerGameDay(⚠ 테스트값 — 밸런싱 #1). 구세이브
    //  마이그레이션: CitizenAge/CitizenSickness 없는 시민에 일괄 부착(zero-init) 후
    //  틱 잡이 기본값으로 부트스트랩(Years<1 → 35세, Threshold≤0 → 0.3).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AgingSystem : ISystem
    {
        // ⚠ 테스트 노화 속도: 게임 1일 = +0.5세(이틀 = 1세) — 밸런싱 #1 대상.
        const float YearsPerGameDay = 0.5f;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 마이그레이션(구세이브, idempotent) — 나이·질병 컴포넌트 일괄 부착(zero-init).
            var ageQ = SystemAPI.QueryBuilder()
                .WithAll<CitizenTag>().WithNone<CitizenAge>().Build();
            if (!ageQ.IsEmpty) state.EntityManager.AddComponent<CitizenAge>(ageQ);
            var sickQ = SystemAPI.QueryBuilder()
                .WithAll<CitizenTag>().WithNone<CitizenSickness>().Build();
            if (!sickQ.IsEmpty) state.EntityManager.AddComponent<CitizenSickness>(sickQ);

            // 상태화 마이그레이션(2026-07-13): DiseasedTag/CitizenHealthcare 부착.
            //   DiseasedTag는 AddComponent가 enabled로 붙이므로, 부착 프레임에 병세로 정규화
            //   (구세이브 건강 시민 Level=0 → disable). CitizenHealthcare는 zero-init(0=미커버).
            var hcQ = SystemAPI.QueryBuilder()
                .WithAll<CitizenTag>().WithNone<CitizenHealthcare>().Build();
            if (!hcQ.IsEmpty) state.EntityManager.AddComponent<CitizenHealthcare>(hcQ);
            // ⚠ WithAbsent (WithNone 아님): DiseasedTag는 IEnableableComponent라 WithNone은
            //   "부재 OR 비활성"을 매칭 → 건강 시민(present-but-disabled)까지 잡혀 dtQ가 영영
            //   안 비고 마이그레이션 블록이 매 프레임 재실행(구조 변경+전체 순회 스톨). WithAbsent는
            //   구조적 부재만 매칭 → 구세이브 첫 프레임 1회로 종료.
            var dtQ = SystemAPI.QueryBuilder()
                .WithAll<CitizenTag>().WithAbsent<DiseasedTag>().Build();
            if (!dtQ.IsEmpty)
            {
                state.EntityManager.AddComponent<DiseasedTag>(dtQ);   // enabled by default
                // 방금 부착된 시민의 토글을 병세로 정규화(SetComponentEnabled = 비구조적, 순회 안전).
                foreach (var (sick, e) in SystemAPI
                             .Query<RefRO<CitizenSickness>>().WithAll<CitizenTag>().WithEntityAccess())
                    state.EntityManager.SetComponentEnabled<DiseasedTag>(e, sick.ValueRO.Level > 0f);
            }

            if (!SystemAPI.GetSingleton<GameClock>().DayChanged) return;

            state.Dependency = new AgeTickJob { Delta = YearsPerGameDay }
                .ScheduleParallel(state.Dependency);
        }
    }

    // ── 하루 1회 노화. Years<1 = 구세이브 zero-init → 35세 부트스트랩. ──
    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct AgeTickJob : IJobEntity
    {
        public float Delta;

        void Execute(ref CitizenAge age)
        {
            if (age.Years < 1f) age.Years = 35f;   // 마이그레이션 기본값
            age.Years += Delta;
        }
    }
}
