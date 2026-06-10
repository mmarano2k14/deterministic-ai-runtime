using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Background;
using Multiplexed.AI.McpServer.Host;
using Multiplexed.AI.McpServer.Host.Configuration;
using Multiplexed.AI.McpServer.Hosting;
using Multiplexed.AI.Runtime.ControlPlane.DI;

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
        /// Gets the runtime base endpoint exposed by this test host.
        /// </summary>
        public const string RuntimeBaseEndpoint = "http://localhost";

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

            Console.WriteLine(
                "[TEST HOST] RuntimeInstanceHttpTestHost ConfigureWebHost called.");

            builder.UseSetting("AiMcpHost:Mode", "RuntimeInstanceOnly");
            builder.UseSetting("AiMcpHost:Port", "5002");
            builder.UseSetting("AiMcpHost:EnableSharedQueuePump", "false");
            builder.UseSetting("AiMcpHost:EnableReplayTools", "false");
            builder.UseSetting("AiMcpHost:EnableObservabilityTools", "false");

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
                        ["AiRuntimeInstanceRegistration:Role"] = "Runtime",
                        ["AiRuntimeInstanceRegistration:WorkerCount"] = "10",
                        ["AiRuntimeInstanceRegistration:MaxConcurrentRuns"] = "5",
                        ["AiRuntimeInstanceRegistration:QueueCapacity"] = "100",
                        ["AiRuntimeInstanceRegistration:RuntimeVersion"] = "test",
                        ["AiRuntimeInstanceRegistration:HeartbeatInterval"] = "00:00:02",

                        [$"AiRuntimeInstanceRegistration:ProviderMetadata:{AiRuntimeInstanceProviderMetadataKeys.ProviderName}"] = "http",
                        [$"AiRuntimeInstanceRegistration:ProviderMetadata:{AiRuntimeInstanceCommandTransportMetadataKeys.TransportName}"] =
                            AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                        [$"AiRuntimeInstanceRegistration:ProviderMetadata:{AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint}"] =
                            RuntimeBaseEndpoint,
                        [$"AiRuntimeInstanceRegistration:ProviderMetadata:{AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId}"] =
                            RuntimeInstanceId,

                        [$"AiRuntimeInstanceRegistration:Metadata:{AiRuntimeInstanceProviderMetadataKeys.ProviderName}"] = "http",
                        [$"AiRuntimeInstanceRegistration:Metadata:{AiRuntimeInstanceCommandTransportMetadataKeys.TransportName}"] =
                            AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                        [$"AiRuntimeInstanceRegistration:Metadata:{AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint}"] =
                            RuntimeBaseEndpoint,
                        [$"AiRuntimeInstanceRegistration:Metadata:{AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId}"] =
                            RuntimeInstanceId,
                        ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "runtime-instance-only",
                        ["AiRuntimeInstanceRegistration:Metadata:deployment"] = "test-http",

                        ["AiLocalRuntimeInstancePool:Enabled"] = "false",
                        ["AiLocalRuntimeInstancePool:InstanceCount"] = "0",
                        ["AiLocalRuntimeInstancePool:WorkerCountPerInstance"] = "0",
                        ["AiLocalRuntimeInstancePool:MaxConcurrentRunsPerInstance"] = "0",
                        ["AiLocalRuntimeInstancePool:RuntimeInstanceIdPrefix"] = "disabled",

                        ["ConnectionStrings:Redis"] = "localhost:6379",
                        ["ConnectionStrings:Mongo"] = "mongodb://localhost:27017",
                        ["Mongo:DatabaseName"] = "multiplexed-ai-mcp-http-tests",

                        ["AiEngine:RuntimeInstanceId"] = RuntimeInstanceId,

                        ["AiEngine:Snapshots:Enabled"] = "true",
                        ["AiEngine:Snapshots:Mongo:Enabled"] = "true",
                        ["AiEngine:Snapshots:Mongo:ConnectionString"] = "mongodb://localhost:27017",
                        ["AiEngine:Snapshots:Mongo:DatabaseName"] = "multiplexed-ai-mcp-http-tests",

                        ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = RuntimeInstanceId,
                        ["AiEngine:PipelineBackgroundController:MaxConcurrentRuns"] = "5",
                        ["AiEngine:PipelineBackgroundController:QueueCapacity"] = "1000",
                        ["AiEngine:PipelineBackgroundController:RejectEnqueueWhenStopped"] = "false",
                        ["AiEngine:PipelineBackgroundController:StopOnFirstFailure"] = "false",

                        ["AiEngine:PipelineBackgroundController:Distributed:Enabled"] = "true",
                        ["AiEngine:PipelineBackgroundController:Distributed:WorkerCount"] = "10",
                        ["AiEngine:PipelineBackgroundController:Distributed:StopOnFirstTerminal"] = "true",
                        ["AiEngine:PipelineBackgroundController:Distributed:TerminalObservationTimeout"] = "00:00:30",

                        ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = RuntimeInstanceId,
                        ["AiEngine:RuntimeInstanceWorker:MaxCycles"] = "-1",
                        ["AiEngine:RuntimeInstanceWorker:MaxStepsPerCycle"] = "10",
                        ["AiEngine:RuntimeInstanceWorker:IdleDelay"] = "00:00:00.025",
                        ["AiEngine:RuntimeInstanceWorker:IgnoreConcurrencyConflicts"] = "true"
                    };

                configurationBuilder.AddInMemoryCollection(values);
            });

            builder.ConfigureServices(services =>
            {
                services.AddAiHttpRuntimeInstanceProvider();

                services.PostConfigure<AiMcpHostOptions>(options =>
                {
                    options.Mode = AiMcpHostMode.RuntimeInstanceOnly;
                    options.Port = 5002;
                    options.EnableSharedQueuePump = false;
                    options.EnableReplayTools = false;
                    options.EnableObservabilityTools = false;

                    Console.WriteLine(
                        $"[TEST HOST] PostConfigure MCP host. Mode='{options.Mode}', Port='{options.Port}', SharedQueuePump='{options.EnableSharedQueuePump}', Replay='{options.EnableReplayTools}', Observability='{options.EnableObservabilityTools}'.");
                });

                services.PostConfigure<AiMcpControlPlaneHostOptions>(options =>
                {
                    options.Enabled = false;
                    options.EnableSharedQueuePump = false;
                    options.RuntimeInstanceId = RuntimeInstanceId;
                    options.WorkerId = $"{RuntimeInstanceId}-worker";

                    Console.WriteLine(
                        $"[TEST HOST] PostConfigure MCP control-plane host. Enabled='{options.Enabled}', SharedQueuePump='{options.EnableSharedQueuePump}', RuntimeInstanceId='{options.RuntimeInstanceId}', WorkerId='{options.WorkerId}'.");
                });

                services.PostConfigure<AiSharedQueueBackgroundServiceOptions>(options =>
                {
                    options.Enabled = false;

                    Console.WriteLine(
                        $"[TEST HOST] PostConfigure shared queue background service. Enabled='{options.Enabled}'.");
                });

                services.PostConfigure<AiRuntimeInstanceRegistrationOptions>(options =>
                {
                    options.Enabled = true;
                    options.RuntimeInstanceId = RuntimeInstanceId;
                    options.ProviderName = "http";
                    options.Role = AiRuntimeInstanceRole.Runtime;
                    options.WorkerCount = 10;
                    options.MaxConcurrentRuns = 5;
                    options.QueueCapacity = 100;
                    options.RuntimeVersion = "test";
                    options.HeartbeatInterval = TimeSpan.FromSeconds(2);

                    options.ProviderMetadata =
                        new Dictionary<string, string>(
                            options.ProviderMetadata ?? new Dictionary<string, string>(),
                            StringComparer.OrdinalIgnoreCase)
                        {
                            [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "http",
                            [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                            [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                                RuntimeBaseEndpoint,
                            [AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] =
                                RuntimeInstanceId
                        };

                    options.Metadata =
                        new Dictionary<string, string>(
                            options.Metadata ?? new Dictionary<string, string>(),
                            StringComparer.OrdinalIgnoreCase)
                        {
                            [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "http",
                            [AiRuntimeInstanceCommandTransportMetadataKeys.TransportName] =
                                AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                            [AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint] =
                                RuntimeBaseEndpoint,
                            [AiRuntimeInstanceCommandTransportMetadataKeys.RuntimeInstanceId] =
                                RuntimeInstanceId,
                            ["hostType"] = "runtime-instance-only",
                            ["deployment"] = "test-http"
                        };

                    Console.WriteLine(
                        $"[TEST HOST] Runtime registration configured. RuntimeInstanceId='{options.RuntimeInstanceId}', Provider='{options.ProviderName}', Transport='{options.ProviderMetadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportName]}', Endpoint='{options.ProviderMetadata[AiRuntimeInstanceCommandTransportMetadataKeys.TransportEndpoint]}'.");
                });
            });
        }
    }
}