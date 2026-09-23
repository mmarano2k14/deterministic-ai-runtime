using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Pipeline.Steps.Control;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;

namespace Multiplexed.AI.Tests.Runtime.Execution.Results
{
    public sealed class PublishExecutionResultStepTests
    {
        [Fact]
        public async Task ExecuteAsync_Publishes_Resolved_Previous_Step_Value_To_Execution_Result()
        {
            var fixture = CreateFixture("execution-1");
            var previous = fixture.StateWriter.GetOrCreateStep(fixture.State, "final-answer");
            previous.Status = AiStepExecutionStatus.Completed;
            previous.Result = AiStepResult.Ok(
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = "final answer"
                });

            var resolved = CreateResolvedStep(
                source: "steps.final-answer.result.data.value");

            var result = await resolved.Step.ExecuteAsync(
                new AiStepExecutionContext(fixture.Execution, resolved));

            Assert.True(result.Success);
            Assert.Equal("final answer", fixture.State.Data[AiExecutionKeys.Result]);
            Assert.Equal("final answer", result.Value);
            Assert.Equal(
                AiExecutionKeys.Result,
                Assert.IsType<string>(result.Data["resultKey"]));
        }

        [Fact]
        public async Task ExecuteAsync_Uses_Explicit_Result_Key_When_Configured()
        {
            var fixture = CreateFixture("execution-2");
            fixture.State.Data["candidate"] = "hello";

            var resolved = CreateResolvedStep(
                source: "state.candidate",
                resultKey: "answer");

            await resolved.Step.ExecuteAsync(
                new AiStepExecutionContext(fixture.Execution, resolved));

            Assert.Equal("hello", fixture.State.Data["answer"]);
            Assert.False(fixture.State.Data.ContainsKey(AiExecutionKeys.Result));
        }

        [Fact]
        public async Task ExecuteAsync_When_Source_Path_Cannot_Be_Resolved_Fails_Closed()
        {
            var fixture = CreateFixture("execution-3");
            var resolved = CreateResolvedStep(
                source: "steps.missing.result.data.value");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolved.Step.ExecuteAsync(
                    new AiStepExecutionContext(fixture.Execution, resolved)));

            Assert.Contains(
                "Required runtime path 'steps.missing.result.data.value' could not be resolved",
                exception.Message,
                StringComparison.Ordinal);
            Assert.False(fixture.State.Data.ContainsKey(AiExecutionKeys.Result));
        }

        [Fact]
        public async Task ExecuteAsync_When_Source_Is_Not_A_Runtime_Path_Fails_Closed()
        {
            var fixture = CreateFixture("execution-4");
            var resolved = CreateResolvedStep(source: "literal-value");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolved.Step.ExecuteAsync(
                    new AiStepExecutionContext(fixture.Execution, resolved)));

            Assert.Contains(
                "is not a supported runtime path expression",
                exception.Message,
                StringComparison.Ordinal);
            Assert.False(fixture.State.Data.ContainsKey(AiExecutionKeys.Result));
        }

        private static ResolvedAiPipelineStep CreateResolvedStep(
            string source,
            string? resultKey = null)
        {
            var config = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PublishExecutionResultStep.SourceConfigKey] = source
            };

            if (!string.IsNullOrWhiteSpace(resultKey))
            {
                config[PublishExecutionResultStep.ResultKeyConfigKey] = resultKey;
            }

            return new ResolvedAiPipelineStep
            {
                Name = "publish-result",
                StepKey = PublishExecutionResultStep.StepKey,
                Step = new PublishExecutionResultStep(),
                Input = new Dictionary<string, object?>(StringComparer.Ordinal),
                Config = config
            };
        }

        private static TestFixture CreateFixture(string executionId)
        {
            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = "result-demo",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };
            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName
            };

            var baseline = ChildDagCompositionTestData.CreateExecutionContext(record, state);
            var resolver = new DefaultAiContextValueResolver();
            var services = new ServiceCollection()
                .AddSingleton<IAiExecutionStateWriter>(baseline.StateWriter)
                .AddSingleton<IAiContextValueResolver>(resolver)
                .AddSingleton<IAiStepContextHelperFactory, DefaultAiStepContextHelperFactory>()
                .BuildServiceProvider();

            var execution = new AiExecutionContext(
                record,
                state,
                services,
                baseline.StateReader,
                baseline.StateWriter,
                CancellationToken.None);

            return new TestFixture(
                execution,
                state,
                baseline.StateWriter);
        }

        private sealed record TestFixture(
            AiExecutionContext Execution,
            AiExecutionState State,
            IAiExecutionStateWriter StateWriter);
    }
}
