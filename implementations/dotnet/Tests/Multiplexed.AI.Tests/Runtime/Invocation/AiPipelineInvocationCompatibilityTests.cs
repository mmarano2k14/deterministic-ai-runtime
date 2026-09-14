using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Tests.Models;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiPipelineInvocationCompatibilityTests
    {
        [Fact]
        public async Task Historical_Native_Pipeline_Preserves_Registry_Defaults_And_Identity()
        {
            var registry = new CountingRegistry();
            var definition = new AiPipelineDefinition
            {
                Name = "legacy", Steps = new[] { Native("first"), Native("second") }
            };
            var pipeline = await new AiPipelineResolver(registry).ResolveAsync(definition);
            Assert.Equal(AiExecutionMode.Sequential, pipeline.ExecutionMode);
            Assert.Null(pipeline.ExecutionLanguage);
            Assert.Equal(2, registry.ResolveCalls);
            Assert.Equal(0, registry.Implementation.ExecuteCalls);
            Assert.Equal(new[] { "first", "second" }, pipeline.Steps.Select(x => x.Name));
            Assert.All(pipeline.Steps, step =>
            {
                Assert.Same(registry.Implementation, step.Step);
                Assert.Equal(1, step.MaxRetries);
                Assert.Equal(500, step.RetryDelayMs);
                Assert.Null(step.InvocationBinding);
            });
        }

        [Theory]
        [InlineData("dotnet")]
        [InlineData("python")]
        [InlineData("typescript")]
        public async Task Language_Default_Does_Not_Convert_Native_Step_Or_Instantiate_Retry_Block(string language)
        {
            var registry = new CountingRegistry();
            var step = Native("work");
            var definition = new AiPipelineDefinition { Name = "test", ExecutionLanguage = language, Steps = new[] { step } };
            var pipeline = await new AiPipelineResolver(registry).ResolveAsync(definition);
            var resolved = Assert.Single(pipeline.Steps);
            Assert.Same(registry.Implementation, resolved.Step);
            Assert.Equal(language, pipeline.ExecutionLanguage);
            Assert.Null(step.Execution);
            Assert.Null(resolved.ExecutionLanguage);
            Assert.Equal(1, resolved.MaxRetries);
            Assert.Equal(500, resolved.RetryDelayMs);
            Assert.Equal(0, registry.Implementation.ExecuteCalls);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(3, 125)]
        public async Task Explicit_Retry_Values_Are_Unchanged(int count, int delay)
        {
            var step = new AiPipelineStepDefinition
            {
                Name = "work", StepKey = "same-key",
                Execution = new AiPipelineStepExecutionDefinition { MaxRetries = count, RetryDelayMs = delay }
            };
            var pipeline = await new AiPipelineResolver(new CountingRegistry()).ResolveAsync(
                new AiPipelineDefinition { Name = "test", ExecutionLanguage = "python", Steps = new[] { step } });
            Assert.Equal(count, Assert.Single(pipeline.Steps).MaxRetries);
            Assert.Equal(delay, pipeline.Steps[0].RetryDelayMs);
        }

        [Theory]
        [InlineData(AiInvocationKind.Custom)]
        [InlineData(AiInvocationKind.Mcp)]
        public async Task Uninstalled_Adapter_Rejects_Before_Any_Native_Registry_Lookup(AiInvocationKind kind)
        {
            var registry = new CountingRegistry();
            var descriptor = kind == AiInvocationKind.Custom
                ? new AiInvocationDefinition { Kind = kind, ImplementationRef = "publication/work/v1" }
                : new AiInvocationDefinition { Kind = kind, ConnectionRef = "reports", Tool = "publish" };
            var definition = new AiPipelineDefinition
            {
                Name = "test", ExecutionMode = AiExecutionMode.Dag, ExecutionLanguage = "python",
                Steps = new[]
                {
                    Native("first"),
                    new AiPipelineStepDefinition { Name = "second", StepKey = "same-key", Invocation = descriptor }
                }
            };
            await Assert.ThrowsAsync<NotSupportedException>(() => new AiPipelineResolver(registry).ResolveAsync(definition));
            Assert.Equal(0, registry.ResolveCalls);
            Assert.Equal(0, registry.Implementation.ExecuteCalls);
        }

        [Fact]
        public async Task Native_Local_Language_Contradiction_Is_Not_Ignored()
        {
            var registry = new CountingRegistry();
            var definition = new AiPipelineDefinition
            {
                Name = "test", Steps = new[]
                {
                    new AiPipelineStepDefinition { Name = "work", StepKey = "same-key", ExecutionLanguage = "python" }
                }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipelineResolver(registry).ResolveAsync(definition));
            Assert.Equal(0, registry.ResolveCalls);
        }

        [Fact]
        public async Task Explicit_Native_Descriptor_Is_Preserved_Without_Changing_Implementation()
        {
            var registry = new CountingRegistry();
            var descriptor = new AiInvocationDefinition { Kind = AiInvocationKind.Native };
            var definition = new AiPipelineDefinition
            {
                Name = "test", Steps = new[]
                {
                    new AiPipelineStepDefinition { Name = "work", StepKey = "same-key", Invocation = descriptor }
                }
            };
            var step = Assert.Single((await new AiPipelineResolver(registry).ResolveAsync(definition)).Steps);
            Assert.Same(descriptor, step.Invocation);
            Assert.Equal(AiInvocationBinding.Native, step.InvocationBinding);
            Assert.Same(registry.Implementation, step.Step);
        }

        [Fact]
        public async Task Sequential_Preparation_Copy_Preserves_Declared_Default()
        {
            var registry = new CountingRegistry();
            var definition = new AiPipelineDefinition
            {
                Name = "test", ExecutionLanguage = "python", Steps = new[] { Native("work") }
            };
            var provider = new FakeInMemoryAiPipelineDefinitionProvider(new[] { definition });
            var executor = new AiSequentialPipelineExecutor(
                new FakeAiPipelineDefinitionSourceSelector(provider), new AiPipelineResolver(registry), new FakeStepExecutor());
            var pipeline = await executor.PrepareAsync(definition);
            Assert.Equal("python", pipeline.ExecutionLanguage);
            Assert.Equal(AiExecutionMode.Sequential, pipeline.ExecutionMode);
            Assert.Equal(0, registry.Implementation.ExecuteCalls);
        }

        [Fact]
        public async Task Structural_Validation_Still_Precedes_Registry_Resolution()
        {
            var registry = new CountingRegistry();
            var definition = new AiPipelineDefinition { Name = "test", Steps = new[] { Native("same"), Native("same") } };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipelineResolver(registry).ResolveAsync(definition));
            Assert.Equal(0, registry.ResolveCalls);
        }

        [Fact]
        public async Task Cancellation_Prevents_Registry_Resolution()
        {
            var registry = new CountingRegistry();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiPipelineResolver(registry).ResolveAsync(
                new AiPipelineDefinition { Name = "test", Steps = new[] { Native("work") } }, cancellation.Token));
            Assert.Equal(0, registry.ResolveCalls);
        }

        private static AiPipelineStepDefinition Native(string name) => new() { Name = name, StepKey = "same-key" };

        private sealed class CountingRegistry : IAiStepRegistry
        {
            public int ResolveCalls { get; private set; }
            public CountingStep Implementation { get; } = new();
            public IAiStep Resolve(string stepKey)
            {
                ResolveCalls++;
                Assert.Equal("same-key", stepKey);
                return Implementation;
            }
        }

        private sealed class CountingStep : IAiStep
        {
            public string Name => "native-implementation";
            public int ExecuteCalls { get; private set; }
            public Task<AiStepResult> ExecuteAsync(AiStepExecutionContext context, CancellationToken cancellationToken = default)
            {
                ExecuteCalls++;
                return Task.FromResult(AiStepResult.Ok("native"));
            }
        }
    }
}
