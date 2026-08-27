using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Execution;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Creates reusable MCP integration test pipeline run requests.
    /// </summary>
    public static class McpTestPipelineFactory
    {
        /// <summary>
        /// Gets the declarative pipeline version used by MCP integration-test definitions.
        /// </summary>
        public const string PipelineVersion = "1.0.0";

        /// <summary>
        /// Gets the stable logical step name used by production test pipelines for one child DAG call-site.
        /// </summary>
        public const string ChildDagStepName = "execute-child-dag";

        public static AiRuntimePipelineRunRequest CreateRunRequest(
            string pipelineName,
            int stepCount,
            object? input = null,
            bool enableRetention = false,
            int flakyStepInterval = 0,
            McpTestCrashCheckpointDefinition? crashCheckpoint = null,
            int childDepth = 0,
            McpTestCrashCheckpointDefinition? childCrashCheckpoint = null,
            int childCrashCheckpointDepth = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
            ArgumentOutOfRangeException.ThrowIfNegative(childDepth);

            ValidateCrashCheckpoint(
                stepCount,
                crashCheckpoint);

            ValidateChildCrashCheckpoint(
                stepCount,
                childDepth,
                childCrashCheckpoint,
                childCrashCheckpointDepth);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = pipelineName,
                PipelineDefinition = CreatePipelineDefinition(
                    pipelineName,
                    stepCount,
                    enableRetention,
                    flakyStepInterval,
                    crashCheckpoint,
                    childDepth,
                    childCrashCheckpoint,
                    childCrashCheckpointDepth),
                Input = input ?? new
                {
                    source = "mcp-integration-test",
                    stepCount,
                    enableRetention,
                    flakyStepInterval
                }
            };
        }

        public static AiPipelineDefinition CreatePipelineDefinition(
            string pipelineName,
            int stepCount,
            bool enableRetention = false,
            int flakyStepInterval = 0,
            McpTestCrashCheckpointDefinition? crashCheckpoint = null,
            int childDepth = 0,
            McpTestCrashCheckpointDefinition? childCrashCheckpoint = null,
            int childCrashCheckpointDepth = 0)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);
            ArgumentOutOfRangeException.ThrowIfNegative(childDepth);

            ValidateCrashCheckpoint(
                stepCount,
                crashCheckpoint);

            ValidateChildCrashCheckpoint(
                stepCount,
                childDepth,
                childCrashCheckpoint,
                childCrashCheckpointDepth);

            var steps = new List<AiPipelineStepDefinition>();

            for (var index = 1; index <= stepCount; index++)
            {
                var isCrashCheckpoint =
                    crashCheckpoint?.StepIndex == index;

                var isFlaky =
                    !isCrashCheckpoint &&
                    IsFlakyStep(
                        index,
                        flakyStepInterval);

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = ToStepName(index),
                        StepKey = isCrashCheckpoint
                            ? McpTestCrashCheckpointDefinition.StepKey
                            : isFlaky
                                ? "distributed.chaos.flaky-provider"
                                : "hello-world",
                        Order = index,
                        DependsOn = CreateVariableDependencies(
                            index,
                            stepCount,
                            crashCheckpoint),
                        Config = CreateStepConfig(
                            pipelineName,
                            index,
                            isFlaky,
                            isCrashCheckpoint
                                ? crashCheckpoint
                                : null)
                    });
            }

            if (childDepth > 0)
            {
                var childOwnCrashCheckpoint =
                    childCrashCheckpointDepth == 1
                        ? childCrashCheckpoint
                        : null;

                var nestedChildCrashCheckpoint =
                    childCrashCheckpointDepth > 1
                        ? childCrashCheckpoint
                        : null;

                var nestedChildCrashCheckpointDepth =
                    childCrashCheckpointDepth > 1
                        ? childCrashCheckpointDepth - 1
                        : 0;

                var childDefinition = CreatePipelineDefinition(
                    CreateChildPipelineName(pipelineName, childDepth),
                    stepCount,
                    enableRetention,
                    flakyStepInterval,
                    crashCheckpoint: childOwnCrashCheckpoint,
                    childDepth: childDepth - 1,
                    childCrashCheckpoint: nestedChildCrashCheckpoint,
                    childCrashCheckpointDepth: nestedChildCrashCheckpointDepth);

                steps.Add(
                    new AiPipelineStepDefinition
                    {
                        Name = ChildDagStepName,
                        StepKey = ExecuteChildDagStep.StepKey,
                        Order = stepCount + 1,
                        DependsOn = steps
                            .Select(step => step.Name)
                            .ToArray(),
                        Config = new Dictionary<string, object?>
                        {
                            [ExecuteChildDagStep.ChildDagIdConfigKey] = childDefinition.Name,
                            [ExecuteChildDagStep.ChildDagVersionConfigKey] = childDefinition.Version,
                            [ExecuteChildDagStep.LogicalInvocationKeyConfigKey] = CreateChildLogicalInvocationKey(pipelineName, childDepth),
                            [ExecuteChildDagStep.ChildDagDefinitionConfigKey] = childDefinition
                        }
                    });

            }

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = PipelineVersion,
                ExecutionMode = AiExecutionMode.Dag,
                Config = CreatePipelineConfig(enableRetention),
                Steps = steps
            };
        }

        /// <summary>
        /// Creates the deterministic child pipeline name used by one production-test nesting level.
        /// </summary>
        /// <param name="parentPipelineName">The parent pipeline name.</param>
        /// <param name="childDepth">The remaining child depth at the parent call-site.</param>
        /// <returns>The child pipeline name embedded in the parent definition.</returns>
        public static string CreateChildPipelineName(
            string parentPipelineName,
            int childDepth)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childDepth);

            return $"{parentPipelineName}-child-depth-{childDepth:000}";
        }

        /// <summary>
        /// Creates the deterministic business invocation key used by one production-test child call-site.
        /// </summary>
        /// <param name="parentPipelineName">The parent pipeline name.</param>
        /// <param name="childDepth">The remaining child depth at the parent call-site.</param>
        /// <returns>The canonical logical invocation key stored in the child relation identity.</returns>
        public static string CreateChildLogicalInvocationKey(
            string parentPipelineName,
            int childDepth)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(parentPipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childDepth);

            return $"{parentPipelineName}|child-depth={childDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        private static string[] CreateVariableDependencies(
            int stepIndex,
            int stepCount,
            McpTestCrashCheckpointDefinition? crashCheckpoint)
        {
            if (stepIndex <= 1)
            {
                return [];
            }

            var previousStepCount = stepIndex - 1;

            if (crashCheckpoint?.StepIndex == stepIndex)
            {
                return Enumerable.Range(1, previousStepCount)
                    .Select(ToStepName)
                    .ToArray();
            }

            var minDependencyCount = Math.Max(
                1,
                (int)Math.Floor(stepCount * 0.10));

            var maxDependencyCount = Math.Max(
                minDependencyCount,
                (int)Math.Ceiling(stepCount * 0.20));

            var targetDependencyCount = GetDeterministicDependencyCount(
                stepIndex,
                minDependencyCount,
                maxDependencyCount);

            var dependencyCount = Math.Min(
                previousStepCount,
                targetDependencyCount);

            var dependencyIndexes =
                Enumerable.Range(1, previousStepCount)
                    .Where(index => ShouldSelectDependency(
                        stepIndex,
                        index,
                        dependencyCount,
                        previousStepCount))
                    .Take(dependencyCount)
                    .ToHashSet();

            if (crashCheckpoint is not null &&
                stepIndex > crashCheckpoint.StepIndex)
            {
                dependencyIndexes.Add(
                    crashCheckpoint.StepIndex);
            }

            return dependencyIndexes
                .OrderBy(index => index)
                .Select(ToStepName)
                .ToArray();
        }

        private static int GetDeterministicDependencyCount(
            int stepIndex,
            int minDependencyCount,
            int maxDependencyCount)
        {
            if (minDependencyCount == maxDependencyCount)
            {
                return minDependencyCount;
            }

            var range = maxDependencyCount - minDependencyCount + 1;

            return minDependencyCount + stepIndex % range;
        }

        private static bool ShouldSelectDependency(
            int stepIndex,
            int candidateDependencyIndex,
            int dependencyCount,
            int previousStepCount)
        {
            if (dependencyCount >= previousStepCount)
            {
                return true;
            }

            var stride = Math.Max(
                1,
                previousStepCount / dependencyCount);

            return candidateDependencyIndex == 1 ||
                   candidateDependencyIndex == previousStepCount ||
                   (candidateDependencyIndex + stepIndex) % stride == 0;
        }

        private static Dictionary<string, object?> CreatePipelineConfig(
            bool enableRetention)
        {
            return new Dictionary<string, object?>
            {
                ["concurrency"] = new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["maxDegreeOfParallelism"] = 12,
                    ["maxProviderConcurrency"] = 3,
                    ["leaseSeconds"] = 60,
                    ["defaultRetryAfterMs"] = 10,
                    ["jitter"] = false
                },
                ["retention"] = new Dictionary<string, object?>
                {
                    ["enabled"] = enableRetention,
                    ["policies"] = enableRetention
                        ? new[]
                        {
                            "retention.compact.terminal",
                            "retention.evict.terminal"
                        }
                        : Array.Empty<string>(),
                    ["archiveReason"] = "mcp-test-retention",
                    ["trigger"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = enableRetention,
                        ["maxStepsInState"] = 15,
                        ["maxCompletedStepsInState"] = 15,
                        ["maxInlinePayloadBytes"] = 1
                    }
                }
            };
        }

        private static Dictionary<string, object?> CreateStepConfig(
            string pipelineName,
            int index,
            bool isFlaky,
            McpTestCrashCheckpointDefinition? crashCheckpoint)
        {
            var config = new Dictionary<string, object?>
            {
                ["provider"] = "openai",
                ["model"] = "gpt-4.1",
                ["operation"] = "llm.chat",
                ["delayMs"] = isFlaky ? 10 : 1
            };

            if (crashCheckpoint is not null)
            {
                config["test.crashCheckpoint.stateKey"] =
                    crashCheckpoint.StateKey;

                config["test.crashCheckpoint.reachedChannel"] =
                    crashCheckpoint.ReachedChannel;

                config["test.crashCheckpoint.releasedChannel"] =
                    crashCheckpoint.ReleasedChannel;

                config["test.crashCheckpoint.ttlSeconds"] =
                    crashCheckpoint.TtlSeconds;
            }

            if (isFlaky)
            {
                config["attemptKey"] =
                    $"{pipelineName}:step-{index:000}";

                config["retry"] = new Dictionary<string, object?>
                {
                    ["maxRetries"] = 2,
                    ["strategy"] = "Fixed",
                    ["baseDelayMs"] = 15,
                    ["maxDelayMs"] = 15,
                    ["jitter"] = false
                };
            }

            return config;
        }

        private static void ValidateChildCrashCheckpoint(
            int stepCount,
            int childDepth,
            McpTestCrashCheckpointDefinition? childCrashCheckpoint,
            int childCrashCheckpointDepth)
        {
            if (childCrashCheckpoint is null)
            {
                if (childCrashCheckpointDepth != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(childCrashCheckpointDepth),
                        childCrashCheckpointDepth,
                        "Child crash checkpoint depth must be zero when no child crash checkpoint is configured.");
                }

                return;
            }

            ValidateCrashCheckpoint(
                stepCount,
                childCrashCheckpoint);

            if (childDepth <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childDepth),
                    childDepth,
                    "A child crash checkpoint requires at least one child DAG level.");
            }

            if (childCrashCheckpointDepth <= 0 ||
                childCrashCheckpointDepth > childDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childCrashCheckpointDepth),
                    childCrashCheckpointDepth,
                    $"Child crash checkpoint depth must be between 1 and '{childDepth}'.");
            }
        }

        private static void ValidateCrashCheckpoint(
            int stepCount,
            McpTestCrashCheckpointDefinition? crashCheckpoint)
        {
            if (crashCheckpoint is null)
            {
                return;
            }

            if (crashCheckpoint.StepIndex <= 1 ||
                crashCheckpoint.StepIndex > stepCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(crashCheckpoint),
                    crashCheckpoint.StepIndex,
                    $"The crash checkpoint step index must be between 2 and '{stepCount}'.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(
                crashCheckpoint.StateKey);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                crashCheckpoint.ReachedChannel);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                crashCheckpoint.ReleasedChannel);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                crashCheckpoint.TtlSeconds);
        }

        private static bool IsFlakyStep(
            int index,
            int flakyStepInterval)
        {
            return flakyStepInterval > 0 &&
                   index % flakyStepInterval == 0;
        }

        private static string ToStepName(
            int index)
        {
            return $"step-{index:000}";
        }
    }
}
