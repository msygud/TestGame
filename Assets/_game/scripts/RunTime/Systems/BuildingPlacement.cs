using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════
    //  PlaceBuildingRequest  (IComponentData)
    //
    //  인게임 건물 배치 명령. UI/입력 시스템이 생성한다.
    //
    //  사용 예:
    //    var e = em.CreateEntity();
    //    em.AddComponentData(e, new PlaceBuildingRequest
    //    {
    //        MainKey   = selectedMainKey,
    //        VariantKey = selectedVariantKey,
    //        Cell      = hoveredCell,
    //        RotationY = 0f,
    //        OwnerLocalId = playerLocalId,
    //        FactionId = playerFactionId,  // 도로 배치 시 필수 (건물/환경은 무시됨)
    //    });
    //
    //  BuildingPlacementSystem이 처리 후 엔티티를 파괴한다.
    //  PrefabMeta.Category(Road/Building/Environment)를 보고 스폰 방식을 자동 판단.
    // ══════════════════════════════════════════════════════════════════
    public struct PlaceBuildingRequest : IComponentData
    {
        /// <summary>배치할 프리팹 MainKey (GamePrefabRegistry 기준).</summary>
        public int   MainKey;
        /// <summary>배치할 프리팹 VariantKey.</summary>
        public int   VariantKey;
        /// <summary>배치 기준 셀 (좌하단, XZ).</summary>
        public int2  Cell;
        /// <summary>Y축 회전 (도). Single 배치에만 적용.</summary>
        public float RotationY;
        /// <summary>배치 주체 플레이어 LocalId (점유 기록용).</summary>
        public int   OwnerLocalId;
        /// <summary>
        /// 배치 주체의 팩션 ID. 도로 분기(EmitRoad)에서 PlaceRoadCommand로 전달.
        /// 건물/환경은 MainKey가 이미 확정돼 있어 직접 쓰지 않지만,
        /// 도로는 (FactionId, dirMask)로 MainKey를 해소하므로 필요하다.
        /// </summary>
        public int   FactionId;

        /// <summary>
        /// 입구-도로 정렬을 배치 조건으로 강제할지.
        ///   · AI 자율 성장 / 팩션 베이스 = true
        ///       → 입구가 도로에 닿지 않으면 NoRoadAccess로 거부(죽은 건물 방지, 최후 방어선).
        ///   · 인간 직접 배치          = false
        ///       → 검증하지 않는다. 연결성은 정보로만 보여주고 선택은 본인 몫.
        ///
        /// 같은 명령 타입을 공유하되, 정책 차이를 이 플래그로 표현한다(데이터 분기).
        /// 입구 정의가 없는 건물은 이 플래그가 true여도 제약 없이 통과한다
        /// (EntranceOps가 "입구 없음 → 제약 없음"으로 처리).
        /// </summary>
        public bool  RequireRoadAccess;
    }

    // ══════════════════════════════════════════════════════════════════
    //  PlacementFailCode  — 배치 실패 원인 코드
    // ══════════════════════════════════════════════════════════════════
    public enum PlacementFailCode : byte
    {
        None           = 0,
        PrefabNotFound = 1,  // PrefabLookup에 해당 키 없음
        OutOfBounds    = 2,  // 셀이 TerrainLayer 범위 밖
        Occupied       = 3,  // 이미 점유된 셀
        WrongTerrain   = 4,  // 지형 타입 불일치 (땅 건물 → 물 위 등)
        HeightMismatch = 5,  // 멀티셀 건물의 셀 높이가 다름
        NoRoadAccess   = 6,  // 입구가 도로에 닿지 않음 (RequireRoadAccess=true일 때만)
    }

    // ══════════════════════════════════════════════════════════════════
    //  BuildingPlacementSystem
    //
    //  PlaceBuildingRequest 처리 순서:
    //    1. PrefabMetaLookup → Size / BuildableOn / IsRoad / IsMulti 조회
    //    2. PrefabLookup → 프리팹 Entity 존재 확인
    //    3. 셀 검증 (Size 전체):
    //       a. TerrainLayer 존재 여부  → OutOfBounds
    //       b. OccupancyLayer 비어있음 → Occupied
    //       c. 지형 TerrainCategory vs BuildableOn → WrongTerrain
    //       d. 모든 셀 Height 동일    → HeightMismatch
    //    4. 검증 통과 → 스폰 요청 발행 + OccupancyLayer 업데이트
    //    5. 요청 엔티티 파괴
    // ══════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoadSystem))]
    public partial struct BuildingPlacementSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate<PrefabMetaLookup>();
            state.RequireForUpdate<CellTypeLookup>();
            state.RequireForUpdate<EntranceLookup>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridMap>();
            state.RequireForUpdate<GridSettings>();
            state.RequireForUpdate<RoadKeyLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var prefabLookup     = SystemAPI.GetSingleton<PrefabLookup>();
            var prefabMetaLookup = SystemAPI.GetSingleton<PrefabMetaLookup>();
            var cellTypeLookup   = SystemAPI.GetSingleton<CellTypeLookup>();
            var entranceLookup   = SystemAPI.GetSingleton<EntranceLookup>();
            var gridMap          = SystemAPI.GetSingleton<GridMap>();
            var gridSettings     = SystemAPI.GetSingleton<GridSettings>();
            var roadKeyLookup    = SystemAPI.GetSingleton<RoadKeyLookup>();
            // OccupancyLayer / TerrainLayer 수정이 필요하므로 RW
            ref var layers = ref SystemAPI.GetSingletonRW<GridLayers>().ValueRW;

            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<PlaceBuildingRequest>>().WithEntityAccess())
            {
                var r = req.ValueRO;
                ProcessRequest(ref r, ref layers, prefabLookup, prefabMetaLookup,
                    cellTypeLookup, entranceLookup, gridMap, gridSettings, roadKeyLookup, ecb);

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        // ── 메인 처리 ─────────────────────────────────────────────────

        static void ProcessRequest(
            ref PlaceBuildingRequest req,
            ref GridLayers           layers,
            PrefabLookup             prefabLookup,
            PrefabMetaLookup         metaLookup,
            CellTypeLookup           cellTypeLookup,
            EntranceLookup           entranceLookup,
            GridMap                  gridMap,
            GridSettings             settings,
            RoadKeyLookup            roadKeyLookup,
            EntityCommandBuffer      ecb)
        {
            // ── 1. 메타 조회 ─────────────────────────────────────────
            if (!metaLookup.TryGetMeta(req.MainKey, req.VariantKey, out var meta))
            {
                LogFail(req, PlacementFailCode.PrefabNotFound,
                    $"PrefabMeta 없음: ({req.MainKey}, {req.VariantKey})");
                return;
            }

            // 프리팹 존재 확인
            var prefab = prefabLookup.Get(req.MainKey, req.VariantKey);
            if (prefab == Entity.Null)
            {
                LogFail(req, PlacementFailCode.PrefabNotFound,
                    $"프리팹 Entity 없음: ({req.MainKey}, {req.VariantKey})");
                return;
            }

            // ── 2. 셀 검증 ──────────────────────────────────────────
            //   회전(req.RotationY)에 따라 footprint 크기를 교환한다.
            //   90°/270°에서 Size.x↔y 교환 — origin은 최소 코너로 유지(EntranceOps 규약).
            //   도로 크기는 GamePrefabRegistry(항상 1×1 고정)가 아니라
            //   RoadPrefabRegistry.DefaultSize(RoadKeyLookup으로 베이크)를 따른다 — 회전 무관.
            int rotSteps = meta.IsRoad ? 0 : EntranceOps.RotationToSteps(req.RotationY);
            byte roadSize = meta.IsRoad ? roadKeyLookup.GetSize(req.FactionId) : (byte)1;
            int2 size = meta.IsRoad
                ? new int2(roadSize, roadSize)
                : EntranceOps.RotateSize(meta.Size, rotSteps);

            var  fail = ValidateCells(req.Cell, size, meta.BuildableOn,
                ref layers, cellTypeLookup, out byte baseHeight);

            if (fail != PlacementFailCode.None)
            {
                LogFail(req, fail, $"셀 검증 실패: {fail} at cell {req.Cell}");
                return;
            }

            // ── 2.5. 입구-도로 정렬 검증 (RequireRoadAccess일 때만) ───
            //   AI 자율 성장 / 팩션 베이스 경로에서만 강제된다.
            //   인간 직접 배치(RequireRoadAccess=false)는 이 검사를 건너뛴다 —
            //   연결성은 정보로만 제공하고 선택은 본인 몫이라는 설계 원칙.
            //   회전은 호출자가 이미 결정해 req.RotationY로 넘긴 값을 신뢰한다
            //   (AI는 EntranceOps.FindRoadFacingRotation으로, 베이스는 SO에서).
            //   여기서는 그 회전이 실제로 도로에 닿는지 "검증"만 한다(탐색하지 않음).
            //   rotSteps는 위에서 이미 계산됨 — 입구 오프셋과 footprint가 동일 회전 공유.
            if (req.RequireRoadAccess && meta.HasEntrance &&
                entranceLookup.TryGet(req.MainKey, out var entrance))
            {
                bool onRoad = EntranceOps.IsEntranceOnRoad(
                    req.Cell, meta.Size, in entrance, rotSteps, in layers.RoadLayer);

                if (!onRoad)
                {
                    LogFail(req, PlacementFailCode.NoRoadAccess,
                        $"입구가 도로에 닿지 않음 at cell {req.Cell} rotY={req.RotationY}");
                    return;
                }
            }

            // ── 3. 스폰 요청 발행 ────────────────────────────────────
            if (meta.IsRoad)
                EmitRoad(req, roadSize, ecb);
            else if (meta.IsMulti)
                EmitMulti(req, meta, size, settings, ecb);
            else
                EmitSingle(req, meta, prefab, baseHeight, rotSteps, entranceLookup, settings, ecb);

            // ── 4. OccupancyLayer 업데이트 (Road는 RoadSystem이 처리) ──
            if (!meta.IsRoad)
            {
                MarkOccupied(req.Cell, size, req.OwnerLocalId, ref layers, gridMap, ecb);

                // 건물 추가 → 잠재적 공급자 변경. 이 플레이어 stamp 무효화.
                // (도로 배치는 RoadSystem이 dirty를 발행하므로 여기선 비-도로만)
                var dirtyE = ecb.CreateEntity();
                ecb.AddComponent(dirtyE, new StampDirtyEvent { OwnerLocalId = req.OwnerLocalId });
            }
        }

        // ── 셀 검증 ───────────────────────────────────────────────────

        static PlacementFailCode ValidateCells(
            int2              origin,
            int2              size,
            TerrainMask       buildableOn,
            ref GridLayers    layers,
            CellTypeLookup    cellTypeLookup,
            out byte          baseHeight)
        {
            baseHeight = 0;
            bool firstCell = true;

            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);

                // a. 범위 확인
                if (!layers.TerrainLayer.TryGetValue(cell, out var terrain))
                    return PlacementFailCode.OutOfBounds;

                // b. 점유 확인
                if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty)
                    return PlacementFailCode.Occupied;

                // c. 지형 타입 확인
                if (cellTypeLookup.TryGet(terrain.TypeId, out var typeInfo))
                {
                    var cellMask = CategoryToMask(typeInfo.TerrainCategory);
                    if ((buildableOn & cellMask) == 0)
                        return PlacementFailCode.WrongTerrain;
                }

                // d. 높이 일치 확인
                if (firstCell)
                {
                    baseHeight = terrain.Height;
                    firstCell  = false;
                }
                else if (terrain.Height != baseHeight)
                    return PlacementFailCode.HeightMismatch;
            }

            return PlacementFailCode.None;
        }

        // ── 지형 카테고리 → 마스크 변환 ──────────────────────────────

        static TerrainMask CategoryToMask(TerrainCategory cat) => cat switch
        {
            TerrainCategory.Water => TerrainMask.Water,
            _                     => TerrainMask.Land,
        };

        // ── 스폰 발행 헬퍼 ───────────────────────────────────────────

        static void EmitSingle(
            PlaceBuildingRequest req,
            PrefabMeta           meta,
            Entity               prefab,
            byte                 baseHeight,
            int                  rotSteps,
            EntranceLookup       entranceLookup,
            GridSettings         settings,
            EntityCommandBuffer  ecb)
        {
            float3 pos = settings.CellCenter(req.Cell.x, req.Cell.y, baseHeight)
                         + meta.Offset;

            // 입구 정보: 입구 있는 건물만 적재 (검증부와 동일한 조회 경로).
            EntranceInfo entrance = default;
            bool hasEntrance = meta.HasEntrance &&
                               entranceLookup.TryGet(req.MainKey, out entrance);

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new SpawnRequest
            {
                MainKey    = req.MainKey,
                VariantKey = req.VariantKey,
                Position   = pos,
                Rotation   = quaternion.RotateY(math.radians(req.RotationY)),
                Scale      = 1f,

                // ── footprint 승격 (모든 단일 건물) ──
                HasFootprint    = true,
                FootprintOrigin = req.Cell,
                FootprintSize   = meta.Size,   // 원본 Size (회전 전) — 정규화 규약
                RotSteps        = rotSteps,
                OwnerLocalId    = req.OwnerLocalId,

                HasEntrance     = hasEntrance,
                Entrance        = entrance,

                IsSupplier      = meta.IsSupplier,
                Relief          = meta.Relief,
                SupplyMaxDist   = meta.SupplyMaxDist,
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        static void EmitMulti(
            PlaceBuildingRequest req,
            PrefabMeta           meta,
            int2                 effectiveSize,   // 회전 반영된 footprint (90°/270°시 x↔y 교환)
            GridSettings         settings,
            EntityCommandBuffer  ecb)
        {
            float cs   = settings.CellSize;
            uint  seed = (uint)(req.Cell.x * 31 + req.Cell.y + 1);

            // 멀티 분산 배치는 Count/ItemSize 기반이라 footprint 크기를 직접 쓰지 않는다.
            // effectiveSize는 점유(MarkOccupied)·검증(ValidateCells)에서 이미 소비되며,
            // 여기서는 회전 정합을 위한 시그니처 일관성으로만 받는다. 멀티 내부 분산을
            // 회전에 맞춰 재배치할 필요가 생기면 이 값을 MultiSpawnRequest로 확장 전달한다.

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new MultiSpawnRequest
            {
                MainKey    = req.MainKey,
                VariantKey = req.VariantKey,
                Cell       = req.Cell,
                CellSize   = cs,
                Height     = 0f,
                Seed       = (int)seed,
                Count      = meta.MultiCount > 0 ? meta.MultiCount : 5,
                ItemSize   = meta.MultiItemSize,
                Scale      = 1f,
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        static void EmitRoad(
            PlaceBuildingRequest req,
            byte                 roadSize,
            EntityCommandBuffer  ecb)
        {
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new PlaceRoadCommand
            {
                Cell         = req.Cell,
                OwnerLocalId = req.OwnerLocalId,
                LaneCount    = 2,
                FactionId    = req.FactionId,
                Size         = roadSize,
            });
            // Road 점유 및 (FactionId,dirMask)→MainKey→프리팹은 RoadSystem이 처리
        }

        // ── OccupancyLayer 점유 등록 ──────────────────────────────────

        static void MarkOccupied(
            int2                 origin,
            int2                 size,
            int                  ownerLocalId,
            ref GridLayers       layers,
            GridMap              gridMap,
            EntityCommandBuffer  ecb)
        {
            // 스폰될 엔티티는 아직 없으므로 임시로 Entity.Null.
            // SpawnSystem이 인스턴스화한 뒤 엔티티 참조를 업데이트하는 방식으로
            // 추후 확장 가능. 현재는 점유 여부(Type)만 기록.
            var cell_data = new OccupancyCell
            {
                Type      = OccupantType.Building,
                Occupant  = Entity.Null,
                OwnerLocalId = ownerLocalId,
            };

            for (int dx = 0; dx < size.x; dx++)
            for (int dz = 0; dz < size.y; dz++)
            {
                var cell = origin + new int2(dx, dz);
                layers.OccupancyLayer.TryAdd(cell, cell_data);
            }
        }

        // ── 로그 헬퍼 ────────────────────────────────────────────────

        static void LogFail(
            PlaceBuildingRequest req,
            PlacementFailCode    code,
            string               detail)
        {
            Debug.LogWarning(
                $"[BuildingPlacementSystem] 배치 실패 [{code}] " +
                $"MainKey={req.MainKey} VariantKey={req.VariantKey} " +
                $"Cell={req.Cell} — {detail}");
        }
    }
}
