using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Serialization;

namespace Game.Unit
{
    public sealed class UnitAuthoring : MonoBehaviour
    {
        [Min(0.01f)]
        public float MoveSpeed = 6f;

        [Header("Identity")]
        public string DisplayName = "Unit";

        [Min(0f)]
        public float TurnSpeedDegrees = 720f;

        [Min(0f)]
        public float StopDistance = 0.15f;

        [Header("Footprint")]
        public Vector2 FootprintSize = new Vector2(1f, 1f);

        [Min(0.01f)]
        public float FootprintRadius = 0.5f;

        [Min(0f)]
        public float SeparationWeight = 1f;

        [Range(0f, 1f)]
        public float SettledPushScale = 0.08f;

        [Range(0f, 1f)]
        public float WorkingPushScale = 0.02f;

        [Range(0f, 1f)]
        public float AnchoredPushScale = 0f;

        [Header("State")]
        public UnitActivityKind InitialActivity = UnitActivityKind.Settled;

        [Header("Avoidance")]
        [Min(0.01f)]
        public float AvoidancePriority = 1f;

        [Range(0f, 2f)]
        public float YieldStrength = 1f;

        [Min(0f)]
        public float SoftAvoidanceDistance = 0.75f;

        [Header("Selection")]
        [Min(0.01f)]
        public float SelectionWorldRadius = 0.6f;

        [Min(1f)]
        public float SelectionScreenPixels = 28f;

        [Min(0.01f)]
        public float SelectionRingRadius = 0.8f;

        public Vector2 SelectionFootprintSize = new Vector2(1f, 1f);

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

        [Header("Command Group")]
        public int CommandGroupId;

        [Header("Combat")]
        public CombatTargetMask TargetType = CombatTargetMask.Ground;
        [Min(0.01f)]
        public float MaxHealth = 100f;
        public Vector3 CombatBoundsSize = Vector3.zero;
        [Range(0f, 1f)]
        public float CombatAimHeightRatio = 0.5f;
        public bool DestroyOnDeath = true;

        class Baker : Baker<UnitAuthoring>
        {
            public override void Bake(UnitAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                FixedString64Bytes displayName = ResolveDisplayName(authoring.DisplayName, authoring.gameObject.name);

                AddComponent<UnitTag>(entity);
                AddComponent(entity, new UnitDisplayName
                {
                    Value = displayName,
                });
                AddComponent(entity, new UnitMoveSpeed
                {
                    MetersPerSecond = math.max(0.01f, authoring.MoveSpeed),
                    TurnSpeedRadians = math.radians(math.max(0f, authoring.TurnSpeedDegrees)),
                });
                float2 footprintSize = ResolveFootprintSize(authoring);
                AddComponent(entity, new UnitFootprint
                {
                    Size = footprintSize,
                    Radius = ResolveFootprintRadius(footprintSize, authoring.FootprintRadius),
                    SeparationWeight = math.max(0f, authoring.SeparationWeight),
                    SettledPushScale = math.saturate(authoring.SettledPushScale),
                    WorkingPushScale = math.saturate(authoring.WorkingPushScale),
                    AnchoredPushScale = math.saturate(authoring.AnchoredPushScale),
                });
                AddComponent(entity, new UnitMoveTarget
                {
                    Position = float3.zero,
                    StopDistance = math.max(0f, authoring.StopDistance),
                    RepathCount = 0,
                    PathStatus = UnitPathStatus.Direct,
                    HasTarget = 0,
                    RepathRequested = 0,
                });
                AddComponent(entity, new UnitMotionState
                {
                    Velocity = float3.zero,
                    DesiredForward = new float3(0f, 0f, 1f),
                    LastTargetDistance = float.MaxValue,
                    StuckTime = 0f,
                    IsMoving = 0,
                });
                AddComponent(entity, new UnitPathSteeringState
                {
                    DetourPosition = float3.zero,
                    DetourRefreshTime = 0f,
                    HasDetour = 0,
                });
                AddComponent(entity, new UnitMovementBlocker
                {
                    Reasons = UnitMovementBlockReason.None,
                });
                AddComponent(entity, new UnitObstacleAvoidanceOffset
                {
                    Value = float3.zero,
                });
                AddComponent(entity, new UnitSeparationOffset
                {
                    Value = float3.zero,
                });
                AddComponent(entity, new UnitAvoidancePriority
                {
                    BasePriority = math.max(0.01f, authoring.AvoidancePriority),
                    YieldStrength = math.max(0f, authoring.YieldStrength),
                    SoftAvoidanceDistance = math.max(0f, authoring.SoftAvoidanceDistance),
                });
                AddComponent(entity, new UnitYieldState
                {
                    Cooldown = 0f,
                });
                AddBuffer<UnitPathWaypoint>(entity);
                AddComponent(entity, new UnitActivityState
                {
                    Value = ResolveInitialActivity(authoring.InitialActivity),
                    TimeInState = 0f,
                });
                AddComponent(entity, new UnitCommandState
                {
                    Kind = UnitCommandKind.None,
                    TargetEntity = Entity.Null,
                    TargetPosition = float3.zero,
                    HasTargetEntity = 0,
                });

                AddComponent<SelectableUnit>(entity);
                AddComponent(entity, new UnitSelectionRadius
                {
                    WorldRadius = math.max(0.01f, authoring.SelectionWorldRadius),
                    ScreenPixels = math.max(1f, authoring.SelectionScreenPixels),
                    RingRadius = math.max(0.01f, authoring.SelectionRingRadius),
                });
                AddComponent<SelectedUnit>(entity);
                SetComponentEnabled<SelectedUnit>(entity, false);

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
                AddComponent(entity, new UnitCommandGroupMember
                {
                    GroupId = math.max(0, authoring.CommandGroupId),
                });
                AddComponent(entity, new CombatTargetable
                {
                    TargetType = authoring.TargetType,
                });
                AddComponent(entity, new CombatHealth
                {
                    Health = math.max(0.01f, authoring.MaxHealth),
                    MaxHealth = math.max(0.01f, authoring.MaxHealth),
                });
                AddComponent(entity, new CombatTargetBounds
                {
                    Size = ResolveCombatBoundsSize(authoring.CombatBoundsSize, footprintSize),
                    AimHeightRatio = math.saturate(authoring.CombatAimHeightRatio),
                });
                if (authoring.DestroyOnDeath)
                    AddComponent<CombatDestroyOnDeath>(entity);

                AddComponent(entity, new CombatAttackTarget
                {
                    Target = Entity.Null,
                    ApproachRefreshTime = 0f,
                    HasTarget = 0,
                });
                AddComponent(entity, new CombatEngagementDecision
                {
                    PreferredRange = 0f,
                    TargetPosition = float3.zero,
                    ShouldApproach = 0,
                    HasUsableWeapon = 0,
                });
                AddComponent(entity, new CombatLineOfSightState
                {
                    Target = Entity.Null,
                    HasLineOfSight = 0,
                    HasState = 0,
                });
                AddComponent(entity, new CombatMoveIntent
                {
                    Kind = CombatMoveIntentKind.None,
                    TargetPosition = float3.zero,
                    StopDistance = 0f,
                });
            }

            static float2 ResolveFootprintSize(UnitAuthoring authoring)
            {
                var size = authoring.FootprintSize;
                if (size.x <= 0f || size.y <= 0f)
                    size = authoring.SelectionFootprintSize;

                return new float2(
                    math.max(0.01f, size.x),
                    math.max(0.01f, size.y));
            }

            static float ResolveFootprintRadius(float2 size, float authoredRadius)
            {
                float sizeRadius = math.length(size) * 0.5f;
                return math.max(math.max(0.01f, authoredRadius), sizeRadius);
            }

            static float3 ResolveCombatBoundsSize(Vector3 authoredSize, float2 footprintSize)
            {
                return new float3(
                    authoredSize.x > 0f ? authoredSize.x : footprintSize.x,
                    authoredSize.y > 0f ? authoredSize.y : 1f,
                    authoredSize.z > 0f ? authoredSize.z : footprintSize.y);
            }

            static UnitActivityKind ResolveInitialActivity(UnitActivityKind activity)
            {
                return activity == UnitActivityKind.Moving
                    ? UnitActivityKind.Settled
                    : activity;
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
