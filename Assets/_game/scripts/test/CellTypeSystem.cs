using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  TerrainCategory  — 셀 지형 분류 (건물 배치 검증에 사용)
    // ══════════════════════════════════════════════════════════════
    public enum TerrainCategory : byte
    {
        Land  = 0,
        Water = 1,
    }


    // ══════════════════════════════════════════════════════════════
    //  CellTypeDefinition  (ScriptableObject)
    //
    //  지형/자원 타입 하나를 정의한다.
    //  TypeId는 MapData에 저장되는 키.
    //  MainKey/VariantKey는 GamePrefabRegistry 참조.
    //
    //  메뉴: Assets > Create > CitySim > Cell Type Definition
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Cell Type Definition",
        fileName = "CellTypeDefinition")]
    public class CellTypeDefinition : ScriptableObject
    {
        [Header("Identity")]
        public int    TypeId;
        public string TypeName;

        [Header("Prefab (GamePrefabRegistry 참조)")]
        [Tooltip("이 타입을 시각화할 프리팹의 MainKey.")]
        public int PrefabMainKey;
        [Tooltip("이 타입을 시각화할 프리팹의 VariantKey.")]
        public int PrefabVariantKey;

        [Header("Gameplay Flags")]
        public bool Passable      = true;
        public bool Buildable     = true;
        public bool RoadBuildable = true;

        [Tooltip("이 지형의 분류. 건물 BuildableOn 검증에 사용.")]
        public TerrainCategory TerrainCategory = TerrainCategory.Land;
    }

    // ══════════════════════════════════════════════════════════════
    //  CellTypeRegistry  (ScriptableObject)
    //
    //  모든 CellTypeDefinition SO를 한 곳에서 관리.
    //  CellTypeRegistryAuthoring이 이 SO를 베이킹에 사용.
    //
    //  메뉴: Assets > Create > CitySim > Cell Type Registry
    // ══════════════════════════════════════════════════════════════
    [CreateAssetMenu(
        menuName = "CitySim/Cell Type Registry",
        fileName = "CellTypeRegistry")]
    public class CellTypeRegistry : ScriptableObject
    {
        public List<CellTypeDefinition> Types = new();
    }

    // ══════════════════════════════════════════════════════════════
    //  CellTypeRegistryAuthoring  (Authoring MonoBehaviour)
    //
    //  SubScene에 배치. CellTypeRegistry SO → BakedCellTypeEntry 버퍼.
    //  Origin SubScene에 하나만 배치하면 충분
    //  (지형 타입은 DLC 간 공유 가능하므로).
    // ══════════════════════════════════════════════════════════════
    public class CellTypeRegistryAuthoring : MonoBehaviour
    {
        [Tooltip("CellTypeRegistry SO를 연결하세요.")]
        public CellTypeRegistry Registry;

        class Baker : Baker<CellTypeRegistryAuthoring>
        {
            public override void Bake(CellTypeRegistryAuthoring authoring)
            {
                if (authoring.Registry == null)
                {
                    Debug.LogError(
                        "[CellTypeRegistryAuthoring] Registry SO가 없습니다.");
                    return;
                }

                DependsOn(authoring.Registry);

                var e      = GetEntity(TransformUsageFlags.None);
                var buffer = AddBuffer<BakedCellTypeEntry>(e);

                foreach (var def in authoring.Registry.Types)
                {
                    if (def == null) continue;
                    DependsOn(def);

                    buffer.Add(new BakedCellTypeEntry
                    {
                        TypeId           = def.TypeId,
                        MainKey          = def.PrefabMainKey,
                        VariantKey       = def.PrefabVariantKey,
                        Passable         = def.Passable,
                        Buildable        = def.Buildable,
                        RoadBuildable    = def.RoadBuildable,
                        TerrainCategory  = def.TerrainCategory,
                    });
                }

                Debug.Log(
                    $"[Baker] CellTypeRegistry 베이킹 완료: " +
                    $"{authoring.Registry.Types.Count}개 타입");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  CellTypeLookupBuildSystem
    //
    //  BakedCellTypeEntry 버퍼 → CellTypeLookup NativeHashMap.
    //  PrefabLookupBuildSystem과 동일한 패턴.
    // ══════════════════════════════════════════════════════════════
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    [UpdateAfter(typeof(PrefabLookupBuildSystem))]
    public partial struct CellTypeLookupBuildSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            var e = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponentData(e, new CellTypeLookup
            {
                Table = new NativeHashMap<int, CellTypeInfo>(64, Allocator.Persistent),
            });
        }

        public void OnUpdate(ref SystemState state)
        {
            var unprocessed = SystemAPI.QueryBuilder()
                .WithAll<BakedCellTypeEntry>()
                .WithNone<CellTypeRegistryProcessed>()
                .Build();

            if (unprocessed.IsEmpty) return;

            var lookup = SystemAPI.GetSingletonRW<CellTypeLookup>();
            var ecb    = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (entries, entity) in
                SystemAPI.Query<DynamicBuffer<BakedCellTypeEntry>>()
                    .WithNone<CellTypeRegistryProcessed>()
                    .WithEntityAccess())
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    var entry = entries[i];
                    lookup.ValueRW.Table.TryAdd(entry.TypeId, new CellTypeInfo
                    {
                        MainKey          = entry.MainKey,
                        VariantKey       = entry.VariantKey,
                        Passable         = entry.Passable,
                        Buildable        = entry.Buildable,
                        RoadBuildable    = entry.RoadBuildable,
                        TerrainCategory  = entry.TerrainCategory,
                    });
                }
                ecb.AddComponent<CellTypeRegistryProcessed>(entity);
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        public void OnDestroy(ref SystemState state)
        {
            if (SystemAPI.HasSingleton<CellTypeLookup>())
            {
                var lookup = SystemAPI.GetSingleton<CellTypeLookup>();
                if (lookup.Table.IsCreated) lookup.Table.Dispose();
            }
        }
    }
}
