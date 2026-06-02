namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment
{
    /// <summary>
    /// Provides runtime environment information for the current runtime process.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Keeps the runtime instance registration system provider-neutral.
    /// - Allows local, Docker, Kubernetes, systemd, Nomad, or cloud-specific
    ///   environments to provide instance metadata without polluting core runtime abstractions.
    /// - Supplies host/process/provider metadata used by the runtime instance registry,
    ///   MCP tools, dashboards, autoscaling, and diagnostics.
    /// </remarks>
    public interface IAiRuntimeEnvironmentProvider
    {
        /// <summary>
        /// Gets a snapshot of the current runtime environment.
        /// </summary>
        /// <param name="cancellationToken">A token used to cancel the operation.</param>
        /// <returns>The current runtime environment snapshot.</returns>
        Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default);
    }
}