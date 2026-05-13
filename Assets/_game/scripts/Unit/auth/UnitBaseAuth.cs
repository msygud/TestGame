using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    class UnitBaseAuth : MonoBehaviour
    {

    }

    class UnitBaseAuthBaker : Baker<UnitBaseAuth>
    {
        public override void Bake(UnitBaseAuth authoring)
        {
            var unit = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent<InstanceIDData>(unit);
            AddComponent<DetectedByRadar>(unit);
            AddComponent<DetectedBySight>(unit);
            AddComponent<VisibleOnMinimapData>(unit);
            SetComponentEnabled<VisibleOnMinimapData>(unit, false);
            AddComponent<VisibleOnScreenData>(unit);
            SetComponentEnabled<VisibleOnScreenData>(unit, false);
            AddComponent<GridPositionData>(unit);
        }
    }
}
