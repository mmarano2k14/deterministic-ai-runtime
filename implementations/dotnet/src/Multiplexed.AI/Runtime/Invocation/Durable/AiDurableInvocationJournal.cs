using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable
{
    /// <summary>
    /// Coordinates journal CAS operations, not DAG scheduling or worker execution.
    /// Storage faults propagate: an ambiguous write never causes a new operation ID.
    /// </summary>
    public sealed partial class AiDurableInvocationJournal
    {
        private const int MaxCasAttempts = 16;
        private readonly IAiDurableInvocationStore _store;
        private readonly TimeProvider _time;

        public AiDurableInvocationJournal(IAiDurableInvocationStore store, TimeProvider? timeProvider = null)
        {
            ArgumentNullException.ThrowIfNull(store);
            _store = store;
            _time = timeProvider ?? TimeProvider.System;
        }

        /// <summary>Persists preparation before any caller may make work visible to a worker.</summary>
        public async Task<AiDurableInvocationRecord> PrepareAsync(
            AiDurableInvocationDefinition definition, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frozen = AiDurableInvocationValidation.Freeze(definition);
            var now = Now();
            var prepared = new AiDurableInvocationRecord
            {
                Definition = frozen,
                OperationId = AiDurableInvocationKeys.OperationId(frozen.Identity),
                EffectIdempotencyKey = AiDurableInvocationKeys.EffectIdempotencyKey(frozen.Identity),
                InputsSha256 = AiDurableInvocationKeys.HashText(frozen.InputsJson),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            AiDurableInvocationValidation.ValidateRecord(prepared);
            var result = await _store.GetOrCreateAsync(prepared, cancellationToken).ConfigureAwait(false);
            AiDurableInvocationValidation.ValidateRecord(result);
            AiDurableInvocationValidation.Require(result.Definition == frozen, "Existing invocation has conflicting frozen preparation.");
            return result;
        }

        /// <summary>Reads a durable snapshot without leasing, dispatching or replaying the operation.</summary>
        public async Task<AiDurableInvocationRecord?> GetAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AiDurableInvocationValidation.ValidateAddress(scope, identity);
            var record = await _store.GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                AiDurableInvocationValidation.ValidateRecord(record);
                AiDurableInvocationValidation.Require(record.Definition.Identity == identity && record.Definition.Scope == scope,
                    "The invocation store returned a different identity or ownership scope.");
            }
            return record;
        }

        /// <summary>Claims prepared work or replaces an expired assignment without changing logical identity.</summary>
        public Task<AiDurableInvocationRecord?> TryAcquireLeaseAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity, string workerId,
            TimeSpan duration, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.Text(workerId, nameof(workerId));
            ValidateDuration(duration);
            return ChangeAsync(scope, identity, (record, now) =>
            {
                if (AiDurableInvocationValidation.Terminal(record) || record.Lease?.ExpiresAtUtc > now) return null;
                return Next(record, now) with
                {
                    Status = AiDurableInvocationStatus.Leased,
                    Lease = new AiDurableInvocationLease(workerId, checked((record.Lease?.Epoch ?? 0) + 1),
                        Guid.NewGuid().ToString("N"), now.Add(duration))
                };
            }, cancellationToken);
        }

        /// <summary>
        /// Extends only the current unexpired assignment. Caller-supplied expiry is not
        /// authoritative. A request that would not extend it returns null, not a synthetic
        /// lease confirmation that bypasses the store's authoritative clock check.
        /// </summary>
        public Task<AiDurableInvocationRecord?> TryRenewLeaseAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity, AiDurableInvocationLease lease,
            TimeSpan duration, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateLease(lease);
            ValidateDuration(duration);
            return ChangeAsync(scope, identity, (record, now) =>
            {
                if (!Live(record, lease, now)) return null;
                var expiry = now.Add(duration);
                if (expiry <= record.Lease!.ExpiresAtUtc) return null;
                return Next(record, now) with { Lease = record.Lease with { ExpiresAtUtc = expiry } };
            }, cancellationToken);
        }

        /// <summary>
        /// Accepts one result with its Pending continuation in the same CAS. An identical
        /// duplicate from the accepted assignment is acknowledged, including after expiry;
        /// a conflicting result throws and an old/wrong assignment is rejected.
        /// </summary>
        public async Task<AiDurableInvocationCompletionStatus> CompleteAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity, AiDurableInvocationLease lease,
            AiDurableInvocationResult result, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.ValidateLease(lease);
            cancellationToken.ThrowIfCancellationRequested();
            var frozen = AiDurableInvocationValidation.Freeze(result);
            var current = await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
            {
                if (current is null) return AiDurableInvocationCompletionStatus.NotFound;
                if (AiDurableInvocationValidation.Terminal(current))
                {
                    if (!AiDurableInvocationValidation.SameAssignment(current.Lease, lease))
                        return AiDurableInvocationCompletionStatus.LeaseRejected;
                    AiDurableInvocationValidation.Require(current.Result == frozen, "The accepted assignment reported a conflicting terminal result.");
                    return AiDurableInvocationCompletionStatus.AlreadyAccepted;
                }
                var now = AtLeast(Now(), current.UpdatedAtUtc);
                if (!Live(current, lease, now)) return AiDurableInvocationCompletionStatus.LeaseRejected;
                var updated = Next(current, now) with
                {
                    Status = frozen.Success ? AiDurableInvocationStatus.Succeeded : AiDurableInvocationStatus.Failed,
                    Result = frozen,
                    ResultSha256 = AiDurableInvocationValidation.ResultHash(frozen),
                    CompletedAtUtc = now,
                    ContinuationStatus = AiDurableInvocationContinuationStatus.Pending
                };
                AiDurableInvocationValidation.ValidateTransition(current, updated);
                var outcome = await TryReplaceClassifiedAsync(scope, identity, current, updated, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome.Kind == AiDurableInvocationCasOutcomeKind.Applied)
                    return AiDurableInvocationCompletionStatus.Accepted;
                if (outcome.Kind == AiDurableInvocationCasOutcomeKind.AuthorityPredicateRejected)
                    return AiDurableInvocationCompletionStatus.LeaseRejected;
                current = outcome.CurrentRecord ??
                    await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            }
            throw Contended();
        }

        /// <summary>
        /// Records queue scheduling, never application. Scheduled records remain eligible
        /// for reconciliation even when a notification was consumed before the DAG parked.
        /// </summary>
        public Task<AiDurableInvocationRecord?> MarkContinuationScheduledAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            CancellationToken cancellationToken = default) =>
            ChangeAsync(scope, identity, (record, now) =>
            {
                if (!AiDurableInvocationValidation.Terminal(record)) return null;
                if (record.ContinuationStatus is AiDurableInvocationContinuationStatus.Applied or AiDurableInvocationContinuationStatus.Suppressed)
                    return null;
                if (record.ContinuationStatus == AiDurableInvocationContinuationStatus.Scheduled) return record;
                return Next(record, now) with { ContinuationStatus = AiDurableInvocationContinuationStatus.Scheduled };
            }, cancellationToken);

        /// <summary>
        /// Rotates a retained candidate after a bounded reconciliation pass. This changes
        /// neither the result nor continuation disposition and is not queue acknowledgement.
        /// A monotonic millisecond avoids starvation when several passes share a clock tick.
        /// </summary>
        public Task<AiDurableInvocationRecord?> DeferContinuationAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            CancellationToken cancellationToken = default) =>
            ChangeAsync(scope, identity, (record, now) =>
            {
                if (!AiDurableInvocationValidation.Terminal(record) || record.ContinuationStatus is
                    AiDurableInvocationContinuationStatus.Applied or AiDurableInvocationContinuationStatus.Suppressed) return null;
                return Next(record, now > record.UpdatedAtUtc ? now : record.UpdatedAtUtc.AddMilliseconds(1));
            }, cancellationToken);

        /// <summary>
        /// Accepts correlated evidence from trusted continuation code, not from a worker.
        /// This journal does not inspect the DAG: the caller must have observed actual
        /// result application or an authoritative reason that the parent cannot resume.
        /// </summary>
        public Task<AiDurableInvocationRecord?> AcknowledgeContinuationAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            AiDurableInvocationContinuationAcknowledgement acknowledgement,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(acknowledgement);
            AiDurableInvocationValidation.Require(acknowledgement.Status is AiDurableInvocationContinuationStatus.Applied or
                AiDurableInvocationContinuationStatus.Suppressed, "Only applied/suppressed evidence can acknowledge a continuation.");
            AiDurableInvocationValidation.Text(acknowledgement.Reason, nameof(acknowledgement.Reason));
            return ChangeAsync(scope, identity, (record, now) =>
            {
                if (!AiDurableInvocationValidation.Terminal(record)) return null;
                AiDurableInvocationValidation.Require(record.OperationId == acknowledgement.OperationId &&
                    record.ResultSha256 == acknowledgement.ResultSha256, "Continuation acknowledgement refers to a different operation/result.");
                if (record.ContinuationStatus is AiDurableInvocationContinuationStatus.Applied or AiDurableInvocationContinuationStatus.Suppressed)
                {
                    AiDurableInvocationValidation.Require(record.ContinuationStatus == acknowledgement.Status &&
                        record.ContinuationReason == acknowledgement.Reason, "Continuation acknowledgement conflicts with the accepted evidence.");
                    return record;
                }
                return Next(record, now) with
                {
                    ContinuationStatus = acknowledgement.Status,
                    ContinuationReason = acknowledgement.Reason
                };
            }, cancellationToken);
        }

        private async Task<AiDurableInvocationRecord?> ChangeAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            Func<AiDurableInvocationRecord, DateTimeOffset, AiDurableInvocationRecord?> change,
            CancellationToken cancellationToken)
        {
            var current = await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt < MaxCasAttempts; attempt++)
            {
                if (current is null) return null;
                var updated = change(current, AtLeast(Now(), current.UpdatedAtUtc));
                if (updated is null || updated == current) return updated;
                AiDurableInvocationValidation.ValidateTransition(current, updated);
                var outcome = await TryReplaceClassifiedAsync(scope, identity, current, updated, cancellationToken)
                    .ConfigureAwait(false);
                if (outcome.Kind == AiDurableInvocationCasOutcomeKind.Applied) return updated;
                if (outcome.Kind == AiDurableInvocationCasOutcomeKind.AuthorityPredicateRejected) throw AuthorityRejected();
                current = outcome.CurrentRecord ??
                    await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false);
            }
            throw Contended();
        }

        private async Task<AiDurableInvocationCasOutcome> TryReplaceClassifiedAsync(
            AiDurableInvocationScope scope, AiDurableInvocationIdentity identity,
            AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement,
            CancellationToken cancellationToken)
        {
            if (_store is IAiDurableInvocationClassifiedCasStore classified)
                return await classified.TryReplaceClassifiedAsync(expected, replacement, cancellationToken).ConfigureAwait(false);

            if (await _store.TryReplaceAsync(expected, replacement, cancellationToken).ConfigureAwait(false))
                return AiDurableInvocationCasOutcome.Applied();

            // Legacy stores cannot classify the rejection. Treat it as contention, reload
            // durable truth once, and preserve the pre-existing retry semantics.
            return AiDurableInvocationCasOutcome.RevisionConflict(
                await GetAsync(scope, identity, cancellationToken).ConfigureAwait(false));
        }

        private DateTimeOffset Now() => AiDurableInvocationValidation.Milliseconds(_time.GetUtcNow());
        private static DateTimeOffset AtLeast(DateTimeOffset value, DateTimeOffset minimum) => value < minimum ? minimum : value;
        private static AiDurableInvocationRecord Next(AiDurableInvocationRecord record, DateTimeOffset now) =>
            record with { Revision = checked(record.Revision + 1), UpdatedAtUtc = now };
        private static bool Live(AiDurableInvocationRecord record, AiDurableInvocationLease lease, DateTimeOffset now) =>
            record.Status == AiDurableInvocationStatus.Leased &&
            AiDurableInvocationValidation.SameAssignment(record.Lease, lease) && record.Lease!.ExpiresAtUtc > now;
        private static void ValidateDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromMinutes(5) || duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
                throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be an integral 1..300000 milliseconds.");
        }
        private static InvalidOperationException AuthorityRejected() =>
            new("Invocation CAS was rejected by an authoritative storage predicate; retrying the same transition would not change the durable authority decision.");
        private static InvalidOperationException Contended() =>
            new("Invocation CAS attempts were exhausted; contention must be reconciled without changing logical identity.");
    }
}
