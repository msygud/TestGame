using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{

    // ══════════════════════════════════════════════════════════════
    //  GridSettings  (ECS 싱글톤)
    //
    //  맵 로드 시 MapLoadSystem이 MapData.Settings에서 채운다.
    //  CellSize를 필요로 하는 모든 시스템이 이 싱글톤을 참조한다.
    // ══════════════════════════════════════════════════════════════
    public struct GridSettings : IComponentData
    {
        public float CellSize;
        public int   Width;
        public int   Height;

        /// <summary>셀 중심 월드 좌표 (XZ). Y는 heightStep * CellSize.</summary>
        public float3 CellCenter(int cx, int cz, byte heightStep = 0)
            => new float3(cx * CellSize + CellSize * 0.5f,
                          heightStep * CellSize,
                          cz * CellSize + CellSize * 0.5f);
    }

    /// <summary>
    /// GridLayers 및 GridMap 싱글톤 초기화/해제.
    /// 에디터 저장 레이어(Terrain/Resource)는 MapLoadSystem이 채움.
    /// 인게임 레이어(Road/Occupancy/Territory)는 빈 상태로 시작.
    /// </summary>
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct GridInitSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            // ── GridLayers ────────────────────────────────────────
            if (!SystemAPI.HasSingleton<GridLayers>())
            {
                var layers = new GridLayers
                {
                    RoadLayer      = new NativeHashMap<int2, RoadCell>    (1024, Allocator.Persistent),
                    OccupancyLayer = new NativeHashMap<int2, OccupancyCell>(2048, Allocator.Persistent),
                    TerrainLayer   = new NativeHashMap<int2, TerrainCell>  (2048, Allocator.Persistent),
                    ResourceLayer  = new NativeHashMap<int2, ResourceCell> (512,  Allocator.Persistent),
                    TerritoryLayer = new NativeHashMap<int2, int>          (1024, Allocator.Persistent),
                };
                var layerEntity = state.EntityManager.CreateEntity(typeof(GridLayers));
                state.EntityManager.SetComponentData(layerEntity, layers);
            }

            // ── GridSettings ─────────────────────────────────────
            if (!SystemAPI.HasSingleton<GridSettings>())
            {
                var settingsEntity = state.EntityManager.CreateEntity(typeof(GridSettings));
                // 초기값 0 — MapLoadSystem이 맵 로드 시 실제 값으로 채운다
                state.EntityManager.SetComponentData(settingsEntity, default(GridSettings));
            }

            // ── GridMap ───────────────────────────────────────────
            if (!SystemAPI.HasSingleton<GridMap>())
            {
                var gridMap = new GridMap
                {
                    BuildingCells = new NativeHashMap<int2, Entity>(2048, Allocator.Persistent),
                };
                var mapEntity = state.EntityManager.CreateEntity(typeof(GridMap));
                state.EntityManager.SetComponentData(mapEntity, gridMap);
            }
        }

        public void OnDestroy(ref SystemState state)
        {
            // GridLayers 해제
            if (SystemAPI.HasSingleton<GridLayers>())
            {
                var layers = SystemAPI.GetSingleton<GridLayers>();
                if (layers.RoadLayer.IsCreated)      layers.RoadLayer.Dispose();
                if (layers.OccupancyLayer.IsCreated) layers.OccupancyLayer.Dispose();
                if (layers.TerrainLayer.IsCreated)   layers.TerrainLayer.Dispose();
                if (layers.ResourceLayer.IsCreated)  layers.ResourceLayer.Dispose();
                if (layers.TerritoryLayer.IsCreated) layers.TerritoryLayer.Dispose();
            }

            // GridMap 해제
            if (SystemAPI.HasSingleton<GridMap>())
            {
                var gridMap = SystemAPI.GetSingleton<GridMap>();
                if (gridMap.BuildingCells.IsCreated) gridMap.BuildingCells.Dispose();
            }
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state) { }
    }
}
