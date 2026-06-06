using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Hosting;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Http;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides an in-memory runtime-instance-only HTTP host for integration tests.
    /// </summary>
    public sealed class RuntimeInstanceHttpTestHost : WebApplicationFactory<Program>
    {
        /// <summary>
        /// Gets the runtime instance identifier used by this test host.
        /// </summary>
        public const string RuntimeInstanceId = "runtime-http-1";

        /// <summary>
        /// Gets the runtime command endpoint exposed by this test host.
        /// </summary>
        public const string RuntimeCommandEndpoint =
            "http://localhost/runtime-instance/commands";

        /// <summary>
        /// Configures the runtime-instance-only test host.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            Console.WriteLine("[TEST HOST] RuntimeInstanceHttpTestHost ConfigureServices called.");

            builder.ConfigureAppConfiguration((context, configurationBuilder) =>
            {
                configurationBuilder.Sources.Clear();

                var values =
                    new Dictionary<string, string?>
                    {
                        ["AiMcpHost:Mode"] = "RuntimeInstanceOnly",
                        ["AiMcpHost:Port"] = "5002",
                        ["AiMcpHost:EnableSharedQueuePump"] = "false",
                        ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                        ["AiMcpHost:EnableReplayTools"] = "false",
                        ["AiMcpHost:EnableObservabilityTools"] = "false",

                        ["AiRuntimeInstanceRegistration:Enabled"] = "true",
                        ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = RuntimeInstanceId,
                        ["AiRuntimeInstanceRegistration:ProviderName"] = "http",
                        ["AiRuntimeInstanceRegistration:WorkerCount"] = "10",
                        ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "5",
                        ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                        ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                        ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                        ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "http",
                        ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.name"] = "http",
                        ["AiRuntimeInstanceRegistration:ProviderMetadata:transport.endpoint"] = RuntimeCommandEndpoint,
                        ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                        ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",

                        ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                        ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                        ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                        ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                        ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled",

                        ["ConnectionStrings:Redis"] = "localhost:6379,defaultDatabase=15",
                        ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017",
                        ["Mongo:DatabaseName"] = "multiplexed-ai-mcp-http-tests",

                        ["AiEngine:Snapshots:Enabled"] = "true",
                        ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                        ["AiEngine:Snapshots:Mongo:ConnectionString"] = "mongodb://localhost:27017",
                        ["AiEngine:Snapshots:Mongo:DatabaseName"] = "multiplexed-ai-mcp-http-tests",

                        ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                        ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "1000",
                        ["AiEngine:PipelineBackgroundController:RejectEnqueueWhenStopped"] = "false",
                        ["AiEngine:PipelineBackgroundController:StopOnFirstFailure"] = "false",

                        ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                        ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                        ["AiEngine:PipelineBackgroundController:Distributed:StopOnFirstTerminal"] = "true",
                        ["AiEngine:PipelineBackgroundController:Distributed:TerminalObservationTimeout"] = "00:00:30",

                        ["AiEngine:RuntimeInstanceWorker:MaxCycles"] = "0",
                        ["AiEngine:RuntimeInstanceWorker:MaxStepsPerCycle"] = "1",
                        ["AiEngine:RuntimeInstanceWorker:IdleDelay"] = "00:00:00.025",
                        ["AiEngine:RuntimeInstanceWorker:IgnoreConcurrencyConflicts"] = "true"
                    };

                configurationBuilder.AddInMemoryCollection(values);
            });

            builder.ConfigureServices(services =>
            {

                services.AddAiHttpRuntimeInstanceProvider();

                services.PostConfigure<AiRuntimeInstanceRegistrationOptions>(options =>
                {
                    options.Enabled = true;
                    options.RuntimeInstanceId = RuntimeInstanceId;
                    options.ProviderName = "http";
                    options.WorkerCount = 10;
                    options.MaxConcurrentRuns = 5;
                    options.QueueCapacity = 100;
                    options.RuntimeVersion = "test";
                    options.HeartbeatInterval = TimeSpan.FromSeconds(2);

                    Console.WriteLine($"[TEST HOST] PostConfigure runtime id = {options.RuntimeInstanceId}");
                });

                services.PostConfigure<AiRuntimeInstanceRegistrationOptions>(options =>
                {
                    options.Enabled = true;
                    options.RuntimeInstanceId = RuntimeInstanceHttpTestHost.RuntimeInstanceId;
                    options.ProviderName = "http";
                    options.Role = AiRuntimeInstanceRole.Runtime;

                    options.ProviderMetadata =
                        new Dictionary<string, string>(
                            options.ProviderMetadata ?? new Dictionary<string, string>(),
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["provider.name"] = "http",
                            ["transport.endpoint"] = "http://localhost"
                        };

                    options.Metadata =
                        new Dictionary<string, string>(
                            options.Metadata ?? new Dictionary<string, string>(),
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["provider.name"] = "http",
                            ["transport.endpoint"] = "http://localhost"
                        };

                    Console.WriteLine(
                        $"[TEST HOST] Runtime provider = {options.ProviderName}, endpoint = {options.ProviderMetadata["transport.endpoint"]}");
                });


                services.PostConfigure<AiMcpControlPlaneHostOptions>(options =>
                {
                    options.EnableSharedQueuePump = false;

                    Console.WriteLine($"[TEST HOST] PostConfigure shared queue pump = {options.EnableSharedQueuePump}");
                });

            });
        }
    }
}