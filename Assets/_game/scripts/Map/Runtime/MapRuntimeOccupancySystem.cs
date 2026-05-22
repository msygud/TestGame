using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(SpawnSystem))]
    [UpdateAfter(typeof(MultiSpawnSystem))]
    [UpdateAfter(typeof(RoadSpawnSystem))]
    public partial struct MapRuntimeOccupancySystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<MapGridInfo>()) return;

            var gridEntity = SystemAPI.GetSingletonEntity<MapGridInfo>();
            var em = state.EntityManager;

            if (!em.HasBuffer<MapOccupancyElement>(gridEntity))
                em.AddBuffer<MapOccupancyElement>(gridEntity);

            if (!em.HasComponent<MapOccupancyVersion>(gridEntity))
                em.AddComponentData(gridEntity, new MapOccupancyVersion { Value = 0 });

            var grid = SystemAPI.GetSingleton<MapGridInfo>();
            var occupancyBuffer = em.GetBuffer<MapOccupancyElement>(gridEntity);
            occupancyBuffer.Clear();

            var version = em.GetComponentData<MapOccupancyVersion>(gridEntity);
            version.Value++;
            if (version.Value == 0)
                version.Value = 1;
            em.SetComponentData(gridEntity, version);

            var identityLookup = SystemAPI.GetComponentLookup<MapPlacementIdentity>(true);

            foreach (var (footprint, entity) in
                SystemAPI.Query<RefRO<MapFootprint>>().WithEntityAccess())
            {
                var fp = footprint.ValueRO;
                if (fp.OccupancyType == MapOccupancyType.Empty)
                    continue;

                var size = math.max(fp.Size, new int2(1, 1));
                if (!ContainsFootprint(grid, fp.Cell, size))
                    continue;

                int localId = RoadNetworkRules.NoOwner;
                if (identityLookup.HasComponent(entity))
                    localId = identityLookup[entity].OwnerLocalId;

                for (int y = 0; y < size.y; y++)
                for (int x = 0; x < size.x; x++)
                {
                    occupancyBuffer.Add(new MapOccupancyElement
                    {
                        Cell = new int2(fp.Cell.x + x, fp.Cell.y + y),
                        Height = fp.Height,
                        Occupancy = new MapOccupancy
                        {
                            Occupier = entity,
                            Type = fp.OccupancyType,
                            LocalId = localId,
                            Version = version.Value,
                        },
                    });
                }
            }
        }

        static bool ContainsFootprint(MapGridInfo grid, int2 origin, int2 size)
        {
            size = math.max(size, new int2(1, 1));
            return origin.x >= 0
                && origin.y >= 0
                && origin.x + size.x <= grid.Size.x
                && origin.y + size.y <= grid.Size.y;
        }
    }
}
