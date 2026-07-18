using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    /// <summary>
    /// 수상 유닛 authoring(2026-07-19) — 유조선·군함 프리팹에 부착.
    /// UnitAuthoring(이동 골격)과 함께 사용: 이 태그가 경로 도메인을 물로 바꾼다.
    /// </summary>
    public class NavalUnitAuthoring : MonoBehaviour
    {
        class Baker : Baker<NavalUnitAuthoring>
        {
            public override void Bake(NavalUnitAuthoring a)
            {
                var e = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<NavalUnit>(e);
            }
        }
    }
}
