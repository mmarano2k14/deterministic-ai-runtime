using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Mcp;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.DI.Engine;
using Multiplexed.AI.Runtime;
using Multiplexed.AI.Runtime.Invocation;
using Multiplexed.AI.Runtime.Invocation.Mcp;
using Multiplexed.AI.Runtime.Pipeline;
using Multiplexed.AI.Runtime.Pipeline.Steps;
using static Multiplexed.AI.Tests.Runtime.Invocation.McpStepTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Binding, native compatibility and real step discovery, without a network transport.</summary>
    public sealed class AiMcpStepBindingTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("python")]
        [InlineData("typescript")]
        [InlineData("dotnet")]
        public async Task Mcp_Binding_Is_Language_Free_And_Resolution_Has_No_Effect(string? language)
        {
            using var fixture = await CreateAsync(McpStepTestSupport.Pipeline(language: language));
            var step = Assert.Single(fixture.Plan.Steps);
            Assert.Equal(AiInvocationKind.Mcp, step.InvocationBinding!.Kind);
            Assert.Null(step.InvocationBinding.ExecutionLanguage);
            Assert.Equal(AiExecutionLanguageSource.None, step.InvocationBinding.LanguageSource);
            Assert.Equal("publish", fixture.Adapter.Name);
            Assert.Equal(1, step.MaxRetries);
            Assert.Equal(500, step.RetryDelayMs);
            Assert.Equal(0, fixture.Registry.Calls);
            Assert.Empty(fixture.Resolver.Calls);
            Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Native_Step_Keeps_Its_Registry_In_A_Python_Pipeline()
        {
            var native = new NativeRegistry();
            var plan = await new AiPipelineResolver(native).ResolveAsync(McpStepTestSupport.Pipeline(
                new AiPipelineStepDefinition { Name = "work", StepKey = "same-key" }));
            Assert.IsType<NativeStep>(Assert.Single(plan.Steps).Step);
            Assert.Equal(1, native.Calls);
        }

        [Fact]
        public async Task Missing_Mcp_Capability_Does_Not_Fall_Back_To_The_Native_Key()
        {
            var native = new NativeRegistry();
            await Assert.ThrowsAsync<NotSupportedException>(() => new AiPipelineResolver(native).ResolveAsync(McpStepTestSupport.Pipeline()));
            Assert.Equal(0, native.Calls);
        }

        [Fact]
        public async Task Sequential_Definition_Is_Not_Rewritten_As_A_Dag()
        {
            var native = new NativeRegistry();
            var factory = new AiMcpStepAdapterFactory(new Resolver(), new Transport());
            await Assert.ThrowsAsync<NotSupportedException>(() => new AiPipelineResolver(native, new[] { factory })
                .ResolveAsync(  McpStepTestSupport.Pipeline(mode: AiExecutionMode.Sequential)));
            Assert.Equal(0, native.Calls);
        }

        [Theory]
        [InlineData("https://example.com/mcp", "publish")]
        [InlineData("../connection", "publish")]
        [InlineData("/connection", "publish")]
        [InlineData("reports", "*")]
        [InlineData("reports", "publish:admin")]
        [InlineData("reports", "publish report")]
        public async Task Urls_And_Non_Concrete_Targets_Are_Refused(string connection, string tool)
        {
            var resolver = new Resolver(); var transport = new Transport(); var native = new NativeRegistry();
            var factory = new AiMcpStepAdapterFactory(resolver, transport);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipelineResolver(native, new[] { factory })
                .ResolveAsync(McpStepTestSupport.Pipeline(Step(connection: connection, tool: tool))));
            Assert.Empty(resolver.Calls); Assert.Empty(transport.Calls); Assert.Equal(0, native.Calls);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(30001)]
        public void Server_Timeout_Is_Bounded(int milliseconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiMcpStepAdapterFactory(new Resolver(), new Transport(),
                new AiMcpStepInvocationOptions { InvocationTimeout = TimeSpan.FromMilliseconds(milliseconds) }));
        }

        [Theory]
        [InlineData(AiInvocationKind.Native)]
        [InlineData(AiInvocationKind.Custom)]
        public void Mcp_Factory_Does_Not_Accept_Other_Invocation_Kinds(AiInvocationKind kind)
        {
            var factory = new AiMcpStepAdapterFactory(new Resolver(), new Transport());
            var binding = new AiInvocationBinding(kind, null, AiExecutionLanguageSource.None, ConnectionRef: "reports", Tool: "publish");
            Assert.Throws<InvalidOperationException>(() => factory.Create(new AiStepInvocationAdapterContext("test", "v1", "publish", "key", binding)));
        }

        [Fact]
        public async Task Admission_Does_Not_Load_Or_Invoke_The_Mcp_Implementation()
        {
            using var fixture = await CreateAsync();
            var admission = AiStepAdmissionContextFactory.Create(fixture.Context.Execution,
                fixture.Context.Step, new AiConcurrencyDefinition { Enabled = true });
            Assert.Null(admission.Step.Step);
            Assert.Equal(fixture.Context.InvocationBinding, admission.InvocationBinding);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WithLiveAsync(fixture.Live,
                () => fixture.Adapter.ExecuteAsync(admission)));
            Assert.Empty(fixture.Resolver.Calls); Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Foreign_Execution_Metadata_Is_Refused_Before_Reference_Resolution()
        {
            using var fixture = await CreateAsync();
            fixture.Context.Record.PipelineName = "other-pipeline";
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InvokeAsync());
            Assert.Empty(fixture.Resolver.Calls); Assert.Empty(fixture.Transport.Calls);
        }

        [Fact]
        public async Task Definition_Json_Roundtrip_Preserves_Target_And_Invocation()
        {
            var json = JsonSerializer.Serialize(McpStepTestSupport.Pipeline(Step(connection: "reports/v2", tool: "publish.v2")));
            var definition = JsonSerializer.Deserialize<AiPipelineDefinition>(json)!;
            using var fixture = await CreateAsync(definition);
            await fixture.InvokeAsync();
            var request = Assert.Single(fixture.Transport.Calls);
            Assert.Equal("reports/v2", request.ConnectionRef); Assert.Equal("publish.v2", request.Tool);
            Assert.Equal("v1", request.Context.PipelineVersion);
        }

        [Fact]
        public async Task Real_Native_Step_Host_Starts_Without_The_Mcp_Boundary()
        {
            using var host = new HostBuilder()
                .UseDefaultServiceProvider((_, options) => { options.ValidateScopes = true; options.ValidateOnBuild = false; })
                .ConfigureServices((_, services) =>
                {
                    services.AddAiStepsFromAssemblies(typeof(AiRuntimeAssemblyMarker).Assembly);
                    services.AddHostedService<NativeStepStartupProbe>();
                    Assert.DoesNotContain(services, d => d.ServiceType == typeof(AiMcpStepAdapter) ||
                        d.ImplementationType == typeof(AiMcpStepAdapter) || d.ServiceType == typeof(IAiStepInvocationAdapterFactory));
                }).Build();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await host.StartAsync(deadline.Token);
            try
            {
                Assert.True(Assert.Single(host.Services.GetServices<IHostedService>().OfType<NativeStepStartupProbe>()).Started);
                Assert.Null(host.Services.GetService<IAiMcpToolTransport>());
                Assert.Null(host.Services.GetService<IAiMcpToolResolver>());
                Assert.Null(typeof(AiMcpStepAdapter).GetCustomAttribute<AiStepAttribute>());
            }
            finally { await host.StopAsync(deadline.Token); }
        }

        public sealed class NativeStepStartupProbe : IHostedService
        {
            private readonly IServiceScopeFactory _scopes;
            public NativeStepStartupProbe(IServiceScopeFactory scopes) { _scopes = scopes; }
            public bool Started { get; private set; }
            public Task StartAsync(CancellationToken cancellationToken)
            {
                using var scope = _scopes.CreateScope();
                Assert.IsType<HelloWorldStep>(scope.ServiceProvider.GetRequiredService<IAiStepRegistry>().Resolve("hello-world"));
                Started = true;
                return Task.CompletedTask;
            }
            public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        }
    }
}
