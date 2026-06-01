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
        /// 이 도로를 소유한 팩션 ID. 게임 시작 시 팀별로 고정.
        /// RoadSystem이 (FactionId, dirMask) → MainKey (RoadKeyLookup) →
        /// PrefabLookup.Get(MainKey, VariantKey) 로 시각 프리팹을 찾는다.
        /// dirMask가 바뀌면 MainKey도 바뀌므로(방향마다 다른 MainKey),
        /// MainKey는 저장하지 않고 매번 FactionId+dirMask로 조회한다.
        /// </summary>
        public int FactionId;

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
        /// 이 도로를 짓는 팩션 ID. (FactionId, dirMask) → MainKey 조회에 사용.
        /// 유저: UI 진입 시 자기 팀의 FactionId. AI: 의도 발행 시 자기 FactionId.
        /// </summary>
        public int  FactionId;
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
