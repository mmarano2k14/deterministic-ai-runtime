using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
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

            var policyDefinition =
                _policyDefinitionFactory.Create();

            // The provider receives the exact same declarative policy
            // envelope that the validation step below executes.
            //
            // This keeps AI proposal generation policy-aware without moving
            // execution authority into the model.
            var effectiveRequest =
                new RuntimeAnalysisProviderRequest
                {
                    Question = request.Question,
                    InvestigationMode = request.InvestigationMode,
                    Snapshot = request.Snapshot,
                    ScenarioPolicyDefinition = policyDefinition
                };

            var requestJson = JsonSerializer.Serialize(
                effectiveRequest);

            return new AiPipelineDefinition
            {
                Name = PipelineName,
                Version = "10",
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
                                    policyDefinition
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
                    },
                    CreateChildDagStep(
                        requestJson)
                ]
            };
        }

        private AiPipelineStepDefinition CreateChildDagStep(
            string providerRequestJson)
        {
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
                StepKey = RuntimeAnalysisStepKeys.ExecuteApprovedChildDag,
                Order = 6,
                DependsOn =
                [
                    VerifyScenarioOutcomeStepName
                ],
                Input =
                    RuntimeAnalysisChildDagDefinitionFactory.CreateRootInputs(
                        providerRequestJson),
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
