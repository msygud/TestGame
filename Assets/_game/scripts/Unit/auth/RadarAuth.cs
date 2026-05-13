using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    class RadarAuth : MonoBehaviour
    {
        public float Radius;
        public byte Strength;
    }

    class RadarAuthBaker : Baker<RadarAuth>
    {
        public override void Bake(RadarAuth authoring)
        {
            var unit = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent<Radar>(unit, new Radar
            {
                IsActivate = true,
                Strength = authoring.Strength,
                Range = authoring.Radius,
                Mode = RadarMode.BlipOnly
            });
        }
    }
}
