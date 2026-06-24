using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CitySim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GridInitSystem))]
    [UpdateBefore(typeof(BuildingPlacementSystem))]   // AI가 한 턴에 발행한 도로가 건물 검증 전에 깔리도록
    public partial struct RoadSystem : ISystem
    {
        public struct DirtyRoadTag : IComponentData { }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate<RoadKeyLookup>();
            state.RequireForUpdate<GridSettings>();
            state.RequireForUpdate<CellTypeLookup>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers = SystemAPI.GetSingleton<GridLayers>();
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            var roadKeys = SystemAPI.GetSingleton<RoadKeyLookup>();
            var cellTypeLookup = SystemAPI.GetSingleton<CellTypeLookup>();
            var cellSize = SystemAPI.GetSingleton<GridSettings>().CellSize;
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 1) 프리뷰
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<PreviewRoadCommand>>().WithEntityAccess())
            {
                var cell = cmd.ValueRO.Cell;
                // 프리뷰는 축 미정(드래그 방향 확정 전)이라 Any로 최대 연결을 보여준다.
                var dirs = ComputeDirections(cell, cmd.ValueRO.OwnerLocalId, RoadPlacedAxis.Any,
                    layers.RoadLayer, layers.TerrainLayer);

                var oldQ = SystemAPI.QueryBuilder().WithAll<RoadPreview>().Build();
                ecb.DestroyEntity(oldQ, EntityQueryCaptureMode.AtPlayback);

                var preview = ecb.CreateEntity();
                ecb.AddComponent(preview, new RoadPreview { Cell = cell, Directions = dirs });
                ecb.DestroyEntity(cmdEntity);
            }

            // 2) 도로 배치
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<PlaceRoadCommand>>().WithEntityAccess())
            {
                var origin    = cmd.ValueRO.Cell;
                var ownerLocalId = cmd.ValueRO.OwnerLocalId;
                var laneCount = cmd.ValueRO.LaneCount == 0 ? (byte)2 : cmd.ValueRO.LaneCount;
                var factionId = cmd.ValueRO.FactionId;
                var size      = cmd.ValueRO.Size <= 1 ? (byte)1 : cmd.ValueRO.Size;

                // footprint 전체 셀 중 하나라도
                //   · 환경 외 점유물(도로/건물)         → 거부
                //   · 채취 자원(ResourceLayer Amount>0) → 거부 (자원 보존)
                //   · footprint 내부 단차              → 거부 (도로는 경사에 못 앉음)
                // 환경(나무/바위)은 막지 않고 통과 후 제거한다.
                // 단차 너머 인접 도로와는 "연결만" 안 함(ComputeDirections 높이 필터). 배치는 허용.
                // 교차(도로 위 도로) 허용 조건: footprint 1×1(size==1)이고 같은 소유자 도로일 때만.
                //   → 가로지르는 도로가 기존 도로 셀을 '통과(공유)'해 교차로로 승격.
                //   size>1은 footprint/엔티티 모델이 부분 겹침을 못 다뤄 기존대로 거부.
                bool blocked = false;
                bool firstCell = true; byte baseHeight = 0;
                for (int dx = 0; dx < size && !blocked; dx++)
                for (int dz = 0; dz < size && !blocked; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                        && occ.Type != OccupantType.Environment)
                    {
                        bool sameOwnerRoad = size == 1
                            && occ.Type == OccupantType.Road && occ.OwnerLocalId == ownerLocalId;
                        if (!sameOwnerRoad) blocked = true;   // 교차 허용 외엔 거부
                    }
                    else if (layers.ResourceLayer.TryGetValue(c, out var res) && res.Amount > 0)
                        blocked = true;

                    // 다른 플레이어 영역(클레임) 안엔 도로 배치 불가 (적 건물 M칸 이내).
                    //   도로는 영역이 아니므로 적 도로 옆/근처엔 깔 수 있음(도로 위 충돌은 위 점유 검사 소관).
                    if (!blocked &&
                        ClaimOps.InEnemyClaim(c, ownerLocalId, ClaimOps.DefaultMargin, in layers.OccupancyLayer))
                        blocked = true;

                    // 지형 타입 거부 — 도로는 물 위에 못 깐다 (다리 미지원). Land 전용.
                    if (!layers.TerrainLayer.TryGetValue(c, out var ct))
                        blocked = true;          // 맵 밖
                    else
                    {
                        if (cellTypeLookup.TryGet(ct.TypeId, out var ti)
                            && ti.TerrainCategory == TerrainCategory.Water)
                            blocked = true;

                        // footprint 내부 단차 거부 (모든 셀 지형 높이 동일해야 함)
                        if (firstCell) { baseHeight = ct.Height; firstCell = false; }
                        else if (ct.Height != baseHeight) blocked = true;
                    }
                }
                if (blocked) { ecb.DestroyEntity(cmdEntity); continue; }

                // 통과 → footprint 내 환경 오브젝트(나무/바위)를 경고 없이 제거.
                // 직접 destroy하지 않고 셀 단위 요청 발행(EnvironmentClearSystem이 처리).
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (layers.OccupancyLayer.TryGetValue(c, out var eo)
                        && eo.Type == OccupantType.Environment)
                    {
                        var clr = ecb.CreateEntity();
                        ecb.AddComponent(clr, new EnvironmentClearRequest { Cell = c });
                        layers.OccupancyLayer.Remove(c);
                    }
                }

                // ── 그린-방향(연속성) 모델 — 유저 드래그(항상 1×1) ──────────────
                //   Directions(드래그가 그린 진입/탈출 비트)를 RoadCell에 직접 반영.
                //     · 새 셀  → set
                //     · 기존 같은-소유자 셀 → OR (겹침 = 교차/T 승격, 기존 비트 보존)
                //   축 재계산·경계 전파 없음: 경로 이웃을 향한 비트만 담겨 상호 비트
                //   불변식이 자동 성립하고, 인접만 한(안 겹친) 도로엔 비트가 안 생긴다
                //   → 평행 분리 + "겹쳐야만 연결"(끊어진 도로 가능)이 공짜로 성립.
                var drawnDirs = cmd.ValueRO.Directions;
                if (drawnDirs != RoadDir.None)
                {
                    if (layers.RoadLayer.TryGetValue(origin, out var ecell)
                        && ecell.OwnerLocalId == ownerLocalId)
                    {
                        ecell.Directions |= drawnDirs;          // 겹침: 진입/탈출 비트 추가
                        ecell.Explicit    = true;
                        ecell.FlowAxis    = ComputeFlowAxis(ecell.Directions);
                        layers.RoadLayer[origin] = ecell;
                        if (ecell.RoadEntity != Entity.Null
                            && !em.HasComponent<DirtyRoadTag>(ecell.RoadEntity))
                            ecb.AddComponent<DirtyRoadTag>(ecell.RoadEntity);
                    }
                    else
                    {
                        layers.RoadLayer[origin] = new RoadCell
                        {
                            Directions      = drawnDirs,
                            FlowAxis        = ComputeFlowAxis(drawnDirs),
                            LaneCount       = laneCount,
                            OwnerLocalId    = ownerLocalId,
                            RoadEntity      = Entity.Null,
                            FootprintOrigin = origin,
                            Size            = 1,
                            Axis            = RoadPlacedAxis.Any,
                            Explicit        = true,
                        };
                        layers.OccupancyLayer[origin] = new OccupancyCell
                        {
                            Type         = OccupantType.Road,
                            Occupant     = Entity.Null,
                            OwnerLocalId = ownerLocalId,
                        };
                        var newRoad = ecb.CreateEntity();
                        ecb.AddComponent(newRoad, new GridPosition { Value = origin });
                        ecb.AddComponent(newRoad, new Road
                        {
                            Directions               = drawnDirs,
                            FactionId                = factionId,
                            LaneCount                = laneCount,
                            Size                     = 1,
                            FootprintOrigin          = origin,
                            VisualDirectionsOverride = RoadDir.None,
                            Axis                     = RoadPlacedAxis.Any,
                        });
                        ecb.AddComponent(newRoad, new RoadVisualInstance { Instance = Entity.Null });
                        ecb.AddComponent(newRoad, new DirtyRoadTag());
                    }

                    var drawnDirty = ecb.CreateEntity();
                    ecb.AddComponent(drawnDirty, new StampDirtyEvent { OwnerLocalId = ownerLocalId });
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

                var placedAxis = cmd.ValueRO.Axis;

                // footprint 전체 셀 등록. 기존 같은-소유자 도로 셀은 '교차'로 축 병합(신규 등록 X).
                //   originFresh = origin 셀이 새 도로인가(아니면 = 기존 도로 위 교차 → 새 엔티티 안 만듦).
                bool originFresh = !(layers.RoadLayer.TryGetValue(origin, out var oc)
                                     && oc.OwnerLocalId == ownerLocalId);
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (layers.RoadLayer.TryGetValue(c, out var existing)
                        && existing.OwnerLocalId == ownerLocalId)
                    {
                        // 교차: 기존 셀 축에 새 축 병합 → 교차로 승격. 기존 엔티티는 dirty.
                        existing.Axis = CombineAxis(existing.Axis, placedAxis);
                        layers.RoadLayer[c] = existing;
                        if (existing.RoadEntity != Entity.Null
                            && !em.HasComponent<DirtyRoadTag>(existing.RoadEntity))
                            ecb.AddComponent<DirtyRoadTag>(existing.RoadEntity);
                    }
                    else
                    {
                        layers.RoadLayer[c] = new RoadCell
                        {
                            Directions      = RoadDir.None,   // 아래 재계산 루프에서 채움
                            FlowAxis        = default,
                            LaneCount       = laneCount,
                            OwnerLocalId    = ownerLocalId,
                            RoadEntity      = Entity.Null,
                            FootprintOrigin = origin,
                            Size            = size,
                            Axis            = placedAxis,
                        };
                        layers.OccupancyLayer[c] = new OccupancyCell
                        {
                            Type         = OccupantType.Road,
                            Occupant     = Entity.Null,
                            OwnerLocalId = ownerLocalId,
                        };
                    }
                }

                // footprint 셀들의 방향 비트를 이웃 포함해 재계산 (각 셀의 병합된 축 사용)
                // (먼저 전체 등록이 끝난 뒤 재계산해야 내부 연결이 올바름)
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (!layers.RoadLayer.TryGetValue(c, out var rc)) continue;
                    rc.Directions = ComputeDirections(c, ownerLocalId, rc.Axis, layers.RoadLayer, layers.TerrainLayer);
                    rc.FlowAxis   = ComputeFlowAxis(rc.Directions);
                    layers.RoadLayer[c] = rc;
                }

                // origin이 새 도로일 때만 Road 엔티티 1개 생성(FixupRoadLayer가 셀에 참조 채움).
                // origin이 기존 도로 위 교차면 새 엔티티 없이 기존 엔티티만 dirty(위에서 처리됨).
                if (originFresh)
                {
                    var road = ecb.CreateEntity();
                    ecb.AddComponent(road, new GridPosition { Value = origin });
                    ecb.AddComponent(road, new Road
                    {
                        Directions               = layers.RoadLayer[origin].Directions,
                        FactionId                = factionId,
                        LaneCount                = laneCount,
                        Size                     = size,
                        FootprintOrigin          = origin,
                        VisualDirectionsOverride = cmd.ValueRO.VisualDirectionsOverride,
                        Axis                     = layers.RoadLayer[origin].Axis,
                    });
                    ecb.AddComponent(road, new RoadVisualInstance { Instance = Entity.Null });
                    ecb.AddComponent(road, new DirtyRoadTag());
                }

                // footprint 외곽 이웃 셀 방향 비트 갱신
                UpdateFootprintBoundaryDirections(origin, size, layers, em, ecb, removing: false);

                var dirtyAdd = ecb.CreateEntity();
                ecb.AddComponent(dirtyAdd, new StampDirtyEvent { OwnerLocalId = ownerLocalId });

                ecb.DestroyEntity(cmdEntity);
            }

            // 3) 도로 철거 — 어느 셀이든 footprint 전체를 한 번에 제거
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<RemoveRoadCommand>>().WithEntityAccess())
            {
                var cell         = cmd.ValueRO.Cell;
                var forced       = cmd.ValueRO.Forced != 0;

                if (!layers.RoadLayer.TryGetValue(cell, out var hitCell) ||
                    (!forced && hitCell.OwnerLocalId != cmd.ValueRO.OwnerLocalId))
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }
                // 실제 도로 소유자(강제 철거 시 cmd.OwnerLocalId와 다를 수 있음) — 정리·StampDirty에 사용.
                var ownerLocalId = hitCell.OwnerLocalId;

                var origin = hitCell.FootprintOrigin;
                var size   = hitCell.Size <= 1 ? (byte)1 : hitCell.Size;

                // Road 엔티티 파괴 (origin 셀 기준 1개)
                if (layers.RoadLayer.TryGetValue(origin, out var originCell) &&
                    originCell.RoadEntity != Entity.Null)
                {
                    var vis = em.GetComponentData<RoadVisualInstance>(originCell.RoadEntity);
                    if (vis.Instance != Entity.Null)
                        ecb.DestroyEntity(vis.Instance);
                    ecb.DestroyEntity(originCell.RoadEntity);
                }

                // footprint 전체 셀 제거
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    layers.RoadLayer.Remove(c);
                    layers.OccupancyLayer.Remove(c);
                }

                // 외곽 이웃 방향 비트 갱신 — 철거: 살아남은 이웃의 '제거된 쪽' 비트를 지워 모양 갱신(T→직선)
                UpdateFootprintBoundaryDirections(origin, size, layers, em, ecb, removing: true);

                var dirtyRemove = ecb.CreateEntity();
                ecb.AddComponent(dirtyRemove, new StampDirtyEvent { OwnerLocalId = ownerLocalId });

                ecb.DestroyEntity(cmdEntity);
            }

            // 4) 도로 업그레이드
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<UpgradeRoadCommand>>().WithEntityAccess())
            {
                var cell = cmd.ValueRO.Cell;
                var ownerLocalId = cmd.ValueRO.OwnerLocalId;

                if (!layers.RoadLayer.TryGetValue(cell, out var roadCell) ||
                    roadCell.OwnerLocalId != ownerLocalId ||
                    roadCell.LaneCount >= 4)
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

                roadCell.LaneCount = 4;
                layers.RoadLayer[cell] = roadCell;

                if (roadCell.RoadEntity != Entity.Null)
                {
                    var roadComp = em.GetComponentData<Road>(roadCell.RoadEntity);
                    roadComp.LaneCount = 4;
                    ecb.SetComponent(roadCell.RoadEntity, roadComp);
                    ecb.AddComponent<DirtyRoadTag>(roadCell.RoadEntity);
                }

                // 차선 업그레이드는 '도달성'을 바꾸지 않으므로 stamp 무효화 불필요.
                // (stamp = 어디까지 닿는가. 혼잡도/거리 가중에 LaneCount를 쓰게 되면 그때 추가)
                ecb.DestroyEntity(cmdEntity);
            }

            ecb.Playback(em);
            ecb.Dispose();

            FixupRoadLayer(ref state, ref layers);

            // 5) 시각 인스턴스 교체 (비트마스크 → PrefabLookup.GetRoad)
            var ecb2 = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (gp, road, vis, e) in
                     SystemAPI.Query<
                         RefRO<GridPosition>,
                         RefRW<Road>,
                         RefRW<RoadVisualInstance>>()
                     .WithAll<DirtyRoadTag>()
                     .WithEntityAccess())
            {
                if (vis.ValueRO.Instance != Entity.Null)
                {
                    ecb2.DestroyEntity(vis.ValueRO.Instance);
                    ecb2.SetComponent(e, new RoadVisualInstance { Instance = Entity.Null });
                }

                var cell = gp.ValueRO.Value;
                int visualOwner = -1;
                RoadDir cellDirs = RoadDir.None;
                if (layers.RoadLayer.TryGetValue(cell, out var roadCell))
                {
                    road.ValueRW.Directions = roadCell.Directions;
                    road.ValueRW.LaneCount  = roadCell.LaneCount;
                    road.ValueRW.Axis       = roadCell.Axis;   // 교차 병합된 축 반영
                    visualOwner             = roadCell.OwnerLocalId;
                    cellDirs                = roadCell.Directions;
                }

                // 비주얼 방향: size==1은 셀의 권위 Directions(축-AND·교차 병합 반영)를 그대로 사용
                //   → 데이터=시각 일치(평행 분리/교차 사거리). size>1 블록만 매크로 경로.
                byte macroSize = road.ValueRO.Size <= 1 ? (byte)1 : road.ValueRO.Size;
                var dirs2 = macroSize == 1
                    ? cellDirs
                    : ComputeAxisFilteredMacroDirections(
                        cell, macroSize, road.ValueRO.Axis, visualOwner, layers.RoadLayer, layers.TerrainLayer);
                var factionId = road.ValueRO.FactionId;
                Entity prefabEntity = Entity.Null;

                if (dirs2 != RoadDir.None
                    && roadKeys.TryGet(factionId, dirs2, out int mainKey))
                {
                    // VariantKey는 외형 베리언트 자리(현재 0=기본).
                    // 도로 외형 베리언트가 생기면 여기서 해소한다.
                    prefabEntity = lookup.Get(mainKey, 0);
                }

                if (prefabEntity != Entity.Null)
                {
                    var instance = ecb2.Instantiate(prefabEntity);
                    float roadHalf = road.ValueRO.Size * 0.5f;
                    // Y는 지형 높이를 따라간다 (CellToWorld 규약과 동일: height * cellSize).
                    // 예전엔 0f 하드코딩이라 높이 1+ 지형 위 도로가 전부 0에 깔리던 버그.
                    float roadY = layers.TerrainLayer.TryGetValue(cell, out var rtc)
                        ? rtc.Height * cellSize : 0f;
                    float3 worldPos = new float3(
                        (cell.x + roadHalf) * cellSize,
                        roadY,
                        (cell.y + roadHalf) * cellSize);

                    // 프리팹에 미리 회전·스케일이 베이크돼 있으므로(방향별로 직접
                    // 회전된 15종 프리팹) 위치만 바꾸고 회전/스케일은 보존한다.
                    // LocalTransform.FromPosition은 회전을 identity로 덮어써버려
                    // 모든 도로가 회전 0으로 보이는 버그의 원인이었다.
                    var prefabTransform = em.GetComponentData<LocalTransform>(prefabEntity);
                    ecb2.SetComponent(instance, new LocalTransform
                    {
                        Position = worldPos,
                        Rotation = prefabTransform.Rotation,
                        Scale    = prefabTransform.Scale,
                    });
                    // instance는 ecb2의 지연 엔티티 — 직접 쓰면 다음 프레임 ecb2에서
                    // "different command buffer" 오류 발생. ECB에 위임해 playback 시 해소.
                    ecb2.SetComponent(e, new RoadVisualInstance { Instance = instance });
                }

                ecb2.RemoveComponent<DirtyRoadTag>(e);
            }

            ecb2.Playback(em);
            ecb2.Dispose();
        }

        // ── 헬퍼 ──────────────────────────────────────────────────

        // 셀 단위 — BFS/물류/시각의 권위 데이터(RoadCell.Directions) 계산.
        //   연결 규칙(축-AND): 방향 d로 이으려면
        //     ① 같은 소유자 ② 같은 지형 높이(단차 너머 X)
        //     ③ '두 셀이 그 방향 축을 모두 허용'해야 한다.
        //        E/W 연결 = 두 셀 다 EW(또는 Any=교차) 보유.
        //        N/S 연결 = 두 셀 다 NS(또는 Any) 보유.
        //   → 평행 도로(둘 다 NS)는 E/W로 안 이어짐(인접해도 분리).
        //     가로지른 교차 셀(Any=EW+NS)만 4방향으로 이어짐.
        //   myAxis = 이 셀의 배치 축(레이어에 아직 없을 수 있어 인자로 받음).
        public static RoadDir ComputeDirections(
            int2 cell, int ownerLocalId, RoadPlacedAxis myAxis,
            NativeHashMap<int2, RoadCell> roadLayer,
            NativeHashMap<int2, TerrainCell> terrainLayer)
        {
            byte myH = CellHeight(cell, terrainLayer);
            RoadDir dirs = RoadDir.None;
            for (int i = 0; i < 4; i++)
            {
                bool isEW = i == 1 || i == 3;          // E/W = 동서, N/S = 남북
                if (!AxisAllows(myAxis, isEW)) continue;
                var nCell = cell + RoadDirOps.Offsets[i];
                if (roadLayer.TryGetValue(nCell, out var nc)
                    && nc.OwnerLocalId == ownerLocalId
                    && CellHeight(nCell, terrainLayer) == myH
                    && NeighborAllows(nc, i, isEW))
                    dirs |= RoadDirOps.FromIndex(i);
            }
            return dirs;
        }

        // 이웃 셀이 '나'에게서 방향 i로 연결을 되받는가(상호 일치).
        //   · 명시(Explicit) 이웃: 그 셀의 Directions에 반대 비트가 있어야 함(그린 권위값).
        //     → 인접만 한 그린 도로엔 비트가 없으니 자동 분리(겹쳐야만 연결).
        //   · 레거시 이웃: 기존 축(Axis) 규칙.
        static bool NeighborAllows(RoadCell nc, int i, bool isEW)
            => nc.Explicit
                ? (nc.Directions & RoadDirOps.FromIndex(RoadDirOps.OppositeIndex(i))) != 0
                : AxisAllows(nc.Axis, isEW);

        // 축이 그 방향(isEW=동서 / else 남북) 연결을 허용하는가.
        //   Any = 둘 다 허용(교차 셀). EW = 동서만. NS = 남북만.
        public static bool AxisAllows(RoadPlacedAxis axis, bool isEW)
            => axis == RoadPlacedAxis.Any || (isEW ? axis == RoadPlacedAxis.EW : axis == RoadPlacedAxis.NS);

        // 같은 셀에 두 세그먼트가 겹치면(가로지름) 축을 합쳐 교차(Any)로 승격.
        public static RoadPlacedAxis CombineAxis(RoadPlacedAxis a, RoadPlacedAxis b)
            => a == b ? a : RoadPlacedAxis.Any;

        static byte CellHeight(int2 cell, NativeHashMap<int2, TerrainCell> terrainLayer)
            => terrainLayer.IsCreated && terrainLayer.TryGetValue(cell, out var t)
                ? t.Height : (byte)0;

        // 매크로 단위 — 비주얼 프리팹 선택용.
        // footprint 외곽 경계 너머의 같은 소유자 도로만 본다.
        public static RoadDir ComputeMacroDirections(
            int2 origin, byte size, int ownerLocalId,
            NativeHashMap<int2, RoadCell> roadLayer,
            NativeHashMap<int2, TerrainCell> terrainLayer)
        {
            byte myH = CellHeight(origin, terrainLayer);
            RoadDir dirs = RoadDir.None;
            for (int d = 0; d < 4; d++)
            {
                var off = RoadDirOps.Offsets[d];
                bool found = false;
                for (int i = 0; i < size && !found; i++)
                {
                    int2 check = off.x != 0
                        ? new int2(origin.x + (off.x > 0 ? size : -1), origin.y + i)
                        : new int2(origin.x + i, origin.y + (off.y > 0 ? size : -1));
                    if (roadLayer.TryGetValue(check, out var nc) && nc.OwnerLocalId == ownerLocalId
                        && CellHeight(check, terrainLayer) == myH)
                        found = true;
                }
                if (found) dirs |= RoadDirOps.FromIndex(d);
            }
            return dirs;
        }

        // 축 필터링 매크로 방향.
        // 두 도로가 방향 D로 연결되려면:
        //   ① 같은 소유자여야 하고
        //   ② 적어도 한 쪽이 그 방향 축을 허용해야 한다.
        //   EW 도로: E/W 연결 허용. N/S는 이웃이 NS 또는 Any일 때만 허용(교차로).
        //   NS 도로: N/S 연결 허용. E/W는 이웃이 EW 또는 Any일 때만 허용.
        //   Any    : 모든 방향 연결 허용 (지도 사전 배치, 베이스캠프).
        // → 같은 축 평행 도로끼리는 연결 안 됨. 다른 소유자 도로와도 연결 안 됨.
        public static RoadDir ComputeAxisFilteredMacroDirections(
            int2 origin, byte size, RoadPlacedAxis myAxis, int ownerLocalId,
            NativeHashMap<int2, RoadCell> roadLayer,
            NativeHashMap<int2, TerrainCell> terrainLayer)
        {
            byte myH = CellHeight(origin, terrainLayer);
            RoadDir dirs = RoadDir.None;
            for (int d = 0; d < 4; d++)
            {
                var off   = RoadDirOps.Offsets[d];
                bool isEW = off.x != 0; // true=E/W, false=N/S

                bool myAllows = myAxis == RoadPlacedAxis.Any
                    || (myAxis == RoadPlacedAxis.EW && isEW)
                    || (myAxis == RoadPlacedAxis.NS && !isEW);

                bool found = false;
                for (int i = 0; i < size && !found; i++)
                {
                    int2 check = isEW
                        ? new int2(origin.x + (off.x > 0 ? size : -1), origin.y + i)
                        : new int2(origin.x + i, origin.y + (off.y > 0 ? size : -1));

                    if (!roadLayer.TryGetValue(check, out var neighbor)) continue;
                    if (neighbor.OwnerLocalId != ownerLocalId) continue; // 다른 플레이어 무시
                    if (CellHeight(check, terrainLayer) != myH) continue; // 단차 너머 연결 안 함

                    bool neighborAllows = neighbor.Axis == RoadPlacedAxis.Any
                        || (neighbor.Axis == RoadPlacedAxis.EW && isEW)
                        || (neighbor.Axis == RoadPlacedAxis.NS && !isEW);

                    if (myAllows || neighborAllows)
                        found = true;
                }
                if (found) dirs |= RoadDirOps.FromIndex(d);
            }
            return dirs;
        }

        static RoadFlowAxis ComputeFlowAxis(RoadDir dirs)
        {
            if (RoadDirOps.PopCount(dirs) >= 3) return RoadFlowAxis.Cross;
            bool hasEW = (dirs & (RoadDir.E | RoadDir.W)) != 0;
            bool hasNS = (dirs & (RoadDir.N | RoadDir.S)) != 0;
            if (hasEW && hasNS) return RoadFlowAxis.Cross;
            return hasEW ? RoadFlowAxis.Horizontal : RoadFlowAxis.Vertical;
        }

        // footprint 외곽에 인접한 기존 도로 셀들의 방향 비트를 갱신한다.
        // footprint 내부 셀들은 이미 올바르게 설정되어 있으므로 스킵.
        // removing=true: 철거 경로 — 살아남은 그린(Explicit) 이웃의 '제거된 footprint 쪽' 비트를
        //   지워 모양을 갱신한다(예: 블록이 사라진 경계의 T → 직선). 레거시 이웃은 ComputeDirections
        //   재계산으로 자동 반영. removing=false(배치): 그린 권위값 보존(기존 동작).
        static void UpdateFootprintBoundaryDirections(
            int2 origin, byte size,
            GridLayers layers,
            EntityManager em,
            EntityCommandBuffer ecb,
            bool removing)
        {
            // footprint 4변의 바깥쪽 셀들만 순회
            var visited = new NativeHashSet<int2>(32, Allocator.Temp);

            for (int dx = 0; dx < size; dx++)
            for (int dz = 0; dz < size; dz++)
            {
                var c = origin + new int2(dx, dz);
                for (int i = 0; i < 4; i++)
                {
                    var nCell = c + RoadDirOps.Offsets[i];
                    // footprint 내부 셀이면 스킵
                    bool inside = nCell.x >= origin.x && nCell.x < origin.x + size
                               && nCell.y >= origin.y && nCell.y < origin.y + size;
                    if (inside) continue;
                    if (!visited.Add(nCell)) continue;
                    if (!layers.RoadLayer.TryGetValue(nCell, out var nRoadCell)) continue;

                    RoadDir newDirs;
                    if (nRoadCell.Explicit)
                    {
                        // 그린(명시) 셀: Directions가 권위값.
                        //   배치 → 보존(축 재계산 금지). 철거 → 제거된 footprint(c) 쪽 비트만 제거.
                        if (!removing) continue;
                        var clearBit = RoadDirOps.FromIndex(RoadDirOps.OppositeIndex(i));
                        newDirs = nRoadCell.Directions & ~clearBit;
                    }
                    else
                    {
                        newDirs = ComputeDirections(nCell, nRoadCell.OwnerLocalId, nRoadCell.Axis, layers.RoadLayer, layers.TerrainLayer);
                    }

                    if (newDirs == nRoadCell.Directions) continue;

                    nRoadCell.Directions = newDirs;
                    nRoadCell.FlowAxis   = ComputeFlowAxis(newDirs);
                    layers.RoadLayer[nCell] = nRoadCell;

                    if (nRoadCell.RoadEntity != Entity.Null)
                    {
                        var roadComp = em.GetComponentData<Road>(nRoadCell.RoadEntity);
                        roadComp.Directions = newDirs;
                        ecb.SetComponent(nRoadCell.RoadEntity, roadComp);
                        if (!em.HasComponent<DirtyRoadTag>(nRoadCell.RoadEntity))
                            ecb.AddComponent<DirtyRoadTag>(nRoadCell.RoadEntity);
                    }
                }
            }

            visited.Dispose();
        }

        void FixupRoadLayer(ref SystemState state, ref GridLayers layers)
        {
            foreach (var (gp, road, e) in
                     SystemAPI.Query<RefRO<GridPosition>, RefRO<Road>>()
                     .WithEntityAccess())
            {
                var origin = gp.ValueRO.Value;
                var size   = road.ValueRO.Size <= 1 ? (byte)1 : road.ValueRO.Size;

                // footprint 전체 셀에 RoadEntity + Occupant 참조를 채운다
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c = origin + new int2(dx, dz);

                    if (layers.RoadLayer.TryGetValue(c, out var roadCell) &&
                        roadCell.RoadEntity == Entity.Null)
                    {
                        roadCell.RoadEntity = e;
                        layers.RoadLayer[c] = roadCell;
                    }

                    if (layers.OccupancyLayer.TryGetValue(c, out var occ) &&
                        occ.Occupant == Entity.Null)
                    {
                        occ.Occupant = e;
                        layers.OccupancyLayer[c] = occ;
                    }
                }
            }
        }
    }
}
