using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    /// <summary>
    /// Builds the approval-driven child workflow.
    /// </summary>
    /// <remarks>
    /// One approved decision creates one child relation. Deeper relations are
    /// materialized only after the current child completes re-analysis,
    /// deterministic policy validation, and another human approval.
    /// </remarks>
    public sealed class RuntimeAnalysisChildDagDefinitionFactory
    {
        private readonly RuntimeAnalysisScenarioPolicyDefinitionFactory
            _policyDefinitionFactory;

        public RuntimeAnalysisChildDagDefinitionFactory(
            RuntimeAnalysisScenarioPolicyDefinitionFactory policyDefinitionFactory)
        {
            _policyDefinitionFactory =
                policyDefinitionFactory
                ?? throw new ArgumentNullException(
                    nameof(policyDefinitionFactory));
        }

        public const int InitialApprovedChildDepth = 1;

        /// <summary>
        /// Hard deterministic safety boundary for the demo. This is not an
        /// automatic target depth; it only caps successive approved decisions.
        /// </summary>
        public const int MaximumApprovedChildDepth = 5;

        public const int MaxProjectedApprovalDepth = MaximumApprovedChildDepth;

        public const string PipelineVersion = "3.1.0";

        public const string ChildDagStepName = "execute-child-dag";

        public const string CaptureEvidenceStepName =
            "capture-runtime-analysis-evidence";

        public const string ReanalysisStepName =
            "re-analyze-verified-outcome";

        public const string ValidateReanalysisStepName =
            "validate-reanalysis-scenario";

        public const string AwaitHumanApprovalStepName =
            "await-child-human-approval";

        public const string ExecuteApprovedScenarioStepName =
            "execute-child-approved-scenario";

        public const string VerifyScenarioOutcomeStepName =
            "verify-child-scenario-outcome";

        public AiPipelineDefinition CreateApprovedChildDefinition(
            int childDepth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                childDepth);

            if (childDepth > MaximumApprovedChildDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childDepth),
                    childDepth,
                    $"Approval-driven Child DAG depth cannot exceed {MaximumApprovedChildDepth}.");
            }

            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = CaptureEvidenceStepName,
                    StepKey = RuntimeAnalysisStepKeys.CaptureChildDagEvidence,
                    Order = 1,
                    Input = CreateInheritedEvidenceInputs(),
                    Config = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepConfigKeys.ChildDagDepth] =
                            childDepth
                    },
                    Execution = NoRetry()
                },
                new()
                {
                    Name = ReanalysisStepName,
                    StepKey = RuntimeAnalysisStepKeys.ReanalyzeVerifiedOutcome,
                    Order = 2,
                    DependsOn =
                    [
                        CaptureEvidenceStepName
                    ],
                    Input = CreateReanalysisInputs(),
                    Config = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepConfigKeys.ChildDagDepth] =
                            childDepth
                    },
                    Execution = NoRetry()
                },
                new()
                {
                    Name = ValidateReanalysisStepName,
                    StepKey = RuntimeAnalysisStepKeys.ValidateReanalysisScenario,
                    Order = 3,
                    DependsOn =
                    [
                        ReanalysisStepName
                    ],
                    Input = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepInputKeys.ReanalysisResultJson] =
                            $"steps.{ReanalysisStepName}.result.output"
                    },
                    Config = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepConfigKeys.ChildDagDepth] =
                            childDepth,
                        [RuntimeAnalysisStepConfigKeys.ScenarioPolicyDefinition] =
                            _policyDefinitionFactory.Create()
                    },
                    Execution = NoRetry()
                },
                new()
                {
                    Name = AwaitHumanApprovalStepName,
                    StepKey = RuntimeAnalysisStepKeys.AwaitHumanApproval,
                    Order = 4,
                    DependsOn =
                    [
                        ValidateReanalysisStepName
                    ],
                    Input = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                            $"steps.{ValidateReanalysisStepName}.result.output"
                    },
                    Execution = NoRetry()
                },
                new()
                {
                    Name = ExecuteApprovedScenarioStepName,
                    StepKey = RuntimeAnalysisStepKeys.ExecuteApprovedScenario,
                    Order = 5,
                    DependsOn =
                    [
                        AwaitHumanApprovalStepName
                    ],
                    Input = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                            $"steps.{ValidateReanalysisStepName}.result.output",
                        [RuntimeAnalysisStepInputKeys.HumanApprovalJson] =
                            $"steps.{AwaitHumanApprovalStepName}.result.output"
                    },
                    Execution = NoRetry()
                },
                new()
                {
                    Name = VerifyScenarioOutcomeStepName,
                    StepKey = RuntimeAnalysisStepKeys.VerifyScenarioOutcome,
                    Order = 6,
                    DependsOn =
                    [
                        ExecuteApprovedScenarioStepName
                    ],
                    Input = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                            $"steps.{ExecuteApprovedScenarioStepName}.result.output",
                        [RuntimeAnalysisStepInputKeys.ProviderRequestJson] =
                            $"state.{RuntimeAnalysisStepInputKeys.ProviderRequestJson}"
                    },
                    Execution = NoRetry()
                }
            };

            if (childDepth < MaximumApprovedChildDepth)
            {
                steps.Add(
                    CreateNextApprovedChildStep(
                        childDepth));
            }

            return new AiPipelineDefinition
            {
                Name = CreateChildPipelineName(
                    childDepth),
                Version = PipelineVersion,
                ExecutionMode = AiExecutionMode.Dag,
                Steps = steps
            };
        }

        public static string CreateChildPipelineName(
            int childDepth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                childDepth);

            return
                $"runtime-analysis-approved-child-depth-{childDepth:000}";
        }

        public static string CreateChildLogicalInvocationKey(
            int childDepth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                childDepth);

            return
                $"approved-child-depth={childDepth:000}";
        }

        public static Dictionary<string, object?> CreateRootInputs(
            string providerRequestJson)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                providerRequestJson);

            return new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                    "execution.executionId",
                [RuntimeAnalysisStepInputKeys.ProviderRequestJson] =
                    providerRequestJson,
                [RuntimeAnalysisStepInputKeys.RootAnalysisResultJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.PreviousReanalysisJson] =
                    "null",
                [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.ValidateScenarioStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.ExecuteApprovedScenarioStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.VerificationJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.VerifyScenarioOutcomeStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.HumanApprovalJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.AwaitHumanApprovalStepName}.result.output"
            };
        }

        private AiPipelineStepDefinition CreateNextApprovedChildStep(
            int currentDepth)
        {
            var nextDepth = currentDepth + 1;
            var childDefinition = CreateApprovedChildDefinition(
                nextDepth);

            return new AiPipelineStepDefinition
            {
                Name = ChildDagStepName,
                StepKey = RuntimeAnalysisStepKeys.ExecuteApprovedChildDag,
                Order = 7,
                DependsOn =
                [
                    VerifyScenarioOutcomeStepName
                ],
                Input = new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                        $"steps.{ValidateReanalysisStepName}.result.output",
                    [RuntimeAnalysisStepInputKeys.HumanApprovalJson] =
                        $"steps.{AwaitHumanApprovalStepName}.result.output",
                    [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                        $"state.{RuntimeAnalysisStepInputKeys.RootExecutionId}",
                    [RuntimeAnalysisStepInputKeys.ProviderRequestJson] =
                        $"state.{RuntimeAnalysisStepInputKeys.ProviderRequestJson}",
                    [RuntimeAnalysisStepInputKeys.RootAnalysisResultJson] =
                        $"state.{RuntimeAnalysisStepInputKeys.RootAnalysisResultJson}",
                    [RuntimeAnalysisStepInputKeys.PreviousReanalysisJson] =
                        $"steps.{ReanalysisStepName}.result.output",
                    [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                        $"state.{RuntimeAnalysisStepInputKeys.RootAnalysisResultJson}",
                    [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                        $"steps.{ExecuteApprovedScenarioStepName}.result.output",
                    [RuntimeAnalysisStepInputKeys.VerificationJson] =
                        $"steps.{VerifyScenarioOutcomeStepName}.result.output"
                },
                Config = new Dictionary<string, object?>(
                    StringComparer.Ordinal)
                {
                    [ExecuteChildDagStep.ChildDagIdConfigKey] =
                        childDefinition.Name,
                    [ExecuteChildDagStep.ChildDagVersionConfigKey] =
                        childDefinition.Version,
                    [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] =
                        CreateChildLogicalInvocationKey(
                            nextDepth),
                    [ExecuteChildDagStep.ChildDagDefinitionConfigKey] =
                        childDefinition
                },
                Execution = NoRetry()
            };
        }

        private static Dictionary<string, object?> CreateInheritedEvidenceInputs()
        {
            return new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                    $"state.{RuntimeAnalysisStepInputKeys.RootExecutionId}",
                [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.RootAnalysisResultJson}",
                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.PolicyValidationJson}",
                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.ScenarioExecutionJson}"
            };
        }

        private static Dictionary<string, object?> CreateReanalysisInputs()
        {
            return new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                    $"state.{RuntimeAnalysisStepInputKeys.RootExecutionId}",
                [RuntimeAnalysisStepInputKeys.ProviderRequestJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.ProviderRequestJson}",
                [RuntimeAnalysisStepInputKeys.RootAnalysisResultJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.RootAnalysisResultJson}",
                [RuntimeAnalysisStepInputKeys.PreviousReanalysisJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.PreviousReanalysisJson}",
                [RuntimeAnalysisStepInputKeys.ChildDagEvidenceJson] =
                    $"steps.{CaptureEvidenceStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.ScenarioExecutionJson}",
                [RuntimeAnalysisStepInputKeys.VerificationJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.VerificationJson}"
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
