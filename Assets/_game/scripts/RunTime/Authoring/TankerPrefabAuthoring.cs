using Unity.Entities;
using UnityEngine;

namespace CitySim.Authoring
{
    // ══════════════════════════════════════════════════════════════════════════
    //  TankerPrefabAuthoring (2026-07-19) — CarrierPrefabAuthoring 패턴.
    //
    //  사용:
    //    1. SubScene 내 빈 GameObject에 부착.
    //    2. TankerPrefab에 수상 유닛 프리팹 할당 — 반드시 UnitAuthoring(이동 골격) +
    //       NavalUnitAuthoring(물 도메인)이 붙어 있어야 한다.
    //    3. TankerSystem이 항만(WarehouseSeaRange>0)마다 1척 자동 스폰.
    //  미할당 시 해상 물류는 OffshorePushSystem 텔레포트 폴백으로 동작.
    // ══════════════════════════════════════════════════════════════════════════
    public class TankerPrefabAuthoring : MonoBehaviour
    {
        [Tooltip("유조선 프리팹 — UnitAuthoring + NavalUnitAuthoring 필수.")]
        public GameObject TankerPrefab;

        [Tooltip("적재량(품목 단위). 시추 Output을 이만큼씩 싣고 항만에 하역.")]
        public int Capacity = 40;

        class Baker : Baker<TankerPrefabAuthoring>
        {
            public override void Bake(TankerPrefabAuthoring a)
            {
                if (a.TankerPrefab == null) return;
                var e = GetEntity(TransformUsageFlags.None);
                AddComponent(e, new TankerPrefabSingleton
                {
                    Prefab   = GetEntity(a.TankerPrefab, TransformUsageFlags.Dynamic),
                    Capacity = Mathf.Max(1, a.Capacity),
                });
            }
        }
    }
}
