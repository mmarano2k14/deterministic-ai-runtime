namespace Multiplexed.Abstractions.AI.ControlPlane.Signals
{
    /// <summary>
    /// Subscribes to lightweight runtime state-change signals.
    /// </summary>
    public interface IAiRuntimeSignalSubscriber
    {
        /// <summary>
        /// Creates an active subscription for one durable runtime subject.
        /// </summary>
        /// <param name="signalType">The expected signal type.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="subjectId">
        /// The execution identifier for DAG progress signals, or the shared run
        /// identifier for shared-run dispatch signals.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>An active runtime signal subscription.</returns>
        Task<IAiRuntimeSignalSubscription> SubscribeAsync(
            AiRuntimeSignalType signalType,
            string controlPlaneId,
            string subjectId,
            CancellationToken cancellationToken = default);
    }
}