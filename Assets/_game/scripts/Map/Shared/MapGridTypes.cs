using System;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    public enum MapTerrainType : byte
    {
        Land = 0,
        Water = 1,
        Mountain = 2,
    }

    [Flags]
    public enum MapTerrainMask : byte
    {
        None = 0,
        Land = 1 << 0,
        Water = 1 << 1,
        Mountain = 1 << 2,
        All = Land | Water | Mountain,
    }

    public static class MapTerrainMaskUtility
    {
        public static MapTerrainMask ToMask(MapTerrainType terrain)
        {
            return terrain switch
            {
                MapTerrainType.Water => MapTerrainMask.Water,
                MapTerrainType.Mountain => MapTerrainMask.Mountain,
                _ => MapTerrainMask.Land,
            };
        }
    }

    [Flags]
    public enum PlacementRuleFlags : byte
    {
        None = 0,
        RequiresFlatFootprint = 1 << 0,
        RequiresRoadAdjacency = 1 << 1,
        BlocksOccupancy = 1 << 2,
    }

    [Flags]
    public enum MapBuildFlags : ushort
    {
        None = 0,
        Buildable = 1 << 0,
        Walkable = 1 << 1,
        RoadBuildable = 1 << 2,
        NavalBuildable = 1 << 3,
        FarmBuildable = 1 << 4,
        BlocksVision = 1 << 5,
        HasResource = 1 << 6,
    }

    [Obsolete("Legacy only. Placement now uses Terrain/Height and RegistryItem.AllowedTerrains.")]
    public enum MapTerrainFlags : ushort
    {
        None = 0,
        Buildable = 1 << 0,
        Walkable = 1 << 1,
        RoadBuildable = 1 << 2,
        NavalBuildable = 1 << 3,
        FarmBuildable = 1 << 4,
        BlocksVision = 1 << 5,
        HasResource = 1 << 6,
    }

    public enum MapOccupancyType : byte
    {
        Empty = 0,
        Reserved = 1,
        Road = 2,
        Building = 3,
        ConstructionSite = 4,
        Blocked = 5,
        ResourceNode = 6,
    }

    public enum MapPlacementKind : byte
    {
        Single = 0,
        Multi = 1,
        Road = 2,
    }

    public struct MapPlacementIdentity : IComponentData
    {
        public int MainKey;
        public int VariantKey;
        public MapPlacementKind Kind;
        public int OwnerLocalId;
    }

    public struct MapFootprint : IComponentData
    {
        public int2 Cell;
        public int2 Size;
        public int Height;
        public MapOccupancyType OccupancyType;
    }

    public readonly struct MapTerrainLayer
    {
        public readonly byte Height;
        public readonly MapTerrainType Terrain;

        public MapTerrainLayer(byte height, MapTerrainType terrain)
        {
            Height = height;
            Terrain = terrain;
        }
    }

    public struct MapGridInfo : IComponentData
    {
        public int2 Size;
        public float CellSize;
        public float2 Origin;
        public float HeightStep;
    }

    public struct MapTerrainCellElement : IBufferElementData
    {
        public int FlatIndex;
        public byte Height;
        public MapTerrainType Terrain;
    }

    public struct MapOccupancyKey : IEquatable<MapOccupancyKey>
    {
        public int2 Cell;
        public int Height;

        public MapOccupancyKey(int2 cell, int height)
        {
            Cell = cell;
            Height = height;
        }

        public bool Equals(MapOccupancyKey other)
            => Cell.Equals(other.Cell) && Height == other.Height;

        public override bool Equals(object obj)
            => obj is MapOccupancyKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + Cell.x;
                hash = hash * 31 + Cell.y;
                hash = hash * 31 + Height;
                return hash;
            }
        }
    }

    public struct MapOccupancy
    {
        public Entity Occupier;
        public MapOccupancyType Type;
        public int LocalId;
        public ushort Version;
    }

    public struct MapOccupancyVersion : IComponentData
    {
        public ushort Value;
    }

    public struct MapOccupancyElement : IBufferElementData
    {
        public int2 Cell;
        public int Height;
        public MapOccupancy Occupancy;
    }
}
