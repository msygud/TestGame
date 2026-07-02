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
    /// 도로가 배치된 주행 축. 평행 도로 간 자동 연결 방지에 사용.
    /// Any  = 모든 방향 연결 (지도 사전 배치, 베이스캠프 도로).
    /// EW   = 동서 축으로 배치된 도로 (남북 방향 연결은 교차 도로만 허용).
    /// NS   = 남북 축으로 배치된 도로.
    /// </summary>
    public enum RoadPlacedAxis : byte { Any = 0, EW = 1, NS = 2 }

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

        /// <summary>footprint 한 변 셀 수 (항상 정사각형). 1 = 1×1.</summary>
        public byte Size;

        /// <summary>footprint 원점(좌하단). FixupRoadLayer + 시각 스케일 계산에 사용.</summary>
        public int2 FootprintOrigin;

        /// <summary>
        /// 비주얼(프리팹 선택) 방향 강제값. RoadDir.None(0) = 강제 없음(셀 단위 자동 계산 사용).
        /// Size>1인 블록은 origin 셀 기준 자동 계산이 블록 내부 방향까지 섞여 오염되므로,
        /// 매크로(블록) 단위로 미리 계산한 값을 여기에 채워 비주얼 선택에만 사용한다.
        /// 칸 단위 보행 경로(CivilianBFS)는 이 값과 무관하게 GridLayers.RoadLayer의
        /// 셀별 Directions(자동 계산, 정확함)를 그대로 쓴다.
        /// </summary>
        public RoadDir VisualDirectionsOverride;

        /// <summary>배치 축. 평행 도로 간 자동 연결 차단에 사용.</summary>
        public RoadPlacedAxis Axis;
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
        public int OwnerLocalId;
        public byte LaneCount; // 기본 2
        /// <summary>
        /// 이 도로를 짓는 팩션 ID. (FactionId, dirMask) → MainKey 조회에 사용.
        /// 유저: UI 진입 시 자기 팀의 FactionId. AI: 의도 발행 시 자기 FactionId.
        /// </summary>
        public int  FactionId;
        /// <summary>footprint 한 변 셀 수 (정사각형). 0 또는 1 = 1×1.</summary>
        public byte Size;
        /// <summary>
        /// 비주얼 방향 강제값. RoadDir.None(0) = 강제 없음(셀 단위 자동 계산).
        /// Size>1 블록을 매크로 단위로 이어붙일 때(예: 베이스캠프 외곽 도로 링)
        /// 호출자가 미리 계산한 블록 간 연결 방향을 여기에 채운다.
        /// </summary>
        public RoadDir VisualDirectionsOverride;

        /// <summary>배치 축. RoadBuildController가 드래그 방향에서 결정. (레거시 축 모델 전용)</summary>
        public RoadPlacedAxis Axis;

        /// <summary>
        /// 그린-방향(연속성) 모델: 이 셀이 드래그 경로에서 실제로 이어준 방향 비트.
        /// None(0) = 미지정 → RoadSystem이 레거시 축(Axis) 모델로 Directions 파생.
        /// 비-None = 명시 모델 → RoadSystem이 이 비트를 RoadCell.Directions에 직접
        ///   set(새 셀) 또는 OR(기존 같은-소유자 셀, 겹침=교차 승격)하고, 축 재계산·
        ///   경계 전파를 생략한다. (유저 드래그 = 항상 1×1 명시 모델.)
        /// 경로 이웃(이전/다음)을 향한 비트만 담기므로 상호 비트 불변식이 자동 성립.
        /// </summary>
        public RoadDir Directions;
    }

    /// <summary>도로 철거 명령. 단발성.</summary>
    public struct RemoveRoadCommand : IComponentData
    {
        public int2 Cell;
        public int OwnerLocalId; // 소유 플레이어만 철거 가능 (Forced=1이면 소유 무시)
        /// <summary>1 = 소유자 일치 무시(강제 철거). 적 공격·광역 raze 등 비소유 파괴에 사용.</summary>
        public byte Forced;
    }

    /// <summary>
    /// 광역 raze 명령(단발성). [Min,Max] 사각형 안의 건물을 파괴(점유 해제)하고,
    /// 살아있는 건물에 더는 닿지 않는(=공유 안 되는) 도로를 orphan으로 제거한다.
    ///   · 소유 무관(공평) — 플레이어·적·AI 누구의 것이든 영역 안이면 파괴.
    ///   · RazeSystem이 처리: 건물 파괴 + orphan 도로 → 강제 RemoveRoadCommand 발행(RoadSystem 실행).
    /// </summary>
    public struct RazeAreaCommand : IComponentData
    {
        public int2 Min;   // 좌하단 셀(포함)
        public int2 Max;   // 우상단 셀(포함)
    }

    /// <summary>도로 업그레이드 명령 (2차선 → 4차선). 단발성.</summary>
    public struct UpgradeRoadCommand : IComponentData
    {
        public int2 Cell;
        public int OwnerLocalId;
    }

    /// <summary>프리뷰 명령. 단발성.</summary>
    public struct PreviewRoadCommand : IComponentData
    {
        public int2 Cell;
        public int OwnerLocalId;
    }

    /// <summary>
    /// 특수 목적 도로 연장 요청(단발성). **목적 무관 재사용 메커니즘**:
    /// 팀 도로 네트워크에서 Target(또는 그 인접)까지 그린-방향(연속) 도로 경로를 깐다.
    ///   · 발행: 항구/자원 등 목적 로직(미구현) 또는 테스트. 처리: RoadPathSystem.
    ///   · 경로는 같은 높이 평지·Land·빈땅(환경물 치움)만 통과 — 물/단차/자원/건물 회피.
    ///   · 현재 1×1 도로 전용(그린 모델). 멀티셀은 추후.
    /// </summary>
    /// <summary>
    /// 활성 지선(자원/항구 등 목적 도로) 등록 — RoadPathSystem이 경로 성공 시 자동 생성(중복 방지).
    /// AiRoadJanitorSystem이 ① 온전한(베이스 연결) 지선 끝을 트림에서 보호
    /// ② 끊기면(타겟 인접 자기 도로가 없거나 단절) RoadPathRequest 재발행 = 자가 수리.
    /// 목적 소멸 시(자원 고갈 등)엔 목적 로직이 이 엔티티를 파괴해 등록 해제(미구현 — 발행 주체 소관).
    /// </summary>
    public struct RoadSpur : IComponentData
    {
        public int2 Target;
        public int  OwnerLocalId;
        public int  FactionId;
        public byte StopAdjacent;
    }

    public struct RoadPathRequest : IComponentData
    {
        /// <summary>도달하려는 특수 셀(예: 물가 접근지/자원). 보통 도로 불가 셀 → 인접까지 연장.</summary>
        public int2 Target;
        public int  OwnerLocalId;
        /// <summary>(FactionId, dirMask) → 시각 프리팹 조회에 필요.</summary>
        public int  FactionId;
        /// <summary>1=Target 인접까지(기본, Target 자체가 도로 불가일 때) / 0=Target 셀까지.</summary>
        public byte StopAdjacent;
    }
}
