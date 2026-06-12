using Unity.Collections;
using Unity.Entities;

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
    //    - 빈 집/직장이 부족하면 일부 시민은 태그를 유지한 채 다음 프레임 재시도.
    //      장기 미배정 = Homeless/Unemployed 욕구로 이어짐(Stage B/G).
    //    - "가장 먼저 찾은 빈 건물"에 배정(거리·선호 무시). 추후 §4 연결 캐싱과
    //      묶어 "가까운 집" 배정으로 고도화 가능.
    //    - 태그 제거 조건: 집·직장이 "모두" 배정된 시점. 한쪽만 되면 태그 유지.
    //      (집만 배정돼도 위 초기 배치는 이미 적용되어 거주·이동은 가능.)
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

            // ── 2. 빈 집 / 빈 직장 수집 ────────────────────────────────────
            var freeHomes = new NativeList<BuildingSlot>(Allocator.Temp);
            var freeWorks = new NativeList<WorkSlot>(Allocator.Temp);

            foreach (var (occ, e) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>>()
                         .WithAll<ResidenceBuilding>()
                         .WithEntityAccess())
            {
                int free = occ.ValueRO.Capacity - occ.ValueRO.Current;
                if (free > 0)
                    freeHomes.Add(new BuildingSlot { Building = e, Free = free });
            }

            foreach (var (occ, work, e) in
                     SystemAPI.Query<RefRO<BuildingOccupancy>, RefRO<WorkplaceBuilding>>()
                         .WithEntityAccess())
            {
                int free = occ.ValueRO.Capacity - occ.ValueRO.Current;
                if (free > 0)
                    freeWorks.Add(new WorkSlot
                    {
                        Building = e,
                        Free     = free,
                        Job      = work.ValueRO.ProvidedJob,
                    });
            }

            // 빈 건물이 둘 다 없으면 더 할 일 없음.
            if (freeHomes.Length == 0 && freeWorks.Length == 0)
            {
                freeHomes.Dispose();
                freeWorks.Dispose();
                return;
            }

            // ── 3. 매칭 ────────────────────────────────────────────────────
            var ecb        = new EntityCommandBuffer(Allocator.Temp);
            var occLookup  = SystemAPI.GetComponentLookup<BuildingOccupancy>(false);

            int assigned   = 0;
            int homeCursor = 0;
            int workCursor = 0;

            foreach (var (res, job, cstate, e) in
                     SystemAPI.Query<RefRW<CitizenResidence>, RefRW<JobData>,
                                     RefRW<CitizenState>>()
                         .WithAll<CitizenTag, UnassignedTag>()
                         .WithEntityAccess())
            {
                if (assigned >= MaxAssignPerFrame) break;

                // 집 배정
                if (res.ValueRO.Home == Entity.Null &&
                    TakeSlot(ref freeHomes, ref homeCursor, occLookup, out Entity home))
                {
                    res.ValueRW.Home = home;

                    // P1: 막 집을 배정받은 "이 순간 한 번만" 집에 앉힌다.
                    //  다음 프레임부터 Home!=Null → 이 분기를 건너뛰므로 이동 상태를
                    //  덮지 않는다(이동 출발점 = CurrentBuilding 확보).
                    cstate.ValueRW.Activity        = CitizenActivity.AtHome;
                    cstate.ValueRW.CurrentBuilding = home;
                }

                // 직장 배정
                if (res.ValueRO.Work == Entity.Null &&
                    TakeWorkSlot(ref freeWorks, ref workCursor, occLookup,
                                 out Entity work, out JobType providedJob))
                {
                    res.ValueRW.Work = work;
                    job.ValueRW.Job  = providedJob;   // 직장이 직업 부여
                }

                // 집·직장 모두 배정되면 태그 제거(구조 변경 → ECB)
                if (res.ValueRO.Home != Entity.Null && res.ValueRO.Work != Entity.Null)
                {
                    ecb.RemoveComponent<UnassignedTag>(e);
                    assigned++;
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            freeHomes.Dispose();
            freeWorks.Dispose();
        }

        // ── 빈 거주 슬롯 하나 점유 ──────────────────────────────────────────
        static bool TakeSlot(
            ref NativeList<BuildingSlot>          slots,
            ref int                               cursor,
            ComponentLookup<BuildingOccupancy>    occLookup,
            out Entity                            building)
        {
            while (cursor < slots.Length)
            {
                var slot = slots[cursor];
                if (slot.Free > 0)
                {
                    slot.Free--;
                    slots[cursor] = slot;

                    var occ = occLookup[slot.Building];
                    occ.Current++;                       // 장기 거주 점유
                    occLookup[slot.Building] = occ;

                    building = slot.Building;
                    return true;
                }
                cursor++;
            }
            building = Entity.Null;
            return false;
        }

        // ── 빈 일자리 슬롯 하나 점유 ────────────────────────────────────────
        static bool TakeWorkSlot(
            ref NativeList<WorkSlot>              slots,
            ref int                               cursor,
            ComponentLookup<BuildingOccupancy>    occLookup,
            out Entity                            building,
            out JobType                           job)
        {
            while (cursor < slots.Length)
            {
                var slot = slots[cursor];
                if (slot.Free > 0)
                {
                    slot.Free--;
                    slots[cursor] = slot;

                    var occ = occLookup[slot.Building];
                    occ.Current++;
                    occLookup[slot.Building] = occ;

                    building = slot.Building;
                    job      = slot.Job;
                    return true;
                }
                cursor++;
            }
            building = Entity.Null;
            job      = JobType.Unemployed;
            return false;
        }

        // ── 수집용 임시 구조 ────────────────────────────────────────────────
        struct BuildingSlot
        {
            public Entity Building;
            public int    Free;
        }

        struct WorkSlot
        {
            public Entity  Building;
            public int     Free;
            public JobType Job;
        }
    }
}
