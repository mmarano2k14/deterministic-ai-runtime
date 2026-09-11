using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    public sealed record AiDurableInvocationDagReconciliationResult(int Candidates, int Applied, int Suppressed, int Errors);

    /// <summary>Bounded, tenant-scoped polling; a poisoned parent cannot stop other candidates.</summary>
    public sealed class AiDurableInvocationDagReconciler
    {
        private readonly IAiDurableInvocationStore _store;
        private readonly AiDurableInvocationJournal _journal;
        private readonly AiDurableInvocationDagContinuationCoordinator _coordinator;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly ILogger<AiDurableInvocationDagReconciler> _logger;
        public AiDurableInvocationDagReconciler(IAiDurableInvocationStore store, AiDurableInvocationJournal journal,
            AiDurableInvocationDagContinuationCoordinator coordinator, IAiControlPlaneIdResolver controlPlane,
            ILogger<AiDurableInvocationDagReconciler> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _controlPlane = controlPlane ?? throw new ArgumentNullException(nameof(controlPlane));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        public async Task<AiDurableInvocationDagReconciliationResult> ReconcileAsync(AiDurableInvocationScope scope,
            int maxCount = 100, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateScope(scope);
            if (maxCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxCount));
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new InvalidOperationException("The current control plane cannot poll this invocation scope.");
            var candidates = await _store.ListContinuationCandidatesAsync(scope, maxCount, cancellationToken).ConfigureAwait(false);
            var applied = 0; var suppressed = 0; var errors = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await _coordinator.ReconcileAsync(scope, candidate.Definition.Identity, cancellationToken).ConfigureAwait(false);
                    if (result?.ContinuationStatus == AiDurableInvocationContinuationStatus.Applied) applied++;
                    if (result?.ContinuationStatus == AiDurableInvocationContinuationStatus.Suppressed) suppressed++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    _logger.LogWarning("Durable invocation continuation failed. OperationId={OperationId}, ExceptionType={ExceptionType}.",
                        candidate.OperationId, exception.GetType().FullName);
                }
                // Rotate retained/failed candidates behind unvisited records. Do not discard a
                // Scheduled result and do not let the oldest batch starve the rest of the tenant.
                try
                {
                    await _journal.DeferContinuationAsync(scope, candidate.Definition.Identity, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    _logger.LogWarning("Durable invocation candidate deferral failed. OperationId={OperationId}, ExceptionType={ExceptionType}.",
                        candidate.OperationId, exception.GetType().FullName);
                }
            }
            return new(candidates.Count, applied, suppressed, errors);
        }
    }
}
