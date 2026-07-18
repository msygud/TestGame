using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using System;

namespace Game.Unit
{
    public struct UnitTag : IComponentData
    {
    }

    public struct SelectableUnit : IComponentData
    {
    }

    public struct SelectedUnit : IComponentData, IEnableableComponent
    {
    }

    public struct UnitMoveSpeed : IComponentData
    {
        public float MetersPerSecond;
        public float TurnSpeedRadians;
    }

    public struct UnitFootprint : IComponentData
    {
        public float2 Size;
        public float Radius;
        public float SeparationWeight;
        public float SettledPushScale;
        public float WorkingPushScale;
        public float AnchoredPushScale;
    }

    public struct ObstacleFootprint : IComponentData
    {
        public float2 Size;
        public float Radius;
        public float ExtraPadding;
    }

    public struct UnitMoveTarget : IComponentData
    {
        public float3 Position;
        public float StopDistance;
        public int RepathCount;
        public UnitPathStatus PathStatus;
        public byte HasTarget;
        public byte RepathRequested;

        public static UnitMoveTarget None => new UnitMoveTarget
        {
            Position = float3.zero,
            StopDistance = 0.1f,
            RepathCount = 0,
            PathStatus = UnitPathStatus.Direct,
            HasTarget = 0,
            RepathRequested = 0,
        };
    }

    public enum UnitPathStatus : byte
    {
        Direct,
        PathReady,
        PathPartial,
        PathFailed,
    }

    public struct UnitMotionState : IComponentData
    {
        public float3 Velocity;
        public float3 DesiredForward;
        public float LastTargetDistance;
        public float StuckTime;
        public byte IsMoving;
    }

    public struct UnitPathSteeringState : IComponentData
    {
        public float3 DetourPosition;
        public float DetourRefreshTime;
        public byte HasDetour;
    }

    [Flags]
    public enum UnitMovementBlockReason : uint
    {
        None = 0,
        WeaponSetup = 1u << 0,
    }

    public struct UnitMovementBlocker : IComponentData
    {
        public UnitMovementBlockReason Reasons;
    }

    public struct UnitObstacleAvoidanceOffset : IComponentData
    {
        public float3 Value;
    }

    public struct UnitSeparationOffset : IComponentData
    {
        public float3 Value;
    }

    public struct UnitAvoidancePriority : IComponentData
    {
        public float BasePriority;
        public float YieldStrength;
        public float SoftAvoidanceDistance;
    }

    public struct UnitYieldState : IComponentData
    {
        public float Cooldown;
    }

    public struct UnitPathWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    public struct UnitNavigationGrid : IComponentData
    {
        public float3 Origin;
        public int2 Size;
        public float CellSize;
        public int MaxSearchNodes;
    }

    /// <summary>
    /// 수상 유닛 태그(2026-07-19 해상 이동) — 이동 도메인 = 물.
    ///   · 있음: 경로 차단 그리드가 "물 아닌 셀 = 차단"으로 반전(항해).
    ///   · 없음(기본 = 지상): "물 셀 = 차단" — 지상 유닛의 도하 차단도 이 도입과 함께 활성.
    /// 유조선·군함 공통 기반. 물 정보는 UnitWaterMask(지형 정적 → 맵 로드 후 1회 빌드).
    /// </summary>
    public struct NavalUnit : IComponentData { }

    /// <summary>
    /// 내비 그리드 해상도의 물 마스크 싱글톤(1=물). 지형은 맵 로드 시 고정이므로
    /// UnitWaterMaskBuildSystem이 1회 빌드(지형 셀 수 변경 시 재빌드). 경로 차단
    /// 그리드 빌드(UnitPathfinding.BuildBlockedGrid)가 도메인에 따라 소비.
    /// 수명주기: UnitWaterMaskBuildSystem OnCreate/OnDestroy.
    /// </summary>
    public struct UnitWaterMask : IComponentData
    {
        public NativeArray<byte> Water;   // grid.Size.x * grid.Size.y, 1=물
        public int2 Size;                 // 빌드 당시 grid.Size(불일치 = 미사용 가드)

        public readonly bool IsUsable(int2 gridSize)
            => Water.IsCreated && Size.Equals(gridSize);
    }

    public enum UnitActivityKind : byte
    {
        Moving,
        Settled,
        Working,
        Anchored,
    }

    public struct UnitActivityState : IComponentData
    {
        public UnitActivityKind Value;
        public float TimeInState;
    }

    public struct UnitSelectionRadius : IComponentData
    {
        public float WorldRadius;
        public float ScreenPixels;
        public float RingRadius;
    }

    public struct UnitDisplayName : IComponentData
    {
        public FixedString64Bytes Value;
    }

    public struct UnitDisplayNameSynced : IComponentData
    {
    }

    public enum UnitCommandKind : byte
    {
        None,
        ForceMove,
        ForceAttack,
        AttackMove,
        IdleAutoEngage,
    }

    public struct UnitCommandState : IComponentData
    {
        public UnitCommandKind Kind;
        public Entity TargetEntity;
        public float3 TargetPosition;
        public byte HasTargetEntity;
    }

    public struct MoveOrderRequest : IComponentData
    {
        public Entity Unit;
        public float3 Target;
        public float StopDistance;
        public int RepathCount;
        public UnitCommandKind CommandKind;
        public byte SkipPathfinding;
    }

    public struct MoveOrderPathWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    public struct UnitDirectMoveOrderBatch : IComponentData
    {
    }

    public struct UnitDirectMoveOrderElement : IBufferElementData
    {
        public Entity Unit;
        public float3 Target;
        public float StopDistance;
        public UnitCommandKind CommandKind;
    }

    public struct SelectedUnitMoveOrderRequest : IComponentData
    {
        public float3 Target;
        public float3 FormationForward;
        public float StopDistance;
        public float FormationSpacing;
        public int UnitCount;
        public UnitCommandKind CommandKind;
    }

    public struct SelectedUnitAttackOrderRequest : IComponentData
    {
        public Entity Target;
        public UnitCommandKind CommandKind;
    }

    public struct UnitCommandGroupMember : IComponentData
    {
        public int GroupId;
    }

    public struct UnitGroupMoveOrderRequest : IComponentData
    {
        public int LocalId;
        public int GroupId;
        public float3 Target;
        public float3 FormationForward;
        public float StopDistance;
        public float FormationSpacing;
        public int UnitCount;
        public UnitCommandKind CommandKind;
    }

    public struct UnitGroupAttackOrderRequest : IComponentData
    {
        public int LocalId;
        public int GroupId;
        public Entity Target;
        public UnitCommandKind CommandKind;
    }

    [Flags]
    public enum CombatTargetMask : uint
    {
        None = 0,
        Ground = 1u << 0,
        Air = 1u << 1,
        Building = 1u << 2,
        Naval = 1u << 3,
        Resource = 1u << 4,
    }

    // IEnableableComponent — 타겟 가능 여부를 구조 변경 없이 토글(중립 도로 등 가변 타겟).
    //   ⚠ 쿼리(WithAll)는 disabled를 자동 제외하지만 ComponentLookup.HasComponent는 true를
    //   반환하므로, 룩업 기반 유효성 검사는 IsComponentEnabled를 함께 확인해야 한다.
    public struct CombatTargetable : IComponentData, IEnableableComponent
    {
        public CombatTargetMask TargetType;
    }

    public struct CombatHealth : IComponentData
    {
        public float Health;
        public float MaxHealth;
    }

    public struct CombatTargetBounds : IComponentData
    {
        public float3 Size;
        public float AimHeightRatio;
    }

    public struct CombatDestroyOnDeath : IComponentData
    {
    }

    public struct CombatDeadTag : IComponentData
    {
    }

    public struct CombatWeaponOwner : IComponentData
    {
        public Entity Owner;
        public int WeaponIndex;
    }

    public struct CombatWeapon : IComponentData
    {
        public float Range;
        public float Damage;
        public CombatTargetMask TargetMask;
    }

    public struct CombatWeaponEnabled : IComponentData, IEnableableComponent
    {
    }

    public struct CombatWeaponUnlockGroup : IComponentData
    {
        public int GroupId;
    }

    public struct CombatWeaponCooldown : IComponentData
    {
        public float Duration;
        public float Remaining;
    }

    public struct BodyForwardWeapon : IComponentData
    {
    }

    public struct TurretWeapon : IComponentData
    {
    }

    public struct PrimaryEngagementWeapon : IComponentData
    {
    }

    public struct CombatWeaponFireArc : IComponentData
    {
        public float FireArcCosine;
    }

    public struct CombatWeaponTurretReference : IComponentData
    {
        public Entity Turret;
    }

    public struct CombatWeaponTurretAim : IComponentData
    {
        public float TurnSpeedRadians;
    }

    public struct CombatWeaponMuzzle : IComponentData
    {
        public Entity Weapon;
        public int MuzzleIndex;
    }

    public struct CombatWeaponMuzzleReference : IBufferElementData
    {
        public Entity Muzzle;
        public int MuzzleIndex;
    }

    public struct CombatWeaponMuzzleCycle : IComponentData
    {
        public int NextMuzzleIndex;
    }

    public struct RequiresStoppedToFire : IComponentData
    {
    }

    public struct RequiresLineOfSight : IComponentData
    {
    }

    public struct RequiresWeaponSetup : IComponentData
    {
        public float SetupTime;
        public float PackTime;
    }

    public struct BlocksOwnerMovementWhileSetup : IComponentData
    {
    }

    public struct CombatWeaponSetupState : IComponentData
    {
        public float Progress;
    }

    public struct CombatWeaponDeployRotation : IComponentData
    {
        public Entity Weapon;
        public quaternion OffLocalRotation;
        public quaternion OnLocalRotation;
    }

    [Flags]
    public enum CombatWeaponBlockReason : uint
    {
        None = 0,
        NoOwner = 1u << 0,
        NoTarget = 1u << 1,
        InvalidTarget = 1u << 2,
        UnsupportedTargetType = 1u << 3,
        OutOfRange = 1u << 4,
        NeedStop = 1u << 5,
        NeedBodyAim = 1u << 6,
        NeedSetup = 1u << 7,
        Cooldown = 1u << 8,
        BlockedLineOfSight = 1u << 9,
        NeedTurretAim = 1u << 10,
    }

    public struct CombatWeaponReadyState : IComponentData
    {
        public Entity Target;
        public CombatWeaponBlockReason BlockedReasons;
        public byte CanFire;
    }

    public struct CombatAttackTarget : IComponentData
    {
        public Entity Target;
        public float ApproachRefreshTime;
        public byte HasTarget;
    }

    public struct CombatEngagementDecision : IComponentData
    {
        public float PreferredRange;
        public float3 TargetPosition;
        public byte ShouldApproach;
        public byte HasUsableWeapon;
    }

    public struct CombatLineOfSightState : IComponentData
    {
        public Entity Target;
        public byte HasLineOfSight;
        public byte HasState;
    }

    public enum CombatMoveIntentKind : byte
    {
        None,
        ApproachTarget,
        StopForAttack,
    }

    public struct CombatMoveIntent : IComponentData
    {
        public CombatMoveIntentKind Kind;
        public float3 TargetPosition;
        public float StopDistance;
    }

    public struct AttackOrderRequest : IComponentData
    {
        public Entity Attacker;
        public Entity Target;
        public UnitCommandKind CommandKind;
    }

    public struct CombatWeaponUnlockGroupRequest : IComponentData
    {
        public Entity Owner;
        public int GroupId;
    }

    public struct CombatWeaponFireRequest : IComponentData
    {
        public Entity Source;
        public Entity Target;
        public float3 SourcePosition;
        public float3 TargetPosition;
        public float Damage;
        public int WeaponIndex;
    }

    public struct CombatDamageRequest : IComponentData
    {
        public Entity Source;
        public Entity Target;
        public float Damage;
    }
}
