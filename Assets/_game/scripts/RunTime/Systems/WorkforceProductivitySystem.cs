using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  WorkforceProductivitySystem — 노동력 → 생산 속도 (고용 2차, 2026-07-06)
    //
    //  "일꾼 없는 공장은 안 돈다": 직장(WorkplaceBuilding+ProductionJob)의 SkillFactor를
    //  현재 출근(AtWork) 노동자들의 개인 생산성 합 ÷ 정원으로 갱신한다.
    //    개인 생산성 = 숙련 계수(0.5 + Skill[직업]/100) × 컨디션 계수(0.5 + 0.5×Energy)
    //    → 무인 = 0(생산 정지, 진행은 클램프 대기) / 신입 만석 ≈ 0.5× / 베테랑 만석 ≈ 1.5×
    //  능력치는 여기 직접 안 들어옴(숙련 성장속도로만 — 이중 계산 방지, 설계 확정).
    //
    //  읽기/쓰기 분리: 이 시스템이 SkillFactor를 '쓰고' ProductionSystem은 '읽기만'
    //  (기존 인터페이스 그대로 — 생산 코드 무수정).
    //
    //  실행 모델: HourChanged 게이트(저빈도, 통계적 원칙 — 교대 중간 변동은 다음 시간에 반영).
    //    ① 수집 잡(병렬): AtWork 시민 → (직장, 개인 생산성) 큐
    //    ② 적용 잡(단일): 큐 합산 → 직장별 SkillFactor 기록(ComponentLookup 단일 스레드 쓰기)
    //    전부 잡 — 메인 블로킹 없음. WorkplaceBuilding 없는 생산 건물(무인 설계)은 미갱신(1.0 유지).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ProductionSystem))]
    public partial struct WorkforceProductivitySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<ProductionJob>();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.GetSingleton<GameClock>().HourChanged) return;

            // 대상 직장 목록(엔티티+정원) — 메인 수집(수십 개, 소량).
            var workplaces = new NativeList<WorkplaceInfo>(64, Allocator.TempJob);
            foreach (var (occ, e) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>>()
                         .WithAll<WorkplaceBuilding, ProductionJob>()
                         .WithEntityAccess())
            {
                workplaces.Add(new WorkplaceInfo
                { Building = e, Capacity = math.max(1, occ.ValueRO.Capacity) });
            }
            if (workplaces.IsEmpty) { workplaces.Dispose(); return; }

            var prods = new NativeQueue<WorkerProd>(Allocator.TempJob);

            var collectH = new CollectWorkerProdJob
            {
                Out = prods.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);

            state.Dependency = new ApplyWorkforceJob
            {
                In         = prods,
                Workplaces = workplaces.AsArray(),
                ProdLookup = SystemAPI.GetComponentLookup<ProductionJob>(false),
            }.Schedule(collectH);

            prods.Dispose(state.Dependency);
            workplaces.Dispose(state.Dependency);
        }

        public struct WorkplaceInfo { public Entity Building; public int Capacity; }
        public struct WorkerProd    { public Entity Building; public float Prod; }

        // ── ① 출근 노동자의 개인 생산성 수집(병렬) ─────────────────────────────
        //   컨디션 계수 = 직업별 가중 욕구 만족도(JobAptitude.ConditionFactor —
        //   육체직은 포만 민감, 지식직은 피로 민감). "어떤 욕구를 채울지"가 노동
        //   구성에 따라 전략이 되는 축(2026-07-07 유저 설계).
        //   ※ 쿼리가 Hunger를 요구 — 휴먼 전용. 메카닉 팩션 도입 시 자기 욕구 조합의
        //     수집 잡을 하나 더 스케줄(욕구 시스템과 같은 팩션 분리 패턴).
        [BurstCompile]
        [WithAll(typeof(CitizenTag))]
        public partial struct CollectWorkerProdJob : IJobEntity
        {
            public NativeQueue<WorkerProd>.ParallelWriter Out;

            void Execute(in CitizenState st, in JobData job,
                         in CitizenSkills skills, in CitizenConditions cond, in Hunger hunger)
            {
                if (st.Activity != CitizenActivity.AtWork) return;
                if (st.CurrentBuilding == Entity.Null) return;

                float skill   = skills.Get(job.Job);
                float satiety = 1f - hunger.Level;   // 포만도 = 욕구 만족도(부정 게이지 반전)
                float prod    = (0.5f + skill / 100f)
                                * JobAptitude.ConditionFactor(job.Job, cond.Energy, satiety);
                Out.Enqueue(new WorkerProd { Building = st.CurrentBuilding, Prod = prod });
            }
        }

        // ── ② 직장별 합산 → SkillFactor = Σ개인 생산성 / 정원 ──────────────────
        [BurstCompile]
        public struct ApplyWorkforceJob : IJob
        {
            public NativeQueue<WorkerProd> In;
            [ReadOnly] public NativeArray<WorkplaceInfo> Workplaces;
            public ComponentLookup<ProductionJob> ProdLookup;

            public void Execute()
            {
                var sums = new NativeHashMap<Entity, float>(Workplaces.Length * 2, Allocator.Temp);
                while (In.TryDequeue(out var w))
                {
                    sums.TryGetValue(w.Building, out float s);
                    sums[w.Building] = s + w.Prod;
                }

                for (int i = 0; i < Workplaces.Length; i++)
                {
                    var wp = Workplaces[i];
                    if (!ProdLookup.HasComponent(wp.Building)) continue;

                    sums.TryGetValue(wp.Building, out float sum);
                    var pj = ProdLookup[wp.Building];
                    pj.SkillFactor = sum / wp.Capacity;   // 무인 = 0 → 생산 정지
                    ProdLookup[wp.Building] = pj;
                }

                sums.Dispose();
            }
        }
    }
}
