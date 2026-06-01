using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    class AuthInstanceID : MonoBehaviour
    {
        public uint InstanceID = 0;
    }

    class AuthInstanceIDBaker : Baker<AuthInstanceID>
    {
        public override void Bake(AuthInstanceID authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            //AddComponent(entity, new InstanceIDData { InstanceID = authoring.InstanceID });
        }
    }
}
