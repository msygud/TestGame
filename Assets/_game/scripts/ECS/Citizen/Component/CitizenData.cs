using Unity.Entities;

namespace Game.Citizen
{
    public enum CitizenStressKind : byte
    {
        Hunger,
        Housing,
        Leisure,
        Safety,
        Commute,
        Employment,
        Fatigue
    }

    public enum CitizenAbilityKind : byte
    {
        PhysicalLabor,
        Service,
        Logistics,
        Research,
        Administration,
        Combat
    }

    public enum CitizenJobKind : byte
    {
        None,
        Farmer,
        FactoryWorker,
        Cook,
        LogisticsWorker,
        Researcher,
        Administrator,
        Soldier
    }

    public enum CitizenOutputKind : byte
    {
        None,
        Food,
        Goods,
        Service,
        Logistics,
        Research,
        Administration,
        Defense
    }

    public enum CitizenActionKind : byte
    {
        None,
        Eat,
        FindHousing,
        SeekLeisure,
        SeekSafety,
        Commute,
        SeekEmployment,
        Rest
    }

    public enum CitizenResolutionPhase : byte
    {
        Idle,
        Requested,
        InProgress,
        Cooldown
    }

    public enum CitizenResolutionOutcome : byte
    {
        Succeeded,
        Failed
    }

    public enum ResolutionFailureReason : byte
    {
        None,
        NoProvider,
        NoInventory,
        NoFreeTime,
        TooExpensive,
        NoRoute,
        UnsafeRoute,
        CapacityFull,
        NoJob,
        AbilityMismatch
    }

    public enum PolicyDomain : byte
    {
        None,
        Zoning,
        Logistics,
        Labor,
        Welfare,
        Transport,
        Security,
        Employment,
        Education
    }

    public enum CitizenResponseStage : byte
    {
        Stable,
        RequestingPolicy,
        Complaining,
        Protesting,
        Striking,
        Rioting
    }

    public struct CitizenTag : IComponentData
    {
    }

    public struct CitizenDistrict : IComponentData
    {
        public int Id;
    }

    public struct CitizenTraits : IComponentData
    {
        // Values are normalized from 0 to 1.
        public float Patience;
    }

    public struct CitizenGovernmentResponse : IComponentData
    {
        // Trust is normalized from 0 to 1. Discontent is an open-ended pressure value.
        public float Trust;
        public float Discontent;
        public CitizenResponseStage Stage;
    }

    public struct CitizenJob : IComponentData
    {
        public CitizenJobKind Kind;
        public CitizenAbilityKind RequiredAbility;
        public CitizenOutputKind Output;
        public Entity Workplace;
        public float BaseOutputPerSecond;
    }

    public struct CitizenProductivity : IComponentData
    {
        public float AbilityMultiplier;
        public float StressMultiplier;
        public float OutputPerSecond;
    }

    public struct CitizenResolution : IComponentData
    {
        public CitizenStressKind Stress;
        public CitizenActionKind Action;
        public CitizenResolutionPhase Phase;
        public Entity Target;
        public ResolutionFailureReason LastFailure;
        public double RetryAt;
    }

    public struct CitizenSimulationSettings : IComponentData
    {
        public float MaxStress;
        public float DefaultFailureSeverity;
        public float DemandCooldownSeconds;
        public float MinimumProductivityMultiplier;
    }

    public struct PolicyDemandInboxTag : IComponentData
    {
    }

    public struct CitizenAbility : IBufferElementData
    {
        public CitizenAbilityKind Kind;
        public float Value;
    }

    public struct CitizenStress : IBufferElementData
    {
        public CitizenStressKind Kind;
        public float Value;
        public float BaseIncreasePerSecond;
        public float SeekResolutionAt;
        public float CrisisAt;
    }

    /// <summary>
    /// Persistent or temporary pressure supplied by jobs, policies, disasters, or services.
    /// Negative deltas represent relief provided without a citizen action.
    /// </summary>
    public struct CitizenStressModifier : IBufferElementData
    {
        public CitizenStressKind Kind;
        public float DeltaPerSecond;
        public Entity Source;
        public double ExpiresAt;
    }

    /// <summary>
    /// Defines which stress values have a causal effect on this citizen's current output.
    /// </summary>
    public struct CitizenOutputStressEffect : IBufferElementData
    {
        public CitizenStressKind Stress;
        public CitizenOutputKind Output;
        public float PenaltyAtMaxStress;
    }

    /// <summary>
    /// Written by service, movement, workplace, or housing systems after handling a request.
    /// </summary>
    public struct CitizenResolutionResult : IBufferElementData
    {
        public CitizenStressKind Stress;
        public CitizenResolutionOutcome Outcome;
        public ResolutionFailureReason FailureReason;
        public float ReliefAmount;
        public float Severity;
    }

    /// <summary>
    /// Transient events. PolicyDemandAggregationSystem consumes these into summaries each tick.
    /// </summary>
    public struct PolicyDemandEvent : IBufferElementData
    {
        public Entity Citizen;
        public int DistrictId;
        public CitizenStressKind Stress;
        public ResolutionFailureReason FailureReason;
        public PolicyDomain Domain;
        public float Severity;
    }

    /// <summary>
    /// Persistent player-facing counts, grouped by root cause rather than symptom only.
    /// </summary>
    public struct PolicyDemandSummary : IBufferElementData
    {
        public int DistrictId;
        public CitizenStressKind Stress;
        public ResolutionFailureReason FailureReason;
        public PolicyDomain Domain;
        public int Occurrences;
        public float TotalSeverity;
    }
}
