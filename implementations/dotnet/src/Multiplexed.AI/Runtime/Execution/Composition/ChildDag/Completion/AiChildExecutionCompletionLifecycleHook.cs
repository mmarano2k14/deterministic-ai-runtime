using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion
{
    /// <summary>
    /// Provides the low-latency child-completion fast path for runtime pipeline finalization.
    /// </summary>
    /// <remarks>
    /// This hook is an optimization only. It commits authoritative child completion and immediately attempts durable
    /// continuation scheduling, while the continuation reconciler remains the liveness authority if this hook is
    /// missed, duplicated, or interrupted by a crash.
    /// </remarks>
    public sealed class AiChildExecutionCompletionLifecycleHook : IAiRuntimePipelineRunLifecycleHook
    {
        private readonly AiChildExecutionCompletionCoordinator completionCoordinator;
        private readonly AiChildContinuationCoordinator continuationCoordinator;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildExecutionCompletionLifecycleHook"/> class.
        /// </summary>
        /// <param name="completionCoordinator">The authoritative child completion coordinator.</param>
        /// <param name="continuationCoordinator">The durable parent continuation coordinator.</param>
        public AiChildExecutionCompletionLifecycleHook(
            AiChildExecutionCompletionCoordinator completionCoordinator,
            AiChildContinuationCoordinator continuationCoordinator)
        {
            this.completionCoordinator = completionCoordinator ?? throw new ArgumentNullException(nameof(completionCoordinator));
            this.continuationCoordinator = continuationCoordinator ?? throw new ArgumentNullException(nameof(continuationCoordinator));
        }

        /// <inheritdoc />
        public async Task OnFinalizedAsync(
            AiRuntimePipelineRunFinalizedContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            var relation = await this.completionCoordinator
                .CompleteIfTerminalAsync(context.ExecutionId, cancellationToken)
                .ConfigureAwait(false);

            if (relation is null)
            {
                return;
            }

            await this.continuationCoordinator
                .EnqueueContinuationAsync(relation.ToInvocationIdentity(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
