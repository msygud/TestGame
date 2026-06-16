using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
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
