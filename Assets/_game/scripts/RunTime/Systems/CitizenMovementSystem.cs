using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CitizenMovementSystem — 활동 우선순위 트리 + 이동 골격 (시간 예산 모델)
    //
    //  "시민의 하루 = 시간 예산": 욕구 처리(필수) + 이동(손실) + 근무(산출) + 휴식(회복).
    //  활동을 마칠 때마다(식사 종료·퇴근·대기) 다음 활동을 결정:
    //    ① 목적지 있는 욕구(ServiceTarget) → 공급자 방문          (욕구 우선)
    //    ② 근무 시간대 + 직장 있음        → 출근 (식사 후 직행 포함)
    //    ③ 그 외                          → 집에서 휴식(AtHome)
    //  근무 중 욕구는 잠금(교대 종료까지) — 결정/탐색 게이트가 Idle/AtHome 전용이라 자동.
    //
    //  상태 전이(모든 이동은 Traveling + Purpose로 통일 — 귀가도 시간 소모):
    //    (Idle|AtHome) ─목적지──→ Traveling(Service) ─도착─→ AtDestination(식사) + 서빙 큐
    //    (Idle|AtHome) ─근무시간─→ Traveling(Work)    ─도착─→ AtWork
    //    AtDestination ─종료─→ 트리 재평가 → Traveling(Work | Home)
    //    AtWork ─근무시간 종료─→ Traveling(Home) ─도착─→ AtHome
    //
    //  ※ 물리 이동 없음 — 타이머만. 시각 이동은 보행 비주얼(CitizenWalkerRequest, 코스메틱).
    //  ※ 이동 시간: 공급자 = stamp BFS Dist(정확) / 출퇴근·귀가 = 입구 간 맨해튼 근사
    //    (보행 비주얼은 실제 BFS 경로 — 논리 시간은 근사 수용, 통계적 원칙).
    //  ※ 게임시간 기반(GameClock.TotalSeconds) — 일시정지 자동 멈춤. 근무 시간대는
    //    CitizenConfig.WorkStart/EndHour(절대 시각 게이트).
    //
    //  실행 모델 — Burst 잡(인구 1.8만 실측 후 잡화). 보행 요청은 EndSimulation ECB.
    //  욕구 Level 감소는 욕구별 시스템(HungerSystem) 소관 — 여기는 상태만(팩션 무지).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServiceSearchSystem))]
    public partial struct CitizenMovementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<CitizenTag>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            var ccfg  = SystemAPI.TryGetSingleton<CitizenConfig>(out var cc)
                        ? cc : CitizenConfig.Default;

            // 근무 시간대는 이제 시민의 직업별(교대 그룹, 2026-07-07)로 잡 안에서 판정 —
            //   전 직업 동시 출퇴근이면 수요 피크(퇴근 후 식사·여가)가 서비스 노동자의
            //   퇴근과 겹쳐 "저녁 무인 식당" 모순 발생(실측). 여기선 현재 시각만 넘긴다.
            float hour = clock.DayProgress01 * 24f;

            bool visuals = SystemAPI.TryGetSingleton<CitizenVisualPrefabSingleton>(out var vp)
                           && vp.Prefab != Entity.Null;

            var ecbSingleton = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>();

            // 서비스 데스크 큐 3종 — 이동 잡(병렬)이 채우고 데스크 잡(단일)이 소비.
            //   방문 정원 예약/해제는 건물 단위 원자성이 필요 → 단일 잡 직렬화(수집-적용 원칙).
            var departQueue = new NativeQueue<Entity>(Allocator.TempJob);        // 출발 희망(예약 대기)
            var leaveQueue  = new NativeQueue<Entity>(Allocator.TempJob);        // 식사 종료(자리 해제)
            var serveQueue  = new NativeQueue<ServeRequest>(Allocator.TempJob);  // 도착(서빙)

            state.Dependency = new CitizenMoveJob
            {
                Now         = clock.TotalSeconds,
                Hour        = hour,
                WorkStart   = ccfg.WorkStartHour,
                WorkEnd     = ccfg.WorkEndHour,
                LunchStart  = ccfg.LunchStartHour,
                LunchHours  = ccfg.LunchGameHours,
                Visuals     = visuals,
                SkillGrowth = math.max(0f, ccfg.SkillGrowthPerWorkHour),
                // 게임 1시간 = SecondsPerDay/24 게임초(기본 50초 — 3600 아님!) — 숙련 산정용.
                SecPerGameHour = math.max(1f, clock.SecondsPerDay) / 24.0,
                ResLookup   = SystemAPI.GetComponentLookup<CitizenResidence>(true),
                FpLookup    = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                EntLookup   = SystemAPI.GetComponentLookup<BuildingEntrance>(true),
                Ecb         = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged).AsParallelWriter(),
                DepartQueue = departQueue.AsParallelWriter(),
                LeaveQueue  = leaveQueue.AsParallelWriter(),
                ServeQueue  = serveQueue.AsParallelWriter(),
            }.ScheduleParallel(state.Dependency);

            // 서비스 데스크(단일 Burst): ①자리 해제 ②출발(예약 — 만석 레이스면 target 비움
            //   → 재탐색이 차선 선택) ③서빙(재고 차감 — 거절 시 자리 해제 + 귀가).
            state.Dependency = new ServiceDeskJob
            {
                Now           = clock.TotalSeconds,
                DepartQueue   = departQueue,
                LeaveQueue    = leaveQueue,
                ServeQueue    = serveQueue,
                VisitorLookup = SystemAPI.GetComponentLookup<VisitorOccupancy>(false),
                StockLookup   = SystemAPI.GetBufferLookup<StockEntry>(false),
                StateLookup   = SystemAPI.GetComponentLookup<CitizenState>(false),
                TargetLookup  = SystemAPI.GetComponentLookup<ServiceTarget>(false),
                ResLookup     = SystemAPI.GetComponentLookup<CitizenResidence>(true),
                FpLookup      = SystemAPI.GetComponentLookup<BuildingFootprint>(true),
                EntLookup     = SystemAPI.GetComponentLookup<BuildingEntrance>(true),
                Visuals       = visuals,
                Ecb           = ecbSingleton.CreateCommandBuffer(state.WorldUnmanaged),
            }.Schedule(state.Dependency);

            departQueue.Dispose(state.Dependency);
            leaveQueue.Dispose(state.Dependency);
            serveQueue.Dispose(state.Dependency);
        }
    }

    /// <summary>식사 도착 1건 — 이동 잡이 수집, 서빙 잡이 재고 차감/거절 처리.</summary>
    public struct ServeRequest
    {
        public Entity Citizen;
        public Entity Supplier;
    }

    [BurstCompile]
    [WithAll(typeof(CitizenTag))]
    public partial struct CitizenMoveJob : IJobEntity
    {
        // 도로 한 칸당 이동 게임초(정적). 추후 교통통계(혼잡도)로 동적 보정.
        public const double SecPerCell = 0.5;

        // 목적지에서 머무는(서비스/식사) 게임초. 추후 서비스 def별 시간으로 분리.
        const double EatSeconds = 3.0;

        public double Now;
        public float  Hour;             // 현재 게임 시각(0~24)
        public int    WorkStart, WorkEnd, LunchStart, LunchHours;   // 기본 근무 창(직업 시프트 전)
        public bool   Visuals;
        public float  SkillGrowth;      // 근무 1게임시간당 숙련 기본 성장(적성 배율 전)
        public double SecPerGameHour;   // 게임 1시간의 게임초(= SecondsPerDay/24)
        [ReadOnly] public ComponentLookup<CitizenResidence>  ResLookup;
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public ComponentLookup<BuildingEntrance>  EntLookup;
        public EntityCommandBuffer.ParallelWriter Ecb;
        public NativeQueue<Entity>.ParallelWriter       DepartQueue;
        public NativeQueue<Entity>.ParallelWriter       LeaveQueue;
        public NativeQueue<ServeRequest>.ParallelWriter ServeQueue;

        // 이 직업의 근무 시간대인가 — 교대 그룹(JobSchedule.ShiftHours)만큼 창을 이동.
        //   점심 창도 함께 이동(퇴근 게이트와 동일 취급). ※ 자정 넘는 창(wrap) 미지원.
        bool IsWorkHours(JobType job)
        {
            int shift = JobSchedule.ShiftHours(job);
            float ws = WorkStart + shift;
            float we = WorkEnd + shift;
            if (!(ws < we)) return false;
            if (Hour < ws || Hour >= we) return false;
            if (LunchHours > 0)
            {
                float ls = LunchStart + shift;
                if (Hour >= ls && Hour < ls + LunchHours) return false;   // 점심 = 근무 아님
            }
            return true;
        }

        void Execute(Entity entity, [ChunkIndexInQuery] int sortKey,
                     ref CitizenState cs, ref ServiceTarget target, ref CitizenNeeds needs,
                     ref CitizenSkills skills, in JobData job, in CitizenAttributes attr)
        {
            bool workHours = IsWorkHours(job.Job);

            switch (cs.Activity)
            {
                // ── 대기/집 — 활동 우선순위 트리: ①욕구 ②출근 ③휴식(현상 유지) ──
                case CitizenActivity.Idle:
                case CitizenActivity.AtHome:
                    if (target.Has)
                    {
                        // ① 공급자 방문 — 출발 확정은 서비스 데스크(단일 잡)가 방문 정원
                        //   예약 후 수행(2026-07-07). 여기선 출발 희망만 등록(상태 불변 —
                        //   데스크가 같은 프레임 뒤에서 Traveling 전환 또는 만석 시 target 비움).
                        DepartQueue.Enqueue(entity);
                    }
                    else if (workHours)
                    {
                        // ② 출근 — 직장이 있고 아직 직장에 있지 않으면.
                        //   탐색 실패로 대기 중(Pursuing 유지)이어도 출근한다 — 못 먹는
                        //   채로 집에 매이는 것보다 일하는 게 낫고, 해소는 퇴근 후 재시도.
                        Entity work = ResLookup.HasComponent(entity)
                            ? ResLookup[entity].Work : Entity.Null;
                        if (work != Entity.Null && cs.CurrentBuilding != work)
                            BeginTravel(sortKey, ref cs, cs.CurrentBuilding, work, TravelPurpose.Work);
                    }
                    // ③ 휴식 = 현상 유지(AtHome). Energy 회복은 고용 2차(컨디션 동역학).
                    break;

                // ── 이동 중 → 도착: Purpose 분기 ─────────────────────────────
                case CitizenActivity.Traveling:
                    if (Now >= cs.ActionEndTime)
                    {
                        switch (cs.Purpose)
                        {
                            case TravelPurpose.Work:
                            {
                                Entity work = ResLookup.HasComponent(entity)
                                    ? ResLookup[entity].Work : Entity.Null;
                                if (work != Entity.Null)
                                {
                                    cs.Activity        = CitizenActivity.AtWork;
                                    cs.CurrentBuilding = work;
                                    cs.ActionEndTime   = Now;   // 교대 시작 기록(퇴근 시 근무시간 산정)
                                }
                                else
                                { cs.Activity = CitizenActivity.Idle; }   // 이동 중 실직(방어)
                                break;
                            }
                            case TravelPurpose.Home:
                            {
                                Entity home = ResLookup.HasComponent(entity)
                                    ? ResLookup[entity].Home : Entity.Null;
                                if (home != Entity.Null)
                                { cs.Activity = CitizenActivity.AtHome; cs.CurrentBuilding = home; }
                                else
                                { cs.Activity = CitizenActivity.Idle; }   // 이동 중 집 소실(방어)
                                break;
                            }
                            default:   // Service (+ None 레거시)
                                cs.Activity        = CitizenActivity.AtDestination;
                                cs.CurrentBuilding = target.Supplier;
                                cs.ActionEndTime   = Now + EatSeconds;
                                ServeQueue.Enqueue(new ServeRequest
                                { Citizen = entity, Supplier = target.Supplier });
                                break;
                        }
                        if (cs.Activity != CitizenActivity.AtDestination)
                            cs.Purpose = TravelPurpose.None;
                    }
                    break;

                // ── 식사(머무름) 종료 → 자리 해제 + 리셋 + 트리 재평가(직장 직행) ──
                case CitizenActivity.AtDestination:
                    if (Now >= cs.ActionEndTime)
                    {
                        // 방문 자리 해제(데스크가 처리) — 출발 시 예약했던 좌석 반납.
                        if (cs.CurrentBuilding != Entity.Null)
                            LeaveQueue.Enqueue(cs.CurrentBuilding);

                        needs.Pursuing = NeedType.None;        // 추구 종료
                        target         = ServiceTarget.None;   // 목적지 비움
                        cs.Purpose     = TravelPurpose.None;

                        Entity work = Entity.Null, home = Entity.Null;
                        if (ResLookup.HasComponent(entity))
                        { var r = ResLookup[entity]; work = r.Work; home = r.Home; }

                        if (workHours && work != Entity.Null)
                            BeginTravel(sortKey, ref cs, cs.CurrentBuilding, work, TravelPurpose.Work);
                        else if (home != Entity.Null)
                            BeginTravel(sortKey, ref cs, cs.CurrentBuilding, home, TravelPurpose.Home);
                        else
                            cs.Activity = CitizenActivity.Idle;   // 노숙 — 제자리(기준점 유지)
                    }
                    break;

                // ── 근무 → 근무 시간대 종료 시 퇴근 (+숙련 성장 일괄 반영) ─────
                case CitizenActivity.AtWork:
                    if (!workHours)
                    {
                        // 숙련 성장: 근무시간 × 기본률 × 적성(능력치 — 성장속도에만 관여).
                        //   교대 종료 시 1회(저빈도 원칙). ActionEndTime = 교대 시작 시각.
                        //   ⚠ 게임 1시간 = SecondsPerDay/24 게임초(3600 하드코딩 버그 수정, 2026-07-07).
                        double workedH = math.max(0.0, (Now - cs.ActionEndTime) / SecPerGameHour);
                        if (workedH > 0.0 && job.Job != JobType.Unemployed)
                            skills.Add(job.Job, (float)(workedH * SkillGrowth
                                * JobAptitude.GrowthFactor(job.Job, in attr)));

                        Entity home = ResLookup.HasComponent(entity)
                            ? ResLookup[entity].Home : Entity.Null;
                        if (home != Entity.Null)
                            BeginTravel(sortKey, ref cs, cs.CurrentBuilding, home, TravelPurpose.Home);
                        else
                            cs.Activity = CitizenActivity.Idle;   // 노숙 일꾼 — 직장에서 휴식
                    }
                    break;

                default:
                    break;
            }
        }

        // 이동 개시 공통(출퇴근·귀가): 입구 간 맨해튼 근사 시간 + 보행 비주얼.
        //   입구를 못 풀면(레거시 건물 등) 즉시 도착(dist 0)으로 폴백 — 멈춤 방지.
        void BeginTravel(int sortKey, ref CitizenState cs, Entity from, Entity to, TravelPurpose purpose)
        {
            int dist = 0;
            bool haveA = TryEntranceCell(from, out int2 a, out _);
            bool haveB = TryEntranceCell(to,   out int2 b, out _);
            if (haveA && haveB)
            {
                dist = math.abs(a.x - b.x) + math.abs(a.y - b.y);
                if (Visuals && !a.Equals(b))
                    EmitWalker(sortKey, from, to);
            }

            cs.Activity        = CitizenActivity.Traveling;
            cs.Purpose         = purpose;
            cs.ActionEndTime   = Now + dist * SecPerCell;
            cs.CurrentBuilding = Entity.Null;
        }

        // 두 건물의 입구 도로셀을 풀어 보행 비주얼 요청 발행. 입구가 없거나 같은 셀이면 생략.
        //   owner = 출발 건물 footprint 소유자(배정 owner-일치 전제).
        void EmitWalker(int sortKey, Entity from, Entity to)
        {
            if (!TryEntranceCell(from, out int2 a, out int owner)) return;
            if (!TryEntranceCell(to,   out int2 b, out _))         return;
            if (a.Equals(b)) return;   // 같은 입구 도로셀(바로 옆) — 보행 생략

            var e = Ecb.CreateEntity(sortKey);
            Ecb.AddComponent(sortKey, e, new CitizenWalkerRequest
            {
                FromRoadCell = a, ToRoadCell = b, OwnerLocalId = owner,
            });
        }

        bool TryEntranceCell(Entity building, out int2 cell, out int owner)
        {
            cell = default; owner = -1;
            if (building == Entity.Null
                || !FpLookup.HasComponent(building) || !EntLookup.HasComponent(building))
                return false;
            var f  = FpLookup[building];
            var en = EntLookup[building];
            cell  = EntranceOps.EntranceRoadCell(f.Origin, f.Size, in en.Entrance, f.RotSteps);
            owner = f.OwnerLocalId;
            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ServiceDeskJob — 방문 예약·서빙·해제의 단일 창구(Burst, 2026-07-07).
    //
    //  방문 정원(VisitorOccupancy)과 재고(StockEntry)는 같은 건물에 여러 시민이
    //  동시에 접근하므로 원자적 확인-갱신이 필요 → 병렬 이동 잡이 큐로 모으고
    //  여기서 순차 처리(수집-적용 원칙). 처리 순서:
    //    ① 자리 해제(식사 종료) — 이번 프레임에 빈 좌석을 ②가 쓸 수 있게 먼저.
    //    ② 출발(예약): TryReserve 성공 → Traveling(Service) 전환 + 보행 비주얼.
    //       만석(탐색-출발 사이 레이스) → target 비움 → 다음 프레임 재탐색이 차선 선택.
    //    ③ 서빙(도착): Meal 재고 차감. 재고 0 → 거절(자리 해제 + 식사 없이 귀가,
    //       Pursuing 유지 → 재탐색). 재고 버퍼 없는 공급자 = 무한 공급(하위호환).
    //  거절 시 Activity가 AtDestination을 벗어나 HungerRelief 조건 불충족 → 해소 없음.
    // ══════════════════════════════════════════════════════════════════════════
    [BurstCompile]
    public struct ServiceDeskJob : IJob
    {
        public double Now;
        public NativeQueue<Entity>       DepartQueue;
        public NativeQueue<Entity>       LeaveQueue;
        public NativeQueue<ServeRequest> ServeQueue;
        public ComponentLookup<VisitorOccupancy> VisitorLookup;
        public BufferLookup<StockEntry>          StockLookup;
        public ComponentLookup<CitizenState>     StateLookup;
        public ComponentLookup<ServiceTarget>    TargetLookup;
        [ReadOnly] public ComponentLookup<CitizenResidence>  ResLookup;
        [ReadOnly] public ComponentLookup<BuildingFootprint> FpLookup;
        [ReadOnly] public ComponentLookup<BuildingEntrance>  EntLookup;
        public bool Visuals;
        public EntityCommandBuffer Ecb;   // 보행 비주얼 요청(EndSim)

        public void Execute()
        {
            // ── ① 자리 해제 ────────────────────────────────────────────────
            while (LeaveQueue.TryDequeue(out var supplier))
                ReleaseVisitor(supplier);

            // ── ② 출발(예약) ───────────────────────────────────────────────
            while (DepartQueue.TryDequeue(out var citizen))
            {
                if (!StateLookup.HasComponent(citizen) || !TargetLookup.HasComponent(citizen))
                    continue;
                var cs = StateLookup[citizen];
                if (cs.Activity != CitizenActivity.Idle && cs.Activity != CitizenActivity.AtHome)
                    continue;                                  // 상태 가드(중복/경합)
                var tgt = TargetLookup[citizen];
                if (!tgt.Has) continue;

                // 방문 정원 예약(있을 때만). 만석 레이스 → target 비움(재탐색이 차선 선택).
                if (VisitorLookup.HasComponent(tgt.Supplier))
                {
                    var vo = VisitorLookup[tgt.Supplier];
                    if (!vo.TryReserve())
                    {
                        TargetLookup[citizen] = ServiceTarget.None;
                        continue;
                    }
                    VisitorLookup[tgt.Supplier] = vo;
                }

                // 출발 확정.
                if (Visuals) EmitWalker(cs.CurrentBuilding, tgt.Supplier);
                cs.Activity        = CitizenActivity.Traveling;
                cs.Purpose         = TravelPurpose.Service;
                cs.ActionEndTime   = Now + tgt.Dist * CitizenMoveJob.SecPerCell;
                cs.CurrentBuilding = Entity.Null;
                StateLookup[citizen] = cs;
            }

            // ── ③ 서빙(도착) ───────────────────────────────────────────────
            while (ServeQueue.TryDequeue(out var req))
            {
                // 재고 버퍼 없음 = 무한 공급(통과).
                if (!StockLookup.HasBuffer(req.Supplier)) continue;

                var stock  = StockLookup[req.Supplier];
                bool served = false;
                for (int i = 0; i < stock.Length; i++)
                {
                    var s = stock[i];
                    if (s.Commodity != Commodity.Meal || s.Role != StockRole.LocalFinal)
                        continue;
                    if (s.Current > 0)
                    {
                        s.Current--;
                        stock[i] = s;
                        served = true;
                    }
                    break;
                }
                if (served) continue;

                // ── 거절: 자리 해제 + 식사 없이 즉시 귀가(해소 없음, Pursuing 유지) ──
                if (!StateLookup.HasComponent(req.Citizen)) continue;
                var cs = StateLookup[req.Citizen];
                if (cs.Activity != CitizenActivity.AtDestination) continue;   // 상태 가드

                ReleaseVisitor(req.Supplier);   // 출발 시 예약했던 좌석 반납

                if (TargetLookup.HasComponent(req.Citizen))
                    TargetLookup[req.Citizen] = ServiceTarget.None;

                Entity home = ResLookup.HasComponent(req.Citizen)
                    ? ResLookup[req.Citizen].Home : Entity.Null;

                if (home != Entity.Null)
                {
                    int dist = 0;
                    if (TryCells(req.Supplier, home, out int2 a, out int2 b, out int owner))
                    {
                        dist = math.abs(a.x - b.x) + math.abs(a.y - b.y);
                        if (Visuals && !a.Equals(b))
                        {
                            var e = Ecb.CreateEntity();
                            Ecb.AddComponent(e, new CitizenWalkerRequest
                            { FromRoadCell = a, ToRoadCell = b, OwnerLocalId = owner });
                        }
                    }
                    cs.Activity        = CitizenActivity.Traveling;
                    cs.Purpose         = TravelPurpose.Home;
                    cs.ActionEndTime   = Now + dist * CitizenMoveJob.SecPerCell;
                    cs.CurrentBuilding = Entity.Null;
                }
                else
                {
                    cs.Activity = CitizenActivity.Idle;   // 기준점(공급자) 유지 — 재탐색 가능
                    cs.Purpose  = TravelPurpose.None;
                }
                StateLookup[req.Citizen] = cs;
            }
        }

        void ReleaseVisitor(Entity supplier)
        {
            if (supplier == Entity.Null || !VisitorLookup.HasComponent(supplier)) return;
            var vo = VisitorLookup[supplier];
            vo.Release();
            VisitorLookup[supplier] = vo;
        }

        void EmitWalker(Entity from, Entity to)
        {
            if (!TryCells(from, to, out int2 a, out int2 b, out int owner)) return;
            if (a.Equals(b)) return;
            var e = Ecb.CreateEntity();
            Ecb.AddComponent(e, new CitizenWalkerRequest
            { FromRoadCell = a, ToRoadCell = b, OwnerLocalId = owner });
        }

        bool TryCells(Entity from, Entity to, out int2 a, out int2 b, out int owner)
        {
            a = default; b = default; owner = -1;
            if (from == Entity.Null || to == Entity.Null) return false;
            if (!FpLookup.HasComponent(from) || !EntLookup.HasComponent(from)) return false;
            if (!FpLookup.HasComponent(to)   || !EntLookup.HasComponent(to))   return false;
            var ff = FpLookup[from]; var fe = EntLookup[from];
            var tf = FpLookup[to];   var te = EntLookup[to];
            a = EntranceOps.EntranceRoadCell(ff.Origin, ff.Size, in fe.Entrance, ff.RotSteps);
            b = EntranceOps.EntranceRoadCell(tf.Origin, tf.Size, in te.Entrance, tf.RotSteps);
            owner = ff.OwnerLocalId;
            return true;
        }
    }
}
