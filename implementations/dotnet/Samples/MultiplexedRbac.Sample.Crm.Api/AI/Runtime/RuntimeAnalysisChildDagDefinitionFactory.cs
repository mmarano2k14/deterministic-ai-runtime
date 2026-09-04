using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    /// <summary>
    /// Builds one durable child execution for one approved decision.
    /// </summary>
    /// <remarks>
    /// Child depth is deliberately NOT pre-expanded.
    ///
    /// The product invariant is:
    ///
    /// one durable approval -> one durable child execution.
    ///
    /// A deeper child may only be created later after that child has its own
    /// re-analysis, deterministic policy validation, and human approval.
    ///
    /// The MCP production matrix remains the proof that the runtime primitive
    /// itself supports recursive depth. The demo no longer turns that proof
    /// shape into automatic product semantics.
    /// </remarks>
    public sealed class RuntimeAnalysisChildDagDefinitionFactory
    {
        public const int InitialApprovedChildDepth = 1;

        public const int MaxProjectedApprovalDepth = 16;

        public const string PipelineVersion = "2.0.0";

        public const string ChildDagStepName = "execute-child-dag";

        public const string CaptureEvidenceStepName =
            "capture-runtime-analysis-evidence";

        public AiPipelineDefinition CreateApprovedChildDefinition(
            int childDepth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                childDepth);

            if (childDepth > MaxProjectedApprovalDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childDepth),
                    childDepth,
                    $"Approval-driven Child DAG depth cannot exceed {MaxProjectedApprovalDepth}.");
            }

            return new AiPipelineDefinition
            {
                Name = CreateChildPipelineName(
                    childDepth),
                Version = PipelineVersion,
                ExecutionMode = AiExecutionMode.Dag,
                Steps =
                [
                    new AiPipelineStepDefinition
                    {
                        Name = CaptureEvidenceStepName,
                        StepKey =
                            RuntimeAnalysisStepKeys.CaptureChildDagEvidence,
                        Order = 1,
                        Input = CreateEvidenceInputs(),
                        Config = new Dictionary<string, object?>(
                            StringComparer.Ordinal)
                        {
                            [RuntimeAnalysisStepConfigKeys.ChildDagDepth] =
                                childDepth
                        },
                        Execution = NoRetry()
                    }
                ]
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

        public static Dictionary<string, object?> CreateRootInputs()
        {
            return new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                    "execution.executionId",
                [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.AnalyzeStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.ValidateScenarioStepName}.result.output",
                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                    $"steps.{RuntimeAnalysisPipelineDefinitionFactory.ExecuteApprovedScenarioStepName}.result.output"
            };
        }

        private static Dictionary<string, object?> CreateEvidenceInputs()
        {
            // Frozen child invocation input is seeded into the child
            // execution's structured state by the normal DAG creation path.
            return new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                    $"state.{RuntimeAnalysisStepInputKeys.RootExecutionId}",
                [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.AnalysisResultJson}",
                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.PolicyValidationJson}",
                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                    $"state.{RuntimeAnalysisStepInputKeys.ScenarioExecutionJson}"
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
