using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Convergence;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Convergence
{
    /// <summary>
    /// Validates DAG convergence semantics when one or more steps wait for an external durable condition.
    /// </summary>
    public sealed class AiDagExternalWaitingConvergenceTests
    {
        [Fact]
        public async Task EvaluateAsync_Should_Remain_Running_When_Independent_Sibling_Is_Runnable()
        {
            var pipeline = CreatePipeline(
                CreateResolvedStep("child", 0),
                CreateResolvedStep("independent", 1));

            var state = CreateState(
                new AiStepState
                {
                    StepName = "child",
                    Status = AiStepExecutionStatus.WaitingForExternal
                },
                new AiStepState
                {
                    StepName = "independent",
                    Status = AiStepExecutionStatus.Ready
                });

            var result = await AiDagExecutionConvergenceEvaluator.EvaluateAsync(
                pipeline,
                state,
                NoOpStateWriter.Instance,
                StateBackedStepResolver.Instance,
                DateTime.UtcNow);

            Assert.Equal(AiExecutionStatus.Running, result.Status);
        }

        [Fact]
        public async Task EvaluateAsync_Should_Wait_When_No_Runnable_Work_Remains_And_External_Completion_Is_Required()
        {
            var pipeline = CreatePipeline(
                CreateResolvedStep("child", 0),
                CreateResolvedStep("after-child", 1, "child"));

            var state = CreateState(
                new AiStepState
                {
                    StepName = "child",
                    Status = AiStepExecutionStatus.WaitingForExternal
                },
                new AiStepState
                {
                    StepName = "after-child",
                    Status = AiStepExecutionStatus.None,
                    DependsOn = new List<string> { "child" }
                });

            var result = await AiDagExecutionConvergenceEvaluator.EvaluateAsync(
                pipeline,
                state,
                NoOpStateWriter.Instance,
                StateBackedStepResolver.Instance,
                DateTime.UtcNow);

            Assert.Equal(AiExecutionStatus.Waiting, result.Status);
            Assert.False(result.IsTerminal);
        }

        private static ResolvedAiPipeline CreatePipeline(params ResolvedAiPipelineStep[] steps)
        {
            return new ResolvedAiPipeline
            {
                Name = "external-wait",
                Version = "v1",
                ExecutionMode = AiExecutionMode.Dag,
                Steps = steps
            };
        }

        private static ResolvedAiPipelineStep CreateResolvedStep(
            string name,
            int order,
            params string[] dependsOn)
        {
            return new ResolvedAiPipelineStep
            {
                Name = name,
                StepKey = name,
                Step = NoOpStep.Instance,
                Order = order,
                DependsOn = dependsOn
            };
        }

        private static AiExecutionState CreateState(params AiStepState[] steps)
        {
            return new AiExecutionState
            {
                ExecutionId = "execution-1",
                PipelineName = "external-wait",
                Steps = steps.ToDictionary(step => step.StepName, StringComparer.Ordinal)
            };
        }

        private sealed class NoOpStep : IAiStep
        {
            public static NoOpStep Instance { get; } = new();

            public string Name => "noop";

            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(AiStepResult.Ok());
            }
        }

        private sealed class StateBackedStepResolver : IAiExecutionStepResolver
        {
            public static StateBackedStepResolver Instance { get; } = new();

            public Task WarmAsync(
                string executionId,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task WarmStepsAsync(
                string executionId,
                AiExecutionState state,
                IReadOnlyCollection<string> stepNames,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<AiStepState?> GetStepAsync(
                string executionId,
                string stepName,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                state.Steps.TryGetValue(stepName, out var step);
                return Task.FromResult(step);
            }

            public Task<AiStepState?> GetStepStatusAsync(
                string executionId,
                string stepName,
                AiExecutionState state,
                CancellationToken cancellationToken = default)
            {
                return GetStepAsync(executionId, stepName, state, cancellationToken);
            }
        }

        private sealed class NoOpStateWriter : IAiExecutionStateWriter
        {
            public static NoOpStateWriter Instance { get; } = new();

            public void SetData<T>(AiExecutionState state, string key, T value) => throw new NotSupportedException();
            public bool RemoveData(AiExecutionState state, string key) => throw new NotSupportedException();
            public void SetDataPayload(AiExecutionState state, string key, AiStoredPayload payload) => throw new NotSupportedException();
            public bool RemoveDataPayload(AiExecutionState state, string key) => throw new NotSupportedException();
            public void SetMetadata<T>(AiExecutionState state, string key, T value) => throw new NotSupportedException();
            public bool RemoveMetadata(AiExecutionState state, string key) => throw new NotSupportedException();
            public void SetMetadataPayload(AiExecutionState state, string key, AiStoredPayload payload) => throw new NotSupportedException();
            public bool RemoveMetadataPayload(AiExecutionState state, string key) => throw new NotSupportedException();
            public void EnsureStepInitialized(AiExecutionState state, ResolvedAiPipelineStep stepDefinition) => throw new NotSupportedException();
            public AiStepState GetOrCreateStep(AiExecutionState state, string stepName) => throw new NotSupportedException();
            public void SetStepResult(AiExecutionState state, string stepName, AiStepResult result) => throw new NotSupportedException();
        }
    }
}
