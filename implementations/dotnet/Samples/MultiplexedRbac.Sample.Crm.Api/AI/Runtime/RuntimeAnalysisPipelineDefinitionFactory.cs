using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;
using MultiplexedRbac.Sample.Crm.Api.AI.Policies;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    public sealed class RuntimeAnalysisPipelineDefinitionFactory
    {
        public const string PipelineName = "runtime-analysis";
        public const string AnalyzeStepName = "analyze-runtime-with-openai";
        public const string ValidateScenarioStepName =
            "validate-suggested-scenario";
        public const string AwaitHumanApprovalStepName =
            "await-human-approval";
        public const string ExecuteApprovedScenarioStepName =
            "execute-approved-scenario";
        public const string VerifyScenarioOutcomeStepName =
            "verify-scenario-outcome";

        private readonly RuntimeAnalysisScenarioPolicyDefinitionFactory
            _policyDefinitionFactory;

        public RuntimeAnalysisPipelineDefinitionFactory(
            RuntimeAnalysisScenarioPolicyDefinitionFactory policyDefinitionFactory)
        {
            _policyDefinitionFactory =
                policyDefinitionFactory
                ?? throw new ArgumentNullException(
                    nameof(policyDefinitionFactory));
        }

        public AiPipelineDefinition Create(
            RuntimeAnalysisProviderRequest request)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            var requestJson = JsonSerializer.Serialize(
                request);

            return new AiPipelineDefinition
            {
                Name = PipelineName,
                Version = "5",
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = AnalyzeStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.AnalyzeWithOpenAi,
                        Order = 1,
                        Config =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepConfigKeys.ProviderRequestJson] =
                                    requestJson
                            },
                        Execution = NoRetry()
                    },
                    new AiPipelineStepDefinition
                    {
                        Name = ValidateScenarioStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.ValidateSuggestedScenario,
                        Order = 2,
                        DependsOn =
                        [
                            AnalyzeStepName
                        ],
                        Input =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                                    $"steps.{AnalyzeStepName}.result.output"
                            },
                        Config =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepConfigKeys.ScenarioPolicyDefinition] =
                                    _policyDefinitionFactory.Create()
                            },
                        Execution = NoRetry()
                    },
                    new AiPipelineStepDefinition
                    {
                        Name = AwaitHumanApprovalStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.AwaitHumanApproval,
                        Order = 3,
                        DependsOn =
                        [
                            ValidateScenarioStepName
                        ],
                        Input =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                                    $"steps.{ValidateScenarioStepName}.result.output"
                            },
                        Execution = NoRetry()
                    },
                    new AiPipelineStepDefinition
                    {
                        Name = ExecuteApprovedScenarioStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.ExecuteApprovedScenario,
                        Order = 4,
                        DependsOn =
                        [
                            AwaitHumanApprovalStepName
                        ],
                        Input =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                                    $"steps.{ValidateScenarioStepName}.result.output",
                                [RuntimeAnalysisStepInputKeys.HumanApprovalJson] =
                                    $"steps.{AwaitHumanApprovalStepName}.result.output"
                            },
                        Execution = NoRetry()
                    },
                    new AiPipelineStepDefinition
                    {
                        Name = VerifyScenarioOutcomeStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.VerifyScenarioOutcome,
                        Order = 5,
                        DependsOn =
                        [
                            ExecuteApprovedScenarioStepName
                        ],
                        Input =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                                    $"steps.{ExecuteApprovedScenarioStepName}.result.output"
                            },
                        Config =
                            new Dictionary<string, object?>(
                                StringComparer.Ordinal)
                            {
                                [RuntimeAnalysisStepConfigKeys.ProviderRequestJson] =
                                    requestJson
                            },
                        Execution = NoRetry()
                    }
                ]
            };
        }

        private static AiPipelineStepExecutionDefinition NoRetry()
        {
            return new AiPipelineStepExecutionDefinition
            {
                MaxRetries = 0,
                RetryDelayMs = 0
            };
        }
    }
}
