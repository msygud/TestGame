using Unity.Mathematics;

namespace CitySim
{
    public readonly struct MapGridDefinition
    {
        public readonly int2 Size;
        public readonly float CellSize;
        public readonly float2 Origin;
        public readonly float HeightStep;

        public MapGridDefinition(int2 size, float cellSize, float2 origin, float heightStep)
        {
            Size = math.max(size, new int2(1, 1));
            CellSize = math.max(0.01f, cellSize);
            Origin = origin;
            HeightStep = math.max(0.01f, heightStep);
        }

        public MapGridDefinition(MapSettings settings)
            : this(new int2(settings.Width, settings.Height),
                settings.CellSize,
                float2.zero,
                settings.CellSize * 0.5f)
        {
        }

        public bool Contains(int2 cell)
            => cell.x >= 0 && cell.y >= 0 && cell.x < Size.x && cell.y < Size.y;

        public bool ContainsFootprint(int2 origin, int2 size)
        {
            size = math.max(size, new int2(1, 1));
            return origin.x >= 0
                && origin.y >= 0
                && origin.x + size.x <= Size.x
                && origin.y + size.y <= Size.y;
        }

        public int ToFlatIndex(int2 cell)
            => cell.x + cell.y * Size.x;

        public int2 FromFlatIndex(int flatIndex)
            => new int2(flatIndex % Size.x, flatIndex / Size.x);

        public int2 WorldToCell(float3 worldPosition)
        {
            int x = (int)math.floor((worldPosition.x - Origin.x) / CellSize);
            int y = (int)math.floor((worldPosition.z - Origin.y) / CellSize);
            return new int2(x, y);
        }

        public float3 CellCenterWorld(int2 cell, float y = 0f)
            => new float3(
                Origin.x + (cell.x + 0.5f) * CellSize,
                y,
                Origin.y + (cell.y + 0.5f) * CellSize);

        public float3 FootprintCenterWorld(int2 cell, int2 size, float y = 0f)
        {
            size = math.max(size, new int2(1, 1));
            return new float3(
                Origin.x + (cell.x + size.x * 0.5f) * CellSize,
                y,
                Origin.y + (cell.y + size.y * 0.5f) * CellSize);
        }

        public float HeightToWorldY(int height, float yOffset = 0f)
            => height * HeightStep + yOffset;

        public MapGridInfo ToComponent()
            => new MapGridInfo
            {
                Size = Size,
                CellSize = CellSize,
                Origin = Origin,
                HeightStep = HeightStep,
            };
    }
}
