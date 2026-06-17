using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Generic;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.McpServer.Tests.Integration.Helpers
{
    /// <summary>
    /// Provides helper methods for configuring MCP test clients with RBAC context headers.
    /// </summary>
    public static class McpRbacTestClientHelper
    {
        /// <summary>
        /// Creates an MCP test client and configures the HTTP client with a stored RBAC execution context.
        /// </summary>
        /// <param name="host">The generic MCP server test host.</param>
        /// <param name="client">The HTTP client created from the host.</param>
        /// <param name="requestedBy">The test actor/user id.</param>
        /// <param name="tenantId">Optional tenant id to store in the RBAC execution context.</param>
        /// <param name="tenantGroupId">Optional tenant group id to store in the RBAC execution context.</param>
        /// <returns>The configured MCP test client.</returns>
        public static async Task<McpTestClient> CreateConfiguredClientAsync(
            GenericMcpServerTestHost host,
            HttpClient client,
            string requestedBy,
            string? tenantId = null,
            string? tenantGroupId = null)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

            var contextStore =
                host.Services.GetRequiredService<IContextStore>();

            var contextRuntimeOptions =
                host.Services
                    .GetRequiredService<IOptions<ContextRuntimeOptions>>()
                    .Value;

            var executionContext =
                McpRbacTestContextFactory.CreateDefaultContext(
                    requestedBy,
                    tenantId,
                    tenantGroupId);

            var contextKey =
                await contextStore
                    .StoreAsync(executionContext)
                    .ConfigureAwait(false);

            client.DefaultRequestHeaders.Remove(
                contextRuntimeOptions.AccessContextHeader);

            client.DefaultRequestHeaders.Add(
                contextRuntimeOptions.AccessContextHeader,
                contextKey);

            client.DefaultRequestHeaders.Remove(
                McpRbacTestContextFactory.DemoUserIdHeaderName);

            client.DefaultRequestHeaders.Add(
                McpRbacTestContextFactory.DemoUserIdHeaderName,
                requestedBy);

            var mcp =
                new McpTestClient(
                    client);

            mcp.SetRbacHeaders(
                contextRuntimeOptions.AccessContextHeader,
                contextKey,
                requestedBy);

            return mcp;
        }
    }
}