using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  NeedMappingAuthoring
    //
    //  서브씬에 하나만 배치.
    //  등록된 모든 GamePrefabRegistry SO의 NeedMaps[]를
    //  BakedNeedMapping 버퍼로 구워 넣는다.
    //
    //  이후 LookupBuildSystem이 이 버퍼를 읽어 팩션별 L2 테이블을 구성.
    //  SO가 추가/변경되면 이 Authoring에 레퍼런스를 추가하고 Re-bake.
    // ══════════════════════════════════════════════════════════════════════════
    public class NeedMappingAuthoring : MonoBehaviour
    {
        [Tooltip("NeedMaps[]를 가진 모든 GamePrefabRegistry SO를 여기에 등록")]
        public List<GamePrefabRegistry> Registries = new();

        class Baker : Baker<NeedMappingAuthoring>
        {
            public override void Bake(NeedMappingAuthoring a)
            {
                var e   = GetEntity(TransformUsageFlags.None);
                var buf = AddBuffer<BakedNeedMapping>(e);

                foreach (var reg in a.Registries)
                {
                    if (reg == null) continue;

                    foreach (var entry in reg.NeedMaps)
                    {
                        if (entry == null) continue;
                        buf.Add(new BakedNeedMapping
                        {
                            MainKey      = entry.MainKey,
                            NeedMask     = entry.NeedMask,
                            FactionFlags = entry.FactionFlags,
                        });
                    }
                }
            }
        }
    }
}
