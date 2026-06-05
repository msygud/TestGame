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
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers = SystemAPI.GetSingleton<GridLayers>();
            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            var roadKeys = SystemAPI.GetSingleton<RoadKeyLookup>();
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
                var cell = cmd.ValueRO.Cell;
                var ownerLocalId = cmd.ValueRO.OwnerLocalId;
                var laneCount = cmd.ValueRO.LaneCount == 0 ? (byte)2 : cmd.ValueRO.LaneCount;
                var factionId = cmd.ValueRO.FactionId;

                if (layers.OccupancyLayer.TryGetValue(cell, out var occ) && !occ.IsEmpty)
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

                var dirs = ComputeDirections(cell, layers.RoadLayer);
                var flowAxis = ComputeFlowAxis(dirs);

                var road = ecb.CreateEntity();
                ecb.AddComponent(road, new GridPosition { Value = cell });
                ecb.AddComponent(road, new Road
                {
                    Directions = dirs,
                    FactionId = factionId,
                    LaneCount = laneCount,
                });
                ecb.AddComponent(road, new RoadVisualInstance { Instance = Entity.Null });
                ecb.AddComponent(road, new DirtyRoadTag());

                layers.RoadLayer.Add(cell, new RoadCell
                {
                    Directions = dirs,
                    FlowAxis = flowAxis,
                    LaneCount = laneCount,
                    OwnerLocalId = ownerLocalId,
                    RoadEntity = Entity.Null,
                });

                layers.OccupancyLayer[cell] = new OccupancyCell
                {
                    Type = OccupantType.Road,
                    Occupant = Entity.Null,
                    OwnerLocalId = ownerLocalId,
                };

                UpdateNeighborDirections(cell, layers, em, ecb);
                ecb.DestroyEntity(cmdEntity);
            }

            // 3) 도로 철거
            foreach (var (cmd, cmdEntity) in
                     SystemAPI.Query<RefRO<RemoveRoadCommand>>().WithEntityAccess())
            {
                var cell = cmd.ValueRO.Cell;
                var ownerLocalId = cmd.ValueRO.OwnerLocalId;

                if (!layers.RoadLayer.TryGetValue(cell, out var roadCell) ||
                    roadCell.OwnerLocalId != ownerLocalId)
                {
                    ecb.DestroyEntity(cmdEntity);
                    continue;
                }

                if (roadCell.RoadEntity != Entity.Null)
                {
                    var vis = em.GetComponentData<RoadVisualInstance>(roadCell.RoadEntity);
                    if (vis.Instance != Entity.Null)
                        ecb.DestroyEntity(vis.Instance);
                    ecb.DestroyEntity(roadCell.RoadEntity);
                }

                layers.RoadLayer.Remove(cell);
                layers.OccupancyLayer.Remove(cell);
                UpdateNeighborDirections(cell, layers, em, ecb);
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
                var dirs2 = road.ValueRO.Directions;
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
                    float3 worldPos = new float3(cell.x, 0f, cell.y);
                    ecb2.SetComponent(instance, LocalTransform.FromPosition(worldPos));
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

        static void UpdateNeighborDirections(
            int2 cell,
            GridLayers layers,
            EntityManager em,
            EntityCommandBuffer ecb)
        {
            for (int i = 0; i < 4; i++)
            {
                var nCell = cell + RoadDirOps.Offsets[i];
                if (!layers.RoadLayer.TryGetValue(nCell, out var nRoadCell)) continue;

                var newDirs = ComputeDirections(nCell, layers.RoadLayer);
                nRoadCell.Directions = newDirs;
                nRoadCell.FlowAxis = ComputeFlowAxis(newDirs);
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

        void FixupRoadLayer(ref SystemState state, ref GridLayers layers)
        {
            foreach (var (gp, e) in
                     SystemAPI.Query<RefRO<GridPosition>>()
                     .WithAll<Road>().WithEntityAccess())
            {
                var cell = gp.ValueRO.Value;
                if (!layers.RoadLayer.TryGetValue(cell, out var roadCell)) continue;

                if (roadCell.RoadEntity == Entity.Null)
                {
                    roadCell.RoadEntity = e;
                    layers.RoadLayer[cell] = roadCell;
                }

                if (layers.OccupancyLayer.TryGetValue(cell, out var occ) &&
                    occ.Occupant == Entity.Null)
                {
                    occ.Occupant = e;
                    layers.OccupancyLayer[cell] = occ;
                }
            }
        }
    }
}
