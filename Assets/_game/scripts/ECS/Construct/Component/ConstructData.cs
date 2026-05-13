using System;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Construct
{
    public class ConstructData
    {

    }
    
    public struct ConstructGridSettingData:IComponentData
    {
        public int WidthCount;
        public int HeightCount;
        public float CellSize;
    }

    //터레인
    public enum CellTerrainType
    {
        Land,Water, Mountain
    }
    public enum HeightType : byte { 
        Level0 = 0,Level1 = 1,Level2 = 2,Level3 = 3,Level4 = 4,Level5 = 5,Level6 = 6,Level7 = 7,
    }

    [Flags]
    public enum CellTerrainFlags : ushort
    {
        None = 0,
        Buildable = 1<<0,
        Walkable = 1<<1,
        RoadBuildable = 1<<2,
        NavalBuildable = 1<<3,
        FarmBuildable = 1<<4,
        BlockVision = 1<<5,
        HasResource = 1<<6,
    }
    public struct CellTerrain
    {
        public int2 Index;
        public CellTerrainType Type;
        public CellTerrainFlags Flags;
        public HeightType Height;
    }
    //터레인
    
    //점유
    public enum OccupancyType:byte
    {
        Empty ,Reserced,Road,Building,ConstructSite,Blocked,ResourceNode
    }
    public struct CellOccupancy
    {
        public Entity Occupier;
        public OccupancyType Type;
        public int LocalID;
        public ushort Version;
    }

}
