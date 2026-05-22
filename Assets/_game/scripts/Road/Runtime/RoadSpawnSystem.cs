using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace CitySim
{
    public struct RoadOwner : IComponentData
    {
        public int LocalId;
    }

    public struct RoadCell : IComponentData
    {
        public int2 Cell;
        public int Height;
    }

    public struct RoadNetworkMember : IComponentData
    {
        public int OwnerLocalId;
        public int NetworkId;
    }

    public static class RoadNetworkRules
    {
        public const int NoOwner = -1;

        public static bool CanConnect(int builderLocalId, int existingRoadOwnerLocalId)
            => builderLocalId != NoOwner && builderLocalId == existingRoadOwnerLocalId;

        public static bool CanUseLogistics(int requesterLocalId, int roadOwnerLocalId)
            => requesterLocalId != NoOwner && requesterLocalId == roadOwnerLocalId;

        public static bool BlocksConstruction(int builderLocalId, int existingRoadOwnerLocalId)
            => existingRoadOwnerLocalId != NoOwner && builderLocalId != existingRoadOwnerLocalId;
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(MapLoaderSystem))]
    public partial struct RoadSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<PrefabLookup>()) return;

            var lookup = SystemAPI.GetSingleton<PrefabLookup>();
            var transformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true);
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (req, reqEntity) in
                SystemAPI.Query<RefRO<RoadSpawnRequest>>().WithEntityAccess())
            {
                int mainKey = req.ValueRO.MainKey;
                int variantKey = req.ValueRO.VariantKey;

                if (!TryResolveRoadPrefab(lookup, req.ValueRO.Directions, variantKey, mainKey, out Entity prefab))
                {
                    Debug.LogWarning(
                        $"[RoadSpawnSystem] Prefab not found: ({mainKey}, {variantKey}), directions={req.ValueRO.Directions}.");
                    ecb.DestroyEntity(reqEntity);
                    continue;
                }

                quaternion rotation = quaternion.identity;
                if (transformLookup.HasComponent(prefab))
                    rotation = transformLookup[prefab].Rotation;

                var instance = ecb.Instantiate(prefab);
                ecb.SetComponent(instance, LocalTransform.FromPositionRotationScale(
                    req.ValueRO.Position,
                    rotation,
                    req.ValueRO.Scale));
                ecb.AddComponent(instance, new RoadOwner
                {
                    LocalId = req.ValueRO.OwnerLocalId,
                });
                ecb.AddComponent(instance, new RoadCell
                {
                    Cell = req.ValueRO.Cell,
                    Height = req.ValueRO.HeightIndex,
                });
                ecb.AddComponent(instance, new RoadNetworkMember
                {
                    OwnerLocalId = req.ValueRO.OwnerLocalId,
                    NetworkId = 0,
                });
                ecb.AddComponent(instance, new MapPlacementIdentity
                {
                    MainKey = req.ValueRO.MainKey,
                    VariantKey = req.ValueRO.VariantKey,
                    Kind = MapPlacementKind.Road,
                    OwnerLocalId = req.ValueRO.OwnerLocalId,
                });
                ecb.AddComponent(instance, new MapFootprint
                {
                    Cell = req.ValueRO.Cell,
                    Size = new int2(1, 1),
                    Height = req.ValueRO.HeightIndex,
                    OccupancyType = MapOccupancyType.Road,
                });
                ecb.AddComponent<MapLoaded>(instance);

                ecb.DestroyEntity(reqEntity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        static bool TryResolveRoadPrefab(
            PrefabLookup lookup,
            byte directions,
            int variantKey,
            int mainKey,
            out Entity prefab)
        {
            if (directions != 0)
            {
                if (lookup.TryGetRoad(directions, variantKey, out prefab))
                    return true;

                if (variantKey != 0 && lookup.TryGetRoad(directions, 0, out prefab))
                    return true;
            }

            return lookup.TryGet(mainKey, variantKey, out prefab);
        }
    }
}
