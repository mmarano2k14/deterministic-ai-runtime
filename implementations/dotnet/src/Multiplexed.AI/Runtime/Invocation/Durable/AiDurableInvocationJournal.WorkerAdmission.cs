using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable
{
    public sealed partial class AiDurableInvocationJournal
    {
        /// <summary>
        /// Applies host retry admission inside every existing CAS attempt, not only to a stale
        /// discovery snapshot. The general lease API retains its historical behavior.
        /// </summary>
        public async Task<AiDurableInvocationRecord?> TryAcquireWorkerLeaseAsync(AiDurableInvocationScope scope,
            AiDurableInvocationIdentity identity, string workerId, TimeSpan duration,
            bool allowExpiredLeaseReassignment, int maxAssignmentEpoch, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AiDurableInvocationValidation.ValidateAddress(scope, identity);
            ValidateWorkerLeaseRequest(scope, workerId, duration, maxAssignmentEpoch);
            var current = await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            return current is null
                ? null
                : await TryAcquireWorkerLeaseAsync(scope, current, workerId, duration, allowExpiredLeaseReassignment,
                    maxAssignmentEpoch, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Attempts worker admission from an already validated dispatch snapshot. The snapshot is
        /// only the first CAS expectation; storage remains authoritative. A failed CAS reloads the
        /// latest durable record before any subsequent attempt.
        /// </summary>
        public async Task<AiDurableInvocationRecord?> TryAcquireWorkerLeaseAsync(AiDurableInvocationScope scope,
            AiDurableInvocationRecord candidateSnapshot, string workerId, TimeSpan duration,
            bool allowExpiredLeaseReassignment, int maxAssignmentEpoch, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(candidateSnapshot);
            cancellationToken.ThrowIfCancellationRequested();
            AiDurableInvocationValidation.ValidateScope(scope);
            AiDurableInvocationValidation.ValidateRecord(candidateSnapshot);
            AiDurableInvocationValidation.Require(candidateSnapshot.Definition.Scope == scope,
                "The dispatch candidate belongs to a different ownership scope.");
            ValidateWorkerLeaseRequest(scope, workerId, duration, maxAssignmentEpoch);

            var identity = candidateSnapshot.Definition.Identity;
            var current = candidateSnapshot;
            for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
            {
                var now = AtLeast(Now(), current.UpdatedAtUtc);
                if (AiDurableInvocationValidation.Terminal(current) || current.Lease?.ExpiresAtUtc > now ||
                    current.Lease is not null && !allowExpiredLeaseReassignment) return null;
                var epoch = checked((current.Lease?.Epoch ?? 0) + 1);
                if (epoch > maxAssignmentEpoch) return null;
                var updated = Next(current, now) with
                {
                    Status = AiDurableInvocationStatus.Leased,
                    Lease = new AiDurableInvocationLease(workerId, epoch, Guid.NewGuid().ToString("N"), now.Add(duration))
                };
                AiDurableInvocationValidation.ValidateTransition(current, updated);
                var outcome = await TryReplaceClassifiedAsync(scope, identity, current, updated, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome.Kind == AiDurableInvocationCasOutcomeKind.Applied) return updated;
                if (outcome.Kind == AiDurableInvocationCasOutcomeKind.AuthorityPredicateRejected) return null;

                // The dispatch-page snapshot is a hint only. A genuine revision conflict may
                // provide the latest durable snapshot directly; legacy stores reload once.
                current = outcome.CurrentRecord ??
                    await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
                if (current is null) return null;
            }
            throw Contended();
        }

        private static void ValidateWorkerLeaseRequest(AiDurableInvocationScope scope, string workerId,
            TimeSpan duration, int maxAssignmentEpoch)
        {
            AiDurableInvocationValidation.ValidateScope(scope);
            AiDurableInvocationValidation.Text(workerId, nameof(workerId));
            ValidateDuration(duration);
            if (maxAssignmentEpoch is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(maxAssignmentEpoch));
        }
    }
}
