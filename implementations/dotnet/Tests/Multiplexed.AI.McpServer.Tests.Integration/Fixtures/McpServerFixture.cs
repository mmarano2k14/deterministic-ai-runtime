using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures
{
    /// <summary>
    /// Provides a shared MCP server host and HTTP client for integration tests.
    /// </summary>
    public sealed class McpServerFixture : IAsyncLifetime
    {
        private const string RequestedBy = "mcp-health-integration-test";
        private const string TenantId = "test-tenant";

        /// <summary>
        /// Gets the MCP server test host.
        /// </summary>
        public GenericMcpServerTestHost Host { get; private set; } = default!;

        /// <summary>
        /// Gets the HTTP client used to call the MCP server.
        /// </summary>
        public HttpClient Client { get; private set; } = default!;

        /// <summary>
        /// Gets the JSON-RPC MCP client.
        /// </summary>
        public McpTestClient Mcp { get; private set; } = default!;

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            var controlPlaneId =
                GenericMcpServerTestSettings.CreateControlPlaneId(
                    "mcp-health");

            var runtimeInstanceId =
                $"mcp-health-control-plane-{Guid.NewGuid():N}";

            Host =
                new GenericMcpServerTestHost(
                    GenericMcpServerTestSettings.CreateMcpSettings(
                        controlPlaneId,
                        new Dictionary<string, string?>
                        {
                            ["AiMcpHost:Mode"] = "ControlPlaneWithLocalRuntimeInstances",
                            ["AiMcpHost:EnableSharedQueuePump"] = "false",

                            ["AiSharedQueueBackgroundService:Enabled"] = "false",
                            ["AiSharedQueueBackgroundService:WaitForRuntimeReadiness"] = "false",
                            ["AiSharedQueuePump:Enabled"] = "false",

                            ["AiRuntimeInstanceRegistration:ControlPlaneId"] = controlPlaneId,
                            ["AiRuntimeInstanceRegistration:Role"] = "ControlPlane",
                            ["AiRuntimeInstanceRegistration:ProviderName"] = "local",
                            ["AiRuntimeInstanceRegistration:RuntimeInstanceId"] = runtimeInstanceId,

                            ["AiRuntimeInstanceRegistration:ProviderMetadata:controlPlaneId"] = controlPlaneId,
                            ["AiRuntimeInstanceRegistration:ProviderMetadata:provider.name"] = "local",
                            ["AiRuntimeInstanceRegistration:Metadata:controlPlaneId"] = controlPlaneId,
                            ["AiRuntimeInstanceRegistration:Metadata:provider.name"] = "local",
                            ["AiRuntimeInstanceRegistration:Metadata:hostType"] = "health-test",

                            ["AiEngine:ControlPlane:ControlPlaneId"] = controlPlaneId,
                            ["AiEngine:RuntimeInstanceId"] = runtimeInstanceId,
                            ["AiEngine:PipelineBackgroundController:RuntimeInstanceId"] = runtimeInstanceId,
                            ["AiEngine:RuntimeInstanceWorker:RuntimeInstanceId"] = runtimeInstanceId
                        }));

            Client =
                Host.CreateClient();

            Mcp =
                await McpRbacTestClientHelper
                    .CreateConfiguredClientAsync(
                        Host,
                        Client,
                        RequestedBy,
                        tenantId: TenantId)
                    .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            Client.Dispose();

            await Host
                .DisposeAsync()
                .ConfigureAwait(false);
        }
    }
}