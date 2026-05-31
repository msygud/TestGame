using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  GridLayers  (ECS 싱글톤)
    //
    //  레이어별 독립 HashMap. 각 레이어는 목적과 변경 주기가 다르다.
    //
    //  에디터에서 저장/로드:
    //    TerrainLayer  → 맵 파일에 직렬화
    //    ResourceLayer → 맵 파일에 직렬화
    //
    //  인게임 시작 시 생성:
    //    OccupancyLayer → 지형 기반 초기화
    //    RoadLayer      → 빈 상태로 시작
    //    TerritoryLayer → 스타트포인트 기반 초기화
    // ══════════════════════════════════════════════════════════════
    public struct GridLayers : IComponentData
    {
        /// <summary>
        /// 도로 레이어 — 길찾기/통행 전용 (BFS 입력).
        /// 민간 BFS만 이 레이어를 참조.
        /// 인게임 시작 시 빈 상태, 도로 배치 시 갱신.
        /// </summary>
        public NativeHashMap<int2, RoadCell> RoadLayer;

        /// <summary>
        /// 점유 레이어 — 건설 가능 여부 판단.
        /// 도로/건물/유닛 모두 기록.
        /// 인게임 시작 시 TerrainLayer 기반으로 초기화.
        /// </summary>
        public NativeHashMap<int2, OccupancyCell> OccupancyLayer;

        /// <summary>
        /// 지형 레이어 — 이 셀이 어떤 지형인가?
        /// 에디터에서 작업, 맵 파일에 저장.
        /// 게임 중 변경 없음.
        /// key = 셀 좌표, value = TerrainCell (TypeId + Height)
        /// </summary>
        public NativeHashMap<int2, TerrainCell> TerrainLayer;

        /// <summary>
        /// 자원 레이어 — 채취 가능한 자원.
        /// 에디터에서 작업, 맵 파일에 저장.
        /// 채취 시 변경.
        /// key = 셀 좌표, value = ResourceCell (TypeId + Amount)
        /// </summary>
        public NativeHashMap<int2, ResourceCell> ResourceLayer;

        /// <summary>
        /// 팀 영역 레이어 — 어느 팀이 이 셀을 지배하는가.
        /// 인게임 시작 시 스타트포인트 기반 초기화.
        /// 전투/점령 시 실시간 변경.
        /// value = TeamIndex (-1 = 중립)
        /// </summary>
        public NativeHashMap<int2, int> TerritoryLayer;
    }

    // ══════════════════════════════════════════════════════════════
    //  TerrainCell  — 지형 레이어 셀 데이터
    // ══════════════════════════════════════════════════════════════
    public struct TerrainCell
    {
        /// <summary>
        /// 지형 타입 ID.
        /// CellTypeDefinition SO의 TypeId와 매핑.
        /// 동적 추가/삭제 가능 (int 키 기반).
        /// </summary>
        public int TypeId;

        /// <summary>
        /// 높이 단계 (0~15).
        /// 0 = 기본 평면. 큐브 Y 위치/스케일에 반영.
        /// </summary>
        public byte Height;
    }

    // ══════════════════════════════════════════════════════════════
    //  ResourceCell  — 자원 레이어 셀 데이터
    // ══════════════════════════════════════════════════════════════
    public struct ResourceCell
    {
        /// <summary>자원 타입 ID. CellTypeDefinition SO의 TypeId.</summary>
        public int TypeId;

        /// <summary>잔여 자원량 (0 = 고갈).</summary>
        public int Amount;
    }

    // ══════════════════════════════════════════════════════════════
    //  RoadCell  — 도로 레이어 셀 데이터
    // ══════════════════════════════════════════════════════════════
    public struct RoadCell
    {
        /// <summary>연결 방향 비트마스크 (프리팹 선택용).</summary>
        public RoadDir Directions;

        /// <summary>통행 축 (우측통행 BFS용).</summary>
        public RoadFlowAxis FlowAxis;

        /// <summary>차선 수 (2=기본, 4=업그레이드). 혼잡도 계산용.</summary>
        public byte LaneCount;

        /// <summary>소유 팀. 타 팀 통행 불가.</summary>
        public int TeamIndex;

        /// <summary>도로 엔티티 참조 (시각 프리팹 교체용).</summary>
        public Entity RoadEntity;
    }

    // ══════════════════════════════════════════════════════════════
    //  OccupancyCell  — 점유 레이어 셀 데이터
    // ══════════════════════════════════════════════════════════════
    public struct OccupancyCell
    {
        /// <summary>점유 종류.</summary>
        public OccupantType Type;

        /// <summary>점유 엔티티.</summary>
        public Entity Occupant;

        /// <summary>점유 팀 (-1 = 중립/없음).</summary>
        public int TeamIndex;

        public bool IsEmpty => Type == OccupantType.None;
    }

    // ══════════════════════════════════════════════════════════════
    //  열거형
    // ══════════════════════════════════════════════════════════════

    public enum RoadFlowAxis : byte
    {
        Horizontal = 0,  // E↔W 직선
        Vertical   = 1,  // N↔S 직선
        Cross      = 2,  // 교차로
    }

    public enum OccupantType : byte
    {
        None     = 0,
        Road     = 1,
        Building = 2,
        Unit     = 3,
        Terrain  = 4,  // 이동 불가 지형 (산/절벽 등)
    }
}
