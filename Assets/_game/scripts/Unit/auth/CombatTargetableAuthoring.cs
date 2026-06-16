using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Unit
{
    public sealed class CombatTargetableAuthoring : MonoBehaviour
    {
        [Header("Identity")]
        public string DisplayName = "Target";

        [Header("Team")]
        [Tooltip("Shared alliance/team group id. Use the same value for players that belong to one team.")]
        public int TeamId = -1;

        [FormerlySerializedAs("TeamIndex")]
        [Range(0, 7)]
        public int LocalId;

        public TeamMask EnemyTeams;
        public TeamMask AllyTeams;
        public TeamMask NeutralTeams;
        public bool IsPlayerTeam;
        public bool IsPlayer;

        [Header("Target")]
        public CombatTargetMask TargetType = CombatTargetMask.Building;
        [Min(0.01f)]
        public float MaxHealth = 300f;
        public Vector3 CombatBoundsSize = Vector3.one;
        [Range(0f, 1f)]
        public float CombatAimHeightRatio = 0.5f;
        public bool DestroyOnDeath = true;

        class Baker : Baker<CombatTargetableAuthoring>
        {
            public override void Bake(CombatTargetableAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                float maxHealth = math.max(0.01f, authoring.MaxHealth);
                FixedString64Bytes displayName = ResolveDisplayName(authoring.DisplayName, authoring.gameObject.name);
                AddComponent(entity, new UnitDisplayName
                {
                    Value = displayName,
                });
                TeamInfoData teamInfo = TeamInfoData.CreateTeamInfo(
                    authoring.LocalId,
                    ResolveEnemyTeams(authoring.LocalId, authoring.EnemyTeams, authoring.AllyTeams, authoring.NeutralTeams),
                    authoring.AllyTeams,
                    authoring.NeutralTeams,
                    authoring.IsPlayerTeam,
                    authoring.IsPlayer);
                teamInfo.TeamID = ResolveTeamId(authoring.TeamId, authoring.LocalId);
                teamInfo.LocalID = authoring.LocalId;
                AddComponent(entity, teamInfo);
                AddComponent(entity, new CombatTargetable
                {
                    TargetType = authoring.TargetType,
                });
                AddComponent(entity, new CombatHealth
                {
                    Health = maxHealth,
                    MaxHealth = maxHealth,
                });
                AddComponent(entity, new CombatTargetBounds
                {
                    Size = ResolveCombatBoundsSize(authoring.CombatBoundsSize),
                    AimHeightRatio = math.saturate(authoring.CombatAimHeightRatio),
                });
                if (authoring.DestroyOnDeath)
                    AddComponent<CombatDestroyOnDeath>(entity);
            }

            static float3 ResolveCombatBoundsSize(Vector3 authoredSize)
            {
                return new float3(
                    math.max(0.01f, authoredSize.x),
                    math.max(0.01f, authoredSize.y),
                    math.max(0.01f, authoredSize.z));
            }

            static int ResolveTeamId(int teamId, int localId)
            {
                return teamId >= 0 ? teamId : localId;
            }

            static TeamMask ResolveEnemyTeams(
                int localId,
                TeamMask enemyTeams,
                TeamMask allyTeams,
                TeamMask neutralTeams)
            {
                if (enemyTeams != TeamMask.None ||
                    allyTeams != TeamMask.None ||
                    neutralTeams != TeamMask.None)
                    return enemyTeams;

                int clampedLocalId = math.clamp(localId, 0, 7);
                return (TeamMask)(((1 << 8) - 1) & ~(1 << clampedLocalId));
            }

            static FixedString64Bytes ResolveDisplayName(string displayName, string fallback)
            {
                return new FixedString64Bytes(string.IsNullOrWhiteSpace(displayName) ? fallback : displayName);
            }
        }
    }
}
