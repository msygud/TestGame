using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Unit
{
    public sealed class ObstacleFootprintAuthoring : MonoBehaviour
    {
        public Vector2 FootprintSize = new Vector2(2f, 2f);

        [Min(0.01f)]
        public float FootprintRadius = 1f;

        [Min(0f)]
        public float ExtraPadding = 0.25f;

        class Baker : Baker<ObstacleFootprintAuthoring>
        {
            public override void Bake(ObstacleFootprintAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                float2 size = ResolveFootprintSize(authoring.FootprintSize);
                AddComponent(entity, new ObstacleFootprint
                {
                    Size = size,
                    Radius = math.max(math.max(0.01f, authoring.FootprintRadius), math.length(size) * 0.5f),
                    ExtraPadding = math.max(0f, authoring.ExtraPadding),
                });
            }

            static float2 ResolveFootprintSize(Vector2 size)
            {
                return new float2(
                    math.max(0.01f, size.x),
                    math.max(0.01f, size.y));
            }
        }
    }
}
