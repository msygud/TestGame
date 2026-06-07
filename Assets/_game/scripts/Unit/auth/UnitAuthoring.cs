using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Game.Unit
{
    public sealed class UnitAuthoring : MonoBehaviour
    {
        [Min(0.01f)]
        public float MoveSpeed = 6f;

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

        [Header("Selection")]
        [Min(0.01f)]
        public float SelectionWorldRadius = 0.6f;

        [Min(1f)]
        public float SelectionScreenPixels = 28f;

        [Min(0.01f)]
        public float SelectionRingRadius = 0.8f;

        public Vector2 SelectionFootprintSize = new Vector2(1f, 1f);

        class Baker : Baker<UnitAuthoring>
        {
            public override void Bake(UnitAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent<UnitTag>(entity);
                AddComponent(entity, new UnitMoveSpeed
                {
                    MetersPerSecond = math.max(0.01f, authoring.MoveSpeed),
                    TurnSpeedRadians = math.radians(math.max(0f, authoring.TurnSpeedDegrees)),
                });
                AddComponent(entity, new UnitFootprint
                {
                    Size = ResolveFootprintSize(authoring),
                    Radius = math.max(0.01f, authoring.FootprintRadius),
                    SeparationWeight = math.max(0f, authoring.SeparationWeight),
                    SettledPushScale = math.saturate(authoring.SettledPushScale),
                    WorkingPushScale = math.saturate(authoring.WorkingPushScale),
                    AnchoredPushScale = math.saturate(authoring.AnchoredPushScale),
                });
                AddComponent(entity, new UnitMoveTarget
                {
                    Position = float3.zero,
                    StopDistance = math.max(0f, authoring.StopDistance),
                    HasTarget = 0,
                });
                AddComponent(entity, new UnitMotionState
                {
                    Velocity = float3.zero,
                    DesiredForward = new float3(0f, 0f, 1f),
                    IsMoving = 0,
                });
                AddComponent(entity, new UnitActivityState
                {
                    Value = ResolveInitialActivity(authoring.InitialActivity),
                    TimeInState = 0f,
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

            static UnitActivityKind ResolveInitialActivity(UnitActivityKind activity)
            {
                return activity == UnitActivityKind.Moving
                    ? UnitActivityKind.Settled
                    : activity;
            }
        }
    }
}
