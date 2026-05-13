using System.Runtime.InteropServices;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Minimap
{
    public class MinimapData
    {

    }
    public struct MinimapTracked : IComponentData
    {
        public float2 WorldPosition;
        public int TeamIndex;       // 0=player, 1=enemy, 2=neutral
        public int UnitTypeIndex;   // icon 종류
        public bool IsVisible;      // fog of war
    }

    public struct MinimapChunkKey : IComponentData
    {
        public int2 ChunkCoord; // spatial partition용
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MinimapUnitData
    {
        public float2 UV;        // 월드 좌표 → 미니맵 UV (0~1)
        public int TeamIndex;    // 0=player, 1=enemy, 2=neutral
        public int UnitTypeIndex;// icon 종류
    }
}
