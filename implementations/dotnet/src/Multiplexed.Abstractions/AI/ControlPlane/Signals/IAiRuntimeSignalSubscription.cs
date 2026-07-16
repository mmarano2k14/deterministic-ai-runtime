namespace Multiplexed.Abstractions.AI.ControlPlane.Signals
{
    /// <summary>
    /// Represents an active runtime signal subscription.
    /// </summary>
    public interface IAiRuntimeSignalSubscription : IAsyncDisposable
    {
        /// <summary>
        /// Reads runtime signals received by this subscription.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The asynchronous runtime signal stream.</returns>
        IAsyncEnumerable<AiRuntimeSignal> ReadAllAsync(
            CancellationToken cancellationToken = default);
    }
}