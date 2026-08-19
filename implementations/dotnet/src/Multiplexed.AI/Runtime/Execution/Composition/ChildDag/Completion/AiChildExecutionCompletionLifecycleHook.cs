using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Continuation;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Completion
{
    /// <summary>
    /// Bridges terminal normal DAG execution lifecycle events into durable child completion and parent continuation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The runtime background controller is singleton-scoped, while child completion coordinators depend on normal
    /// scoped DAG engine services. This hook therefore creates a short-lived DI scope for each finalized run instead
    /// of capturing scoped execution services in the singleton controller lifetime.
    /// </para>
    /// <para>
    /// Executions that are not authoritative children are ignored by the completion coordinator, so enabling the
    /// hook does not change terminal semantics for ordinary DAG executions.
    /// </para>
    /// </remarks>
    public sealed class AiChildExecutionCompletionLifecycleHook : IAiRuntimePipelineRunLifecycleHook
    {
        private readonly IServiceScopeFactory scopeFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiChildExecutionCompletionLifecycleHook"/> class.
        /// </summary>
        /// <param name="scopeFactory">The service scope factory used to resolve scoped child completion services.</param>
        public AiChildExecutionCompletionLifecycleHook(
            IServiceScopeFactory scopeFactory)
        {
            this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        /// <inheritdoc />
        public async Task OnFinalizedAsync(
            AiRuntimePipelineRunFinalizedContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            using var scope = this.scopeFactory.CreateScope();
            var completionCoordinator = scope.ServiceProvider.GetRequiredService<AiChildExecutionCompletionCoordinator>();
            var continuationCoordinator = scope.ServiceProvider.GetRequiredService<AiChildContinuationCoordinator>();

            var relation = await completionCoordinator
                .CompleteIfTerminalAsync(context.ExecutionId, cancellationToken)
                .ConfigureAwait(false);

            if (relation is null)
            {
                return;
            }

            await continuationCoordinator
                .EnqueueContinuationAsync(relation.ToInvocationIdentity(), cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
