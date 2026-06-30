namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl
{
    /// <summary>
    /// Provides controlled process lifecycle operations for runtime host processes.
    /// </summary>
    public interface IAiRuntimeHostProcessControl
    {
        /// <summary>
        /// Kills a runtime host process by runtime instance identifier.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>True when the process was found and killed; otherwise, false.</returns>
        Task<bool> KillAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default);
    }
}