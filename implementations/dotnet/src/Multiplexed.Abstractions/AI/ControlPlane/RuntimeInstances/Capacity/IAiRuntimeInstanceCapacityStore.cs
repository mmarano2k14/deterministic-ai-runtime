namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity
{
    /// <summary>
    /// Stores distributed runtime instance capacity descriptors.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Provides a distributed visibility layer for runtime capacity.
    /// - Allows control-plane services, MCP tools, dashboards, and autoscalers
    ///   to observe local runtime capacity across multiple hosts or pods.
    ///
    /// IMPORTANT:
    /// - This store is not a dispatch registry.
    /// - This store does not contain live runtime objects.
    /// - Local dispatch can still use in-memory runtime instance registries.
    /// </remarks>
    public interface IAiRuntimeInstanceCapacityStore
    {
        /// <summary>
        /// Publishes or updates the capacity descriptor of a runtime instance.
        /// </summary>
        Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets a capacity descriptor by runtime instance identifier.
        /// </summary>
        Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Lists all known capacity descriptors.
        /// </summary>
        Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes a capacity descriptor.
        /// </summary>
        Task<bool> RemoveAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}