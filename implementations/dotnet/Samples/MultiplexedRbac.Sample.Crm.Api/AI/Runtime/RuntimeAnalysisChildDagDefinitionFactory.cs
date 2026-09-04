using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    /// <summary>
    /// Builds the demo's recursive Child DAG definition by following the same
    /// deterministic nesting pattern exercised by the MCP production matrix.
    /// </summary>
    public sealed class RuntimeAnalysisChildDagDefinitionFactory
    {
        public const int ChildDepth = 3;

        public const string PipelineVersion = "1.0.0";

        public const string ChildDagStepName = "execute-child-dag";

        public const string CaptureEvidenceStepName =
            "capture-runtime-analysis-evidence";

        public AiPipelineDefinition CreateChildDefinition(
            string parentPipelineName,
            int remainingDepth)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                remainingDepth);

            var childPipelineName = CreateChildPipelineName(
                parentPipelineName,
                remainingDepth);

            var depth = ChildDepth - remainingDepth + 1;

            var steps = new List<AiPipelineStepDefinition>
            {
                new()
                {
                    Name = CaptureEvidenceStepName,
                    StepKey = RuntimeAnalysisStepKeys.CaptureChildDagEvidence,
                    Order = 1,
                    Input = CreateEvidenceInputs(),
                    Config = new Dictionary<string, object?>(
                        StringComparer.Ordinal)
                    {
                        [RuntimeAnalysisStepConfigKeys.ChildDagDepth] =
                            depth
                    },
                    Execution = NoRetry()
                }
            };

            if (remainingDepth > 1)
            {
                var nestedChild = CreateChildDefinition(
                    childPipelineName,
                    remainingDepth - 1);

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = ChildDagStepName,
                        StepKey = ExecuteChildDagStep.StepKey,
                        Order = 2,
                        DependsOn =
                        [
                            CaptureEvidenceStepName
                        ],
                        Input = CreateEvidenceInputs(),
                        Config = new Dictionary<string, object?>(
                            StringComparer.Ordinal)
                        {
                            [ExecuteChildDagStep.ChildDagIdConfigKey] =
                                nestedChild.Name,
                            [ExecuteChildDagStep.ChildDagVersionConfigKey] =
                                nestedChild.Version,
                            [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] =
                                CreateChildLogicalInvocationKey(
                                    childPipelineName,
                                    remainingDepth - 1),
                            [ExecuteChildDagStep.ChildDagDefinitionConfigKey] =
                                nestedChild
                        },
                        Execution = NoRetry()
                    });
            }

            return new AiPipelineDefinition
            {
                Name = childPipelineName,
                Version = PipelineVersion,
                ExecutionMode = AiExecutionMode.Dag,
                Steps = steps
            };
        }

        public static string CreateChildPipelineName(
            string parentPipelineName,
            int remainingDepth)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                remainingDepth);

            return
                $"{parentPipelineName}-child-depth-{remainingDepth:000}";
        }

        public static string CreateChildLogicalInvocationKey(
            string parentPipelineName,
            int remainingDepth)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                remainingDepth);

            return
                $"{parentPipelineName}|child-depth={remainingDepth}";
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
            return new Dictionary<string, object?>(
                StringComparer.Ordinal)
            {
                [RuntimeAnalysisStepInputKeys.RootExecutionId] =
                    $"input.{RuntimeAnalysisStepInputKeys.RootExecutionId}",
                [RuntimeAnalysisStepInputKeys.AnalysisResultJson] =
                    $"input.{RuntimeAnalysisStepInputKeys.AnalysisResultJson}",
                [RuntimeAnalysisStepInputKeys.PolicyValidationJson] =
                    $"input.{RuntimeAnalysisStepInputKeys.PolicyValidationJson}",
                [RuntimeAnalysisStepInputKeys.ScenarioExecutionJson] =
                    $"input.{RuntimeAnalysisStepInputKeys.ScenarioExecutionJson}"
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
