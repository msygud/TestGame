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
        /// 영역 레이어 — 어느 플레이어가 이 셀을 지배하는가.
        /// 인게임 시작 시 스타트포인트 기반 초기화.
        /// 전투/점령 시 실시간 변경.
        /// value = OwnerLocalId (-1 = 중립)
        /// </summary>
        public NativeHashMap<int2, int> TerritoryLayer;

        /// <summary>
        /// 구획 레이어 — 저해상도 격자 (1셀 = BlockGrid.UNIT × UNIT 실셀).
        ///
        /// 구획(Block) 단위 메타데이터의 단일 소스:
        ///   "이 구획이 등록됐나 / 누구 것이냐 / 어느 구획 소속이냐 / 그 크기는".
        ///
        /// 셀 단위 점유는 담지 않는다 (그것은 OccupancyLayer의 단일 소스).
        /// 구획 내 빈 셀을 알려면:
        ///   BlockCell.BlockOrigin → 실셀 범위 변환 → OccupancyLayer 조회.
        ///
        /// key   = 저해상도 셀 좌표 (실셀 좌표 / UNIT)
        /// value = BlockCell
        /// </summary>
        public NativeHashMap<int2, BlockCell> BlockLayer;
    }

    // ══════════════════════════════════════════════════════════════
    //  BlockCell  — 구획 레이어 셀 데이터 (저해상도)
    //
    //  하나의 구획은 BlockOrigin이 동일한 저해상도 셀들의 묶음이다.
    //  큰 구획(예: 실셀 4×8 = 저해상도 2×4)은 여러 BlockCell이
    //  같은 BlockOrigin을 가리켜 하나의 구획으로 묶인다.
    // ══════════════════════════════════════════════════════════════
    public struct BlockCell
    {
        /// <summary>소유 플레이어 LocalId (슬롯 0~7). -1 = 빈 격자 (구획 미등록).</summary>
        public int OwnerLocalId;

        /// <summary>
        /// 이 저해상도 셀이 속한 구획의 원점 (저해상도 좌표).
        /// 어느 셀을 찍어도 이 값으로 구획 대표(원점 셀)에 도달한다.
        /// </summary>
        public int2 BlockOrigin;

        /// <summary>이 구획의 크기 (저해상도 단위). 예: 실셀 4×8 → (2,4).</summary>
        public int2 BlockSize;

        /// <summary>이 격자가 구획에 등록돼 있는가 (OwnerLocalId 음수 여부로도 판별 가능).</summary>
        public bool IsRegistered => OwnerLocalId >= 0;
    }

    // ══════════════════════════════════════════════════════════════
    //  BlockGrid  — 저해상도 ↔ 실해상도 변환 헬퍼
    //
    //  저해상도 1셀 = UNIT × UNIT 실셀.
    //  구획 변 길이는 {2,4,8} (전부 UNIT의 배수)이므로
    //  저해상도에서는 {1,2,4}로 환원된다.
    // ══════════════════════════════════════════════════════════════
    public static class BlockGrid
    {
        /// <summary>저해상도 1칸이 덮는 실셀 변 길이. 구획 최소 단위(=2)와 일치.</summary>
        public const int UNIT = 2;

        /// <summary>실셀 좌표 → 저해상도 셀 좌표 (내림).</summary>
        public static int2 ToBlock(int2 realCell)
            => new int2(FloorDiv(realCell.x, UNIT), FloorDiv(realCell.y, UNIT));

        /// <summary>저해상도 셀 좌표 → 실셀 좌표 (구획 원점의 좌하단 실셀).</summary>
        public static int2 ToReal(int2 blockCell)
            => new int2(blockCell.x * UNIT, blockCell.y * UNIT);

        /// <summary>저해상도 크기 → 실셀 크기.</summary>
        public static int2 RealSize(int2 blockSize)
            => new int2(blockSize.x * UNIT, blockSize.y * UNIT);

        /// <summary>음수 좌표에서도 올바르게 내림 나눗셈.</summary>
        static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }
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

        /// <summary>소유 플레이어 LocalId. 타 플레이어 통행 불가.</summary>
        public int OwnerLocalId;

        /// <summary>도로 엔티티 참조 (시각 프리팹 교체용).</summary>
        public Entity RoadEntity;

        /// <summary>이 셀이 속한 도로 footprint의 원점(좌하단). 철거 시 전체 footprint 제거에 사용.</summary>
        public int2 FootprintOrigin;

        /// <summary>footprint 한 변 셀 수 (항상 정사각형). 1 = 1×1.</summary>
        public byte Size;
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

        /// <summary>점유 플레이어 LocalId (-1 = 중립/없음).</summary>
        public int OwnerLocalId;

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
