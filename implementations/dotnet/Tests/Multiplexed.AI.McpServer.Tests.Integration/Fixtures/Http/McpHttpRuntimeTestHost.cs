using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Hosting;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides an in-memory MCP control-plane host configured to dispatch to HTTP runtime instances.
    /// </summary>
    public sealed class McpHttpRuntimeTestHost : WebApplicationFactory<Program>
    {
        private readonly HttpClient? runtimeClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHttpRuntimeTestHost"/> class.
        /// </summary>
        /// <param name="runtimeClient">
        /// The in-memory HTTP client of the runtime-instance-only test host.
        /// When provided, the HTTP runtime provider uses this client instead of a real network client.
        /// </param>
        public McpHttpRuntimeTestHost(
            HttpClient? runtimeClient = null)
        {
            this.runtimeClient = runtimeClient;
        }

        /// <summary>
        /// Configures the MCP control-plane test host.
        /// </summary>
        /// <param name="builder">The web host builder.</param>
        protected override void ConfigureWebHost(
            IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");

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
                if (runtimeClient is not null)
                {
                    services.AddSingleton(runtimeClient);

                    services.AddSingleton<IHttpClientFactory>(
                        new TestRuntimeHttpClientFactory(
                            runtimeClient));

                    Console.WriteLine(
                        "[TEST MCP HOST] Runtime HTTP client injected into control-plane host.");
                }

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
                });

                services.PostConfigure<AiMcpControlPlaneHostOptions>(options =>
                {
                    options.EnableSharedQueuePump = false;

                    Console.WriteLine(
                        $"[TEST MCP HOST] PostConfigure shared queue pump = {options.EnableSharedQueuePump}");
                });
            });
        }

        private sealed class TestRuntimeHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient client;

            public TestRuntimeHttpClientFactory(
                HttpClient client)
            {
                this.client =
                    client ?? throw new ArgumentNullException(nameof(client));
            }

            public HttpClient CreateClient(
                string name)
            {
                return client;
            }
        }
    }
}