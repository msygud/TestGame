using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Unit
{
    public sealed class CombatWeaponAuthoring : MonoBehaviour
    {
        public UnitAuthoring Owner;
        [Min(0)]
        public int WeaponIndex;
        [Min(0.01f)]
        public float Range = 6f;
        public bool StartsEnabled = true;
        [Min(0)]
        public int UnlockGroupId;
        [Min(0f)]
        public float Damage = 10f;
        [Min(0.01f)]
        public float Cooldown = 1f;
        public CombatTargetMask TargetMask = CombatTargetMask.Ground | CombatTargetMask.Building;
        public bool RequiresLineOfSight = true;
        public bool RequiresStoppedToFire;
        [Range(1f, 360f)]
        public float FireArcDegrees = 360f;
        public bool RequiresSetupToFire;
        [Min(0f)]
        public float SetupTime;
        [Min(0f)]
        public float PackTime;
        public bool BlocksMovementWhileSetup;
        [FormerlySerializedAs("PrimaryEngagementWeapon")]
        public bool IsPrimaryEngagementWeapon;
        [Min(0f)]
        public float TurretTurnSpeedDegrees = 720f;
        public CombatWeaponMuzzleAuthoring[] Muzzles;

        class Baker : Baker<CombatWeaponAuthoring>
        {
            public override void Bake(CombatWeaponAuthoring authoring)
            {
                UnitAuthoring ownerAuthoring = authoring.Owner != null
                    ? authoring.Owner
                    : authoring.GetComponentInParent<UnitAuthoring>();
                if (ownerAuthoring == null)
                {
                    Debug.LogWarning(
                        $"CombatWeaponAuthoring on '{authoring.gameObject.name}' has no UnitAuthoring owner.");
                    return;
                }

                if (authoring.TargetMask == CombatTargetMask.None)
                    return;

                Entity weapon = GetEntity(TransformUsageFlags.Dynamic);
                Entity owner = GetEntity(ownerAuthoring, TransformUsageFlags.Dynamic);
                float setupTime = math.max(0f, authoring.SetupTime);
                float fireArcDegrees = math.clamp(authoring.FireArcDegrees <= 0f ? 360f : authoring.FireArcDegrees, 1f, 360f);
                float halfArcRadians = math.radians(fireArcDegrees * 0.5f);

                AddComponent(weapon, new CombatWeaponOwner
                {
                    Owner = owner,
                    WeaponIndex = math.max(0, authoring.WeaponIndex),
                });
                AddComponent(weapon, new CombatWeapon
                {
                    Range = math.max(0.01f, authoring.Range),
                    Damage = math.max(0f, authoring.Damage),
                    TargetMask = authoring.TargetMask,
                });
                AddComponent<CombatWeaponEnabled>(weapon);
                SetComponentEnabled<CombatWeaponEnabled>(weapon, authoring.StartsEnabled);
                AddComponent(weapon, new CombatWeaponUnlockGroup
                {
                    GroupId = math.max(0, authoring.UnlockGroupId),
                });
                AddComponent(weapon, new CombatWeaponCooldown
                {
                    Duration = math.max(0.01f, authoring.Cooldown),
                    Remaining = 0f,
                });
                AddComponent(weapon, new CombatWeaponSetupState
                {
                    Progress = authoring.RequiresSetupToFire && setupTime <= 0f ? 1f : 0f,
                });
                AddComponent(weapon, new CombatWeaponReadyState
                {
                    Target = Entity.Null,
                    BlockedReasons = CombatWeaponBlockReason.NoTarget,
                    CanFire = 0,
                });
                AddComponent(weapon, new CombatWeaponFireArc
                {
                    FireArcCosine = fireArcDegrees >= 360f ? -1f : math.cos(halfArcRadians),
                });
                AddComponent<TurretWeapon>(weapon);
                if (authoring.IsPrimaryEngagementWeapon)
                    AddComponent<PrimaryEngagementWeapon>(weapon);
                AddComponent(weapon, new CombatWeaponTurretReference
                {
                    Turret = weapon,
                });
                AddComponent(weapon, new CombatWeaponTurretAim
                {
                    TurnSpeedRadians = math.radians(ResolveTurretTurnSpeedDegrees(authoring.TurretTurnSpeedDegrees)),
                });
                int muzzleCount = AddMuzzleReferences(authoring, weapon);
                if (muzzleCount > 0)
                {
                    AddComponent(weapon, new CombatWeaponMuzzleCycle
                    {
                        NextMuzzleIndex = 0,
                    });
                }

                if (authoring.RequiresLineOfSight)
                    AddComponent<RequiresLineOfSight>(weapon);
                if (authoring.RequiresStoppedToFire)
                    AddComponent<RequiresStoppedToFire>(weapon);
                if (authoring.BlocksMovementWhileSetup)
                    AddComponent<BlocksOwnerMovementWhileSetup>(weapon);
                if (authoring.RequiresSetupToFire)
                {
                    AddComponent(weapon, new RequiresWeaponSetup
                    {
                        SetupTime = setupTime,
                        PackTime = math.max(0f, authoring.PackTime),
                    });
                }
            }

            static float ResolveTurretTurnSpeedDegrees(float turnSpeedDegrees)
            {
                return turnSpeedDegrees <= 0f ? 720f : turnSpeedDegrees;
            }

            int AddMuzzleReferences(CombatWeaponAuthoring authoring, Entity weapon)
            {
                var buffer = AddBuffer<CombatWeaponMuzzleReference>(weapon);
                var muzzleAuthorings = new List<CombatWeaponMuzzleAuthoring>();
                var knownMuzzles = new HashSet<CombatWeaponMuzzleAuthoring>();
                AddExplicitMuzzles(authoring.Muzzles, muzzleAuthorings, knownMuzzles);
                AddExplicitMuzzles(
                    authoring.GetComponentsInChildren<CombatWeaponMuzzleAuthoring>(true),
                    muzzleAuthorings,
                    knownMuzzles);
                muzzleAuthorings.Sort(
                    (a, b) => math.max(0, a.MuzzleIndex).CompareTo(math.max(0, b.MuzzleIndex)));

                for (int i = 0; i < muzzleAuthorings.Count; i++)
                {
                    CombatWeaponAuthoring muzzleWeapon = ResolveMuzzleWeapon(muzzleAuthorings[i]);
                    if (muzzleWeapon != authoring)
                        continue;

                    buffer.Add(new CombatWeaponMuzzleReference
                    {
                        Muzzle = GetEntity(muzzleAuthorings[i], TransformUsageFlags.Dynamic),
                        MuzzleIndex = math.max(0, muzzleAuthorings[i].MuzzleIndex),
                    });
                }

                return buffer.Length;
            }

            static void AddExplicitMuzzles(
                CombatWeaponMuzzleAuthoring[] source,
                List<CombatWeaponMuzzleAuthoring> destination,
                HashSet<CombatWeaponMuzzleAuthoring> knownMuzzles)
            {
                if (source == null)
                    return;

                for (int i = 0; i < source.Length; i++)
                {
                    CombatWeaponMuzzleAuthoring muzzle = source[i];
                    if (muzzle == null || !knownMuzzles.Add(muzzle))
                        continue;

                    destination.Add(muzzle);
                }
            }

            static CombatWeaponAuthoring ResolveMuzzleWeapon(CombatWeaponMuzzleAuthoring muzzle)
            {
                return muzzle.Weapon != null
                    ? muzzle.Weapon
                    : muzzle.GetComponentInParent<CombatWeaponAuthoring>();
            }
        }
    }

}
