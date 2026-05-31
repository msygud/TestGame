using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Game.Citizen
{
    public class CitizenAuthoring : MonoBehaviour
    {
        [Serializable]
        public struct AbilitySeed
        {
            public CitizenAbilityKind Kind;
            [Range(0f, 1f)] public float Value;
        }

        [Serializable]
        public struct StressSeed
        {
            public CitizenStressKind Kind;
            [Min(0f)] public float InitialValue;
            public float BaseIncreasePerSecond;
            [Min(0f)] public float SeekResolutionAt;
            [Min(0f)] public float CrisisAt;
        }

        [Serializable]
        public struct OutputStressSeed
        {
            public CitizenStressKind Stress;
            public CitizenOutputKind Output;
            [Range(0f, 1f)] public float PenaltyAtMaxStress;
        }

        [Header("Identity")]
        public int DistrictId;
        [Range(0f, 1f)] public float Patience = 0.5f;
        [Range(0f, 1f)] public float InitialTrust = 0.5f;

        [Header("Job")]
        public CitizenJobKind JobKind;
        public CitizenAbilityKind RequiredAbility;
        public CitizenOutputKind OutputKind;
        public GameObject Workplace;
        [Min(0f)] public float BaseOutputPerSecond;

        [Header("Innate Abilities")]
        public List<AbilitySeed> Abilities = new List<AbilitySeed>();

        [Header("Stress Values")]
        public List<StressSeed> Stresses = new List<StressSeed>();

        [Header("Causal Output Effects")]
        public List<OutputStressSeed> OutputStressEffects = new List<OutputStressSeed>();

        private class Baker : Baker<CitizenAuthoring>
        {
            public override void Bake(CitizenAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent<CitizenTag>(entity);
                AddComponent(entity, new CitizenDistrict { Id = authoring.DistrictId });
                AddComponent(entity, new CitizenTraits { Patience = authoring.Patience });
                AddComponent(entity, new CitizenGovernmentResponse
                {
                    Trust = authoring.InitialTrust,
                    Discontent = 0f,
                    Stage = CitizenResponseStage.Stable
                });
                AddComponent(entity, new CitizenJob
                {
                    Kind = authoring.JobKind,
                    RequiredAbility = authoring.RequiredAbility,
                    Output = authoring.OutputKind,
                    Workplace = authoring.Workplace == null
                        ? Entity.Null
                        : GetEntity(authoring.Workplace, TransformUsageFlags.Dynamic),
                    BaseOutputPerSecond = authoring.BaseOutputPerSecond
                });
                AddComponent(entity, new CitizenProductivity
                {
                    AbilityMultiplier = 1f,
                    StressMultiplier = 1f,
                    OutputPerSecond = authoring.BaseOutputPerSecond
                });
                AddComponent(entity, new CitizenResolution
                {
                    Phase = CitizenResolutionPhase.Idle,
                    Action = CitizenActionKind.None,
                    Target = Entity.Null,
                    LastFailure = ResolutionFailureReason.None
                });

                DynamicBuffer<CitizenAbility> abilities = AddBuffer<CitizenAbility>(entity);
                foreach (AbilitySeed seed in authoring.Abilities)
                {
                    abilities.Add(new CitizenAbility { Kind = seed.Kind, Value = seed.Value });
                }

                DynamicBuffer<CitizenStress> stresses = AddBuffer<CitizenStress>(entity);
                if (authoring.Stresses.Count == 0)
                {
                    AddDefaultStresses(stresses);
                }
                else
                {
                    foreach (StressSeed seed in authoring.Stresses)
                    {
                        stresses.Add(ToStress(seed));
                    }
                }

                DynamicBuffer<CitizenOutputStressEffect> effects = AddBuffer<CitizenOutputStressEffect>(entity);
                foreach (OutputStressSeed seed in authoring.OutputStressEffects)
                {
                    effects.Add(new CitizenOutputStressEffect
                    {
                        Stress = seed.Stress,
                        Output = seed.Output,
                        PenaltyAtMaxStress = seed.PenaltyAtMaxStress
                    });
                }

                AddBuffer<CitizenStressModifier>(entity);
                AddBuffer<CitizenResolutionResult>(entity);
            }

            private static CitizenStress ToStress(StressSeed seed)
            {
                return new CitizenStress
                {
                    Kind = seed.Kind,
                    Value = seed.InitialValue,
                    BaseIncreasePerSecond = seed.BaseIncreasePerSecond,
                    SeekResolutionAt = seed.SeekResolutionAt,
                    CrisisAt = seed.CrisisAt
                };
            }

            private static void AddDefaultStresses(DynamicBuffer<CitizenStress> stresses)
            {
                stresses.Add(new CitizenStress
                {
                    Kind = CitizenStressKind.Hunger,
                    BaseIncreasePerSecond = 0.4f,
                    SeekResolutionAt = 35f,
                    CrisisAt = 80f
                });
                stresses.Add(new CitizenStress
                {
                    Kind = CitizenStressKind.Housing,
                    SeekResolutionAt = 35f,
                    CrisisAt = 80f
                });
                stresses.Add(new CitizenStress
                {
                    Kind = CitizenStressKind.Leisure,
                    BaseIncreasePerSecond = 0.08f,
                    SeekResolutionAt = 45f,
                    CrisisAt = 85f
                });
                stresses.Add(new CitizenStress
                {
                    Kind = CitizenStressKind.Safety,
                    SeekResolutionAt = 20f,
                    CrisisAt = 70f
                });
                stresses.Add(new CitizenStress
                {
                    Kind = CitizenStressKind.Fatigue,
                    BaseIncreasePerSecond = 0.2f,
                    SeekResolutionAt = 40f,
                    CrisisAt = 85f
                });
            }
        }
    }

    public class CitizenSimulationAuthoring : MonoBehaviour
    {
        [Min(1f)] public float MaxStress = 100f;
        [Min(0f)] public float DefaultFailureSeverity = 5f;
        [Min(0f)] public float DemandCooldownSeconds = 5f;
        [Range(0f, 1f)] public float MinimumProductivityMultiplier = 0.05f;

        private class Baker : Baker<CitizenSimulationAuthoring>
        {
            public override void Bake(CitizenSimulationAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.None);
                AddComponent<PolicyDemandInboxTag>(entity);
                AddComponent(entity, new CitizenSimulationSettings
                {
                    MaxStress = authoring.MaxStress,
                    DefaultFailureSeverity = authoring.DefaultFailureSeverity,
                    DemandCooldownSeconds = authoring.DemandCooldownSeconds,
                    MinimumProductivityMultiplier = authoring.MinimumProductivityMultiplier
                });
                AddBuffer<PolicyDemandEvent>(entity);
                AddBuffer<PolicyDemandSummary>(entity);
            }
        }
    }
}
