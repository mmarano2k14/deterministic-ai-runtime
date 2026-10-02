using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable.Dag
{
    public sealed record AiDurableInvocationDagReconciliationResult(
        int Candidates, int Applied, int Suppressed, int Errors,
        AiDurableInvocationContinuationCursor? NextCursor = null, bool Wrapped = false);

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

        public Task<AiDurableInvocationDagReconciliationResult> ReconcileAsync(
            AiDurableInvocationScope scope, int maxCount = 100, CancellationToken cancellationToken = default) =>
            ReconcilePageAsync(scope, maxCount, null, cancellationToken);

        /// <summary>
        /// Reconciles one keyset page when the store supports it. The cursor is a transient
        /// fairness hint only; losing it restarts the scan safely because durable continuation
        /// state remains authoritative. Legacy stores retain the prior defer-write fallback.
        /// </summary>
        public async Task<AiDurableInvocationDagReconciliationResult> ReconcilePageAsync(
            AiDurableInvocationScope scope, int maxCount, AiDurableInvocationContinuationCursor? after,
            CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateScope(scope);
            if (maxCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxCount));
            if (await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false) != scope.ControlPlaneId)
                throw new InvalidOperationException("The current control plane cannot poll this invocation scope.");

            var pageStore = _store as IAiDurableInvocationContinuationPageStore;
            var wrapped = false;
            IReadOnlyList<AiDurableInvocationRecord> candidates;
            if (pageStore is not null)
            {
                candidates = await pageStore.ListContinuationPageAsync(scope, maxCount, after, cancellationToken)
                    .ConfigureAwait(false);
                if (candidates.Count == 0 && after is not null)
                {
                    candidates = await pageStore.ListContinuationPageAsync(scope, maxCount, null, cancellationToken)
                        .ConfigureAwait(false);
                    wrapped = true;
                }
            }
            else
            {
                candidates = await _store.ListContinuationCandidatesAsync(scope, maxCount, cancellationToken)
                    .ConfigureAwait(false);
            }

            var applied = 0; var suppressed = 0; var errors = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await _coordinator.ReconcileAsync(scope, candidate.Definition.Identity, cancellationToken)
                        .ConfigureAwait(false);
                    if (result?.ContinuationStatus == AiDurableInvocationContinuationStatus.Applied) applied++;
                    if (result?.ContinuationStatus == AiDurableInvocationContinuationStatus.Suppressed) suppressed++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    errors++;
                    _logger.LogWarning("Durable invocation continuation failed. OperationId={OperationId}, ExceptionType={ExceptionType}.",
                        candidate.OperationId, exception.GetType().FullName);
                }

                // Mongo/keyset stores rotate candidates through the non-authoritative cursor.
                // Keep the historical durable deferral only for stores that do not expose the
                // page capability, preserving compatibility without write amplification in Mongo.
                if (pageStore is null)
                {
                    try
                    {
                        await _journal.DeferContinuationAsync(scope, candidate.Definition.Identity, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        errors++;
                        _logger.LogWarning("Durable invocation candidate deferral failed. OperationId={OperationId}, ExceptionType={ExceptionType}.",
                            candidate.OperationId, exception.GetType().FullName);
                    }
                }
            }

            var next = pageStore is not null && candidates.Count > 0
                ? new AiDurableInvocationContinuationCursor(candidates[^1].UpdatedAtUtc, candidates[^1].OperationId)
                : null;
            return new(candidates.Count, applied, suppressed, errors, next, wrapped);
        }
    }
}
