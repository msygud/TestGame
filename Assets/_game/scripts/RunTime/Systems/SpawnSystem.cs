using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Game.Unit;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  SpawnSystem — Single 인스턴싱
    //
    //  SpawnRequest 처리:
    //    - PrefabLookup.Get(MainKey, VariantKey) → Entity 조회
    //    - 인스턴싱 + LocalTransform 적용
    //    - MapLoaded 태그 부여 (맵 정리 시 사용)
    //    - SpawnRequest 엔티티 파괴
    //
    //  능력 부착(다지기 ①, 2026-07-11): 능력(재고·생산·정원·공급·창고·내구)은
    //  **BuildingAuthoring이 프리팹에 베이크** → Instantiate가 복사. 여기서는
    //  per-instance 사실(footprint·owner·입구·팀)만 주입한다.
    //  프리팹이 미베이크면 구 MainKey 스텁으로 폴백(무회귀 전환) + 키당 1회 경고 —
    //  프리팹에 BuildingAuthoring 값을 채우는 즉시 스텁이 자동 은퇴한다.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    // 변환 그룹보다 먼저(2026-07-06) — 스폰 프레임 안에 LocalToWorld가 계산되게.
    //   뒤에 돌면 새 인스턴스(+자식)가 1프레임 동안 프리팹 기본 LTW(원점)로 렌더링
    //   ("스폰 시 원점을 잠깐 거쳐감" 실측).
    [UpdateBefore(typeof(Unity.Transforms.TransformSystemGroup))]
    public partial struct SpawnSystem : ISystem
    {
        // 레거시 스텁 상수(다지기 ①, 2026-07-11 이후) — 프리팹 BuildingAuthoring 미입력
        //   폴백 전용. 전 건물 프리팹에 값 입력이 확인되면(경고 로그 0) 스텁 블록째 삭제 예정.
        // 식당(stub) 재고 상수 — BuildingAuthoring 베이킹 이관 전까지의 임시값.
        const int MealInitialStock   = 10;    // 개점 완충 소량(계획 D, 2026-07-11: 100은 생산 체인을 장기간
                                              //   가려 수요 신호를 왜곡 — 곧 소진돼 Flour pull부터 경제 가동)
        const int MealStockCapacity  = 100;
        const int FlourStockCapacity = 50;

        // 생산 체인 stub(2026-07-06) — MainKey 기반 임시 역할. BuildingAuthoring 베이킹 이관 예정.
        //   farm_h(1004)=Grain 산지(무입력 생산) / powder_h(1003)=제분(Grain→Flour) /
        //   stock_h(1005)=창고(Store 허브, WarehouseTag → stamp). 배치 시 BuildingPlacement가
        //   StampDirtyEvent를 이미 발행 → 창고 도장 자동.
        const int MillMainKey       = 1003;
        const int FarmMainKey       = 1004;
        const int WarehouseMainKey  = 1005;
        const int ProducerStockCap  = 40;    // 생산 건물 입출력 칸 용량
        const int WarehouseStoreCap = 200;   // 창고 품목당 보관 용량
        // public: AiCityGrowth가 커버 '잔여 깊이'(MaxDist−Dist) 계산에 참조(드리프트 방지 — 단일 출처).
        public const int WarehouseStampMaxDist = 30;   // 20은 과소(연결 실패 다발) → 30 상향(2026-07-10, 구 40→20→30)
        const int WorkerSlots       = 4;     // 생산 건물 일자리 정원(stub — 프리팹 베이킹 이관 예정.
                                             //   ⚠ ECB AddComponent라 베이킹된 Occupancy가 있으면 덮음)
        const int WarehouseWorkerSlots = 6;  // 창고 일자리(24h 3교대 → 교대당 ~2명)
        const int VisitorSlots      = 30;    // 식당 동시 방문 좌석(stub). ⚠ 좌석은 출발 시점에 예약되어
                                             //   이동 내내 점유(ServiceDeskJob) — 실질 상한은 "동시 이동+식사
                                             //   파이프라인". 10은 UNMET 백로그 주범(2026-07-10 실측) → 30.

        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<PrefabLookup>()) return;
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            // 건물 기본 체력(균일, 임시) — 전투로 파괴 가능하게 부여. SpawnConfig 싱글톤(밸런스).
            //   TODO: 프리팹별 값이 필요하면 BuildingAuthoring 베이킹으로 이전(능력=컴포넌트 원칙).
            float buildingDefaultHealth = (SystemAPI.TryGetSingleton<SpawnConfig>(out var spawnCfg)
                ? spawnCfg : SpawnConfig.Default).BuildingDefaultHealth;

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            var teamsByLocalId = new NativeArray<TeamInfoData>(8, Allocator.Temp);
            var hasTeamByLocalId = new NativeArray<byte>(8, Allocator.Temp);

            foreach (var team in SystemAPI.Query<RefRO<TeamInfoData>>())
            {
                int localId = math.clamp(team.ValueRO.LocalID, 0, 7);
                teamsByLocalId[localId] = team.ValueRO;
                hasTeamByLocalId[localId] = 1;
            }

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<SpawnRequest>>().WithEntityAccess())
            {
                int mk     = req.ValueRO.MainKey;
                int vk     = req.ValueRO.VariantKey;
                var prefab = lookup.Get(mk, vk);

                if (prefab == Entity.Null)
                {
                    Debug.LogWarning($"[SpawnSystem] Prefab not found: ({mk}, {vk}).");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                var instance = ecb.Instantiate(prefab);
                ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                    req.ValueRO.Position,
                    req.ValueRO.Rotation,
                    req.ValueRO.Scale));

                ApplySpawnTeam(
                    ecb,
                    state.EntityManager,
                    prefab,
                    instance,
                    req.ValueRO.OwnerLocalId,
                    teamsByLocalId,
                    hasTeamByLocalId);

                ecb.AddComponent<MapLoaded>(instance);

                // ── footprint/입구/공급자 승격 (인게임 배치 경로만) ──
                //   HasFootprint=false인 경로(맵 로더 등)는 아래를 건너뛴다.
                if (req.ValueRO.HasFootprint)
                {
                    ecb.AddComponent(instance, new BuildingFootprint
                    {
                        Origin       = req.ValueRO.FootprintOrigin,
                        Size         = req.ValueRO.FootprintSize,
                        RotSteps     = req.ValueRO.RotSteps,
                        OwnerLocalId = req.ValueRO.OwnerLocalId,
                    });

                    // 소유: SharedComponent (플레이어 LocalId별 청크 분리 — 시민/도로/캐리어와 동일 축).
                    //   BuildingFootprint.OwnerLocalId(필드)는 Burst 잡의 per-entity 값 읽기용 유지,
                    //   OwnerShared는 시스템 레벨 청크 필터(WithSharedComponentFilter)용. 스폰 후 불변.
                    ecb.AddSharedComponent(instance, new OwnerShared(req.ValueRO.OwnerLocalId));

                    if (req.ValueRO.HasEntrance)
                        ecb.AddComponent(instance, new BuildingEntrance
                        {
                            Entrance = req.ValueRO.Entrance,
                        });

                    // ── 베이크 경로(다지기 ①): 능력은 Instantiate가 복사 — owner만 주입 ──
                    //   StampSupplier/WarehouseTag는 per-instance 사실(OwnerLocalId)을 품으므로
                    //   프리팹의 -1 표식을 요청 owner로 고정(SetComponent). 나머지 능력
                    //   (재고·생산·정원·좌석)은 값 그대로 복사돼 손댈 것 없음.
                    var em = state.EntityManager;
                    bool bakedSupplier  = em.HasComponent<StampSupplier>(prefab);
                    bool bakedWarehouse = em.HasComponent<WarehouseTag>(prefab);
                    bool bakedEconomy   = em.HasBuffer<StockEntry>(prefab);
                    if (bakedSupplier)
                    {
                        var sup = em.GetComponentData<StampSupplier>(prefab);
                        sup.OwnerLocalId = req.ValueRO.OwnerLocalId;
                        ecb.SetComponent(instance, sup);
                    }
                    if (bakedWarehouse)
                    {
                        var wt = em.GetComponentData<WarehouseTag>(prefab);
                        wt.OwnerLocalId = req.ValueRO.OwnerLocalId;
                        ecb.SetComponent(instance, wt);
                    }

                    // ── 레거시 스텁(프리팹 미베이크 폴백) — BuildingAuthoring 값을 채우면
                    //   위 baked 플래그가 서고 이 블록은 자동 은퇴. 키당 1회 경고로 가시화.
                    if (!bakedSupplier && req.ValueRO.IsSupplier)
                    {
                        ecb.AddComponent(instance, new StampSupplier
                        {
                            OwnerLocalId = req.ValueRO.OwnerLocalId,
                            Relief       = req.ValueRO.Relief,
                            MaxDist      = req.ValueRO.SupplyMaxDist,
                        });
                        SpawnLegacyWarn.Once(mk);
                    }
                    if (!bakedEconomy)
                    {
                        // 구 MainKey 스텁(2026-07-06 물류/생산 배선) — 상수 정의부 주석 참조.
                        if (req.ValueRO.IsSupplier
                            && (req.ValueRO.Relief & NeedType.Hunger) != NeedType.None)
                        {
                            var stock = ecb.AddBuffer<StockEntry>(instance);
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Meal,
                                Current   = MealInitialStock,
                                Capacity  = MealStockCapacity,
                                Role      = StockRole.LocalFinal,
                            });
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Flour,
                                Current   = 0,
                                Capacity  = FlourStockCapacity,
                                Role      = StockRole.Input,
                            });
                            ecb.AddComponent(instance, ProductionJob.Make(Commodity.Meal));
                            ecb.AddComponent(instance, new WorkplaceBuilding { ProvidedJob = JobType.Merchant });
                            ecb.AddComponent(instance, new BuildingOccupancy { Current = 0, Capacity = WorkerSlots });
                            ecb.AddComponent(instance, new VisitorOccupancy { Current = 0, Capacity = VisitorSlots });
                            ecb.AddComponent(instance, new ServiceStats { TodayServed = 0, YesterdayServed = 0 });
                            SpawnLegacyWarn.Once(mk);
                        }
                        else if (mk == FarmMainKey)
                        {
                            var stock = ecb.AddBuffer<StockEntry>(instance);
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Grain, Current = 0,
                                Capacity  = ProducerStockCap, Role = StockRole.Output,
                            });
                            ecb.AddComponent(instance, ProductionJob.Make(Commodity.Grain));
                            ecb.AddComponent(instance, new WorkplaceBuilding { ProvidedJob = JobType.Farmer });
                            ecb.AddComponent(instance, new BuildingOccupancy { Current = 0, Capacity = WorkerSlots });
                            SpawnLegacyWarn.Once(mk);
                        }
                        else if (mk == MillMainKey)
                        {
                            var stock = ecb.AddBuffer<StockEntry>(instance);
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Grain, Current = 0,
                                Capacity  = ProducerStockCap, Role = StockRole.Input,
                            });
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Flour, Current = 0,
                                Capacity  = ProducerStockCap, Role = StockRole.Output,
                            });
                            ecb.AddComponent(instance, ProductionJob.Make(Commodity.Flour));
                            ecb.AddComponent(instance, new WorkplaceBuilding { ProvidedJob = JobType.Engineer });
                            ecb.AddComponent(instance, new BuildingOccupancy { Current = 0, Capacity = WorkerSlots });
                            SpawnLegacyWarn.Once(mk);
                        }
                        else if (mk == WarehouseMainKey)
                        {
                            var stock = ecb.AddBuffer<StockEntry>(instance);
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Grain, Current = 0,
                                Capacity  = WarehouseStoreCap, Role = StockRole.Store,
                            });
                            stock.Add(new StockEntry
                            {
                                Commodity = Commodity.Flour, Current = 0,
                                Capacity  = WarehouseStoreCap, Role = StockRole.Store,
                            });
                            if (!bakedWarehouse)
                                ecb.AddComponent(instance, new WarehouseTag
                                {
                                    OwnerLocalId = req.ValueRO.OwnerLocalId,
                                    MaxDist      = WarehouseStampMaxDist,
                                });
                            // 창고 고용(2026-07-07) — Administrator 직종, 24h 3교대(JobSchedule).
                            ecb.AddComponent(instance, new WorkplaceBuilding { ProvidedJob = JobType.Administrator });
                            ecb.AddComponent(instance, new BuildingOccupancy { Current = 0, Capacity = WarehouseWorkerSlots });
                            SpawnLegacyWarn.Once(mk);
                        }
                    }

                    // ── 건물 전투 타겟화: 공격으로 파괴 가능(캡처 후 적 건물 제거의 토대) ──
                    //   타겟 쿼리 요건: CombatTargetable + CombatHealth + TeamInfoData + LocalTransform(스폰 시 부여).
                    //   CombatDestroyOnDeath → 사망 시 CombatDeathSystem이 destroy.
                    //   부착 골격 = 건물 공통(여기), 값 = 능력(BuildingDurability 베이크, 없으면 config 폴백).
                    // 영토 전환 파괴 면제(베이스/HQ) — TerritoryCaptureSystem이 건너뜀.
                    if (req.ValueRO.CaptureExempt)
                        ecb.AddComponent<CaptureExempt>(instance);

                    int ownerLid = math.clamp(req.ValueRO.OwnerLocalId, 0, 7);
                    float hp = em.HasComponent<BuildingDurability>(prefab)
                        ? em.GetComponentData<BuildingDurability>(prefab).MaxHealth
                        : buildingDefaultHealth;
                    ecb.AddComponent(instance, new CombatTargetable { TargetType = CombatTargetMask.Building });
                    ecb.AddComponent(instance, new CombatHealth { Health = hp, MaxHealth = hp });
                    ecb.AddComponent<CombatDestroyOnDeath>(instance);
                    // friend/foe 판정용 팀 — 프리팹에 TeamInfoData 없을 때만 owner 팀으로 부여
                    //   (있으면 위 ApplySpawnTeam이 이미 set).
                    if (hasTeamByLocalId[ownerLid] == 1 && !state.EntityManager.HasComponent<TeamInfoData>(prefab))
                        ecb.AddComponent(instance, teamsByLocalId[ownerLid]);
                }

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            hasTeamByLocalId.Dispose();
            teamsByLocalId.Dispose();
        }

        // 미베이크 프리팹 경고(키당 1회) — 어느 프리팹에 BuildingAuthoring 값을 채워야 하는지 가시화.
        static class SpawnLegacyWarn
        {
            static readonly System.Collections.Generic.HashSet<int> _warned = new();
            public static void Once(int mainKey)
            {
                if (!_warned.Add(mainKey)) return;
                Debug.LogWarning($"[Spawn] key={mainKey} 프리팹 미베이크 — 레거시 스텁으로 능력 부착 중. " +
                                 "프리팹 BuildingAuthoring에 값을 채우면 자동 은퇴(다지기 ①).");
            }
        }

        static void ApplySpawnTeam(
            EntityCommandBuffer ecb,
            EntityManager entityManager,
            Entity prefab,
            Entity instance,
            int ownerLocalId,
            NativeArray<TeamInfoData> teamsByLocalId,
            NativeArray<byte> hasTeamByLocalId)
        {
            if (!entityManager.HasComponent<TeamInfoData>(prefab))
                return;

            int localId = math.clamp(ownerLocalId, 0, 7);
            if (hasTeamByLocalId[localId] == 0)
                return;

            ecb.SetComponent(instance, teamsByLocalId[localId]);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  MultiSpawnSystem — Multi 결정적 랜덤 배치
    //
    //  MultiSpawnRequest 처리:
    //    - Seed 기반 결정적 랜덤 (Unity.Mathematics.Random)
    //    - 1셀 영역 안에 Count개 랜덤 위치 + 랜덤 Y회전
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    public partial struct MultiSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<PrefabLookup>()) return;
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<MultiSpawnRequest>>().WithEntityAccess())
            {
                int mk     = req.ValueRO.MainKey;
                int vk     = req.ValueRO.VariantKey;
                var prefab = lookup.Get(mk, vk);

                if (prefab == Entity.Null)
                {
                    Debug.LogWarning($"[MultiSpawnSystem] Prefab not found: ({mk}, {vk}).");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                float cs   = req.ValueRO.CellSize;
                float orgX = req.ValueRO.Cell.x * cs;
                float orgZ = req.ValueRO.Cell.y * cs;
                float h    = req.ValueRO.Height;

                var rng = new Unity.Mathematics.Random((uint)(req.ValueRO.Seed + 1));

                for (int i = 0; i < req.ValueRO.Count; i++)
                {
                    float localX = rng.NextFloat(0f, cs);
                    float localZ = rng.NextFloat(0f, cs);
                    float rotY   = rng.NextFloat(0f, 360f);

                    float3 pos = new float3(orgX + localX, h, orgZ + localZ);

                    var instance = ecb.Instantiate(prefab);
                    ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                        pos,
                        quaternion.RotateY(math.radians(rotY)),
                        req.ValueRO.Scale));

                    ecb.AddComponent<MapLoaded>(instance);
                }

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }
    }
}
