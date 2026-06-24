using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  BakedPrefabEntry  (DynamicBuffer)
    //
    //  SubScene 베이킹 시 GamePrefabRegistryAuthoring.Baker가 작성.
    //  PrefabLookupBuildSystem이 이 버퍼를 읽어
    //  PrefabLookup / PrefabMetaLookup NativeHashMap에 반영한다.
    // ══════════════════════════════════════════════════════════════
    [InternalBufferCapacity(64)]
    public struct BakedPrefabEntry : IBufferElementData
    {
        public int    MainKey;
        public int    VariantKey;
        public Entity Prefab;

        /// <summary>XZ 점유 크기 (셀 단위). Building 배치 시 점유 검사에 사용.</summary>
        public int2   Size;
        /// <summary>인스턴싱 시 위치 보정 오프셋 (월드 단위).</summary>
        public float3 Offset;
        /// <summary>도로 비트마스크 (RoadDir). 0 = 도로 아님.</summary>
        public byte   RoadMask;
        /// <summary>Environment 모드: 셀당 배치 수.</summary>
        public int    MultiCount;
        /// <summary>Environment 모드: 개별 아이템 크기.</summary>
        public float  MultiItemSize;
        /// <summary>DLC 소속 ID. DLC 보유 검증에 사용.</summary>
        public int          DlcId;
        /// <summary>배치 가능한 지형 종류 (Land / Water / Any).</summary>
        public TerrainMask  BuildableOn;
        /// <summary>스폰 방식·속성을 결정하는 카테고리.</summary>
        public PrefabCategory Category;
        /// <summary>시민 욕구 공급자 여부. true면 SpawnSystem이 StampSupplier를 부착.</summary>
        public bool         IsSupplier;
        /// <summary>해소하는 욕구 비트마스크. IsSupplier=true일 때만 유효.</summary>
        public NeedType     Relief;
        /// <summary>stamp BFS 최대 도달 거리. 0 이하면 무제한.</summary>
        public int          SupplyMaxDist;
        /// <summary>도로 관리시설 여부. true면 SpawnSystem이 RoadMaintenanceDepot를 부착.</summary>
        public bool         IsRoadMaintenance;
        /// <summary>관리 도달 거리(도로 칸 수). 0 이하면 무제한.</summary>
        public int          MaintenanceMaxDist;
    }

    // ══════════════════════════════════════════════════════════════
    //  BakedEntranceEntry  (DynamicBuffer) — 단일 입구
    // ══════════════════════════════════════════════════════════════
    [InternalBufferCapacity(32)]
    public struct BakedEntranceEntry : IBufferElementData
    {
        public int MainKey;
        public int2 Offset;   // footprint 원점(최소코너) 기준 상대 셀
        public byte Dir;      // RoadDir 단일 비트 (N/E/S/W)
    }

    // ══════════════════════════════════════════════════════════════
    //  EntranceInfo  — EntranceLookup 값 타입 (단일 입구)
    // ══════════════════════════════════════════════════════════════
    public struct EntranceInfo
    {
        public int2 Offset;   // 최소코너 기준 상대 셀 (비음수)
        public byte Dir;      // RoadDir 단일 비트
    }

    // ══════════════════════════════════════════════════════════════
    //  PrefabLookup  (ECS 싱글톤)
    //
    //  (MainKey, VariantKey) → 프리팹 Entity 런타임 조회 테이블.
    //  생명주기: PrefabLookupBuildSystem이 관리.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabLookup : IComponentData
    {
        /// <summary>key = int2(MainKey, VariantKey), value = 프리팹 Entity.</summary>
        public NativeHashMap<int2, Entity> Table;

        /// <summary>로드된 DLC ID 집합. HasDlc 검사에 사용.</summary>
        public NativeHashSet<int> LoadedDlcIds;

        /// <summary>키로 프리팹 Entity 조회. 없으면 Entity.Null.</summary>
        public Entity Get(int mainKey, int variantKey)
        {
            Table.TryGetValue(new int2(mainKey, variantKey), out var e);
            return e;
        }

        /// <summary>도로 전용 조회. dirMask = RoadDir 비트마스크(1~15).</summary>
        public Entity GetRoad(int mainKey, byte dirMask)
            => Get(mainKey, dirMask);

        /// <summary>해당 DLC가 로드되어 있는지 확인.</summary>
        public bool HasDlc(int dlcId) => LoadedDlcIds.Contains(dlcId);
    }

    // ══════════════════════════════════════════════════════════════
    //  PrefabRegistryProcessed  (태그)
    //
    //  BakedPrefabEntry/BakedEntranceEntry 버퍼가 룩업에 반영 완료된
    //  엔티티에 부착. PrefabLookupBuildSystem이 중복 처리 방지에 사용.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabRegistryProcessed : IComponentData { }

    // ══════════════════════════════════════════════════════════════
    //  PrefabMeta  — 프리팹 배치 메타정보 (PrefabMetaLookup 값 타입)
    //
    //  스폰 방식·속성은 Category에서 파생한다.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabMeta
    {
        public int2          Size;          // XZ 점유 크기 (셀 단위)
        public float3        Offset;        // 배치 위치 보정
        public byte          RoadMask;      // 도로 셰이프 비트마스크 (0 = 도로 아님)
        public int           MultiCount;    // Environment 배치 수
        public float         MultiItemSize; // Environment 개별 아이템 크기
        public TerrainMask   BuildableOn;   // 배치 가능 지형
        public PrefabCategory Category;     // 스폰 방식 결정
        public bool          IsSupplier;    // 욕구 공급자 여부
        public NeedType      Relief;        // 해소 욕구 비트마스크
        public int           SupplyMaxDist; // stamp BFS 최대 거리 (0 이하=무제한)
        public bool          IsRoadMaintenance;  // 도로 관리시설 여부
        public int           MaintenanceMaxDist; // 관리 도달 거리 (0 이하=무제한)

        public bool IsRoad     => Category == PrefabCategory.Road;
        public bool IsMulti    => Category == PrefabCategory.Environment;
        public bool IsBuilding => Category == PrefabCategory.Building;
        public bool HasEntrance => Category == PrefabCategory.Building;
    }

    // ══════════════════════════════════════════════════════════════
    //  PrefabMetaLookup  (ECS 싱글톤)
    //
    //  (MainKey, VariantKey) → PrefabMeta 런타임 조회 테이블.
    //  MapLoadSystem, MapLoaderSystem, BuildingPlacementSystem 등이 참조.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabMetaLookup : IComponentData
    {
        public NativeHashMap<int2, PrefabMeta> Table;

        /// <summary>메타 조회. 없으면 default(PrefabMeta) 반환.</summary>
        public PrefabMeta Get(int mainKey, int variantKey)
        {
            Table.TryGetValue(new int2(mainKey, variantKey), out var m);
            return m;
        }

        /// <summary>메타 조회 (out 패턴).</summary>
        public bool TryGetMeta(int mainKey, int variantKey, out PrefabMeta meta)
            => Table.TryGetValue(new int2(mainKey, variantKey), out meta);
    }

    // ══════════════════════════════════════════════════════════════
    //  EntranceLookup  (ECS 싱글톤) — MainKey → 단일 입구 정보.
    // ══════════════════════════════════════════════════════════════
    public struct EntranceLookup : IComponentData
    {
        public NativeHashMap<int, EntranceInfo> Table;

        /// <summary>MainKey의 입구 정보 조회.</summary>
        public bool TryGet(int mainKey, out EntranceInfo info)
            => Table.TryGetValue(mainKey, out info);

        /// <summary>입구 정의가 있는지 여부.</summary>
        public bool Has(int mainKey) => Table.ContainsKey(mainKey);
    }
}
