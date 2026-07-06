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
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    // 변환 그룹보다 먼저(2026-07-06) — 스폰 프레임 안에 LocalToWorld가 계산되게.
    //   뒤에 돌면 새 인스턴스(+자식)가 1프레임 동안 프리팹 기본 LTW(원점)로 렌더링
    //   ("스폰 시 원점을 잠깐 거쳐감" 실측).
    [UpdateBefore(typeof(Unity.Transforms.TransformSystemGroup))]
    public partial struct SpawnSystem : ISystem
    {
        // 식당(stub) 재고 상수 — BuildingAuthoring 베이킹 이관 전까지의 임시값.
        const int MealInitialStock   = 100;   // 초기 Meal(개점 완충 — 소진 후엔 생산/물류가 공급)
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
        const int WarehouseStampMaxDist = 40;
        const int WorkerSlots       = 4;     // 생산 건물 일자리 정원(stub — 프리팹 베이킹 이관 예정.
                                             //   ⚠ ECB AddComponent라 베이킹된 Occupancy가 있으면 덮음)
        const int VisitorSlots      = 10;    // 식당 동시 방문 좌석(stub) — 식사 3게임초라 회전 빠름

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

                    if (req.ValueRO.HasEntrance)
                        ecb.AddComponent(instance, new BuildingEntrance
                        {
                            Entrance = req.ValueRO.Entrance,
                        });

                    if (req.ValueRO.IsSupplier)
                    {
                        ecb.AddComponent(instance, new StampSupplier
                        {
                            OwnerLocalId = req.ValueRO.OwnerLocalId,
                            Relief       = req.ValueRO.Relief,
                            MaxDist      = req.ValueRO.SupplyMaxDist,
                        });

                        // ── 물류/생산 배선(stub, 2026-07-06): Hunger 공급자 = 식사 소비점 ──
                        //   Meal(완성품)은 LocalFinal — 창고 경유 없이 만든 자리서 소비.
                        //   Flour(중간재)는 Input — 창고에서 pull(LogisticsPullSystem).
                        //   ProductionJob(Meal)이 Flour→Meal 생산(ProductionSystem, 재료 있을 때).
                        //   시민 식사가 이 Meal 재고를 차감(ServeMealsJob) — 경제 실수요의 시작점.
                        //   TODO: 품목/용량/초기재고는 BuildingAuthoring 베이킹(능력=컴포넌트)으로
                        //   이관 — 지금은 "Hunger 공급자 = 식당" stub 상수.
                        if ((req.ValueRO.Relief & NeedType.Hunger) != NeedType.None)
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
                            // 일자리(고용 1차, 2026-07-06) — 직종/정원은 stub 상수.
                            ecb.AddComponent(instance, new WorkplaceBuilding { ProvidedJob = JobType.Merchant });
                            ecb.AddComponent(instance, new BuildingOccupancy { Current = 0, Capacity = WorkerSlots });
                            // 방문 좌석(예약 기반, 2026-07-07) — 고용 정원과 분리.
                            ecb.AddComponent(instance, new VisitorOccupancy { Current = 0, Capacity = VisitorSlots });
                        }
                    }
                    // ── 생산 체인 stub — MainKey 기반 임시 역할(상수 정의부 주석 참조) ──
                    //   식당(IsSupplier+Hunger)과 상호배타 전제(현 레지스트리에서 키가 다름).
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
                        ecb.AddComponent(instance, new WarehouseTag
                        {
                            OwnerLocalId = req.ValueRO.OwnerLocalId,
                            MaxDist      = WarehouseStampMaxDist,
                        });
                    }

                    // ── 건물 전투 타겟화: 공격으로 파괴 가능(캡처 후 적 건물 제거의 토대) ──
                    //   타겟 쿼리 요건: CombatTargetable + CombatHealth + TeamInfoData + LocalTransform(스폰 시 부여).
                    //   CombatDestroyOnDeath → 사망 시 CombatDeathSystem이 destroy.
                    //   CombatTargetBounds는 선택(없으면 ResolveAimPosition이 transform 위치로 폴백) → 1차 생략.
                    // 영토 전환 파괴 면제(베이스/HQ) — TerritoryCaptureSystem이 건너뜀.
                    if (req.ValueRO.CaptureExempt)
                        ecb.AddComponent<CaptureExempt>(instance);

                    int ownerLid = math.clamp(req.ValueRO.OwnerLocalId, 0, 7);
                    ecb.AddComponent(instance, new CombatTargetable { TargetType = CombatTargetMask.Building });
                    ecb.AddComponent(instance, new CombatHealth { Health = buildingDefaultHealth, MaxHealth = buildingDefaultHealth });
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
