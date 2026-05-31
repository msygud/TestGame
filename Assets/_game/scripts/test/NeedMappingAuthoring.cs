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

    // ══════════════════════════════════════════════════════════════════════════
    //  FactionAuthoring
    //
    //  팩션 엔티티를 서브씬에서 미리 구워 둔다.
    //  FactionId 0 = 공통, 1~8 = 개별 팩션.
    //  LookupBuildSystem이 이 엔티티들에 NeedLookupL2를 붙인다.
    // ══════════════════════════════════════════════════════════════════════════
    public class FactionAuthoring : MonoBehaviour
    {
        [Tooltip("0 = 공통, 1~8 = 팩션 ID")]
        public int FactionId;

        class Baker : Baker<FactionAuthoring>
        {
            public override void Bake(FactionAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent(e, new FactionId { Value = a.FactionId });
                // NeedLookupL2는 LookupBuildSystem에서 런타임에 부착
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PlayerVariantAuthoring
    //
    //  싱글플레이 싱글톤. 초기 VariantKey = 0.
    //  유저가 베리언트 선택창에서 바꾸면 이 싱글톤의 값을 업데이트.
    // ══════════════════════════════════════════════════════════════════════════
    public class PlayerVariantAuthoring : MonoBehaviour
    {
        public int InitialVariantKey = 0;

        class Baker : Baker<PlayerVariantAuthoring>
        {
            public override void Bake(PlayerVariantAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent(e, new PlayerVariantSetting { VariantKey = a.InitialVariantKey });
            }
        }
    }
}
