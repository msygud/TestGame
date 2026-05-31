using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ========== 공통 ==========

    /// <summary>그리드 좌표. 도로/건물 모두 공유.</summary>
    public struct GridPosition : IComponentData
    {
        public int2 Value;
    }

    // ========== 도로 ==========

    /// <summary>
    /// 도로 태그 + 시각 데이터.
    /// 길찾기는 GridLayers.RoadLayer가 담당.
    /// 이 컴포넌트는 시각 프리팹 관리용.
    /// </summary>
    public struct Road : IComponentData
    {
        /// <summary>현재 비트마스크 (프리팹 선택 기준, 1~15).</summary>
        public RoadDir Directions;

        /// <summary>
        /// 프리팹 레지스트리의 MainKey.
        /// RoadSystem이 PrefabLookup.GetRoad(MainKey, mask)로 시각 프리팹을 찾는다.
        /// </summary>
        public int MainKey;

        /// <summary>차선 수 (2 or 4).</summary>
        public byte LaneCount;
    }

    /// <summary>
    /// 도로 시각 인스턴스 추적.
    /// 비트마스크 변경 시 기존 인스턴스 파괴 후 재스폰.
    /// </summary>
    public struct RoadVisualInstance : IComponentData
    {
        public Entity Instance;
    }

    /// <summary>미리보기 도로 (프리뷰용 싱글톤).</summary>
    public struct RoadPreview : IComponentData
    {
        public int2    Cell;
        public RoadDir Directions;
    }

    // ========== 명령 ==========

    /// <summary>도로 확정 배치 명령. 단발성.</summary>
    public struct PlaceRoadCommand : IComponentData
    {
        public int2 Cell;
        public int  TeamIndex;
        public byte LaneCount; // 기본 2
        /// <summary>
        /// GamePrefabRegistry의 도로 MainKey.
        /// 이 값으로 PrefabLookup.GetRoad(MainKey, dirMask)를 조회한다.
        /// </summary>
        public int  MainKey;
    }

    /// <summary>도로 철거 명령. 단발성.</summary>
    public struct RemoveRoadCommand : IComponentData
    {
        public int2 Cell;
        public int  TeamIndex; // 소유 팀만 철거 가능
    }

    /// <summary>도로 업그레이드 명령 (2차선 → 4차선). 단발성.</summary>
    public struct UpgradeRoadCommand : IComponentData
    {
        public int2 Cell;
        public int  TeamIndex;
    }

    /// <summary>프리뷰 명령. 단발성.</summary>
    public struct PreviewRoadCommand : IComponentData
    {
        public int2 Cell;
        public int  TeamIndex;
    }
}
