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

        // ── 그리드/입구/공급자 정보 (인게임 건물 배치 경로에서만 채움) ──
        //   맵 로더 등 다른 생성 경로는 이 블록을 비워두면(HasFootprint=false)
        //   SpawnSystem이 BuildingFootprint/BuildingEntrance/StampSupplier를
        //   부착하지 않는다 — 기존 경로 무영향.
        public bool         HasFootprint;
        public int2         FootprintOrigin; // 좌하단 셀
        public int2         FootprintSize;   // 원본(회전 전) Size — EntranceOps 정규화 규약
        public int          RotSteps;        // 0~3
        public int          OwnerLocalId;

        public bool         HasEntrance;
        public EntranceInfo Entrance;

        // 임시 공급자 정보 (이번 단계엔 IsSupplier=false 하드코딩.
        // 나중에 PrefabMeta로 이관하며 EmitSingle에서 채운다.)
        public bool         IsSupplier;
        public NeedType     Relief;
        public int          SupplyMaxDist;   // 0 이하 = 무제한

        /// <summary>영토 전환 파괴 면제(베이스/HQ). SpawnSystem이 CaptureExempt 태그 부착.</summary>
        public bool         CaptureExempt;
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

    // ══════════════════════════════════════════════════════════════
    //  BuildingFootprint — 건물의 그리드 점유 사실 (모든 단일 건물에 부착)
    //
    //  배치 시점에 SpawnSystem이 부착. 입구 유무와 무관.
    //  · Size는 원본(회전 전). EntranceOps가 (원본 Size + RotSteps)로
    //    회전 정규화하는 규약과 일치한다.
    //  · stamp BFS 시작점 계산, 철거 시 점유 해제, UI 표시 등에 두루 쓰인다
    //    (공급자 전용이 아니라 건물 일반 데이터).
    // ══════════════════════════════════════════════════════════════
    public struct BuildingFootprint : IComponentData
    {
        public int2 Origin;       // 좌하단 셀
        public int2 Size;         // 원본(회전 전) Size
        public int  RotSteps;     // 0~3
        public int  OwnerLocalId; // 소유 플레이어 (값 비교 필터용)
    }

    // ══════════════════════════════════════════════════════════════
    //  BuildingEntrance — 입구를 가진 건물에만 부착
    //
    //  EntranceOps.EntranceRoadCell(Footprint.Origin, Footprint.Size,
    //    Entrance, Footprint.RotSteps) 로 입구가 향하는 도로셀을 얻는다.
    // ══════════════════════════════════════════════════════════════
    public struct BuildingEntrance : IComponentData
    {
        public EntranceInfo Entrance;
    }

    // ══════════════════════════════════════════════════════════════
    //  SpawnConfig — 스폰 밸런스 (싱글톤, 없으면 Default)
    //  Test.cs가 인스펙터 값을 매 프레임 push(통합 밸런스 패널).
    // ══════════════════════════════════════════════════════════════
    public struct SpawnConfig : IComponentData
    {
        /// <summary>건물 기본 전투 체력(균일, 임시). 프리팹별 값이 필요해지면
        /// BuildingAuthoring 베이킹으로 이전(능력=컴포넌트 원칙) — 그때 이 필드는 폴백.</summary>
        public float BuildingDefaultHealth;

        public static SpawnConfig Default => new SpawnConfig
        {
            BuildingDefaultHealth = 500f,
        };
    }
}
