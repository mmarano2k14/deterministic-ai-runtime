using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers
{
    /// <summary>
    /// Resolves provider capabilities for runtime instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This resolver centralizes the common control-plane flow:
    /// </para>
    ///
    /// <code>
    /// RuntimeInstanceId
    ///     -> IAiRuntimeInstanceCapacityStore
    ///     -> IAiRuntimeInstanceProviderRouter
    ///     -> provider capability
    /// </code>
    ///
    /// <para>
    /// It avoids duplicating capacity descriptor lookup and provider capability
    /// resolution across MCP tools, HTTP APIs, dashboards, CLI commands, and future
    /// Kubernetes control-plane services.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceProviderCapabilityResolver
    {
        /// <summary>
        /// Resolves a provider capability for the specified runtime instance.
        /// </summary>
        /// <typeparam name="TProvider">The provider capability type to resolve.</typeparam>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The provider capability resolution result.</returns>
        Task<AiRuntimeInstanceProviderCapabilityResolution<TProvider>> ResolveAsync<TProvider>(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
            where TProvider : IAiRuntimeInstanceProvider;
    }
}