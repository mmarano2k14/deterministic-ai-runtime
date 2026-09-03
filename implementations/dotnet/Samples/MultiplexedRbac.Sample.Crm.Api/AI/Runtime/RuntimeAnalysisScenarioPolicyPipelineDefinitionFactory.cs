using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Policies;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Policies;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisScenarioPolicyPipelineDefinitionFactory
    {
        public const string PipelineName =
            "runtime-analysis-policy-validation";

        public const string ValidateStepName =
            "validate-suggested-scenario";

        public AiPipelineDefinition Create(
            RuntimeAnalysisSuggestedScenario scenario)
        {
            ArgumentNullException.ThrowIfNull(
                scenario);

            var scenarioJson = JsonSerializer.Serialize(
                scenario);

            var policyDefinition =
                CreatePolicyDefinition();

            return new AiPipelineDefinition
            {
                Name = PipelineName,
                Version = "2",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = ValidateStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.ValidateSuggestedScenario,
                        Order = 1,
                        Config = new Dictionary<string, object?>(
                            StringComparer.Ordinal)
                        {
                            [RuntimeAnalysisStepConfigKeys.SuggestedScenarioJson] =
                                scenarioJson,

                            // Dynamic policy declaration.
                            //
                            // Same runtime pattern as config.retry:
                            // typed policy definition -> Policies[] ->
                            // AiConfiguredPolicyDefinition { name, kind, config }.
                            [RuntimeAnalysisStepConfigKeys.ScenarioPolicyDefinition] =
                                policyDefinition
                        },
                        Execution = new AiPipelineStepExecutionDefinition
                        {
                            MaxRetries = 0,
                            RetryDelayMs = 0
                        }
                    }
                ]
            };
        }

        private static RuntimeAnalysisScenarioPolicyDefinition
            CreatePolicyDefinition()
        {
            return new RuntimeAnalysisScenarioPolicyDefinition
            {
                PlanKey = "read",
                RequireHumanApproval = true,
                Policies =
                [
                    new AiConfiguredPolicyDefinition
                    {
                        Name =
                            RuntimeAnalysisScenarioPolicyKeys.Limits,
                        Kind =
                            AiPolicyKind.Validation.ToString(),
                        Config =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
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
                        Name =
                            RuntimeAnalysisScenarioPolicyKeys.Safety,
                        Kind =
                            AiPolicyKind.Validation.ToString(),
                        Config =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                ["allowedScenarioTypes"] =
                                    new[]
                                    {
                                        "single-burst",
                                        "maintained-concurrency",
                                        "wave-batches",
                                        "wave-batches-staggered"
                                    },
                                ["allowedPlanKeys"] =
                                    new[]
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
