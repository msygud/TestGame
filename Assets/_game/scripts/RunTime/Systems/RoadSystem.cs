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
                var dirs = ComputeDirections(cell, cmd.ValueRO.OwnerLocalId, layers.RoadLayer, layers.TerrainLayer);

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
                bool blocked = false;
                bool firstCell = true; byte baseHeight = 0;
                for (int dx = 0; dx < size && !blocked; dx++)
                for (int dz = 0; dz < size && !blocked; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty
                        && occ.Type != OccupantType.Environment)
                        blocked = true;
                    else if (layers.ResourceLayer.TryGetValue(c, out var res) && res.Amount > 0)
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

                var placedAxis = cmd.ValueRO.Axis;

                // footprint 전체 셀을 RoadLayer + OccupancyLayer에 등록
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c    = origin + new int2(dx, dz);
                    var dirs = ComputeDirections(c, ownerLocalId, layers.RoadLayer, layers.TerrainLayer);
                    layers.RoadLayer.Add(c, new RoadCell
                    {
                        Directions      = dirs,
                        FlowAxis        = ComputeFlowAxis(dirs),
                        LaneCount       = laneCount,
                        OwnerLocalId    = ownerLocalId,
                        RoadEntity      = Entity.Null,
                        FootprintOrigin = origin,
                        Size            = size,
                        Axis            = placedAxis,
                    });
                    layers.OccupancyLayer[c] = new OccupancyCell
                    {
                        Type         = OccupantType.Road,
                        Occupant     = Entity.Null,
                        OwnerLocalId = ownerLocalId,
                    };
                }

                // footprint 내부 셀들의 방향 비트를 이웃 포함해 재계산
                // (먼저 전체 등록이 끝난 뒤 재계산해야 내부 연결이 올바름)
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (!layers.RoadLayer.TryGetValue(c, out var rc)) continue;
                    rc.Directions = ComputeDirections(c, ownerLocalId, layers.RoadLayer, layers.TerrainLayer);
                    rc.FlowAxis   = ComputeFlowAxis(rc.Directions);
                    layers.RoadLayer[c] = rc;
                }

                // Road 엔티티는 origin 셀 기준 1개 (FixupRoadLayer가 전체 셀에 참조를 채움)
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
                    Axis                     = placedAxis,
                });
                ecb.AddComponent(road, new RoadVisualInstance { Instance = Entity.Null });
                ecb.AddComponent(road, new DirtyRoadTag());

                // footprint 외곽 이웃 셀 방향 비트 갱신
                UpdateFootprintBoundaryDirections(origin, size, layers, em, ecb);

                var dirtyAdd = ecb.CreateEntity();
                ecb.AddComponent(dirtyAdd, new StampDirtyEvent { OwnerLocalId = ownerLocalId });

                ecb.DestroyEntity(cmdEntity);
            }

            // 3) 도로 철거 — 어느 셀이든 footprint 전체를 한 번에 제거
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<RemoveRoadCommand>>().WithEntityAccess())
            {
                var cell         = cmd.ValueRO.Cell;
                var ownerLocalId = cmd.ValueRO.OwnerLocalId;

                if (!layers.RoadLayer.TryGetValue(cell, out var hitCell) ||
                    hitCell.OwnerLocalId != ownerLocalId)
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

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

                // 외곽 이웃 방향 비트 갱신
                UpdateFootprintBoundaryDirections(origin, size, layers, em, ecb);

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
                if (layers.RoadLayer.TryGetValue(cell, out var roadCell))
                {
                    road.ValueRW.Directions = roadCell.Directions;
                    road.ValueRW.LaneCount  = roadCell.LaneCount;
                    visualOwner             = roadCell.OwnerLocalId;
                }

                // 비주얼 방향: 축 필터링된 매크로 방향으로 항상 재계산.
                // 소유자 필터도 포함 — 다른 플레이어의 도로에 시각적으로 연결되지 않도록.
                byte macroSize = road.ValueRO.Size <= 1 ? (byte)1 : road.ValueRO.Size;
                var dirs2 = ComputeAxisFilteredMacroDirections(
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

        // 셀 단위 — BFS/점유 계산용 (Size=1일 때 MacroDirections와 동일).
        // 같은 소유자 + 같은 지형 높이의 도로만 연결 (단차 너머로는 연결 안 됨).
        public static RoadDir ComputeDirections(
            int2 cell, int ownerLocalId,
            NativeHashMap<int2, RoadCell> roadLayer,
            NativeHashMap<int2, TerrainCell> terrainLayer)
        {
            byte myH = CellHeight(cell, terrainLayer);
            RoadDir dirs = RoadDir.None;
            for (int i = 0; i < 4; i++)
            {
                var nCell = cell + RoadDirOps.Offsets[i];
                if (roadLayer.TryGetValue(nCell, out var nc)
                    && nc.OwnerLocalId == ownerLocalId
                    && CellHeight(nCell, terrainLayer) == myH)
                    dirs |= RoadDirOps.FromIndex(i);
            }
            return dirs;
        }

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
        static void UpdateFootprintBoundaryDirections(
            int2 origin, byte size,
            GridLayers layers,
            EntityManager em,
            EntityCommandBuffer ecb)
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

                    var newDirs = ComputeDirections(nCell, nRoadCell.OwnerLocalId, layers.RoadLayer, layers.TerrainLayer);
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
