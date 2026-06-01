using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.McpServer.Tools
{
    /// <summary>
    /// Exposes MCP tools related to runtime instance visibility and diagnostics.
    /// </summary>
    /// <remarks>
    /// This tool class reads runtime instance visibility state only.
    /// It does not dispatch runs, execute DAG steps, or mutate worker state.
    /// </remarks>
    [McpServerToolType]
    public sealed class RuntimeInstanceMcpTools
    {
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly ILogger<RuntimeInstanceMcpTools> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RuntimeInstanceMcpTools"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance visibility registry.</param>
        /// <param name="logger">The logger.</param>
        public RuntimeInstanceMcpTools(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            ILogger<RuntimeInstanceMcpTools> logger)
        {
            this.runtimeInstanceRegistry = runtimeInstanceRegistry
                ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));

            this.logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Lists runtime instance visibility snapshots.
        /// </summary>
        /// <param name="includeStopped">Indicates whether stopped runtime instances should be included.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance snapshots.</returns>
        [McpServerTool(Name = "instance.list")]
        [Description("Lists registered runtime instances with visibility, heartbeat, queue, and capacity information.")]
        public async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListRuntimeInstancesAsync(
            bool includeStopped = false,
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation(
                "MCP instance.list called. IncludeStopped={IncludeStopped}",
                includeStopped);

            return await runtimeInstanceRegistry
                .ListAsync(includeStopped, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Lists active runtime instance visibility snapshots only.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The active runtime instance snapshots.</returns>
        [McpServerTool(Name = "instance.active")]
        [Description("Lists active runtime instances only.")]
        public async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListActiveRuntimeInstancesAsync(
            CancellationToken cancellationToken = default)
        {
            logger.LogInformation("MCP instance.active called.");

            return await runtimeInstanceRegistry
                .ListAsync(includeStopped: false, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a runtime instance visibility snapshot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The runtime instance snapshot, or null when not found.</returns>
        [McpServerTool(Name = "instance.status")]
        [Description("Gets one runtime instance visibility snapshot by runtime instance id.")]
        public async Task<AiRuntimeInstanceSnapshot?> GetRuntimeInstanceStatusAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            logger.LogInformation(
                "MCP instance.status called. RuntimeInstanceId={RuntimeInstanceId}",
                runtimeInstanceId);

            return await runtimeInstanceRegistry
                .GetAsync(runtimeInstanceId, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}