using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Unit
{
    public sealed class UnitNavigationGridAuthoring : MonoBehaviour
    {
        public Vector2Int Size = new Vector2Int(64, 64);

        [Min(0.25f)]
        public float CellSize = 1f;

        public Vector3 Origin = Vector3.zero;

        [Min(64)]
        public int MaxSearchNodes = 4096;

        class Baker : Baker<UnitNavigationGridAuthoring>
        {
            public override void Bake(UnitNavigationGridAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new UnitNavigationGrid
                {
                    Origin = new float3(authoring.Origin.x, authoring.Origin.y, authoring.Origin.z),
                    Size = new int2(math.max(1, authoring.Size.x), math.max(1, authoring.Size.y)),
                    CellSize = math.max(0.25f, authoring.CellSize),
                    MaxSearchNodes = math.max(64, authoring.MaxSearchNodes),
                });
            }
        }
    }
}
