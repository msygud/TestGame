using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Unit
{
    public sealed class CombatWeaponDeployRotationAuthoring : MonoBehaviour
    {
        public CombatWeaponAuthoring Weapon;
        public Vector3 OffLocalEulerDegrees;
        public Vector3 OnLocalEulerDegrees;

        class Baker : Baker<CombatWeaponDeployRotationAuthoring>
        {
            public override void Bake(CombatWeaponDeployRotationAuthoring authoring)
            {
                CombatWeaponAuthoring weaponAuthoring = authoring.Weapon != null
                    ? authoring.Weapon
                    : authoring.GetComponentInParent<CombatWeaponAuthoring>();
                if (weaponAuthoring == null)
                {
                    Debug.LogWarning(
                        $"CombatWeaponDeployRotationAuthoring on '{authoring.gameObject.name}' has no CombatWeaponAuthoring weapon.");
                    return;
                }

                Entity deployPivot = GetEntity(TransformUsageFlags.Dynamic);
                Entity weapon = GetEntity(weaponAuthoring, TransformUsageFlags.Dynamic);
                AddComponent(deployPivot, new CombatWeaponDeployRotation
                {
                    Weapon = weapon,
                    OffLocalRotation = ToQuaternion(authoring.OffLocalEulerDegrees),
                    OnLocalRotation = ToQuaternion(authoring.OnLocalEulerDegrees),
                });
            }

            static quaternion ToQuaternion(Vector3 eulerDegrees)
            {
                return quaternion.EulerXYZ(math.radians(new float3(
                    eulerDegrees.x,
                    eulerDegrees.y,
                    eulerDegrees.z)));
            }
        }
    }
}
