using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RoadKeyAuthoring — 도로 키 매핑 베이킹
    //
    //  서브씬에 하나만 배치.
    //  등록된 RoadPrefabRegistry SO들의 Entries를 BakedRoadKey 버퍼로 구워 넣는다.
    //  이후 RoadKeyBuildSystem이 이 버퍼를 읽어 RoadKeyLookup 싱글톤을 구성.
    //
    //  SO가 추가/변경되면 이 Authoring에 레퍼런스를 추가하고 SubScene Re-bake.
    //  (NeedMappingAuthoring과 동일한 패턴.)
    // ══════════════════════════════════════════════════════════════════════════
    public class RoadKeyAuthoring : MonoBehaviour
    {
        [Tooltip("도로 (팩션,방향)→MainKey 매핑을 가진 RoadPrefabRegistry SO들")]
        public List<RoadPrefabRegistry> Registries = new();

        class Baker : Baker<RoadKeyAuthoring>
        {
            public override void Bake(RoadKeyAuthoring a)
            {
                var e   = GetEntity(TransformUsageFlags.None);
                var buf = AddBuffer<BakedRoadKey>(e);

                foreach (var reg in a.Registries)
                {
                    if (reg == null) continue;
                    foreach (var entry in reg.Entries)
                    {
                        if (entry == null) continue;
                        if (entry.Dir == RoadDir.None) continue;       // 0은 도로 아님
                        if (entry.MainKey == MainKeyRange.NullKey) continue; // 무효 키 스킵

                        buf.Add(new BakedRoadKey
                        {
                            FactionId = entry.FactionId,
                            Dir       = entry.Dir,
                            MainKey   = entry.MainKey,
                            Size      = (byte)reg.DefaultSize,
                        });
                    }
                }
            }
        }
    }
}
