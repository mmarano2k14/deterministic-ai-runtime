using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Runtime.AI.Concurrency;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Engine.Steps;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Observability.Metrics.Policy;
using Multiplexed.AI.Runtime.Observability.Tracing;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support;
using Xunit;
using static Multiplexed.AI.Tests.Runtime.Invocation.Ml1bInvocationTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    public sealed class AiStepImplementationBinderTests
    {
        [Theory]
        [InlineData("dotnet")]
        [InlineData("python")]
        [InlineData("typescript")]
        public async Task Installed_Language_Factory_Receives_Resolved_Identity_Without_Executing(string language)
        {
            var native = new ProbeRegistry();
            var factory = new ProbeFactory(AiInvocationKind.Custom, language);
            var source = Custom("analyze");
            var plan = await new AiPipelineResolver(native, new[] { factory }).ResolveAsync(CreatePipeline(new[] { source }, language));
            var step = Assert.Single(plan.Steps);
            var adapter = Assert.Single(factory.Created);
            Assert.Same(adapter, step.Step);
            Assert.Equal("test", adapter.Metadata!.PipelineName);
            Assert.Equal("v1", adapter.Metadata.PipelineVersion);
            Assert.Equal("analyze", adapter.Metadata.StepName);
            Assert.Equal("same-key", adapter.Metadata.StepKey);
            Assert.Same(step.InvocationBinding, adapter.Metadata.Binding);
            Assert.Equal(language, step.InvocationBinding!.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.Pipeline, step.InvocationBinding.LanguageSource);
            Assert.Null(source.Execution);
            Assert.Equal(1, step.MaxRetries);
            Assert.Equal(500, step.RetryDelayMs);
            Assert.Equal(0, native.Calls);
            Assert.Empty(adapter.Calls);
        }

        [Fact]
        public async Task Mixed_Plan_Routes_Native_Custom_And_Mcp_Without_Language_Contamination()
        {
            var native = new ProbeRegistry();
            var factories = Factories();
            var definition = CreatePipeline(new[] { Custom("first"), Custom("local", "typescript", 1), Mcp(), Native(), Custom("last", order: 2) });
            var plan = await new AiPipelineResolver(native, factories).ResolveAsync(definition);
            Assert.Equal("python", plan.Steps.Single(x => x.Name == "first").InvocationBinding!.ExecutionLanguage);
            Assert.Equal("typescript", plan.Steps.Single(x => x.Name == "local").InvocationBinding!.ExecutionLanguage);
            Assert.Equal("python", plan.Steps.Single(x => x.Name == "last").InvocationBinding!.ExecutionLanguage);
            var tool = plan.Steps.Single(x => x.Name == "tool");
            Assert.Equal(AiInvocationKind.Mcp, tool.InvocationBinding!.Kind);
            Assert.Null(tool.InvocationBinding.ExecutionLanguage);
            Assert.Equal("reports", tool.InvocationBinding.ConnectionRef);
            Assert.Equal("publish", tool.InvocationBinding.Tool);
            Assert.Same(native.Implementation, plan.Steps.Single(x => x.Name == "work").Step);
            Assert.Equal(1, native.Calls);
            Assert.All(factories.Cast<ProbeFactory>().SelectMany(x => x.Created), x => Assert.Empty(x.Calls));
        }

        [Fact]
        public async Task Explicit_Step_Language_Works_Without_Pipeline_Default()
        {
            var plan = await new AiPipelineResolver(new ProbeRegistry(), Factories()).ResolveAsync(CreatePipeline(new[] { Custom(language: "python") }, null));
            Assert.Equal(AiExecutionLanguageSource.Step, Assert.Single(plan.Steps).InvocationBinding!.LanguageSource);
        }

        [Fact]
        public async Task Missing_Later_Capability_Stops_All_Factory_And_Native_Creation()
        {
            var native = new ProbeRegistry();
            var python = new ProbeFactory(AiInvocationKind.Custom, "python");
            var definition = CreatePipeline(new[] { Native(), Custom("ready"), Custom("missing", "typescript") });
            await Assert.ThrowsAsync<NotSupportedException>(() => new AiPipelineResolver(native, new[] { python }).ResolveAsync(definition));
            Assert.Equal(0, native.Calls);
            Assert.Empty(python.Created);
        }

        [Theory]
        [InlineData(AiInvocationKind.Custom)]
        [InlineData(AiInvocationKind.Mcp)]
        public async Task Installed_NonNative_Adapter_Does_Not_Silently_Enable_Sequential_Mode(AiInvocationKind kind)
        {
            var source = kind == AiInvocationKind.Custom ? Custom() : Mcp();
            var definition = new AiPipelineDefinition { Name = "sequential", ExecutionLanguage = "python", Steps = new[] { source } };
            var factories = Factories();
            await Assert.ThrowsAsync<NotSupportedException>(() => new AiPipelineResolver(new ProbeRegistry(), factories).ResolveAsync(definition));
            Assert.Equal(AiExecutionMode.Sequential, definition.ExecutionMode);
            Assert.All(factories.Cast<ProbeFactory>(), x => Assert.Empty(x.Created));
        }

        [Theory]
        [InlineData(AiInvocationKind.Native, null)]
        [InlineData(AiInvocationKind.Mcp, "python")]
        [InlineData(AiInvocationKind.Custom, null)]
        [InlineData(AiInvocationKind.Custom, "Python")]
        [InlineData(AiInvocationKind.Custom, "ruby")]
        public void Invalid_Factory_Capability_Is_Rejected(AiInvocationKind kind, string? language)
        {
            Assert.Throws<InvalidOperationException>(() => new AiPipelineResolver(new ProbeRegistry(), new[] { new ProbeFactory(kind, language) }));
        }

        [Fact]
        public void Duplicate_Capability_Is_Not_Resolved_By_Registration_Order()
        {
            Assert.Throws<InvalidOperationException>(() => new AiPipelineResolver(new ProbeRegistry(), new[]
            {
                new ProbeFactory(AiInvocationKind.Custom, "python"), new ProbeFactory(AiInvocationKind.Custom, "python")
            }));
        }

        [Fact]
        public async Task Factory_Collection_Is_Snapshotted_At_Construction()
        {
            var python = new ProbeFactory(AiInvocationKind.Custom, "python");
            var registrations = new List<IAiStepInvocationAdapterFactory> { python };
            var resolver = new AiPipelineResolver(new ProbeRegistry(), registrations);
            registrations.Clear();
            await resolver.ResolveAsync(CreatePipeline(new[] { Custom() }));
            Assert.Single(python.Created);
        }

        [Fact]
        public async Task Null_Adapter_Or_Factory_Exception_Never_Falls_Back_To_Native()
        {
            var native = new ProbeRegistry();
            var factory = new ProbeFactory(AiInvocationKind.Custom, "python") { OnCreate = _ => null! };
            var resolver = new AiPipelineResolver(native, new[] { factory });
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(CreatePipeline(new[] { Custom() })));
            factory.OnCreate = _ => throw new InvalidOperationException("factory failed");
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(CreatePipeline(new[] { Custom() })));
            Assert.Equal(0, native.Calls);
        }

        [Fact]
        public async Task Cancelled_Resolution_Creates_Nothing()
        {
            var native = new ProbeRegistry();
            var factory = new ProbeFactory(AiInvocationKind.Custom, "python");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiPipelineResolver(native, new[] { factory }).ResolveAsync(CreatePipeline(new[] { Custom() }), cts.Token));
            Assert.Equal(0, native.Calls);
            Assert.Empty(factory.Created);
        }

        [Fact]
        public async Task Invalid_Policy_Binding_Fails_Before_Creating_Step_Adapters()
        {
            var factory = new ProbeFactory(AiInvocationKind.Custom, "python");
            var invalid = CustomPolicy(language: "unknown");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipelineResolver(new ProbeRegistry(), new[] { factory }).ResolveAsync(
                CreatePipeline(new[] { Custom(config: Config(invalid)) })));
            Assert.Empty(factory.Created);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Existing_DI_Registration_Uses_Optional_Factories(bool installed)
        {
            var native = new ProbeRegistry();
            var factory = new ProbeFactory(AiInvocationKind.Custom, "python");
            var services = new ServiceCollection();
            services.AddSingleton<IAiStepRegistry>(native);
            services.AddScoped<IAiPipelineResolver, AiPipelineResolver>();
            if (installed) services.AddSingleton<IAiStepInvocationAdapterFactory>(factory);
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            using var scope = provider.CreateScope();
            var plan = await scope.ServiceProvider.GetRequiredService<IAiPipelineResolver>().ResolveAsync(CreatePipeline(new[] { installed ? Custom() : Native() }));
            Assert.Single(plan.Steps);
            Assert.Equal(installed ? 1 : 0, factory.Created.Count);
            Assert.Equal(installed ? 0 : 1, native.Calls);
        }

        [Fact]
        public async Task Parallel_Plan_Resolution_Does_Not_Share_Step_Adapter_State()
        {
            var factory = new ProbeFactory(AiInvocationKind.Custom, "python");
            var resolver = new AiPipelineResolver(new ProbeRegistry(), new[] { factory });
            var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(() => resolver.ResolveAsync(
                CreatePipeline(new[] { Custom(implementationRef: $"publication/{i}/v1") }))));
            var plans = await Task.WhenAll(tasks);
            Assert.Equal(24, factory.Created.Count);
            Assert.Equal(24, plans.Select(x => x.Steps[0].Step).Distinct().Count());
            Assert.Equal(24, plans.Select(x => x.Steps[0].InvocationBinding!.ImplementationRef).Distinct().Count());
            Assert.All(factory.Created, step => Assert.Empty(step.Calls));
        }

        [Fact]
        public async Task Same_Adapter_Type_And_Keys_Keep_Separate_Execution_And_Tenant_Contexts()
        {
            var factory = new ProbeFactory(AiInvocationKind.Custom, "python");
            var resolver = new AiPipelineResolver(new ProbeRegistry(), new[] { factory });
            var first = await resolver.ResolveAsync(CreatePipeline(new[] { Custom(implementationRef: "publication/a/v1") }));
            var second = await resolver.ResolveAsync(CreatePipeline(new[] { Custom(implementationRef: "publication/b/v1") }));
            var a = Assert.IsType<ProbeStep>(first.Steps[0].Step);
            var b = Assert.IsType<ProbeStep>(second.Steps[0].Step);
            Assert.NotSame(a, b);
            var ea = CreateExecution("execution-a", "tenant-a");
            var eb = CreateExecution("execution-b", "tenant-b");
            await Task.WhenAll(
                a.ExecuteAsync(new AiStepExecutionContext(ea, first.Steps[0])),
                b.ExecuteAsync(new AiStepExecutionContext(eb, second.Steps[0])));
            Assert.Same(ea.Record, Assert.Single(a.Calls).Record);
            Assert.Same(eb.Record, Assert.Single(b.Calls).Record);
            Assert.Equal("tenant-a", Assert.Single(a.Calls).Record.ExecutionContextSnapshot!.TenantId);
            Assert.Equal("tenant-b", Assert.Single(b.Calls).Record.ExecutionContextSnapshot!.TenantId);
            Assert.Equal("publication/a/v1", a.Metadata!.Binding.ImplementationRef);
            Assert.Equal("publication/b/v1", b.Metadata!.Binding.ImplementationRef);
            Assert.Equal(2, factory.Created.Count);
        }
    }
}
