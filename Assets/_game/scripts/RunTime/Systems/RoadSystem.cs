using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CitySim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(GridInitSystem))]
    public partial struct RoadSystem : ISystem
    {
        public struct DirtyRoadTag : IComponentData { }

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate<RoadKeyLookup>();
            state.RequireForUpdate<GridSettings>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers = SystemAPI.GetSingleton<GridLayers>();
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            var roadKeys = SystemAPI.GetSingleton<RoadKeyLookup>();
            var cellSize = SystemAPI.GetSingleton<GridSettings>().CellSize;
            var em = state.EntityManager;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            // 1) 프리뷰
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<PreviewRoadCommand>>().WithEntityAccess())
            {
                var cell = cmd.ValueRO.Cell;
                var dirs = ComputeDirections(cell, layers.RoadLayer);

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

                // footprint 전체 셀 중 하나라도 점유돼 있으면 배치 거부
                bool blocked = false;
                for (int dx = 0; dx < size && !blocked; dx++)
                for (int dz = 0; dz < size && !blocked; dz++)
                {
                    var c = origin + new int2(dx, dz);
                    if (layers.OccupancyLayer.TryGetValue(c, out var occ) && !occ.IsEmpty)
                        blocked = true;
                }
                if (blocked) { ecb.DestroyEntity(cmdEntity); continue; }

                // footprint 전체 셀을 RoadLayer + OccupancyLayer에 등록
                for (int dx = 0; dx < size; dx++)
                for (int dz = 0; dz < size; dz++)
                {
                    var c    = origin + new int2(dx, dz);
                    var dirs = ComputeDirections(c, layers.RoadLayer);
                    layers.RoadLayer.Add(c, new RoadCell
                    {
                        Directions      = dirs,
                        FlowAxis        = ComputeFlowAxis(dirs),
                        LaneCount       = laneCount,
                        OwnerLocalId    = ownerLocalId,
                        RoadEntity      = Entity.Null,
                        FootprintOrigin = origin,
                        Size            = size,
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
                    rc.Directions = ComputeDirections(c, layers.RoadLayer);
                    rc.FlowAxis   = ComputeFlowAxis(rc.Directions);
                    layers.RoadLayer[c] = rc;
                }

                // Road 엔티티는 origin 셀 기준 1개 (FixupRoadLayer가 전체 셀에 참조를 채움)
                var road = ecb.CreateEntity();
                ecb.AddComponent(road, new GridPosition { Value = origin });
                ecb.AddComponent(road, new Road
                {
                    Directions                = layers.RoadLayer[origin].Directions,
                    FactionId                 = factionId,
                    LaneCount                 = laneCount,
                    Size                      = size,
                    FootprintOrigin           = origin,
                    VisualDirectionsOverride  = cmd.ValueRO.VisualDirectionsOverride,
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
                    vis.ValueRW.Instance = Entity.Null;
                }

                var cell = gp.ValueRO.Value;
                if (layers.RoadLayer.TryGetValue(cell, out var roadCell))
                {
                    road.ValueRW.Directions = roadCell.Directions;
                    road.ValueRW.LaneCount = roadCell.LaneCount;
                }

                // (FactionId, dirMask) → MainKey → 프리팹
                // A 방식: 방향마다 MainKey가 다르므로 dirMask로 매번 MainKey를 찾는다.
                // Size>1 블록은 origin 셀 자동계산 방향이 블록 내부 방향으로 오염되므로,
                // VisualDirectionsOverride가 지정돼 있으면(매크로 단위 사전계산값) 그걸 우선한다.
                var dirs2 = road.ValueRO.VisualDirectionsOverride != RoadDir.None
                    ? road.ValueRO.VisualDirectionsOverride
                    : road.ValueRO.Directions;
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
                    // 합의된 배치 규약: 오브젝트 로컬 원점(0,0)이 footprint 원점 셀에 그대로 맞춰진다.
                    float3 worldPos = new float3(
                        cell.x * cellSize,
                        0f,
                        cell.y * cellSize);

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
                    vis.ValueRW.Instance = instance;
                }

                ecb2.RemoveComponent<DirtyRoadTag>(e);
            }

            ecb2.Playback(em);
            ecb2.Dispose();
        }

        // ── 헬퍼 ──────────────────────────────────────────────────

        public static RoadDir ComputeDirections(
            int2 cell,
            NativeHashMap<int2, RoadCell> roadLayer)
        {
            RoadDir dirs = RoadDir.None;
            for (int i = 0; i < 4; i++)
            {
                var nCell = cell + RoadDirOps.Offsets[i];
                if (roadLayer.ContainsKey(nCell))
                    dirs |= RoadDirOps.FromIndex(i);
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

                    var newDirs = ComputeDirections(nCell, layers.RoadLayer);
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
