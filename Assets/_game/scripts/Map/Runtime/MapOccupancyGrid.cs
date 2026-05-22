using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    public sealed class MapOccupancyGrid : IDisposable
    {
        NativeParallelHashMap<MapOccupancyKey, MapOccupancy> cells;

        public MapGridDefinition Definition { get; }
        public bool IsCreated => cells.IsCreated;

        public MapOccupancyGrid(MapGridDefinition definition, Allocator allocator)
        {
            Definition = definition;
            cells = new NativeParallelHashMap<MapOccupancyKey, MapOccupancy>(
                math.max(16, definition.Size.x * definition.Size.y),
                allocator);
        }

        public bool IsFree(int2 cell, int height)
            => Definition.Contains(cell)
            && !cells.ContainsKey(new MapOccupancyKey(cell, height));

        public bool IsFootprintFree(int2 origin, int2 size, int height)
        {
            if (!Definition.ContainsFootprint(origin, size)) return false;

            size = math.max(size, new int2(1, 1));
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                if (!IsFree(new int2(origin.x + x, origin.y + y), height))
                    return false;
            }

            return true;
        }

        public bool TryGet(int2 cell, int height, out MapOccupancy occupancy)
            => cells.TryGetValue(new MapOccupancyKey(cell, height), out occupancy);

        public void Set(int2 cell, int height, MapOccupancy occupancy)
        {
            cells[new MapOccupancyKey(cell, height)] = occupancy;
        }

        public void SetFootprint(int2 origin, int2 size, int height, MapOccupancy occupancy)
        {
            size = math.max(size, new int2(1, 1));
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
                Set(new int2(origin.x + x, origin.y + y), height, occupancy);
        }

        public bool Remove(int2 cell, int height)
            => cells.Remove(new MapOccupancyKey(cell, height));

        public void RemoveFootprint(int2 origin, int2 size, int height)
        {
            size = math.max(size, new int2(1, 1));
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
                Remove(new int2(origin.x + x, origin.y + y), height);
        }

        public void Clear()
        {
            cells.Clear();
        }

        public void Dispose()
        {
            if (cells.IsCreated)
                cells.Dispose();
        }
    }
}
