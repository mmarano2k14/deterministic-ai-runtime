using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Pipeline;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Creates reusable MCP integration test pipeline run requests.
    /// </summary>
    public static class McpTestPipelineFactory
    {
        public static AiRuntimePipelineRunRequest CreateRunRequest(
            string pipelineName,
            int stepCount,
            object? input = null,
            bool enableRetention = false,
            int flakyStepInterval = 0,
            McpTestCrashCheckpointDefinition? crashCheckpoint = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);

            ValidateCrashCheckpoint(
                stepCount,
                crashCheckpoint);

            return new AiRuntimePipelineRunRequest
            {
                PipelineName = pipelineName,
                PipelineDefinition = CreatePipelineDefinition(
                    pipelineName,
                    stepCount,
                    enableRetention,
                    flakyStepInterval,
                    crashCheckpoint),
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
            McpTestCrashCheckpointDefinition? crashCheckpoint = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepCount);

            ValidateCrashCheckpoint(
                stepCount,
                crashCheckpoint);

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

            return new AiPipelineDefinition
            {
                Name = pipelineName,
                Version = "1.0.0",
                ExecutionMode = AiExecutionMode.Dag,
                Config = CreatePipelineConfig(enableRetention),
                Steps = steps
            };
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