using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    class NameAuth : MonoBehaviour
    {
        public string Name;
    }

    class NameAuthBaker : Baker<NameAuth>
    {
        public override void Bake(NameAuth authoring)
        {
#if UNITY_EDITOR
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            //AddComponent(entity, new UnitName { Name = authoring.Name });
#endif
        }
    }
}
