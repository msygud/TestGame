using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Game.Utility;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  MapLoadCommand — 맵 로드 요청 (이벤트 엔티티)
    //
    //  런타임에서 맵을 로드하려면:
    //    var e = em.CreateEntity();
    //    em.AddComponentData(e, new MapLoadCommand { JsonPath = "..." });
    // ══════════════════════════════════════════════════════════════
    public struct MapLoadCommand : IComponentData
    {
        public FixedString512Bytes JsonPath;
    }

    // ══════════════════════════════════════════════════════════════
    //  MapLoaderSystem
    //
    //  MapLoadCommand를 받아:
    //    1. JSON 파일 읽기
    //    2. RequiredDlcs 검증 (PrefabLookup에 DLC 키 있는지)
    //    3. 검증 통과 시:
    //       - Singles  → SpawnRequest 엔티티
    //       - Multis   → MultiSpawnRequest 엔티티
    //       - Roads    → RoadSpawnRequest 엔티티 (PlaceRoadCommand로 변환)
    //       - StartPoints → BaseSingles/BaseRoads도 동일하게 처리
    //    4. MapLoadState 싱글톤 갱신
    //
    //  인게임 RoadSystem.cs가 PlaceRoadCommand를 처리하므로
    //  도로는 RoadSpawnRequest → PlaceRoadCommand 변환.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PrefabLookupInitSystem))]
    public partial struct MapLoaderSystem : ISystem
    {
        enum LoadCommandResult
        {
            Finished,
            WaitingForContent,
        }

        public void OnCreate(ref SystemState state)
        {
            // MapLoadState 싱글톤 초기화
            var e = state.EntityManager.CreateEntity(typeof(MapLoadState));
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status = MapLoadStatus.Idle,
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            // MapLoadCommand 처리
            var commandQuery = SystemAPI.QueryBuilder()
                .WithAll<MapLoadCommand>()
                .Build();
            var commandEntities = commandQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < commandEntities.Length; i++)
            {
                var cmdEntity = commandEntities[i];
                if (!state.EntityManager.Exists(cmdEntity))
                    continue;

                var cmd = state.EntityManager.GetComponentData<MapLoadCommand>(cmdEntity);
                var path = cmd.JsonPath.ToString();
                var result = ProcessLoadCommand(ref state, path);
                if (result == LoadCommandResult.Finished)
                    state.EntityManager.DestroyEntity(cmdEntity);
            }

            commandEntities.Dispose();
        }

        LoadCommandResult ProcessLoadCommand(ref SystemState state, string path)
        {
            // 로드 상태 → Loading
            SetStatus(ref state, MapLoadStatus.Loading);

            // 파일 읽기
            if (!File.Exists(path))
            {
                Debug.LogError($"[MapLoaderSystem] File not found: {path}");
                SetStatus(ref state, MapLoadStatus.Failed);
                return LoadCommandResult.Finished;
            }

            MapData mapData;
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                mapData = UnityEngine.JsonUtility.FromJson<MapData>(json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[MapLoaderSystem] JSON parse failed: {ex.Message}");
                SetStatus(ref state, MapLoadStatus.Failed);
                return LoadCommandResult.Finished;
            }

            if (mapData == null)
            {
                Debug.LogError("[MapLoaderSystem] MapData is null after parse.");
                SetStatus(ref state, MapLoadStatus.Failed);
                return LoadCommandResult.Finished;
            }

            // DLC 검증
            if (!SystemAPI.HasSingleton<PrefabLookup>())
            {
                SetStatusWaitingForContent(ref state);
                return LoadCommandResult.WaitingForContent;
            }

            var lookup = SystemAPI.GetSingleton<PrefabLookup>();

            if (mapData.RequiredDlcs != null)
            {
                foreach (int dlcId in mapData.RequiredDlcs)
                {
                    if (dlcId == 0) continue; // Origin은 항상 보유

                    // DlcLookup에 해당 DLC의 항목이 하나라도 있는지 확인
                    if (!IsDlcAvailable(lookup, dlcId))
                    {
                        SetStatusWaitingForDlc(ref state, dlcId);
                        return LoadCommandResult.WaitingForContent;
                    }
                }
            }

            if (!AreMapPrefabsReady(ref state, mapData, lookup))
                return LoadCommandResult.WaitingForContent;

            // 기존 맵 오브젝트 정리 (MapLoaded 태그 가진 엔티티들)
            var em = state.EntityManager;
            var oldMapQ = SystemAPI.QueryBuilder().WithAll<MapLoaded>().Build();
            em.DestroyEntity(oldMapQ);

            var ecb = new EntityCommandBuffer(Allocator.Temp);
            EmitMapGrid(ecb, mapData);

            PrefabMetaLookup metaLookup;
            metaLookup = SystemAPI.HasSingleton<PrefabMetaLookup>()
                ? SystemAPI.GetSingleton<PrefabMetaLookup>()
                : default;

            // Singles 처리
            if (mapData.Singles != null)
            {
                foreach (var p in mapData.Singles)
                    EmitSingleSpawn(ecb, p, metaLookup, RoadNetworkRules.NoOwner);
            }

            // MetaLookup 조회
            metaLookup = SystemAPI.HasSingleton<PrefabMetaLookup>()
                ? SystemAPI.GetSingleton<PrefabMetaLookup>()
                : default;

            // Multis 처리
            if (mapData.Multis != null)
            {
                foreach (var p in mapData.Multis)
                    EmitMultiSpawn(ecb, p, mapData.Settings.CellSize, lookup, metaLookup, RoadNetworkRules.NoOwner);
            }

            // Roads 처리
            if (mapData.Roads != null)
            {
                foreach (var p in mapData.Roads)
                    EmitRoadSpawn(ecb, p, mapData.Settings.CellSize, metaLookup, RoadNetworkRules.NoOwner);
            }

            // StartPoints 처리
            if (mapData.StartPoints != null)
            {
                foreach (var sp in mapData.StartPoints)
                {
                    if (sp.BaseSingles != null)
                        foreach (var p in sp.BaseSingles)
                            EmitSingleSpawn(ecb, p, metaLookup, sp.Number);

                    if (sp.BaseMultis != null)
                        foreach (var p in sp.BaseMultis)
                            EmitMultiSpawn(ecb, p, mapData.Settings.CellSize, lookup, metaLookup, sp.Number);

                    if (sp.BaseRoads != null)
                        foreach (var p in sp.BaseRoads)
                            EmitRoadSpawn(ecb, p, mapData.Settings.CellSize, metaLookup, sp.Number);
                }
            }

            ecb.Playback(em);
            ecb.Dispose();

            SetStatus(ref state, MapLoadStatus.Done);
            Debug.Log(
                $"[MapLoaderSystem] Map loaded: {mapData.MapName}. " +
                $"singles={Count(mapData.Singles)}, multis={Count(mapData.Multis)}, roads={Count(mapData.Roads)}, " +
                $"startPoints={Count(mapData.StartPoints)}, requiredDlcs=[{FormatRequiredDlcs(mapData)}].");
            return LoadCommandResult.Finished;
        }

        // ── 요청 발행 헬퍼 ────────────────────────────────────────

        static void EmitMapGrid(EntityCommandBuffer ecb, MapData mapData)
        {
            var grid = new MapGridDefinition(mapData.Settings);
            var e = ecb.CreateEntity();
            ecb.AddComponent(e, grid.ToComponent());
            ecb.AddComponent(e, new MapOccupancyVersion { Value = 0 });
            ecb.AddComponent<MapLoaded>(e);

            var buffer = ecb.AddBuffer<MapTerrainCellElement>(e);
            ecb.AddBuffer<MapOccupancyElement>(e);
            if (mapData.TerrainCells == null) return;

            foreach (var cell in mapData.TerrainCells)
            {
                if (!grid.Contains(cell.Cell)) continue;
                buffer.Add(new MapTerrainCellElement
                {
                    FlatIndex = grid.ToFlatIndex(cell.Cell),
                    Height = cell.TerrainLayer.Height,
                    Terrain = cell.TerrainLayer.Terrain,
                });
            }
        }

        static void EmitSingleSpawn(
            EntityCommandBuffer ecb,
            SinglePlacement p,
            PrefabMetaLookup metaLookup,
            int ownerLocalId)
        {
            var size = new int2(1, 1);
            var occupancyType = MapOccupancyType.Empty;
            if (metaLookup.TryGetMeta(p.MainKey, p.VariantKey, out var meta))
            {
                size = math.max(meta.Size, new int2(1, 1));
                occupancyType = ToOccupancyType(meta.PlacementRules, MapOccupancyType.Building);
            }

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new SpawnRequest
            {
                MainKey    = p.MainKey,
                VariantKey = p.VariantKey,
                Cell       = p.Cell,
                Size       = size,
                HeightIndex = p.Height,
                OwnerLocalId = ownerLocalId,
                OccupancyType = occupancyType,
                Position   = p.Position,
                Rotation   = quaternion.RotateY(math.radians(p.RotationY)),
                Scale      = p.Scale > 0f ? p.Scale : 1f,
            });
            ecb.AddComponent<MapLoaded>(e); // 나중에 정리하기 위한 태그
        }

        static void EmitMultiSpawn(
            EntityCommandBuffer ecb,
            MultiPlacement p,
            float cellSize,
            PrefabLookup lookup,
            PrefabMetaLookup metaLookup,
            int ownerLocalId)
        {
            // MetaLookup에서 Count, ItemSize 조회
            int   count    = 5;    // 기본값
            float itemSize = 0.5f;
            var   size     = new int2(1, 1);
            float yOffset  = 0f;
            var occupancyType = MapOccupancyType.Empty;
            if (metaLookup.TryGetMeta(p.MainKey, p.VariantKey, out var meta))
            {
                count    = meta.MultiCount;
                itemSize = meta.MultiItemSize;
                size     = math.max(meta.Size, new int2(1, 1));
                yOffset  = meta.YOffset;
                occupancyType = ToOccupancyType(meta.PlacementRules, MapOccupancyType.ResourceNode);
            }

            var e = ecb.CreateEntity();
            ecb.AddComponent(e, new MultiSpawnRequest
            {
                MainKey    = p.MainKey,
                VariantKey = p.VariantKey,
                Cell       = p.Cell,
                Size       = size,
                HeightIndex = p.Height,
                OwnerLocalId = ownerLocalId,
                OccupancyType = occupancyType,
                Position   = ResolveMultiPosition(p, cellSize, size, yOffset),
                CellSize   = cellSize,
                Height     = GameUtility.GetHeightWorldPosition(p.Height, cellSize, yOffset),
                Seed       = p.Seed,
                Count      = count,
                ItemSize   = itemSize,
                Scale      = p.Scale > 0f ? p.Scale : 1f,
            });
            ecb.AddComponent<MapLoaded>(e);
        }

        static float3 ResolveMultiPosition(MultiPlacement p, float cellSize, int2 size, float yOffset)
        {
            float y = GameUtility.GetHeightWorldPosition(p.Height, cellSize, yOffset);
            return GameUtility.GetFootprintCenterWorldPosition(p.Cell, size, cellSize, y);
        }


        /// <summary>
        /// 테스트
        /// </summary>
        /// <param name="ecb"></param>
        /// <param name="p"></param>
        static void EmitRoadSpawn(
            EntityCommandBuffer ecb,
            RoadPlacement p,
            float cellSize,
            PrefabMetaLookup metaLookup,
            int ownerLocalId)
        {
            float yOffset = 0f;
            if (metaLookup.TryGetMeta(p.MainKey, p.VariantKey, out var meta))
                yOffset = meta.YOffset;

            var roadRequestEntity = ecb.CreateEntity();
            ecb.AddComponent(roadRequestEntity, new RoadSpawnRequest
            {
                MainKey    = p.MainKey,
                VariantKey = p.VariantKey,
                Directions = p.Directions,
                Cell       = p.Cell,
                OwnerLocalId = ownerLocalId,
                HeightIndex = p.Height,
                Position   = ResolveRoadPosition(p, cellSize, yOffset),
                Height     = GameUtility.GetHeightWorldPosition(p.Height, cellSize, yOffset),
                Scale      = p.Scale > 0f ? p.Scale : 1f,
            });
            ecb.AddComponent<MapLoaded>(roadRequestEntity);
            return;
#if false

            // RoadSpawnRequest → PlaceRoadCommand로 변환
            // RoadSystem이 Cell 기반으로 비트마스크 재계산
            var e = ecb.CreateEntity();
            //ecb.AddComponent(e, new PlaceRoadCommand { Cell = p.Cell });
            // MapLoaded 태그는 RoadSystem이 생성하는 Road 엔티티에 추가 필요
            // 단순화: 요청 엔티티에만 태그
            //ecb.AddComponent<MapLoaded>(e);
#endif
        }

        // ── DLC 검증 헬퍼 ─────────────────────────────────────────

        static float3 ResolveRoadPosition(RoadPlacement p, float cellSize, float yOffset)
        {
            if (!p.Position.Equals(Vector3.zero))
                return p.Position;

            float y = GameUtility.GetHeightWorldPosition(p.Height, cellSize, yOffset);
            return GameUtility.GetFootprintCenterWorldPosition(p.Cell, new int2(1, 1), cellSize, y);
        }

        static MapOccupancyType ToOccupancyType(
            PlacementRuleFlags placementRules,
            MapOccupancyType blockingType)
            => (placementRules & PlacementRuleFlags.BlocksOccupancy) != 0
                ? blockingType
                : MapOccupancyType.Empty;

        static bool IsDlcAvailable(PrefabLookup lookup, int dlcId)
        {
            // DlcLookup에 해당 DLC의 항목이 하나라도 있으면 보유 판정
            // NativeHashMap 순회 (값이 dlcId인 것 찾기)
            if (!lookup.DlcLookup.IsCreated)
                return false;

            foreach (var kv in lookup.DlcLookup)
            {
                if (kv.Value == dlcId) return true;
            }
            return false;
        }

        // ── 상태 변경 헬퍼 ────────────────────────────────────────

        bool AreMapPrefabsReady(ref SystemState state, MapData mapData, PrefabLookup lookup)
        {
            if (!AreSinglePlacementsReady(ref state, mapData.Singles, lookup))
                return false;

            if (!AreMultiPlacementsReady(ref state, mapData.Multis, lookup))
                return false;

            if (!AreRoadPlacementsReady(ref state, mapData.Roads, lookup))
                return false;

            if (mapData.StartPoints != null)
            {
                foreach (var sp in mapData.StartPoints)
                {
                    if (!AreSinglePlacementsReady(ref state, sp.BaseSingles, lookup))
                        return false;

                    if (!AreMultiPlacementsReady(ref state, sp.BaseMultis, lookup))
                        return false;

                    if (!AreRoadPlacementsReady(ref state, sp.BaseRoads, lookup))
                        return false;
                }
            }

            return true;
        }

        bool AreSinglePlacementsReady(
            ref SystemState state,
            List<SinglePlacement> placements,
            PrefabLookup lookup)
        {
            if (placements == null)
                return true;

            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                if (!TryGetPrefab(lookup, p.MainKey, p.VariantKey, out _))
                {
                    SetStatusWaitingForPrefab(ref state, p.MainKey, p.VariantKey, 0);
                    return false;
                }
            }

            return true;
        }

        bool AreMultiPlacementsReady(
            ref SystemState state,
            List<MultiPlacement> placements,
            PrefabLookup lookup)
        {
            if (placements == null)
                return true;

            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                if (!TryGetPrefab(lookup, p.MainKey, p.VariantKey, out _))
                {
                    SetStatusWaitingForPrefab(ref state, p.MainKey, p.VariantKey, 0);
                    return false;
                }
            }

            return true;
        }

        bool AreRoadPlacementsReady(
            ref SystemState state,
            List<RoadPlacement> placements,
            PrefabLookup lookup)
        {
            if (placements == null)
                return true;

            for (int i = 0; i < placements.Count; i++)
            {
                var p = placements[i];
                if (!IsRoadPrefabReady(lookup, p))
                {
                    SetStatusWaitingForPrefab(ref state, p.MainKey, p.VariantKey, p.Directions);
                    return false;
                }
            }

            return true;
        }

        static bool IsRoadPrefabReady(PrefabLookup lookup, RoadPlacement placement)
        {
            if (placement.Directions != 0 && lookup.RoadMap.IsCreated)
            {
                if (lookup.TryGetRoad(placement.Directions, placement.VariantKey, out _))
                    return true;

                if (placement.VariantKey != 0
                    && lookup.TryGetRoad(placement.Directions, 0, out _))
                    return true;
            }

            return TryGetPrefab(lookup, placement.MainKey, placement.VariantKey, out _);
        }

        static bool TryGetPrefab(
            PrefabLookup lookup,
            int mainKey,
            int variantKey,
            out Entity prefab)
        {
            prefab = Entity.Null;
            return lookup.Map.IsCreated && lookup.TryGet(mainKey, variantKey, out prefab);
        }

        static int Count<T>(List<T> list)
        {
            return list?.Count ?? 0;
        }

        static string FormatRequiredDlcs(MapData mapData)
        {
            if (mapData.RequiredDlcs == null || mapData.RequiredDlcs.Count == 0)
                return "";

            var sb = new StringBuilder();
            for (int i = 0; i < mapData.RequiredDlcs.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(mapData.RequiredDlcs[i]);
            }

            return sb.ToString();
        }

        void SetStatus(ref SystemState state, MapLoadStatus status)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status                = status,
                MissingDlcId          = 0,
                MissingMainKey        = 0,
                MissingVariantKey     = 0,
                MissingRoadDirections = 0,
            });
        }

        void SetStatusWaitingForContent(ref SystemState state)
        {
            SetStatusWaitingForPrefab(ref state, 0, 0, 0);
        }

        void SetStatusWaitingForDlc(ref SystemState state, int dlcId)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status                = MapLoadStatus.WaitingForContent,
                MissingDlcId          = dlcId,
                MissingMainKey        = 0,
                MissingVariantKey     = 0,
                MissingRoadDirections = 0,
            });
        }

        void SetStatusWaitingForPrefab(
            ref SystemState state,
            int mainKey,
            int variantKey,
            int roadDirections)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status                = MapLoadStatus.WaitingForContent,
                MissingDlcId          = 0,
                MissingMainKey        = mainKey,
                MissingVariantKey     = variantKey,
                MissingRoadDirections = roadDirections,
            });
        }

        void SetStatusMissingDlc(ref SystemState state, int dlcId)
        {
            if (!SystemAPI.HasSingleton<MapLoadState>()) return;
            var e = SystemAPI.GetSingletonEntity<MapLoadState>();
            state.EntityManager.SetComponentData(e, new MapLoadState
            {
                Status                = MapLoadStatus.DlcMissing,
                MissingDlcId          = dlcId,
                MissingMainKey        = 0,
                MissingVariantKey     = 0,
                MissingRoadDirections = 0,
            });
        }
    }
}
