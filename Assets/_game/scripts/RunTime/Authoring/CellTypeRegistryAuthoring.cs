using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace CitySim
{
    // ══════════════════════════════════════════════════════════════
    //  CellTypeRegistryAuthoring  (Authoring MonoBehaviour)
    //
    //  SubScene에 배치. 등록된 모든 CellTypeRegistry SO를
    //  하나의 BakedCellTypeEntry 버퍼로 통합하여 굽는다.
    //  (NeedMappingAuthoring / RoadKeyAuthoring과 동일한 패턴.
    //   DLC별로 registry를 분리해 추가하고 싶을 때 여러 개 등록 가능.)
    //
    //  SO 추가/변경 후 Re-bake 필요.
    // ══════════════════════════════════════════════════════════════
    public class CellTypeRegistryAuthoring : MonoBehaviour
    {
        [Tooltip("모든 CellTypeRegistry SO 목록. 추가/변경 후 Re-bake.")]
        public List<CellTypeRegistry> Registries = new();

        class Baker : Baker<CellTypeRegistryAuthoring>
        {
            public override void Bake(CellTypeRegistryAuthoring authoring)
            {
                var e      = GetEntity(TransformUsageFlags.None);
                var buffer = AddBuffer<BakedCellTypeEntry>(e);

                int totalTypes = 0;

                foreach (var registry in authoring.Registries)
                {
                    if (registry == null) continue;
                    DependsOn(registry);

                    foreach (var def in registry.Types)
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
                        totalTypes++;
                    }
                }

                Debug.Log($"[Baker] CellTypeRegistry 베이킹 완료: {totalTypes}개 타입");
            }
        }
    }
}
