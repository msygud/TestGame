using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Stage A — 시민 배정 (Home / Work Assignment)
    //
    //  미배정 시민(UnassignedTag)을 빈 건물에 배정한다.
    //
    //  설계(§0):
    //    - 주쿼리 = "변하는 데이터": UnassignedTag를 가진 시민만 순회.
    //      배정 완료 시 태그 제거 → 평상시 쿼리가 비어 즉시 반환(전체 스캔 없음).
    //    - 배정은 가끔(새 시민 발생 시) → 건물 Occupancy 접근 빈도 낮아 허용.
    //    - 시민 Residence/Job, 건물 Occupancy 수정은 "값 변경"이라 구조 변경 아님
    //      → RefRW / ComponentLookup로 직접 수정.
    //    - 단, UnassignedTag 제거는 구조 변경 → EntityCommandBuffer로 처리.
    //    - 대량 동시 생성 대비 프레임당 버짓(MaxAssignPerFrame).
    //
    //  점유 의미: 거주/소속 점유는 장기 유지(방문 예약과 달리 해제하지 않음).
    //
    //  초기 배치(P1):
    //    집을 "막 배정받은 순간 한 번만" 그 집에 앉힌다(Activity=AtHome,
    //    CurrentBuilding=Home). ServiceSearchSystem이 현재 건물(CurrentBuilding)을
    //    검색 출발점으로 쓰므로, 자리를 잡아 줘야 정식 스폰 시민도 욕구 추구 시
    //    이동을 시작한다(미설정이면 CurrentBuilding=Null → 영영 못 움직임).
    //    Home!=Null이 된 다음 프레임부터 배정 분기를 건너뛰므로, 이후의 이동/도착
    //    상태(Traveling 등)를 덮어쓰지 않는다 — 즉 "한 번만" 보장.
    //
    //  한계/메모:
    //    - 빈 집이 부족하면 일부 시민은 태그를 유지한 채 다음 프레임 재시도.
    //      장기 미배정 = Homeless 욕구로 이어짐(Stage B/G).
    //    - "가장 먼저 찾은 빈 건물"에 배정(거리·선호 무시). 추후 §4 연결 캐싱과
    //      묶어 "가까운 집" 배정으로 고도화 가능.
    //    - 태그 제거 조건(2026-07-06 재정의): **집 배정 시점** — UnassignedTag = 집 없음 큐.
    //      직장은 큐에 있는 동안 덤으로 시도할 뿐, 고용 배정/재배정은 직장 건물이 생길 때
    //      별도 큐(JobData.Job==Unemployed 필터 등)로 분리 예정. 구 '집+직장 모두' 기준은
    //      직장이 없는 단계에서 큐가 영영 안 비어 전 시민 상시 순회를 유발했다(1.8만 실측).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenSpawnSystem))]
    public partial struct CitizenAssignmentSystem : ISystem
    {
        const int MaxAssignPerFrame = 256;   // 프레임당 버짓(스파이크 방지)

        public void OnUpdate(ref SystemState state)
        {
            // ── 1. 미배정 시민이 하나도 없으면 즉시 반환 ─────────────────────
            //  UnassignedTag 쿼리가 비면 건물 수집 비용조차 들이지 않음.
            var unassignedQuery = SystemAPI.QueryBuilder()
                .WithAll<CitizenTag, UnassignedTag>()
                .WithAllRW<CitizenResidence, JobData>()
                .Build();
            if (unassignedQuery.IsEmpty) return;

            // ── 2. 빈 집 수집 (소유자 포함 — 자기 소유자 건물에만 배정) ──────────
            //  ※ 소유자 무시 배정은 멀티팀에서 남의 집을 채워 영역/인구 회계를 오염시킴
            //    (이민 도입(2026-07-05)으로 실제 발생 가능해져 owner 일치로 교정).
            //  ※ 직장 배정은 EmploymentAssignmentSystem으로 분리(고용 1차, 2026-07-06).
            var freeHomes = new NativeList<BuildingSlot>(Allocator.Temp);

            foreach (var (occ, bf, e) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<BuildingFootprint>>()
                         .WithAll<ResidenceBuilding>()
                         .WithEntityAccess())
            {
                int free = occ.ValueRO.Capacity - occ.ValueRO.Current;
                if (free > 0)
                    freeHomes.Add(new BuildingSlot
                    { Building = e, Free = free, Owner = bf.ValueRO.OwnerLocalId });
            }

            if (freeHomes.Length == 0)
            {
                freeHomes.Dispose();
                return;
            }

            // ── 3. 매칭 (시민 소유자 == 건물 소유자) ─────────────────────────
            var ecb        = new EntityCommandBuffer(Allocator.Temp);
            var occLookup  = SystemAPI.GetComponentLookup<BuildingOccupancy>(false);

            int processed  = 0;

            foreach (var (res, cstate, owner, e) in
                     SystemAPI.Query<RefRW<CitizenResidence>,
                                     RefRW<CitizenState>, CitizenOwner>()
                         .WithAll<CitizenTag, UnassignedTag>()
                         .WithEntityAccess())
            {
                // 버짓은 '처리 수' 기준(2026-07-06) — 구 '완료 수' 기준은 완료가 0인 상황
                //   에서 발동하지 않아 매 프레임 전 시민을 순회했다(인구 1.8만 실측 정체).
                if (processed++ >= MaxAssignPerFrame) break;

                // 집 배정
                if (res.ValueRO.Home == Entity.Null &&
                    TakeSlot(ref freeHomes, owner.LocalId, occLookup, out Entity home))
                {
                    res.ValueRW.Home = home;

                    // P1: 막 집을 배정받은 "이 순간 한 번만" 집에 앉힌다.
                    //  다음 프레임부터 Home!=Null → 이 분기를 건너뛰므로 이동 상태를
                    //  덮지 않는다(이동 출발점 = CurrentBuilding 확보).
                    cstate.ValueRW.Activity        = CitizenActivity.AtHome;
                    cstate.ValueRW.CurrentBuilding = home;
                }

                // 태그 제거 = '집 배정' 기준(UnassignedTag = 주거 대기 큐).
                //   직장 배정은 EmploymentAssignmentSystem(JobSeekerTag 큐)이 독립 수행.
                if (res.ValueRO.Home != Entity.Null)
                    ecb.RemoveComponent<UnassignedTag>(e);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            freeHomes.Dispose();
        }

        // ── 빈 거주 슬롯 하나 점유 (소유자 일치만) ──────────────────────────
        //  선형 스캔 — 배정은 이벤트성 소량(프레임 버짓 256)이라 O(슬롯) 충분.
        static bool TakeSlot(
            ref NativeList<BuildingSlot>          slots,
            int                                   owner,
            ComponentLookup<BuildingOccupancy>    occLookup,
            out Entity                            building)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.Free <= 0 || slot.Owner != owner) continue;

                slot.Free--;
                slots[i] = slot;

                var occ = occLookup[slot.Building];
                occ.Current++;                       // 장기 거주 점유
                occLookup[slot.Building] = occ;

                building = slot.Building;
                return true;
            }
            building = Entity.Null;
            return false;
        }

        // ── 수집용 임시 구조 ────────────────────────────────────────────────
        struct BuildingSlot
        {
            public Entity Building;
            public int    Free;
            public int    Owner;   // BuildingFootprint.OwnerLocalId
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  EmploymentAssignmentSystem — 고용 배정 (고용 1차, 2026-07-06)
    //
    //  구직 시민(JobSeekerTag)을 owner 일치 직장(WorkplaceBuilding)의 빈 정원에 배정.
    //  주거 배정(CitizenAssignmentSystem, UnassignedTag)과 큐를 분리 — 두 큐가 섞이면
    //  한쪽 부족이 다른 쪽 순회를 인질로 잡는다(직장 0개 시절의 상시 순회 실측).
    //
    //  · 배정 = Residence.Work + JobData.Job(직장이 직업 부여) + 직장 Occupancy.Current++
    //    (장기 점유 — 거주와 동일 메커니즘). 태그 제거 = 직장 배정 시.
    //  · 재고용: 직장 소실 시 DeadReferenceReclaim이 JobSeekerTag 재부착 → 큐 복귀.
    //  · 노숙 여부와 무관하게 고용 가능(집 없어도 일함 — 출퇴근 트리가 처리).
    //  · 출퇴근/근무는 CitizenMovementSystem의 활동 우선순위 트리 소관 — 여기선 배정만.
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenAssignmentSystem))]
    public partial struct EmploymentAssignmentSystem : ISystem
    {
        const int MaxAssignPerFrame = 128;   // 처리 수 기준 버짓

        public void OnUpdate(ref SystemState state)
        {
            // 구직자가 없으면 즉시 반환(태그 큐 — 평상시 비어 있음).
            var seekerQuery = SystemAPI.QueryBuilder()
                .WithAll<CitizenTag, JobSeekerTag>()
                .WithAllRW<CitizenResidence, JobData>()
                .Build();
            if (seekerQuery.IsEmpty) return;

            // 빈 일자리 수집(소유자 포함).
            var freeWorks = new NativeList<WorkSlot>(Allocator.Temp);
            foreach (var (occ, work, bf, e) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<WorkplaceBuilding>,
                                     RefRO<BuildingFootprint>>()
                         .WithEntityAccess())
            {
                int free = occ.ValueRO.Capacity - occ.ValueRO.Current;
                if (free > 0)
                    freeWorks.Add(new WorkSlot
                    {
                        Building = e,
                        Free     = free,
                        Job      = work.ValueRO.ProvidedJob,
                        Owner    = bf.ValueRO.OwnerLocalId,
                    });
            }
            if (freeWorks.Length == 0) { freeWorks.Dispose(); return; }

            var ecb       = new EntityCommandBuffer(Allocator.Temp);
            var occLookup = SystemAPI.GetComponentLookup<BuildingOccupancy>(false);
            int processed = 0;

            foreach (var (res, job, owner, e) in
                     SystemAPI.Query<RefRW<CitizenResidence>, RefRW<JobData>, CitizenOwner>()
                         .WithAll<CitizenTag, JobSeekerTag>()
                         .WithEntityAccess())
            {
                if (processed++ >= MaxAssignPerFrame) break;
                if (res.ValueRO.Work != Entity.Null)          // 이미 취업(방어) → 태그만 정리
                {
                    ecb.RemoveComponent<JobSeekerTag>(e);
                    continue;
                }

                if (TakeWorkSlot(ref freeWorks, owner.LocalId, occLookup,
                        out Entity work, out JobType providedJob, out byte shift))
                {
                    res.ValueRW.Work  = work;
                    job.ValueRW.Job   = providedJob;          // 직장이 직업 부여
                    job.ValueRW.Shift = shift;                // 교대 슬롯(라운드로빈)
                    ecb.RemoveComponent<JobSeekerTag>(e);
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            freeWorks.Dispose();
        }

        // ── 빈 일자리 슬롯 하나 점유 (소유자 일치만) ────────────────────────
        static bool TakeWorkSlot(
            ref NativeList<WorkSlot>              slots,
            int                                   owner,
            ComponentLookup<BuildingOccupancy>    occLookup,
            out Entity                            building,
            out JobType                           job,
            out byte                              shift)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = slots[i];
                if (slot.Free <= 0 || slot.Owner != owner) continue;

                slot.Free--;
                slots[i] = slot;

                var occ = occLookup[slot.Building];
                // 교대 슬롯 = 이번 고용 이전 인원 % 교대수(라운드로빈 → 교대 균등 분산).
                int preCount = occ.Current;
                occ.Current++;                       // 장기 고용 점유
                occLookup[slot.Building] = occ;

                building = slot.Building;
                job      = slot.Job;
                shift    = (byte)(preCount % math.max(1, JobSchedule.ShiftCount(slot.Job)));
                return true;
            }
            building = Entity.Null;
            job      = JobType.Unemployed;
            shift    = 0;
            return false;
        }

        struct WorkSlot
        {
            public Entity  Building;
            public int     Free;
            public JobType Job;
            public int     Owner;
        }
    }
}
