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

        /// <summary>XZ 점유 크기 (셀 단위). Single 배치 시 점유 검사에 사용.</summary>
        public int2   Size;
        /// <summary>인스턴싱 시 위치 보정 오프셋 (월드 단위).</summary>
        public float3 Offset;
        /// <summary>도로 비트마스크 (RoadDir). 0 = 도로 아님.</summary>
        public byte   RoadMask;
        /// <summary>Multi 모드: 셀당 배치 수.</summary>
        public int    MultiCount;
        /// <summary>Multi 모드: 개별 아이템 크기.</summary>
        public float  MultiItemSize;
        /// <summary>DLC 소속 ID. DLC 보유 검증에 사용.</summary>
        public int          DlcId;
        /// <summary>배치 가능한 지형 종류 (Land / Water / Any).</summary>
        public TerrainMask  BuildableOn;
        /// <summary>Single / Multi 배치 모드.</summary>
        public PrefabSpawnMode SpawnMode;
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

        /// <summary>로드된 DLC ID 집합. IsDlcAvailable 검사에 사용.</summary>
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
    //  BakedPrefabEntry 버퍼가 PrefabLookup에 반영 완료된 엔티티에 부착.
    //  PrefabLookupBuildSystem이 중복 처리를 방지하기 위해 사용.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabRegistryProcessed : IComponentData { }

    // ══════════════════════════════════════════════════════════════
    //  PrefabMeta  — 프리팹 배치 메타정보 (PrefabMetaLookup 값 타입)
    // ══════════════════════════════════════════════════════════════
    public struct PrefabMeta
    {
        public int2          Size;          // XZ 점유 크기 (셀 단위)
        public float3        Offset;        // 배치 위치 보정
        public byte          RoadMask;      // 0 = 도로 아님
        public int           MultiCount;    // Multi 배치 수
        public float         MultiItemSize; // Multi 개별 아이템 크기
        public TerrainMask   BuildableOn;   // 배치 가능 지형
        public PrefabSpawnMode SpawnMode;   // Single / Multi

        public bool IsRoad  => RoadMask != 0;
        public bool IsMulti => SpawnMode == PrefabSpawnMode.Multi && !IsRoad;
    }

    // ══════════════════════════════════════════════════════════════
    //  PrefabMetaLookup  (ECS 싱글톤)
    //
    //  (MainKey, VariantKey) → PrefabMeta 런타임 조회 테이블.
    //  MapLoadSystem, MapLoaderSystem 등이 참조.
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

        /// <summary>메타 조회 (out 패턴). MultiCount 등 있을 때만 쓰는 경우.</summary>
        public bool TryGetMeta(int mainKey, int variantKey, out PrefabMeta meta)
            => Table.TryGetValue(new int2(mainKey, variantKey), out meta);
    }
}
