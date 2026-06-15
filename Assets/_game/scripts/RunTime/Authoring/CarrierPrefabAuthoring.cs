using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  CarrierPrefabAuthoring
    //
    //  SubScene 안의 GameObject에 이 컴포넌트를 붙이고
    //  CarrierPrefab에 작은 구/큐브 프리팹을 할당하면
    //  베이킹 시 CarrierPrefabSingleton 싱글톤이 생성된다.
    //
    //  사용:
    //    1. SubScene 내에 빈 GameObject 생성.
    //    2. 이 컴포넌트 부착.
    //    3. CarrierPrefab 필드에 소규모 메시 프리팹(예: 0.3m 크기 큐브) 할당.
    // ══════════════════════════════════════════════════════════════════════════
    public class CarrierPrefabAuthoring : MonoBehaviour
    {
        [Tooltip("운반자 비주얼 프리팹. 작은 구/큐브 등 단순한 메시 추천.")]
        public GameObject CarrierPrefab;

        class Baker : Baker<CarrierPrefabAuthoring>
        {
            public override void Bake(CarrierPrefabAuthoring a)
            {
                if (a.CarrierPrefab == null) return;
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent(e, new CarrierPrefabSingleton
                {
                    Prefab = GetEntity(a.CarrierPrefab, TransformUsageFlags.Dynamic),
                });
            }
        }
    }
}
