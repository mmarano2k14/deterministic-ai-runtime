namespace Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition
{
    /// <summary>
    /// Applies controlled runtime execution recovery transitions.
    /// </summary>
    /// <remarks>
    /// This service owns mutation boundaries for recovery transitions.
    /// It must not detect runtime health, restart hosts, kill processes, or decide
    /// which runtime instance should be recovered.
    /// </remarks>
    public interface IAiRuntimeExecutionRecoveryTransitionService
    {
        /// <summary>
        /// Applies a runtime execution recovery transition.
        /// </summary>
        /// <param name="request">The recovery transition request.</param>
        /// <param name="cancellationToken">A token used to cancel the transition.</param>
        /// <returns>The recovery transition result.</returns>
        Task<AiRuntimeExecutionRecoveryTransitionResult> ApplyAsync(
            AiRuntimeExecutionRecoveryTransitionRequest request,
            CancellationToken cancellationToken = default);
    }
}