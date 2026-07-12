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
    //  "일꾼 없는 직장은 안 돈다": 유인 직장(WorkplaceBuilding)의 **StaffEffect**(범용
    //  직무 효과, 2026-07-12 일반화 — 산출 = 그 직무의 긍정 효과)를 현재 출근(AtWork)
    //  노동자들의 개인 생산성 합 ÷ 정원으로 갱신한다.
    //    개인 생산성 = 숙련 계수(0.5 + Skill[직업]/100)
    //                × 컨디션 계수(직업별 욕구 가중: 피로·포만·치안 — JobAptitude)
    //    → 무인 = 0 / 신입 만석 ≈ 0.5× / 베테랑 만석 ≈ 1.5×
    //  능력치는 여기 직접 안 들어옴 — 숙련 성장속도로만(2026-07-12 재확정: "물고 물리는
    //  관계 해소" — 능력+근무시간→숙련→산출의 외길, 이중 경로 철회).
    //  소비자별 해석: 생산=속도 승수 / 서비스=영업 게이트 / (미래) 여가 가산·검거율·물류.
    //
    //  읽기/쓰기 분리: 이 시스템이 StaffEffect를 '쓰고' 소비자들은 '읽기만'.
    //
    //  실행 모델: HourChanged 게이트(저빈도, 통계적 원칙 — 교대 중간 변동은 다음 시간에 반영).
    //    ① 수집 잡(병렬): AtWork 시민 → (직장, 개인 생산성) 큐
    //    ② 적용 잡(단일): 큐 합산 → 직장별 StaffEffect 기록(ComponentLookup 단일 스레드 쓰기)
    //    전부 잡 — 메인 블로킹 없음. WorkplaceBuilding 없는 생산 건물(무인 설계)은 미갱신(1.0 유지).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ProductionSystem))]
    public partial struct WorkforceProductivitySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<WorkplaceBuilding>();
        }

        public void OnUpdate(ref SystemState state)
        {
            // 마이그레이션(구세이브, idempotent): StaffEffect 없는 유인 직장에 일괄 부착 —
            //   없으면 구세이브 식당이 영업 게이트를 영구 통과(무인 폐점 무력화)한다.
            var migrateQ = SystemAPI.QueryBuilder()
                .WithAll<WorkplaceBuilding>().WithNone<StaffEffect>().Build();
            if (!migrateQ.IsEmpty)
                state.EntityManager.AddComponent<StaffEffect>(migrateQ);

            if (!SystemAPI.GetSingleton<GameClock>().HourChanged) return;

            // 대상 직장 목록(엔티티+정원) — 메인 수집(수십 개, 소량).
            var workplaces = new NativeList<WorkplaceInfo>(64, Allocator.TempJob);
            foreach (var (occ, e) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>>()
                         .WithAll<WorkplaceBuilding, StaffEffect>()
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
                In          = prods,
                Workplaces  = workplaces.AsArray(),
                StaffLookup = SystemAPI.GetComponentLookup<StaffEffect>(false),
            }.Schedule(collectH);

            prods.Dispose(state.Dependency);
            workplaces.Dispose(state.Dependency);
        }

        public struct WorkplaceInfo { public Entity Building; public int Capacity; }
        public struct WorkerProd    { public Entity Building; public float Prod; }

        // ── ① 출근 노동자의 개인 생산성 수집(병렬) ─────────────────────────────
        //   컨디션 계수 = 직업별 가중 욕구 만족도(JobAptitude.ConditionFactor —
        //   육체직은 포만 민감, 지식직은 피로·치안 민감). "어떤 욕구를 채울지"가 노동
        //   구성에 따라 전략이 되는 축(2026-07-07 유저 설계).
        //   ※ 합의 골격(2026-07-12): 욕구 원천(Hunger/CitizenSafety)을 룩업하지 않는다 —
        //     ConditionUpdateSystem이 느슨한 주기로 투영한 **CitizenConditions 스냅샷만**
        //     청크-선형으로 읽음(룩업 0, 팩션 무관 — 구 Hunger 쿼리 요구 은퇴).
        [BurstCompile]
        [WithAll(typeof(CitizenTag))]
        public partial struct CollectWorkerProdJob : IJobEntity
        {
            public NativeQueue<WorkerProd>.ParallelWriter Out;

            void Execute(in CitizenState st, in JobData job,
                         in CitizenSkills skills, in CitizenConditions cond)
            {
                if (st.Activity != CitizenActivity.AtWork) return;
                if (st.CurrentBuilding == Entity.Null) return;

                // 결근(생애주기 v1): 병세 중(건강 투영 < 0.5) 노동 기여 0 — "일 못함".
                //   입원 중은 AtWork가 아니라 자동 제외; 이 게이트는 병원 못 간 병자용.
                if (cond.Health < 0.5f) return;

                float skill = skills.Get(job.Job);
                // 산출 = 숙련 × 컨디션(욕구 가중 스냅샷) — 능력치는 숙련 성장으로만(외길).
                float prod  = (0.5f + skill / 100f)
                              * JobAptitude.ConditionFactor(job.Job, cond.Energy, cond.Satiety, cond.Safety);
                Out.Enqueue(new WorkerProd { Building = st.CurrentBuilding, Prod = prod });
            }
        }

        // ── ② 직장별 합산 → StaffEffect.Factor = Σ개인 생산성 / 정원 ────────────
        [BurstCompile]
        public struct ApplyWorkforceJob : IJob
        {
            public NativeQueue<WorkerProd> In;
            [ReadOnly] public NativeArray<WorkplaceInfo> Workplaces;
            public ComponentLookup<StaffEffect> StaffLookup;

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
                    if (!StaffLookup.HasComponent(wp.Building)) continue;

                    sums.TryGetValue(wp.Building, out float sum);
                    StaffLookup[wp.Building] = new StaffEffect
                    { Factor = sum / wp.Capacity };   // 무인 = 0 → 생산 정지·영업 중단
                }

                sums.Dispose();
            }
        }
    }
}
