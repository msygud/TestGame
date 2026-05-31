using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  SpawnRequest — Single 인스턴싱 요청
    //
    //  MapLoaderSystem이 생성 → SpawnSystem이 처리 후 엔티티 파괴.
    // ══════════════════════════════════════════════════════════════
    public struct SpawnRequest : IComponentData
    {
        public int        MainKey;
        public int        VariantKey;
        public float3     Position;
        public quaternion Rotation;
        public float      Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  MultiSpawnRequest — Multi 결정적 랜덤 배치 요청
    //
    //  Seed를 기반으로 셀 영역 안에 Count개를 랜덤 배치.
    //  MultiSpawnSystem이 처리 후 엔티티 파괴.
    // ══════════════════════════════════════════════════════════════
    public struct MultiSpawnRequest : IComponentData
    {
        public int   MainKey;
        public int   VariantKey;
        public int2  Cell;      // 좌하단 셀
        public float CellSize;  // 셀 월드 크기
        public float Height;    // 배치 높이
        public int   Seed;      // 결정적 랜덤 시드
        public int   Count;     // 배치 개수
        public float ItemSize;  // 개별 아이템 크기 (겹침 방지용)
        public float Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  RoadSpawnRequest — 도로 배치 요청
    //
    //  MapLoaderSystem이 맵 JSON을 읽고 생성.
    //  RoadSystem의 PlaceRoadCommand로 변환되어 처리됨.
    // ══════════════════════════════════════════════════════════════
    public struct RoadSpawnRequest : IComponentData
    {
        public int   MainKey;
        public int   VariantKey; // 저장용 참고값. 인게임 RoadSystem이 재계산
        public int2  Cell;
        public float Height;
        public float Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  MapLoadStatus / MapLoadState — 맵 로드 진행 상태
    // ══════════════════════════════════════════════════════════════
    public enum MapLoadStatus : byte
    {
        Idle,
        Loading,
        DlcMissing,
        Done,
        Failed,
    }

    public struct MapLoadState : IComponentData
    {
        public MapLoadStatus Status;
        public int           MissingDlcId; // DlcMissing 시 어느 DLC 없는지
    }
}
