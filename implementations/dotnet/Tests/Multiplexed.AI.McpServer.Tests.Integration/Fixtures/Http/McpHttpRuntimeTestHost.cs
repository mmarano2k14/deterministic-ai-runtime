using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.McpServer.Hosting;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides an in-memory MCP control-plane host configured to dispatch to HTTP runtime instances.
    /// </summary>
    public sealed class McpHttpRuntimeTestHost : WebApplicationFactory<Program>
    {
        private readonly HttpClient? runtimeClient;
        private readonly string mongoDatabaseName;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpRuntimeTestHost"/> class.
        /// </summary>
        /// <param name="runtimeClient">
        /// The in-memory HTTP client of the runtime-instance-only test host.
        /// When provided, the HTTP runtime provider uses this client instead of a real network client.
        /// </param>
        public McpHttpRuntimeTestHost(
            HttpClient? runtimeClient = null,
            string? mongoDatabaseName = null)
        {
            this.runtimeClient = runtimeClient;
            this.mongoDatabaseName = string.IsNullOrWhiteSpace(mongoDatabaseName)
                ? GenericMcpServerTestSettings.DefaultDatabaseName
                : mongoDatabaseName;
        }

        /// <summary>
        /// Configures the MCP control-plane test host.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");

            builder.UseSetting("AiMcpHost:Mode", "ControlPlaneWithHttpRuntimeInstances");
            builder.UseSetting("AiMcpHost:Port", "5001");
            builder.UseSetting("AiMcpHost:EnableSharedQueuePump", "false");
            builder.UseSetting("AiMcpHost:EnableReplayTools", "true");
            builder.UseSetting("AiMcpHost:EnableObservabilityTools", "true");

            builder.ConfigureAppConfiguration((context, configurationBuilder) =>
            {
                configurationBuilder.Sources.Clear();

                var values =
                    new Dictionary<string, string?>
                    {
                        ["AiMcpHost:Mode"] = "ControlPlaneWithHttpRuntimeInstances",
                        ["AiMcpHost:Port"] = "5001",
                        ["AiMcpHost:EnableSharedQueuePump"] = "false",
                        ["AiMcpHost:SharedQueuePumpIntervalSeconds"] = "1",
                        ["AiMcpHost:EnableReplayTools"] = "true",
                        ["AiMcpHost:EnableObservabilityTools"] = "true",

                        ["AiRuntimeInstanceRegistration:Enabled"] = "true",
                        ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = "mcp-control-plane-http",
                        ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                        ["AiRuntimeInstanceRegistration:WorkerCount"] = "30",
                        ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "30",
                        ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                        ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                        ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",
                        ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                        ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "control-plane-with-http-runtime",
                        ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",

                        ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                        ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                        ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                        ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                        ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled",

                        ["ConnectionStrings:Redis"] = "localhost:6379",
                        ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017",
                        ["Mongo:DatabaseName"] = this.mongoDatabaseName,

                        ["AiEngine:Snapshots:Enabled"] = "true",
                        ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                        ["AiEngine:Snapshots:Mongo:ConnectionString"] = "mongodb://localhost:27017",
                        ["AiEngine:Snapshots:Mongo:DatabaseName"] = this.mongoDatabaseName,

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
                if (runtimeClient is not null)
                {
                    services.AddSingleton(runtimeClient);

                    services.AddSingleton<IHttpClientFactory>(
                        new TestRuntimeHttpClientFactory(
                            runtimeClient));

                    Console.WriteLine(
                        "[TEST MCP HOST] Runtime HTTP client injected into control-plane host.");
                }

                services.PostConfigure<AiMcpHostOptions>(options =>
                {
                    options.Mode = AiMcpHostMode.ControlPlaneWithHttpRuntimeInstances;
                    options.Port = 5001;
                    options.EnableSharedQueuePump = false;
                    options.EnableReplayTools = true;
                    options.EnableObservabilityTools = true;

                    Console.WriteLine(
                        $"[TEST MCP HOST] PostConfigure MCP host. Mode='{options.Mode}', Port='{options.Port}', SharedQueuePump='{options.EnableSharedQueuePump}', Replay='{options.EnableReplayTools}', Observability='{options.EnableObservabilityTools}'.");
                });

                services.PostConfigure<AiSharedQueueBackgroundServiceOptions>(options =>
                {
                    options.Enabled = false;

                    Console.WriteLine(
                        $"[TEST MCP HOST] PostConfigure shared queue background service. Enabled='{options.Enabled}'.");
                });

                services.PostConfigure<AiRuntimeInstanceRegistrationOptions>(options =>
                {
                    options.Enabled = true;
                    options.RuntimeInstanceId = "mcp-control-plane-http";
                    options.ProviderName = "local";
                    options.WorkerCount = 30;
                    options.MaxConcurrentRuns = 30;
                    options.QueueCapacity = 100;
                    options.RuntimeVersion = "test";
                    options.HeartbeatInterval = TimeSpan.FromSeconds(2);
                    options.Role = AiRuntimeInstanceRole.ControlPlane;

                    options.ProviderMetadata =
                        new Dictionary<string, string>(
                            options.ProviderMetadata ?? new Dictionary<string, string>(),
                            StringComparer.OrdinalIgnoreCase)
                        {
                            [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                        };

                    options.Metadata =
                        new Dictionary<string, string>(
                            options.Metadata ?? new Dictionary<string, string>(),
                            StringComparer.OrdinalIgnoreCase)
                        {
                            ["hostType"] = "control-plane-with-http-runtime",
                            ["deployment"] = "test-http"
                        };

                    Console.WriteLine(
                        $"[TEST MCP HOST] Runtime registration configured. RuntimeInstanceId='{options.RuntimeInstanceId}', Provider='{options.ProviderName}', Role='{options.Role}'.");
                });

                services.PostConfigure<AiMcpControlPlaneHostOptions>(options =>
                {
                    options.Enabled = true;
                    options.EnableSharedQueuePump = false;
                    options.RuntimeInstanceId = "mcp-control-plane-http";
                    options.WorkerId = "mcp-http-background-pump";

                    Console.WriteLine(
                        $"[TEST MCP HOST] PostConfigure MCP control-plane host. Enabled='{options.Enabled}', SharedQueuePump='{options.EnableSharedQueuePump}', RuntimeInstanceId='{options.RuntimeInstanceId}', WorkerId='{options.WorkerId}'.");
                });
            });
        }
    }
}