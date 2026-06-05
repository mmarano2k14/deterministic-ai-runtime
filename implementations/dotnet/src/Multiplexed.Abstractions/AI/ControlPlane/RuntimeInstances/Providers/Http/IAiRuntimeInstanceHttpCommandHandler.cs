using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Http
{
    /// <summary>
    /// Handles HTTP runtime instance command requests on the runtime instance side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This handler is used by runtime instance hosts that expose an HTTP command endpoint.
    /// </para>
    ///
    /// <para>
    /// It receives a transport-level <see cref="AiRuntimeInstanceCommandRequest"/> and
    /// routes it to the local runtime queue/control-plane owned by the current runtime instance.
    /// </para>
    ///
    /// <para>
    /// This abstraction does not replace local runtime queues. It only provides an HTTP-facing
    /// command entry point into the local runtime queue/control-plane.
    /// </para>
    /// </remarks>
    public interface IAiRuntimeInstanceHttpCommandHandler
    {
        /// <summary>
        /// Handles a runtime instance command request.
        /// </summary>
        /// <param name="request">The runtime instance command request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The runtime instance command result.</returns>
        Task<AiRuntimeInstanceCommandResult> HandleAsync(
            AiRuntimeInstanceCommandRequest request,
            CancellationToken cancellationToken = default);
    }
}