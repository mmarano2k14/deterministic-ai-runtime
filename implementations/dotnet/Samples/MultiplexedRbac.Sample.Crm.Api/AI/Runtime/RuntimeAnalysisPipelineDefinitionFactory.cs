using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
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
        private readonly RuntimeAnalysisChildDagDefinitionFactory
            _childDagDefinitionFactory;

        public RuntimeAnalysisPipelineDefinitionFactory(
            RuntimeAnalysisScenarioPolicyDefinitionFactory policyDefinitionFactory,
            RuntimeAnalysisChildDagDefinitionFactory childDagDefinitionFactory)
        {
            _policyDefinitionFactory =
                policyDefinitionFactory
                ?? throw new ArgumentNullException(
                    nameof(policyDefinitionFactory));
            _childDagDefinitionFactory =
                childDagDefinitionFactory
                ?? throw new ArgumentNullException(
                    nameof(childDagDefinitionFactory));
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
                Version = "7",
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
                    CreateChildDagStep(),
                    new AiPipelineStepDefinition
                    {
                        Name = VerifyScenarioOutcomeStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.VerifyScenarioOutcome,
                        Order = 6,
                        DependsOn =
                        [
                            RuntimeAnalysisChildDagDefinitionFactory
                                .ChildDagStepName
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

        private AiPipelineStepDefinition CreateChildDagStep()
        {
            // One approved root decision creates exactly one durable child.
            // There is intentionally no pre-built Depth 2 / Depth 3 chain.
            // A later child will be created only by a new approved decision
            // produced by the re-analysis loop.
            const int childDepth =
                RuntimeAnalysisChildDagDefinitionFactory
                    .InitialApprovedChildDepth;

            var childDefinition =
                _childDagDefinitionFactory.CreateApprovedChildDefinition(
                    childDepth);

            return new AiPipelineStepDefinition
            {
                Name =
                    RuntimeAnalysisChildDagDefinitionFactory.ChildDagStepName,
                StepKey = ExecuteChildDagStep.StepKey,
                Order = 5,
                DependsOn =
                [
                    ExecuteApprovedScenarioStepName
                ],
                Input =
                    RuntimeAnalysisChildDagDefinitionFactory.CreateRootInputs(),
                Config =
                    new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [ExecuteChildDagStep.ChildDagIdConfigKey] =
                            childDefinition.Name,
                        [ExecuteChildDagStep.ChildDagVersionConfigKey] =
                            childDefinition.Version,
                        [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] =
                            RuntimeAnalysisChildDagDefinitionFactory
                                .CreateChildLogicalInvocationKey(
                                    childDepth),
                        [ExecuteChildDagStep.ChildDagDefinitionConfigKey] =
                            childDefinition
                    },
                Execution = NoRetry()
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
