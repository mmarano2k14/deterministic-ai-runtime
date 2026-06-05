namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport
{
    /// <summary>
    /// Defines a transport capable of sending commands to runtime instances.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This abstraction separates provider behavior from transport implementation.
    /// </para>
    ///
    /// <para>
    /// Future implementations may include:
    /// - Redis command queue transport.
    /// - HTTP transport.
    /// - gRPC transport.
    /// - Kubernetes service or pod transport.
    /// - In-memory test transport.
    /// </para>
    ///
    /// <para>
    /// This transport must not replace local runtime queues. It only carries commands
    /// to the runtime instance that owns the local queue.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceCommandTransport
    {
        /// <summary>
        /// Sends a runtime instance command.
        /// </summary>
        /// <param name="request">The runtime instance command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime instance command result.</returns>
        Task<AiRuntimeInstanceCommandResult> SendAsync(
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default);
    }
}