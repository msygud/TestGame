using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  GridMap  (ECS 싱글톤)
    //
    //  맵 로드 시 건물/지형 인스턴스의 셀 점유 정보를 보관한다.
    //  GridLayers.OccupancyLayer가 도로/유닛 등 인게임 점유를 담당하고,
    //  GridMap은 맵 로드 단계에서 배치된 정적 오브젝트의 위치를 추적.
    //
    //  생성: GridInitSystem.OnCreate
    //  해제: GridInitSystem.OnDestroy
    // ══════════════════════════════════════════════════════════════
    public struct GridMap : IComponentData
    {
        /// <summary>
        /// 셀 좌표 → 점유 엔티티.
        /// 지형/자원/Single 배치물이 차지하는 셀을 기록한다.
        /// MapLoadSystem이 채우고, 다른 시스템이 건물 가능 여부 판단에 사용.
        /// </summary>
        public NativeHashMap<int2, Entity> BuildingCells;
    }
}
