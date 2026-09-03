using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Policies
{
    /// <summary>
    /// Creates the declarative policy set embedded in the runtime-analysis DAG.
    /// The executable policy plugins remain resolved through IAiPolicyRegistry.
    /// </summary>
    public sealed class RuntimeAnalysisScenarioPolicyDefinitionFactory
    {
        public RuntimeAnalysisScenarioPolicyDefinition Create()
        {
            return new RuntimeAnalysisScenarioPolicyDefinition
            {
                PlanKey = "read",
                RequireHumanApproval = true,
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name = RuntimeAnalysisScenarioPolicyKeys.Limits,
                        Kind = AiPolicyKind.Validation.ToString(),
                        Config = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["minTotalRequests"] = 1,
                            ["maxTotalRequests"] = 1000,
                            ["minMaxInFlight"] = 1,
                            ["maxMaxInFlight"] = 20,
                            ["maxConcurrency"] = 20,
                            ["maxBatchSize"] = 100,
                            ["maxDelayMs"] = 5000,
                            ["maxWavePauseMs"] = 10000,
                            ["maxRotationOverlapMs"] = 5000,
                            ["maxDurationSeconds"] = 300
                        }
                    },
                    new AiConfiguredPolicyDefinition
                    {
                        Name = RuntimeAnalysisScenarioPolicyKeys.Safety,
                        Kind = AiPolicyKind.Validation.ToString(),
                        Config = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["allowedScenarioTypes"] = new[]
                            {
                                "single-burst",
                                "maintained-concurrency",
                                "wave-batches",
                                "wave-batches-staggered"
                            },
                            ["allowedPlanKeys"] = new[]
                            {
                                "read"
                            }
                        }
                    }
                ]
            };
        }
    }
}
