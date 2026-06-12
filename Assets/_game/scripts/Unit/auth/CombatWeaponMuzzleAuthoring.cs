using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Unit
{
    public sealed class CombatWeaponMuzzleAuthoring : MonoBehaviour
    {
        public CombatWeaponAuthoring Weapon;
        [Min(0)]
        public int MuzzleIndex;

        class Baker : Baker<CombatWeaponMuzzleAuthoring>
        {
            public override void Bake(CombatWeaponMuzzleAuthoring authoring)
            {
                CombatWeaponAuthoring weaponAuthoring = authoring.Weapon != null
                    ? authoring.Weapon
                    : authoring.GetComponentInParent<CombatWeaponAuthoring>();
                if (weaponAuthoring == null)
                {
                    Debug.LogWarning(
                        $"CombatWeaponMuzzleAuthoring on '{authoring.gameObject.name}' has no CombatWeaponAuthoring parent.");
                    return;
                }

                Entity muzzle = GetEntity(TransformUsageFlags.Dynamic);
                Entity weapon = GetEntity(weaponAuthoring, TransformUsageFlags.Dynamic);
                AddComponent(muzzle, new CombatWeaponMuzzle
                {
                    Weapon = weapon,
                    MuzzleIndex = math.max(0, authoring.MuzzleIndex),
                });
            }
        }
    }
}
