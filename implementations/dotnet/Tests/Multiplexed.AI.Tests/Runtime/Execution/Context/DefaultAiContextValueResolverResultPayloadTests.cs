using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.State;

namespace Multiplexed.AI.Tests.Runtime.Execution.Context
{
    public sealed class DefaultAiContextValueResolverResultPayloadTests
    {
        [Fact]
        public async Task ResolveRequiredPathAsync_Traverses_Inline_Json_Result_Payload()
        {
            var payload = AiStoredPayload.Inline(
                "{\"data\":{\"result\":\"child answer\"}}",
                contentType: "application/json");
            var (resolver, context) = CreateContext(payload);

            var value = await resolver.ResolveRequiredPathAsync<string>(
                context,
                "steps.delegate.result.payload.data.result");

            Assert.Equal("child answer", value);
        }

        [Fact]
        public async Task ResolveRequiredPathAsync_Traverses_Artifact_Json_Result_Payload()
        {
            var payload = AiStoredPayload.Artifact(
                "child-result-1",
                contentType: "application/json");
            var (resolver, context) = CreateContext(
                payload,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["child-result-1"] = "{\"data\":{\"result\":\"artifact child answer\"}}"
                });

            var value = await resolver.ResolveRequiredPathAsync<string>(
                context,
                "steps.delegate.result.payload.data.result");

            Assert.Equal("artifact child answer", value);
        }

        [Fact]
        public async Task ResolveAsync_When_Result_Payload_Is_Missing_Preserves_Raw_Fallback()
        {
            var (resolver, context) = CreateContext(payload: null);
            const string path = "steps.delegate.result.payload.data.result";

            var value = await resolver.ResolveAsync<string>(context, path);

            Assert.Equal(path, value);
        }

        [Fact]
        public async Task ResolveRequiredPathAsync_When_Inline_Json_Result_Payload_Is_Invalid_Fails_Closed()
        {
            var payload = AiStoredPayload.Inline(
                "not-json",
                contentType: "application/json");
            var (resolver, context) = CreateContext(payload);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolver.ResolveRequiredPathAsync<string>(
                    context,
                    "steps.delegate.result.payload.data.result"));

            Assert.Contains(
                "declares JSON content but does not contain valid JSON",
                exception.Message,
                StringComparison.Ordinal);
        }

        private static (DefaultAiContextValueResolver Resolver, AiStepExecutionContext Context) CreateContext(
            AiStoredPayload? payload,
            IReadOnlyDictionary<string, string>? artifacts = null)
        {
            var payloadResolver = new TestPayloadResolver(artifacts);
            var services = new ServiceCollection()
                .AddSingleton<IAiExecutionPayloadResolver>(payloadResolver)
                .BuildServiceProvider();

            var record = new AiExecutionRecord
            {
                ExecutionId = "execution-1",
                PipelineName = "parent",
                ExecutionMode = AiExecutionMode.Dag,
                Status = AiExecutionStatus.Running
            };
            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = record.PipelineName,
                Steps = new Dictionary<string, AiStepState>(StringComparer.Ordinal)
                {
                    ["delegate"] = new AiStepState
                    {
                        StepName = "delegate",
                        Status = AiStepExecutionStatus.Completed,
                        Result = payload is null
                            ? AiStepResult.Ok(output: "child completed")
                            : AiStepResult.OkPayload(payload, output: "child completed")
                    }
                }
            };
            var stateReader = new DefaultAiExecutionStateReader(payloadResolver);
            var stateWriter = new DefaultAiExecutionStateWriter();
            var execution = new AiExecutionContext(
                record,
                state,
                services,
                stateReader,
                stateWriter,
                CancellationToken.None);
            var current = new ResolvedAiPipelineStep
            {
                Name = "after-child",
                StepKey = "test.noop",
                Step = new NoopStep()
            };

            return (
                new DefaultAiContextValueResolver(),
                new AiStepExecutionContext(execution, current));
        }

        private sealed class TestPayloadResolver : IAiExecutionPayloadResolver
        {
            private readonly IReadOnlyDictionary<string, string> artifacts;

            public TestPayloadResolver(IReadOnlyDictionary<string, string>? artifacts)
            {
                this.artifacts = artifacts ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }

            public Task<object?> ResolveAsync(
                AiStoredPayload payload,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (payload.IsInline)
                {
                    return Task.FromResult(payload.InlineValue);
                }

                if (string.IsNullOrWhiteSpace(payload.ArtifactId) ||
                    !artifacts.TryGetValue(payload.ArtifactId, out var json))
                {
                    throw new InvalidOperationException("Artifact not found.");
                }

                return Task.FromResult(JsonSerializer.Deserialize<object>(json));
            }
        }

        private sealed class NoopStep : IAiStep
        {
            public string Name => "test.noop";

            public Task<AiStepResult> ExecuteAsync(
                AiStepExecutionContext context,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(AiStepResult.Ok());
        }
    }
}
