namespace Multiplexed.Abstractions.AI.ControlPlane.Signals
{
    /// <summary>
    /// Publishes lightweight runtime state-change signals.
    /// </summary>
    public interface IAiRuntimeSignalPublisher
    {
        /// <summary>
        /// Publishes a best-effort runtime signal.
        /// </summary>
        /// <param name="signal">The runtime signal.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task PublishAsync(
            AiRuntimeSignal signal,
            CancellationToken cancellationToken = default);
    }
}