using Unity.Entities;
using UnityEngine;

namespace Game.Unit
{
    public sealed class NameAuth : MonoBehaviour
    {
        public string Name;
    }

    class NameAuthBaker : Baker<NameAuth>
    {
        public override void Bake(NameAuth authoring)
        {
            // UnitAuthoring/CombatTargetableAuthoring now own UnitDisplayName baking.
            // Keep this legacy authoring inert so old prefabs do not add duplicate components.
        }
    }
}
