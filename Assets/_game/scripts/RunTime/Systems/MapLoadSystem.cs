using CitySim.MapEditor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  MapLoadSystem
    //
    //  MapLoadRequest + ManagedMapLoadRequest를 감지하여
    //  실제 맵을 ECS 월드에 구성한다.
    //
    //  처리 순서:
    //    1. DLC 접근 재검증 (2차 방어)
    //    2. TerrainCells → 큐브 프리팹 Instantiate + TerrainLayer + GridMap
    //    3. ResourceCells → 자원 프리팹 Instantiate + ResourceLayer
    //    4. Singles → PrefabLookup + Instantiate + OccupancyLayer + GridMap
    //    5. Multis  → 시드 기반 랜덤 배치
    //    6. Roads   → PlaceRoadCommand 발행 (RoadSystem이 처리)
    //    7. StartPoints 등록 (TeamStartPoint 컴포넌트 생성)
    //    8. MapLoadRequest 삭제 + MapLoaded 태그 생성
    //
    //  의존:
    //    PrefabLookup     (PrefabLookupBuildSystem)
    //    PrefabMetaLookup (PrefabLookupBuildSystem)
    //    CellTypeLookup   (CellTypeLookupBuildSystem)
    //    GridLayers       (GridInitSystem)
    //    GridMap          (GridInitSystem)
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(RoadSystem))]
    public partial struct MapLoadSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PrefabLookup>();
            state.RequireForUpdate<PrefabMetaLookup>();
            state.RequireForUpdate<CellTypeLookup>();
            state.RequireForUpdate<GridMap>();
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridSettings>();
            state.RequireForUpdate<MapLoadRequest>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var prefabLookup     = SystemAPI.GetSingleton<PrefabLookup>();
            var prefabMetaLookup = SystemAPI.GetSingleton<PrefabMetaLookup>();
            var cellTypeLookup   = SystemAPI.GetSingleton<CellTypeLookup>();
            var gridMap          = SystemAPI.GetSingleton<GridMap>();
            // TerrainLayer 갱신이 필요하므로 RW
            ref var layers       = ref SystemAPI.GetSingletonRW<GridLayers>().ValueRW;
            var em               = state.EntityManager;
            var ecb              = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (request, managed, requestEntity) in
                SystemAPI.Query<RefRO<MapLoadRequest>, ManagedMapLoadRequest>()
                    .WithEntityAccess())
            {
                var mapData = managed.Data;
                var mapId   = request.ValueRO.MapId.ToString();

                // 1. DLC 접근 2차 검증
                var meta = mapData.BuildMeta(null);
                if (!DlcOwnershipService.CanAccess(meta.RequiredDlcIds))
                {
                    Debug.LogError(
                        $"[MapLoadSystem] 맵 '{mapId}' 접근 불가 (DLC 미보유). " +
                        $"필요 DLC: {string.Join(", ", DlcOwnershipService.MissingDlcs(meta.RequiredDlcIds))}");
                    ecb.DestroyEntity(requestEntity);
                    break;
                }

                var s = mapData.Settings;
                Debug.Log($"[MapLoadSystem] 맵 '{mapId}' 로드 시작 " +
                          $"({s.Width}×{s.Height}, CellSize={s.CellSize})");

                // GridSettings 업데이트 — 이후 모든 시스템이 CellSize 참조 가능
                var settingsEntity = SystemAPI.GetSingletonEntity<GridSettings>();
                em.SetComponentData(settingsEntity, new GridSettings
                {
                    CellSize = s.CellSize,
                    Width    = s.Width,
                    Height   = s.Height,
                });

                // 2. TerrainCells
                SpawnTerrain(mapData, s, prefabLookup, cellTypeLookup,
                    prefabMetaLookup, gridMap, ref layers, ecb);

                // 3. ResourceCells
                SpawnResources(mapData, s, prefabLookup, cellTypeLookup,
                    prefabMetaLookup, ecb);

                // 4. Singles
                SpawnSingles(mapData, s, prefabLookup, prefabMetaLookup, gridMap, ecb);

                // 5. Multis
                SpawnMultis(mapData, s, prefabLookup, prefabMetaLookup, ecb);

                // 6. Roads → PlaceRoadCommand
                IssueRoadCommands(mapData, ecb);

                // 7. StartPoints
                RegisterStartPoints(mapData, ecb);

                // 8. 완료
                ecb.DestroyEntity(requestEntity);

                var loadedEntity = ecb.CreateEntity();
                ecb.AddComponent(loadedEntity, new MapLoaded { MapId = request.ValueRO.MapId });

                Debug.Log($"[MapLoadSystem] 맵 '{mapId}' 로드 완료.");
            }

            ecb.Playback(em);
            ecb.Dispose();
        }

        // ══════════════════════════════════════════════════════════
        //  지형 스폰
        // ══════════════════════════════════════════════════════════
        static void SpawnTerrain(
            MapData mapData,
            MapSettings s,
            PrefabLookup prefabLookup,
            CellTypeLookup cellTypeLookup,
            PrefabMetaLookup metaLookup,
            GridMap gridMap,
            ref GridLayers layers,
            EntityCommandBuffer ecb)
        {
            int spawned = 0;
            foreach (var cell in mapData.TerrainCells)
            {
                if (!cellTypeLookup.TryGet(cell.TypeId, out var typeInfo))
                {
                    Debug.LogWarning(
                        $"[MapLoadSystem] 알 수 없는 TypeId={cell.TypeId} at ({cell.X},{cell.Y})");
                    continue;
                }

                // BuildingPlacementSystem.ValidateCells가 OutOfBounds/WrongTerrain/
                // HeightMismatch 판정에 쓰는 레이어 — 여기서 채우지 않으면 모든 셀이
                // 영원히 OutOfBounds로 판정돼 건물 배치가 100% 실패한다.
                var cellPos = new int2(cell.X, cell.Y);
                layers.TerrainLayer[cellPos] = new TerrainCell
                {
                    TypeId = cell.TypeId,
                    Height = cell.Height,
                };

                var prefabEntity = prefabLookup.Get(typeInfo.MainKey, typeInfo.VariantKey);
                if (prefabEntity == Entity.Null) continue;

                var meta     = metaLookup.Get(typeInfo.MainKey, typeInfo.VariantKey);
                var worldPos = CellToWorld(cell.X, cell.Y, cell.Height, s, meta.Offset);

                var instance = ecb.Instantiate(prefabEntity);
                ecb.SetComponent(instance, LocalTransform.FromPosition(worldPos));

                if (!typeInfo.Passable)
                    gridMap.BuildingCells.TryAdd(cellPos, instance);

                spawned++;
            }
            Debug.Log($"[MapLoadSystem] 지형 {spawned}개 스폰");
        }

        // ══════════════════════════════════════════════════════════
        //  자원 스폰
        // ══════════════════════════════════════════════════════════
        static void SpawnResources(
            MapData mapData,
            MapSettings s,
            PrefabLookup prefabLookup,
            CellTypeLookup cellTypeLookup,
            PrefabMetaLookup metaLookup,
            EntityCommandBuffer ecb)
        {
            int spawned = 0;
            foreach (var cell in mapData.ResourceCells)
            {
                if (!cellTypeLookup.TryGet(cell.TypeId, out var typeInfo)) continue;

                var prefabEntity = prefabLookup.Get(typeInfo.MainKey, typeInfo.VariantKey);
                if (prefabEntity == Entity.Null) continue;

                var meta     = metaLookup.Get(typeInfo.MainKey, typeInfo.VariantKey);
                var worldPos = CellToWorld(cell.X, cell.Y, 0, s, meta.Offset);

                var instance = ecb.Instantiate(prefabEntity);
                ecb.SetComponent(instance, LocalTransform.FromPosition(worldPos));
                spawned++;
            }
            Debug.Log($"[MapLoadSystem] 자원 {spawned}개 스폰");
        }

        // ══════════════════════════════════════════════════════════
        //  Single 배치 스폰
        // ══════════════════════════════════════════════════════════
        static void SpawnSingles(
            MapData mapData,
            MapSettings s,
            PrefabLookup prefabLookup,
            PrefabMetaLookup metaLookup,
            GridMap gridMap,
            EntityCommandBuffer ecb)
        {
            int spawned = 0;
            foreach (var p in mapData.Singles)
            {
                var prefabEntity = prefabLookup.Get(p.MainKey, p.VariantKey);
                if (prefabEntity == Entity.Null) continue;

                var meta = metaLookup.Get(p.MainKey, p.VariantKey);
                float cs = s.CellSize;

                // 합의된 배치 규약: 오브젝트 로컬 원점(0,0)이 셀 인덱스에 그대로
                // 맞춰진다 (중심 보정 없음). PositionY(높이) + OffsetX/Z(랜덤 오프셋) 반영.
                var worldPos = new float3(
                    p.CellX * cs + meta.Offset.x + p.OffsetX,
                    p.PositionY + meta.Offset.y,
                    p.CellZ * cs + meta.Offset.z + p.OffsetZ);
                var rot      = quaternion.RotateY(math.radians(p.RotationY));
                var scale    = p.Scale > 0f ? p.Scale : 1f;

                var instance = ecb.Instantiate(prefabEntity);
                ecb.SetComponent(instance,
                    LocalTransform.FromPositionRotationScale(worldPos, rot, scale));

                // 점유 등록 (Size 만큼 모든 셀)
                for (int dx = 0; dx < meta.Size.x; dx++)
                for (int dz = 0; dz < meta.Size.y; dz++)
                    gridMap.BuildingCells.TryAdd(
                        new int2(p.CellX + dx, p.CellZ + dz), instance);

                spawned++;
            }
            Debug.Log($"[MapLoadSystem] Single {spawned}개 스폰");
        }

        // ══════════════════════════════════════════════════════════
        //  Multi 배치 스폰 (셀당 N개 랜덤, 시드 재현)
        // ══════════════════════════════════════════════════════════
        static void SpawnMultis(
            MapData mapData,
            MapSettings s,
            PrefabLookup prefabLookup,
            PrefabMetaLookup metaLookup,
            EntityCommandBuffer ecb)
        {
            int totalSpawned = 0;
            foreach (var p in mapData.Multis)
            {
                var prefabEntity = prefabLookup.Get(p.MainKey, p.VariantKey);
                if (prefabEntity == Entity.Null) continue;

                // PrefabMetaLookup에서 MultiCount / MultiItemSize 조회
                var meta  = metaLookup.Get(p.MainKey, p.VariantKey);
                int count = meta.MultiCount > 0 ? meta.MultiCount : 5;

                uint seed = p.RandomSeed != 0
                    ? (uint)p.RandomSeed
                    : (uint)(p.CellX * 31 + p.CellZ + 1);
                var rng = new Random(seed);

                float cs    = s.CellSize;
                float baseX = p.CellX * cs;
                float baseZ = p.CellZ * cs;

                for (int i = 0; i < count; i++)
                {
                    float rx = rng.NextFloat(0f, cs);
                    float rz = rng.NextFloat(0f, cs);
                    float ry = rng.NextFloat(0f, 360f);

                    var pos = new float3(baseX + rx, 0f, baseZ + rz);
                    var rot = quaternion.RotateY(math.radians(ry));

                    var instance = ecb.Instantiate(prefabEntity);
                    ecb.SetComponent(instance,
                        LocalTransform.FromPositionRotation(pos, rot));
                }

                totalSpawned += count;
            }
            Debug.Log($"[MapLoadSystem] Multi {mapData.Multis.Count}셀 → {totalSpawned}개 스폰");
        }

        // ══════════════════════════════════════════════════════════
        //  도로 → PlaceRoadCommand 발행
        // ══════════════════════════════════════════════════════════
        static void IssueRoadCommands(MapData mapData, EntityCommandBuffer ecb)
        {
            foreach (var road in mapData.Roads)
            {
                var cmdEntity = ecb.CreateEntity();
                ecb.AddComponent(cmdEntity, new PlaceRoadCommand
                {
                    Cell      = new int2(road.CellX, road.CellZ),
                    OwnerLocalId = 0,
                    LaneCount = 2,
                    FactionId = 0,   // 맵에 미리 깔린 도로 = 중립/공통(FactionId 0)
                });
                // RoadSystem이 (FactionId, dirMask)→MainKey→프리팹 + 인접 갱신 처리.
                // 공통 도로 프리팹은 RoadPrefabRegistry에 Faction 0으로 등록해 둔다.
            }
            Debug.Log($"[MapLoadSystem] 도로 {mapData.Roads.Count}개 PlaceRoadCommand 발행");
        }

        // ══════════════════════════════════════════════════════════
        //  팀 시작 위치 등록
        // ══════════════════════════════════════════════════════════
        static void RegisterStartPoints(MapData mapData, EntityCommandBuffer ecb)
        {
            foreach (var sp in mapData.StartPoints)
            {
                var e = ecb.CreateEntity();
                ecb.AddComponent(e, new TeamStartPoint
                {
                    TeamIndex = sp.TeamIndex,
                    Cell      = new int2(sp.Cell.x, sp.Cell.y),
                });
            }
            Debug.Log($"[MapLoadSystem] 시작 위치 {mapData.StartPoints.Count}개 등록");
        }

        // ══════════════════════════════════════════════════════════
        //  헬퍼
        // ══════════════════════════════════════════════════════════
        static float3 CellToWorld(int cx, int cz, int heightStep,
            MapSettings s, float3 offset)
        {
            float cs = s.CellSize;
            return new float3(
                cx * cs,
                heightStep * cs,
                cz * cs
            ) + offset;
        }
    }
}
