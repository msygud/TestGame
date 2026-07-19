using Game.Unit;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  AiExtractionPlanSystem — AI 자원 접근 2단계 (2026-07-19)
    //
    //  "결핍 채널 → ResourceLayer 스캔 → Destination Road/항만" — AI 팀이 전쟁물자
    //  원재료(IronOre·Oil)의 풀 결핍을 감지하면 채취 전초를 스스로 놓는다:
    //    · 육상(광산): 최근접 광맥 셀에 배치 + RoadPathRequest(RegisterSpur=1 —
    //      영구 지선, 끊기면 AiRoadJanitor가 자가 수리). 노동 게이트 적용(유인).
    //    · 해상(시추): 최근접 유전 셀에 배치(무인·도로 불필요). 항만이 없으면
    //      **유전 인근 해안**에 항만도 계획(전초 패턴) → TankerSystem이 배·운송 자동.
    //
    //  결정 원칙(CLAUDE.md):
    //    · 키 하드코딩 없음 — 채취장은 ProducerLookup.Table(ProductionJob 파생),
    //      항만은 ProducerLookup.PortMainKey(WarehouseTag.SeaRange>0 파생)로 해석.
    //    · TypeId·수상 여부는 프리팹 엔티티의 능력 컴포넌트(ResourceExtractor·
    //      OffshoreSupplier)를 읽는다 — 인스턴스 전 능력 질의 = PrefabLookup 핸들.
    //    · 팀당·품목당 시간에 1건(HourChanged 게이트) — 배치 실패는 다음 시간 자연 재시도.
    //      "가동 중 채취장 존재" 게이트로 중복 없음, 고갈되면 자동으로 다음 광맥 계획.
    //  ※ 메인스레드 저빈도: 레이어는 읽기만(확립 계약), 배치는 PlaceBuildingRequest
    //    발행으로 위임(BuildingPlacementSystem이 재검증 — 이중 검증 허용).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct AiExtractionPlanSystem : ISystem
    {
        const int LaborGateDivisor = 4;    // ⚠ AiCityGrowthSystem.LaborGateDivisor와 일치 유지
        const int PortScanRadius   = 40;   // 유전 기준 항만 후보 링 탐색 반경(셀)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GameClock>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<LogisticsPool>();
            state.RequireForUpdate<ProducerLookup>();
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate<CellTypeLookup>();
            state.RequireForUpdate<PrefabMetaLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var clock = SystemAPI.GetSingleton<GameClock>();
            if (!clock.HourChanged) return;

            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.ResourceLayer.IsCreated || !layers.TerrainLayer.IsCreated) return;
            var pool = SystemAPI.GetSingleton<LogisticsPool>();
            if (!pool.Cells.IsCreated) return;
            var prod         = SystemAPI.GetSingleton<ProducerLookup>();
            var prefabLookup = SystemAPI.GetSingleton<PrefabLookup>();
            var cellType     = SystemAPI.GetSingleton<CellTypeLookup>();
            var metaLookup   = SystemAPI.GetSingleton<PrefabMetaLookup>();
            if (!SystemAPI.TryGetSingleton<TeamTable>(out var teamTable)) teamTable = TeamTable.Identity;
            var em = state.EntityManager;

            // ── 계획 대상(능력 파생 — 프리팹 미제작 품목은 자동 제외) ──────────
            var wanted = new NativeList<PlanTarget>(2, Allocator.Temp);
            CollectPlanTarget(Commodity.IronOre, in prod, in prefabLookup, em, ref wanted);
            CollectPlanTarget(Commodity.Oil,     in prod, in prefabLookup, em, ref wanted);
            if (wanted.Length == 0) { wanted.Dispose(); return; }

            // ── AI 팀(플레이어 제외 — AiCityGrowth와 동일 규약) ────────────────
            var teams = new NativeList<TeamPlan>(8, Allocator.Temp);
            foreach (var (teamRO, gridRO) in SystemAPI.Query<RefRO<TeamInfoData>, RefRO<CityGrid>>())
            {
                if (teamRO.ValueRO.IsPlayer()) continue;
                teams.Add(new TeamPlan
                {
                    Owner = teamRO.ValueRO.LocalID,
                    Anchor = gridRO.ValueRO.Anchor,
                    FactionId = gridRO.ValueRO.FactionId,
                });
            }
            if (teams.Length == 0) { wanted.Dispose(); teams.Dispose(); return; }

            // ── 무직 노동력(노동 게이트 입력 — AiCityGrowth와 동일 집계) ──────
            var unemployed = new NativeArray<int>(8, Allocator.Temp);
            foreach (var ownerSh in SystemAPI.Query<OwnerShared>()
                         .WithAll<CitizenTag, JobSeekerTag>())
                if ((uint)ownerSh.LocalId < 8) unemployed[ownerSh.LocalId]++;

            // ── 가동 중 채취장 (owner, TypeId) — 발밑 잔량 있으면 가동(중복 계획 차단).
            //   고갈되면 집합에서 빠져 다음 광맥이 자동 계획된다(확장 = 결핍 재발 주도). ──
            var productive = new NativeHashSet<int2>(16, Allocator.Temp);
            foreach (var (ext, fp) in
                     SystemAPI.Query<RefRO<ResourceExtractor>, RefRO<BuildingFootprint>>())
                if (HasRemaining(fp.ValueRO, ext.ValueRO, in layers))
                    productive.Add(new int2(fp.ValueRO.OwnerLocalId, ext.ValueRO.ResourceTypeId));

            // ── 항만 보유 owner ─────────────────────────────────────────────
            var hasPort = new NativeHashSet<int>(8, Allocator.Temp);
            foreach (var (wt, fp) in
                     SystemAPI.Query<RefRO<WarehouseTag>, RefRO<BuildingFootprint>>())
                if (wt.ValueRO.SeaRange > 0) hasPort.Add(fp.ValueRO.OwnerLocalId);

            // ── 광맥/유전 후보 셀(잔량 있는 것만) — 레이어 1회 순회 ─────────────
            var deposits = new NativeParallelMultiHashMap<int, int2>(
                math.max(64, layers.ResourceLayer.Count), Allocator.Temp);
            foreach (var kv in layers.ResourceLayer)
                if (kv.Value.Amount > 0) deposits.Add(kv.Value.TypeId, kv.Key);

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            for (int t = 0; t < teams.Length; t++)
            {
                int owner = teams[t].Owner;
                if ((uint)owner >= 8) continue;

                for (int w = 0; w < wanted.Length; w++)
                {
                    var target = wanted[w];

                    // 결핍 판정(인구 앵커): 풀 셀 존재 + Target>0 + 미달.
                    if (!pool.Cells.TryGetValue(LogisticsPool.Key(owner, target.Commodity), out var pc)
                        || pc.Target <= 0 || pc.Stored >= pc.Target) continue;

                    // 이미 가동 중인 채취장 있음 → 계획 불필요.
                    if (productive.Contains(new int2(owner, target.TypeId))) continue;

                    // 노동 게이트(유인 채취장만 — AiCityGrowth 규약과 동일 임계).
                    if (target.WorkerNeed > 0
                        && unemployed[owner] < math.max(1, target.WorkerNeed / LaborGateDivisor))
                        continue;

                    // 최근접 유효 광맥/유전.
                    if (!FindNearestDeposit(target.TypeId, teams[t].Anchor, in deposits,
                            in layers, owner, in teamTable, out int2 cell)) continue;

                    ecb.AddComponent(ecb.CreateEntity(), new PlaceBuildingRequest
                    {
                        MainKey = target.MainKey, VariantKey = 0, Cell = cell, RotationY = 0f,
                        OwnerLocalId = owner, FactionId = teams[t].FactionId,
                        RequireRoadAccess = false,   // 전초 — 도로는 아래 지선이 뒤따라온다
                    });
                    UnityEngine.Debug.Log($"[ExtractPlan] P{owner} {target.Commodity} 채취장 계획 " +
                                          $"@{cell} (앵커 거리 {math.length((float2)(cell - teams[t].Anchor)):F0})");

                    if (!target.IsWater)
                    {
                        // 육상: Destination Road 영구 지선(끊기면 재니터 자가 수리).
                        ecb.AddComponent(ecb.CreateEntity(), new RoadPathRequest
                        {
                            Target = cell, OwnerLocalId = owner,
                            FactionId = teams[t].FactionId,
                            StopAdjacent = 1, RegisterSpur = 1,
                        });
                    }
                    else if (!hasPort.Contains(owner) && prod.PortMainKey > 0
                             && metaLookup.TryGetMeta(prod.PortMainKey, 0, out var portMeta)
                             && TryFindPortSite(cell, portMeta.Size, in layers, cellType,
                                    owner, in teamTable, out int2 portOrigin, out float portRot))
                    {
                        // 해상: 항만이 없으면 유전 인근 해안에 함께 계획(전초 패턴).
                        //   TankerSystem이 항만+시추를 보고 배를 자동 스폰 → 운송 개시.
                        ecb.AddComponent(ecb.CreateEntity(), new PlaceBuildingRequest
                        {
                            MainKey = prod.PortMainKey, VariantKey = 0, Cell = portOrigin,
                            RotationY = portRot, OwnerLocalId = owner,
                            FactionId = teams[t].FactionId, RequireRoadAccess = false,
                        });
                        hasPort.Add(owner);   // 같은 틱 중복 계획 방지
                        UnityEngine.Debug.Log($"[ExtractPlan] P{owner} 항만 계획 @{portOrigin} (유전 {cell} 인근 해안)");
                    }
                }
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
            deposits.Dispose();
            hasPort.Dispose();
            productive.Dispose();
            unemployed.Dispose();
            teams.Dispose();
            wanted.Dispose();
        }

        struct PlanTarget
        {
            public Commodity Commodity;
            public int  MainKey;      // 채취 건물(생산자 테이블 파생)
            public int  TypeId;       // 맵 자원 타입(프리팹 ResourceExtractor 능력)
            public int  WorkerNeed;   // 근무 정원(노동 게이트 — 무인 0)
            public bool IsWater;      // OffshoreSupplier 능력 = 해상(도로 대신 항만)
        }

        struct TeamPlan
        {
            public int  Owner;
            public int2 Anchor;
            public int  FactionId;
        }

        /// <summary>품목 → 채취 프리팹 능력 해석. 프리팹 미제작/비채취면 추가 안 함.</summary>
        static void CollectPlanTarget(Commodity c, in ProducerLookup prod,
            in PrefabLookup prefabLookup, EntityManager em, ref NativeList<PlanTarget> outList)
        {
            if (!prod.Table.IsCreated || !prod.Table.TryGetValue((int)c, out int mainKey)) return;
            var prefab = prefabLookup.Get(mainKey, 0);
            if (prefab == Entity.Null || !em.HasComponent<ResourceExtractor>(prefab)) return;

            int workerNeed = prod.WorkerNeeds.IsCreated
                             && prod.WorkerNeeds.TryGetValue(mainKey, out int n) ? n : 0;
            outList.Add(new PlanTarget
            {
                Commodity  = c,
                MainKey    = mainKey,
                TypeId     = em.GetComponentData<ResourceExtractor>(prefab).ResourceTypeId,
                WorkerNeed = workerNeed,
                IsWater    = em.HasComponent<OffshoreSupplier>(prefab),
            });
        }

        /// <summary>채취장 발밑에 매칭 자원 잔량이 있는가(가동 판정 — 라이브 읽기).</summary>
        static bool HasRemaining(in BuildingFootprint fp, in ResourceExtractor ext, in GridLayers layers)
        {
            int2 size = EntranceOps.RotateSize(fp.Size, fp.RotSteps);
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = fp.Origin + new int2(dx, dz);
                if (layers.ResourceLayer.TryGetValue(cell, out var rc)
                    && rc.TypeId == ext.ResourceTypeId && rc.Amount > 0)
                    return true;
            }
            return false;
        }

        /// <summary>앵커 최근접 유효(비점유·비적영토) 자원 셀. 채취장은 1×1 규약.</summary>
        static bool FindNearestDeposit(int typeId, int2 anchor,
            in NativeParallelMultiHashMap<int, int2> deposits, in GridLayers layers,
            int owner, in TeamTable teamTable, out int2 result)
        {
            result = default;
            float best = float.MaxValue;
            if (!deposits.TryGetFirstValue(typeId, out var cell, out var it)) return false;
            do
            {
                if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty
                    && occ.Type != OccupantType.Environment) continue;
                if (TerritoryOps.InEnemyTerritory(in layers.TerritoryLayer, cell, owner, in teamTable)
                    || TerritoryOps.IsContested(in layers.TerritoryLayer, cell)) continue;

                float d = math.lengthsq((float2)(cell - anchor));
                if (d < best) { best = d; result = cell; }
            }
            while (deposits.TryGetNextValue(out cell, ref it));
            return best < float.MaxValue;
        }

        /// <summary>유전 인근 해안 항만 부지: 링 탐색으로 "전체 물 + 비점유 + 육지 접촉 ≥1 +
        /// 높이 균일" footprint 원점을 찾는다. 회전 0/90 모두 시도. 배치 시스템이 재검증.</summary>
        static bool TryFindPortSite(int2 nearCell, int2 portSize, in GridLayers layers,
            CellTypeLookup cellType, int owner, in TeamTable teamTable,
            out int2 origin, out float rotY)
        {
            origin = default; rotY = 0f;
            for (int r = 2; r <= PortScanRadius; r++)
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (math.max(math.abs(dx), math.abs(dz)) != r) continue;   // 링 외곽만
                var cand = nearCell + new int2(dx, dz);
                if (PortRectValid(cand, portSize, in layers, cellType, owner, in teamTable))
                { origin = cand; rotY = 0f; return true; }
                var swapped = new int2(portSize.y, portSize.x);
                if (PortRectValid(cand, swapped, in layers, cellType, owner, in teamTable))
                { origin = cand; rotY = 90f; return true; }
            }
            return false;
        }

        static bool PortRectValid(int2 origin, int2 size, in GridLayers layers,
            CellTypeLookup cellType, int owner, in TeamTable teamTable)
        {
            bool touchesLand = false;
            byte height = 0; bool first = true;
            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);
                if (!IsWater(cell, in layers, cellType)) return false;
                if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty
                    && occ.Type != OccupantType.Environment) return false;
                if (TerritoryOps.InEnemyTerritory(in layers.TerritoryLayer, cell, owner, in teamTable)
                    || TerritoryOps.IsContested(in layers.TerritoryLayer, cell)) return false;
                if (layers.ResourceLayer.TryGetValue(cell, out var rc) && rc.Amount > 0) return false;

                layers.TerrainLayer.TryGetValue(cell, out var terrain);
                if (first) { height = terrain.Height; first = false; }
                else if (terrain.Height != height) return false;

                // 외곽 셀의 4방 이웃에 육지가 있으면 해안 접촉(부두 — 미래 육상 연결 여지).
                if (!touchesLand && (dx == 0 || dz == 0 || dx == size.x - 1 || dz == size.y - 1))
                {
                    if (IsLand(cell + new int2(1, 0), in layers, cellType)
                        || IsLand(cell + new int2(-1, 0), in layers, cellType)
                        || IsLand(cell + new int2(0, 1), in layers, cellType)
                        || IsLand(cell + new int2(0, -1), in layers, cellType))
                        touchesLand = true;
                }
            }
            return touchesLand;
        }

        static bool IsWater(int2 cell, in GridLayers layers, CellTypeLookup cellType)
            => layers.TerrainLayer.TryGetValue(cell, out var t)
               && cellType.TryGet(t.TypeId, out var info)
               && info.TerrainCategory == TerrainCategory.Water;

        static bool IsLand(int2 cell, in GridLayers layers, CellTypeLookup cellType)
            => layers.TerrainLayer.TryGetValue(cell, out var t)
               && (!cellType.TryGet(t.TypeId, out var info)
                   || info.TerrainCategory != TerrainCategory.Water);
    }
}
