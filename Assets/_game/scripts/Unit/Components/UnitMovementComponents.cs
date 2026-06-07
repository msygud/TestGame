using Unity.Entities;
using Unity.Mathematics;

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

    public struct UnitMoveTarget : IComponentData
    {
        public float3 Position;
        public float StopDistance;
        public byte HasTarget;

        public static UnitMoveTarget None => new UnitMoveTarget
        {
            Position = float3.zero,
            StopDistance = 0.1f,
            HasTarget = 0,
        };
    }

    public struct UnitMotionState : IComponentData
    {
        public float3 Velocity;
        public float3 DesiredForward;
        public byte IsMoving;
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

    public struct MoveOrderRequest : IComponentData
    {
        public Entity Unit;
        public float3 Target;
        public float StopDistance;
    }
}
