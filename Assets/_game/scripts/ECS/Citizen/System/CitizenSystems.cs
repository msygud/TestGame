using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace Game.Citizen
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct CitizenStressAccumulationSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenSimulationSettings>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            CitizenSimulationSettings settings = SystemAPI.GetSingleton<CitizenSimulationSettings>();
            float deltaTime = SystemAPI.Time.DeltaTime;
            double elapsedTime = SystemAPI.Time.ElapsedTime;

            foreach (var (stresses, modifiers)
                     in SystemAPI.Query<DynamicBuffer<CitizenStress>, DynamicBuffer<CitizenStressModifier>>()
                         .WithAll<CitizenTag>())
            {
                DynamicBuffer<CitizenStress> writableStresses = stresses;
                DynamicBuffer<CitizenStressModifier> writableModifiers = modifiers;
                RemoveExpiredModifiers(writableModifiers, elapsedTime);

                for (int i = 0; i < writableStresses.Length; i++)
                {
                    CitizenStress stress = writableStresses[i];
                    float increasePerSecond = stress.BaseIncreasePerSecond;

                    for (int modifierIndex = 0; modifierIndex < writableModifiers.Length; modifierIndex++)
                    {
                        CitizenStressModifier modifier = writableModifiers[modifierIndex];
                        if (modifier.Kind == stress.Kind)
                        {
                            increasePerSecond += modifier.DeltaPerSecond;
                        }
                    }

                    stress.Value = math.clamp(
                        stress.Value + increasePerSecond * deltaTime,
                        0f,
                        settings.MaxStress);
                    writableStresses[i] = stress;
                }
            }
        }

        private static void RemoveExpiredModifiers(
            DynamicBuffer<CitizenStressModifier> modifiers,
            double elapsedTime)
        {
            for (int i = modifiers.Length - 1; i >= 0; i--)
            {
                CitizenStressModifier modifier = modifiers[i];
                if (modifier.ExpiresAt > 0d && modifier.ExpiresAt <= elapsedTime)
                {
                    modifiers.RemoveAt(i);
                }
            }
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenStressAccumulationSystem))]
    public partial struct CitizenResolutionSelectionSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenSimulationSettings>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            double elapsedTime = SystemAPI.Time.ElapsedTime;

            foreach (var (resolution, stresses)
                     in SystemAPI.Query<RefRW<CitizenResolution>, DynamicBuffer<CitizenStress>>()
                         .WithAll<CitizenTag>())
            {
                RefRW<CitizenResolution> writableResolution = resolution;

                if (writableResolution.ValueRO.Phase == CitizenResolutionPhase.Cooldown)
                {
                    if (writableResolution.ValueRO.RetryAt > elapsedTime)
                    {
                        continue;
                    }

                    writableResolution.ValueRW.Phase = CitizenResolutionPhase.Idle;
                }

                if (writableResolution.ValueRO.Phase != CitizenResolutionPhase.Idle)
                {
                    continue;
                }

                int selectedIndex = SelectMostUrgentStress(stresses);
                if (selectedIndex < 0)
                {
                    continue;
                }

                CitizenStress selectedStress = stresses[selectedIndex];
                writableResolution.ValueRW.Stress = selectedStress.Kind;
                writableResolution.ValueRW.Action = CitizenUtility.GetAction(selectedStress.Kind);
                writableResolution.ValueRW.Phase = CitizenResolutionPhase.Requested;
                writableResolution.ValueRW.Target = Entity.Null;
                writableResolution.ValueRW.LastFailure = ResolutionFailureReason.None;
            }
        }

        private static int SelectMostUrgentStress(DynamicBuffer<CitizenStress> stresses)
        {
            int selectedIndex = -1;
            float selectedUrgency = 0f;

            for (int i = 0; i < stresses.Length; i++)
            {
                CitizenStress stress = stresses[i];
                if (stress.Value < stress.SeekResolutionAt)
                {
                    continue;
                }

                float range = math.max(1f, stress.CrisisAt - stress.SeekResolutionAt);
                float urgency = (stress.Value - stress.SeekResolutionAt) / range;
                if (selectedIndex < 0 || urgency > selectedUrgency)
                {
                    selectedIndex = i;
                    selectedUrgency = urgency;
                }
            }

            return selectedIndex;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenResolutionSelectionSystem))]
    public partial struct CitizenResolutionResultSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PolicyDemandInboxTag>();
            state.RequireForUpdate<CitizenSimulationSettings>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            CitizenSimulationSettings settings = SystemAPI.GetSingleton<CitizenSimulationSettings>();
            DynamicBuffer<PolicyDemandEvent> demands = SystemAPI.GetSingletonBuffer<PolicyDemandEvent>();
            double elapsedTime = SystemAPI.Time.ElapsedTime;

            foreach (var (resolution,
                          governmentResponse,
                          traits,
                          district,
                          stresses,
                          results,
                          citizen)
                     in SystemAPI.Query<RefRW<CitizenResolution>,
                                        RefRW<CitizenGovernmentResponse>,
                                        RefRO<CitizenTraits>,
                             RefRO<CitizenDistrict>,
                             DynamicBuffer<CitizenStress>,
                             DynamicBuffer<CitizenResolutionResult>>()
                         .WithAll<CitizenTag>()
                         .WithEntityAccess())
            {
                RefRW<CitizenResolution> writableResolution = resolution;
                RefRW<CitizenGovernmentResponse> writableGovernmentResponse = governmentResponse;
                DynamicBuffer<CitizenStress> writableStresses = stresses;
                DynamicBuffer<CitizenResolutionResult> writableResults = results;

                for (int i = 0; i < writableResults.Length; i++)
                {
                    CitizenResolutionResult result = writableResults[i];
                    if (result.Outcome == CitizenResolutionOutcome.Succeeded)
                    {
                        ApplyRelief(writableStresses, result.Stress, result.ReliefAmount);
                        writableResolution.ValueRW.Phase = CitizenResolutionPhase.Idle;
                        writableResolution.ValueRW.LastFailure = ResolutionFailureReason.None;
                        continue;
                    }

                    float severity = result.Severity > 0f
                        ? result.Severity
                        : settings.DefaultFailureSeverity;
                    float trustFactor = math.lerp(1f, 0.35f, writableGovernmentResponse.ValueRO.Trust);
                    writableGovernmentResponse.ValueRW.Discontent += severity * trustFactor;
                    writableGovernmentResponse.ValueRW.Stage = CitizenUtility.GetResponseStage(
                        writableGovernmentResponse.ValueRO.Discontent,
                        writableGovernmentResponse.ValueRO.Trust,
                        traits.ValueRO.Patience);

                    demands.Add(new PolicyDemandEvent
                    {
                        Citizen = citizen,
                        DistrictId = district.ValueRO.Id,
                        Stress = result.Stress,
                        FailureReason = result.FailureReason,
                        Domain = CitizenUtility.GetPolicyDomain(result.Stress, result.FailureReason),
                        Severity = severity
                    });

                    writableResolution.ValueRW.Phase = CitizenResolutionPhase.Cooldown;
                    writableResolution.ValueRW.LastFailure = result.FailureReason;
                    writableResolution.ValueRW.RetryAt = elapsedTime + settings.DemandCooldownSeconds;
                }

                writableResults.Clear();
            }
        }

        private static void ApplyRelief(
            DynamicBuffer<CitizenStress> stresses,
            CitizenStressKind kind,
            float reliefAmount)
        {
            for (int i = 0; i < stresses.Length; i++)
            {
                CitizenStress stress = stresses[i];
                if (stress.Kind != kind)
                {
                    continue;
                }

                stress.Value = math.max(0f, stress.Value - math.max(0f, reliefAmount));
                stresses[i] = stress;
                return;
            }
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenResolutionResultSystem))]
    public partial struct PolicyDemandAggregationSystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<PolicyDemandInboxTag>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            DynamicBuffer<PolicyDemandEvent> events = SystemAPI.GetSingletonBuffer<PolicyDemandEvent>();
            DynamicBuffer<PolicyDemandSummary> summaries = SystemAPI.GetSingletonBuffer<PolicyDemandSummary>();

            for (int i = 0; i < events.Length; i++)
            {
                PolicyDemandEvent demand = events[i];
                int summaryIndex = FindSummaryIndex(summaries, demand);

                if (summaryIndex < 0)
                {
                    summaries.Add(new PolicyDemandSummary
                    {
                        DistrictId = demand.DistrictId,
                        Stress = demand.Stress,
                        FailureReason = demand.FailureReason,
                        Domain = demand.Domain,
                        Occurrences = 1,
                        TotalSeverity = demand.Severity
                    });
                    continue;
                }

                PolicyDemandSummary summary = summaries[summaryIndex];
                summary.Occurrences++;
                summary.TotalSeverity += demand.Severity;
                summaries[summaryIndex] = summary;
            }

            events.Clear();
        }

        private static int FindSummaryIndex(
            DynamicBuffer<PolicyDemandSummary> summaries,
            PolicyDemandEvent demand)
        {
            for (int i = 0; i < summaries.Length; i++)
            {
                PolicyDemandSummary summary = summaries[i];
                if (summary.DistrictId == demand.DistrictId
                    && summary.Stress == demand.Stress
                    && summary.FailureReason == demand.FailureReason
                    && summary.Domain == demand.Domain)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CitizenResolutionResultSystem))]
    public partial struct CitizenProductivitySystem : ISystem
    {
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<CitizenSimulationSettings>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            CitizenSimulationSettings settings = SystemAPI.GetSingleton<CitizenSimulationSettings>();

            foreach (var (job,
                          productivity,
                          abilities,
                          stresses,
                          effects)
                     in SystemAPI.Query<RefRO<CitizenJob>,
                                        RefRW<CitizenProductivity>,
                                        DynamicBuffer<CitizenAbility>,
                             DynamicBuffer<CitizenStress>,
                             DynamicBuffer<CitizenOutputStressEffect>>()
                         .WithAll<CitizenTag>())
            {
                RefRW<CitizenProductivity> writableProductivity = productivity;
                float abilityMultiplier = GetAbilityMultiplier(abilities, job.ValueRO.RequiredAbility);
                float stressMultiplier = GetStressMultiplier(
                    stresses,
                    effects,
                    job.ValueRO.Output,
                    settings.MaxStress,
                    settings.MinimumProductivityMultiplier);

                writableProductivity.ValueRW.AbilityMultiplier = abilityMultiplier;
                writableProductivity.ValueRW.StressMultiplier = stressMultiplier;
                writableProductivity.ValueRW.OutputPerSecond =
                    job.ValueRO.BaseOutputPerSecond * abilityMultiplier * stressMultiplier;
            }
        }

        private static float GetAbilityMultiplier(
            DynamicBuffer<CitizenAbility> abilities,
            CitizenAbilityKind requiredAbility)
        {
            for (int i = 0; i < abilities.Length; i++)
            {
                CitizenAbility ability = abilities[i];
                if (ability.Kind == requiredAbility)
                {
                    return 0.5f + math.saturate(ability.Value);
                }
            }

            return 0.5f;
        }

        private static float GetStressMultiplier(
            DynamicBuffer<CitizenStress> stresses,
            DynamicBuffer<CitizenOutputStressEffect> effects,
            CitizenOutputKind output,
            float maxStress,
            float minimumMultiplier)
        {
            float multiplier = 1f;

            for (int effectIndex = 0; effectIndex < effects.Length; effectIndex++)
            {
                CitizenOutputStressEffect effect = effects[effectIndex];
                if (effect.Output != output)
                {
                    continue;
                }

                for (int stressIndex = 0; stressIndex < stresses.Length; stressIndex++)
                {
                    CitizenStress stress = stresses[stressIndex];
                    if (stress.Kind != effect.Stress)
                    {
                        continue;
                    }

                    float normalizedStress = math.saturate(stress.Value / math.max(1f, maxStress));
                    multiplier *= 1f - normalizedStress * math.saturate(effect.PenaltyAtMaxStress);
                    break;
                }
            }

            return math.max(minimumMultiplier, multiplier);
        }
    }

    public static class CitizenUtility
    {
        public static CitizenActionKind GetAction(CitizenStressKind stress)
        {
            switch (stress)
            {
                case CitizenStressKind.Hunger:
                    return CitizenActionKind.Eat;
                case CitizenStressKind.Housing:
                    return CitizenActionKind.FindHousing;
                case CitizenStressKind.Leisure:
                    return CitizenActionKind.SeekLeisure;
                case CitizenStressKind.Safety:
                    return CitizenActionKind.SeekSafety;
                case CitizenStressKind.Commute:
                    return CitizenActionKind.Commute;
                case CitizenStressKind.Employment:
                    return CitizenActionKind.SeekEmployment;
                case CitizenStressKind.Fatigue:
                    return CitizenActionKind.Rest;
                default:
                    return CitizenActionKind.None;
            }
        }

        public static PolicyDomain GetPolicyDomain(
            CitizenStressKind stress,
            ResolutionFailureReason reason)
        {
            switch (reason)
            {
                case ResolutionFailureReason.NoInventory:
                    return PolicyDomain.Logistics;
                case ResolutionFailureReason.NoFreeTime:
                    return PolicyDomain.Labor;
                case ResolutionFailureReason.TooExpensive:
                    return PolicyDomain.Welfare;
                case ResolutionFailureReason.NoRoute:
                    return PolicyDomain.Transport;
                case ResolutionFailureReason.UnsafeRoute:
                    return PolicyDomain.Security;
                case ResolutionFailureReason.NoJob:
                    return PolicyDomain.Employment;
                case ResolutionFailureReason.AbilityMismatch:
                    return PolicyDomain.Education;
                case ResolutionFailureReason.NoProvider:
                case ResolutionFailureReason.CapacityFull:
                    return stress == CitizenStressKind.Safety
                        ? PolicyDomain.Security
                        : PolicyDomain.Zoning;
                default:
                    return PolicyDomain.None;
            }
        }

        public static CitizenResponseStage GetResponseStage(
            float discontent,
            float trust,
            float patience)
        {
            float threshold = math.lerp(10f, 30f, math.saturate(trust));
            threshold *= math.lerp(0.75f, 1.25f, math.saturate(patience));

            if (discontent >= threshold * 5f)
            {
                return CitizenResponseStage.Rioting;
            }

            if (discontent >= threshold * 4f)
            {
                return CitizenResponseStage.Striking;
            }

            if (discontent >= threshold * 3f)
            {
                return CitizenResponseStage.Protesting;
            }

            if (discontent >= threshold * 2f)
            {
                return CitizenResponseStage.Complaining;
            }

            if (discontent >= threshold)
            {
                return CitizenResponseStage.RequestingPolicy;
            }

            return CitizenResponseStage.Stable;
        }
    }
}
