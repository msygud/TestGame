using Game.Unit;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════════════════
    //  UnitWaterMaskBuildSystem — 지형 물 정보 → 유닛 내비 그리드 물 마스크 (2026-07-19)
    //
    //  해상 이동 도메인(NavalUnit)의 물 판정 소스. 지형은 맵 로드 시 고정이므로
    //  1회 빌드(지형 셀 수 변경 감지 시 재빌드 — 맵 교체 대응). 나브 그리드 셀 중심의
    //  월드 좌표 → 도시 그리드 셀 → TerrainLayer TypeId → CellTypeLookup의
    //  TerrainCategory==Water. 나브 그리드가 도시 그리드보다 촘촘해도 좌표 매핑이라 무관.
    //  메인 저빈도(사실상 1회) — TerrainLayer 읽기는 메인 전용 접근으로 안전.
    //  수명주기: 이 시스템 OnCreate/OnDestroy(UnitWaterMask 싱글톤 소유).
    // ══════════════════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct UnitWaterMaskBuildSystem : ISystem
    {
        int _builtTerrainCount;   // 빌드 당시 지형 셀 수(변경 = 맵 교체 → 재빌드)

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GridLayers>();
            state.RequireForUpdate<GridSettings>();
            state.RequireForUpdate<CellTypeLookup>();
            state.RequireForUpdate<UnitNavigationGrid>();
            _builtTerrainCount = -1;

            if (!SystemAPI.HasSingleton<UnitWaterMask>())
                state.EntityManager.CreateEntity(typeof(UnitWaterMask));
        }

        public void OnDestroy(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<UnitWaterMask>()) return;
            var mask = SystemAPI.GetSingleton<UnitWaterMask>();
            if (mask.Water.IsCreated) mask.Water.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            var layers = SystemAPI.GetSingleton<GridLayers>();
            if (!layers.TerrainLayer.IsCreated) return;
            int terrainCount = layers.TerrainLayer.Count;
            if (terrainCount == 0 || terrainCount == _builtTerrainCount) return;
            _builtTerrainCount = terrainCount;

            var grid     = SystemAPI.GetSingleton<UnitNavigationGrid>();
            var settings = SystemAPI.GetSingleton<GridSettings>();
            var cellType = SystemAPI.GetSingleton<CellTypeLookup>();
            int cellCount = grid.Size.x * grid.Size.y;
            if (cellCount <= 0) return;

            ref var mask = ref SystemAPI.GetSingletonRW<UnitWaterMask>().ValueRW;
            if (mask.Water.IsCreated) mask.Water.Dispose();
            mask.Water = new NativeArray<byte>(cellCount, Allocator.Persistent);
            mask.Size  = grid.Size;

            float inv = 1f / math.max(0.01f, settings.CellSize);
            int water = 0;
            for (int y = 0; y < grid.Size.y; y++)
            for (int x = 0; x < grid.Size.x; x++)
            {
                // 나브 셀 중심 월드 좌표 → 도시 그리드 셀(월드 = 셀 × CellSize, 원점 0).
                float wx = grid.Origin.x + (x + 0.5f) * grid.CellSize;
                float wz = grid.Origin.z + (y + 0.5f) * grid.CellSize;
                var cityCell = new int2((int)math.floor(wx * inv), (int)math.floor(wz * inv));

                bool isWater = layers.TerrainLayer.TryGetValue(cityCell, out var terrain)
                               && cellType.TryGet(terrain.TypeId, out var info)
                               && info.TerrainCategory == TerrainCategory.Water;
                if (isWater) { mask.Water[y * grid.Size.x + x] = 1; water++; }
            }

            UnityEngine.Debug.Log($"[UnitWaterMask] 빌드: {grid.Size.x}x{grid.Size.y} 나브 셀 중 물 {water} (지형 {terrainCount}셀)");
        }
    }
}
