using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    class AuthUnitTag : MonoBehaviour
    {

    }

    class AuthUnitTagBaker : Baker<AuthUnitTag>
    {
        public override void Bake(AuthUnitTag authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new UnitTag());
        }
    }
}
