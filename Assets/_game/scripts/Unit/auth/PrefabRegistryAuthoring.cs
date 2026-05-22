using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CitySim
{
    public class PrefabRegistryAuthoring : MonoBehaviour
    {
        [Tooltip("The single prefab registry baked by this SubScene content module.")]
        public GamePrefabRegistry Source;

        [SerializeField, HideInInspector]
        List<GamePrefabRegistry> Sources = new();

#if UNITY_EDITOR
        void OnValidate()
        {
            if (Source == null)
                Source = GetFirstLegacySource();

            if (Sources != null && Sources.Count > 0)
                Sources.Clear();
        }
#endif

        class Baker : Baker<PrefabRegistryAuthoring>
        {
            public override void Bake(PrefabRegistryAuthoring authoring)
            {
                var source = authoring.ResolveSingleSource();
                if (source == null)
                    return;

                DependsOn(source);

                var entity = GetEntity(TransformUsageFlags.None);
                var registryBuffer = AddBuffer<PrefabRegistryEntry>(entity);
                var metaBuffer = AddBuffer<PrefabMetaEntry>(entity);

                BakeSource(source, registryBuffer, metaBuffer);
            }

            void BakeSource(
                GamePrefabRegistry source,
                DynamicBuffer<PrefabRegistryEntry> registryBuffer,
                DynamicBuffer<PrefabMetaEntry> metaBuffer)
            {
                int skipped = 0;

                foreach (var item in source.Items)
                {
                    if (item.IsDeleted) continue;
                    if (item.Prefab == null) { skipped++; continue; }

                    var prefabEntity = GetEntity(item.Prefab, TransformUsageFlags.Dynamic);

                    registryBuffer.Add(new PrefabRegistryEntry
                    {
                        MainKey = item.MainKey,
                        VariantKey = item.VariantKey,
                        DlcKey = item.DlcKey,
                        RoadDirectionMask = item.GetRoadDirectionMask(),
                        Prefab = prefabEntity,
                    });

                    metaBuffer.Add(new PrefabMetaEntry
                    {
                        MainKey = item.MainKey,
                        VariantKey = item.VariantKey,
                        MultiCount = item.MultiCountPerCell,
                        MultiItemSize = item.MultiItemSize,
                        Size = new int2(item.Size.x, item.Size.y),
                        YOffset = item.Offset.y,
                        ObjectType = item.ObjectType,
                        Domain = item.Domain,
                        PurposeFlags = item.PurposeFlags,
                        RequiredTechLevel = item.RequiredTechLevel,
                        AllowedTerrains = item.GetAllowedTerrainMask(),
                        PlacementRules = item.GetPlacementRuleFlags(),
                    });
                }

                if (skipped > 0)
                {
                    Debug.LogWarning(
                        $"[PrefabRegistryAuthoring] '{source.name}': " +
                        $"skipped {skipped} items with null Prefab.");
                }
            }

        }

        GamePrefabRegistry ResolveSingleSource()
        {
            if (Source != null)
                return Source;

            return GetFirstLegacySource();
        }

        GamePrefabRegistry GetFirstLegacySource()
        {
            if (Sources == null)
                return null;

            for (int i = 0; i < Sources.Count; i++)
                if (Sources[i] != null)
                    return Sources[i];

            return null;
        }
    }
}
