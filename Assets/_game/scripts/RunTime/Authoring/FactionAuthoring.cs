using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
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
}
