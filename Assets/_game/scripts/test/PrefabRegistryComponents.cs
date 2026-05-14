using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  PrefabRegistryEntry — DynamicBuffer 요소
    //
    //  베이크 시 각 RegistryItem이 이 요소로 변환됨.
    //  서브씬마다 하나의 엔티티가 이 버퍼를 가짐.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabRegistryEntry : IBufferElementData
    {
        public int    MainKey;
        public int    VariantKey;
        public int    DlcKey;
        public Entity Prefab;       // ECS 프리팹 엔티티
    }

    /// <summary>
    /// 한 번 처리된 버퍼는 다시 룩업에 추가되지 않도록 마크.
    /// </summary>
    public struct RegistryProcessed : IComponentData { }

    // ══════════════════════════════════════════════════════════════
    //  PrefabLookup — 글로벌 룩업 싱글톤
    //
    //  모든 활성 서브씬의 PrefabRegistryEntry 버퍼들을 합쳐
    //  (MainKey, VariantKey) → Entity 로 빠르게 조회.
    //
    //  서브씬 동적 로드/언로드 시 변경 감지 → 재빌드.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabLookup : IComponentData
    {
        public NativeHashMap<int2, Entity> Map;       // 핵심 룩업
        public NativeHashMap<int2, int>    DlcLookup; // (MainKey, VariantKey) → DlcKey

        public bool TryGet(int mainKey, int variantKey, out Entity prefab)
        {
            return Map.TryGetValue(new int2(mainKey, variantKey), out prefab);
        }

        public int GetDlcKey(int mainKey, int variantKey)
        {
            return DlcLookup.TryGetValue(new int2(mainKey, variantKey), out int dlc)
                ? dlc : 0;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SpawnRequest — Single 인스턴싱 요청
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
        public int    MainKey;
        public int    VariantKey;
        public int2   Cell;          // 좌하단 셀
        public float3 Position;
        public float  CellSize;      // 셀 월드 크기
        public float  Height;        // 배치 높이
        public int    Seed;          // 결정적 랜덤 시드
        public int    Count;         // 배치 갯수
        public float  ItemSize;      // 개별 아이템 크기 (겹침 방지용)
        public float  Scale;
    }

    // ══════════════════════════════════════════════════════════════
    //  RoadSpawnRequest — 도로 배치 요청
    //
    //  RoadSystem의 PlaceRoadCommand와 달리 이미 Directions가 결정됨.
    //  인게임 RoadSystem이 이 요청을 받아 자체 처리.
    //  (에디터에서 저장된 Directions는 참고용, RoadSystem이 재계산)
    // ══════════════════════════════════════════════════════════════
    public struct RoadSpawnRequest : IComponentData
    {
        public int    MainKey;
        public int    VariantKey;
        public int2   Cell;
        public float  Height;
        public float  Scale;
        // 인게임 RoadSystem이 Cell 기반으로 Directions 재계산
    }

    // ══════════════════════════════════════════════════════════════
    //  MapLoadState — 맵 로드 진행 상태 싱글톤
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
        public int           MissingDlcId;   // DlcMissing 시 어느 DLC 없는지
    }

    // ══════════════════════════════════════════════════════════════
    //  MapLoaded — 맵 로드 완료 태그
    // ══════════════════════════════════════════════════════════════
    public struct MapLoaded : IComponentData { }

    // ══════════════════════════════════════════════════════════════
    //  PrefabMetaEntry — RegistryItem 런타임 메타 버퍼
    //
    //  Multi의 Count, ItemSize 등 SpawnRequest만으로는 부족한
    //  메타 정보를 ECS로 전달.
    //  베이커가 RegistryItem 순회하며 이 버퍼를 생성.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabMetaEntry : IBufferElementData
    {
        public int   MainKey;
        public int   VariantKey;
        public int   MultiCount;     // MultiCountPerCell
        public float MultiItemSize;  // Multi 개별 아이템 크기
        public int2  Size;
        public float YOffset;
    }

    // ══════════════════════════════════════════════════════════════
    //  PrefabMetaLookup — 싱글톤 메타 룩업
    //
    //  (MainKey, VariantKey) → PrefabMetaEntry 빠른 조회.
    //  PrefabLookupInitSystem이 함께 빌드.
    // ══════════════════════════════════════════════════════════════
    public struct PrefabMetaLookup : IComponentData
    {
        public NativeHashMap<int2, PrefabMetaEntry> Map;

        public bool TryGetMeta(int mainKey, int variantKey, out PrefabMetaEntry meta)
            => Map.TryGetValue(new int2(mainKey, variantKey), out meta);
    }
}
