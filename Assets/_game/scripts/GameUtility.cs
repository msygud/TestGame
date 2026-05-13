using Unity.Mathematics;
using UnityEngine;

namespace Game.Utility
{
    public static class GameUtility
    {
        public static int2 GetGridIndexFromWorldPosition((float3 worldPosition, float2 origin, float cellSize) gridInfo)
        {
            var (worldPosition, origin, cellSize) = gridInfo;
            int x = Mathf.FloorToInt((worldPosition.x - origin.x) / cellSize);
            int y = Mathf.FloorToInt((worldPosition.z - origin.y) / cellSize);
            return new int2(x, y);
        }
        public static bool IsWithinGridBounds((int2 gridIndex, int2 gridSize) gridInfo)
        {
            var (gridIndex, gridSize) = gridInfo;
            return gridIndex.x >= 0 && gridIndex.x < gridSize.x && gridIndex.y >= 0 && gridIndex.y < gridSize.y;
        }
        public static int ToFlatIndex(int2 index,int widthcount)
        {
            return index.x + index.y * widthcount;
        }
        public static int ToFlatIndex(int x,int y,int widthcount)
        {
            return x + y * widthcount;
        }
        public static float3 GetWorldPositionFromGridIndex((int2 gridIndex, float2 origin, float cellSize) gridInfo)
        {
            var (gridIndex, origin, cellSize) = gridInfo;
            float x = origin.x + gridIndex.x * cellSize + cellSize / 2f;
            float z = origin.y + gridIndex.y * cellSize + cellSize / 2f;
            return new float3(x, origin.y, z);
        }
        public static int GetGridIndexHash(int2 gridIndex, int gridcountwidth)
        {
            return gridIndex.x + gridIndex.y * gridcountwidth;
        }
        public static int2 GetGridIndexFromHash(int hash, int2 gridSize)
        {
            int x = hash % gridSize.x;
            int y = hash / gridSize.x;
            return new int2(x, y);
        }
        public static int2 GetGridIndexFromWorldPosition(float3 worldPosition, float2 origin, float cellSize)
        {
            int x = Mathf.FloorToInt((worldPosition.x - origin.x) / cellSize);
            int y = Mathf.FloorToInt((worldPosition.z - origin.y) / cellSize);
            return new int2(x, y);
        }
        public static int2 GetGridCount(float2 worldsize, float2 origin, float cellSize)
        {
            int x = (int)math.ceil((worldsize.x - origin.x) / cellSize);
            int y = (int)math.ceil((worldsize.y - origin.y) / cellSize);
            return new int2(x, y);
        }
        public static int2 OutBoundInexToNormalize(int2 gridcount,int2 index)
        {
            int normalizedx = math.clamp(index.x, 0, gridcount.x-1);
            int normalizedy = math.clamp(index.y, 0, gridcount.y-1);
            return new int2(normalizedx, normalizedy);
        }
    }
}
