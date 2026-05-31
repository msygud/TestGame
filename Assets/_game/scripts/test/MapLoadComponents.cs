using CitySim.MapEditor;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  MapLoadRequest  (IComponentData)
    //
    //  MonoBehaviour(DlcBootstrap) → ECS 브릿지.
    //  DlcBootstrap이 이 컴포넌트를 가진 엔티티를 생성하면
    //  MapLoadSystem이 감지하여 맵을 로드한다.
    //
    //  MapData JSON을 FixedString4096Bytes 대신
    //  BlobAssetReference로 전달하기엔 과도하므로,
    //  MapLoadSystem.OnUpdate에서 직접 EntityManager를 통해
    //  managed class를 꺼내는 방식을 사용한다.
    //  → ManagedMapLoadRequest 참고.
    // ══════════════════════════════════════════════════════════════
    public struct MapLoadRequest : IComponentData
    {
        /// <summary>로드할 맵 ID. DlcOwnershipService 접근 검증에 사용.</summary>
        public FixedString128Bytes MapId;
    }

    // ══════════════════════════════════════════════════════════════
    //  ManagedMapLoadRequest  (IComponentData, managed)
    //
    //  JSON 문자열처럼 관리형 데이터가 필요한 경우 사용.
    //  MapLoadRequest와 같은 엔티티에 함께 추가한다.
    // ══════════════════════════════════════════════════════════════
    public class ManagedMapLoadRequest : IComponentData
    {
        public MapData Data;  // 파싱 완료된 MapData 객체
    }

    // ══════════════════════════════════════════════════════════════
    //  MapLoaded  (태그)
    //
    //  맵 로드가 완료된 후 월드에 추가되는 싱글톤 태그.
    //  다른 시스템들이 맵 로드 완료를 기다릴 때 사용.
    //  예: RequireForUpdate<MapLoaded>
    // ══════════════════════════════════════════════════════════════
    public struct MapLoaded : IComponentData
    {
        public FixedString128Bytes MapId;
    }

    // ══════════════════════════════════════════════════════════════
    //  BakedCellTypeEntry  (DynamicBuffer)
    //
    //  CellTypeRegistryAuthoring.Baker가 CellTypeRegistry SO를
    //  ECS 버퍼로 변환한 결과.
    //  CellTypeLookupBuildSystem이 수집하여 CellTypeLookup에 반영.
    // ══════════════════════════════════════════════════════════════
    [InternalBufferCapacity(32)]
    public struct BakedCellTypeEntry : IBufferElementData
    {
        public int             TypeId;
        public int             MainKey;          // GamePrefabRegistry의 MainKey
        public int             VariantKey;       // GamePrefabRegistry의 VariantKey
        public bool            Passable;         // 유닛 이동 가능
        public bool            Buildable;        // 건물 건설 가능
        public bool            RoadBuildable;
        public TerrainCategory TerrainCategory;  // 지형 분류 (Land / Water)
    }

    // ══════════════════════════════════════════════════════════════
    //  CellTypeLookup  (ECS 싱글톤)
    //
    //  TypeId → CellTypeInfo 런타임 조회 테이블.
    //  MapLoadSystem이 TerrainCellData.TypeId를 해석할 때 사용.
    // ══════════════════════════════════════════════════════════════
    public struct CellTypeLookup : IComponentData
    {
        public NativeHashMap<int, CellTypeInfo> Table;

        public bool TryGet(int typeId, out CellTypeInfo info)
            => Table.TryGetValue(typeId, out info);
    }

    public struct CellTypeInfo
    {
        public int             MainKey;
        public int             VariantKey;
        public bool            Passable;
        public bool            Buildable;
        public bool            RoadBuildable;
        public TerrainCategory TerrainCategory;  // 지형 분류 (Land / Water)
    }

    // ══════════════════════════════════════════════════════════════
    //  CellTypeRegistryProcessed  (태그)
    //
    //  BakedCellTypeEntry 버퍼 처리 완료 표시.
    // ══════════════════════════════════════════════════════════════
    public struct CellTypeRegistryProcessed : IComponentData { }
}
