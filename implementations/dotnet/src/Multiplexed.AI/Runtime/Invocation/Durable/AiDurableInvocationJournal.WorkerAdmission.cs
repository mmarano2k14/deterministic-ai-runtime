using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable
{
    public sealed partial class AiDurableInvocationJournal
    {
        /// <summary>
        /// Applies host retry admission inside every existing CAS attempt, not only to a stale
        /// discovery snapshot. The general lease API retains its historical behavior.
        /// </summary>
        public Task<AiDurableInvocationRecord?> TryAcquireWorkerLeaseAsync(AiDurableInvocationScope scope,
            AiDurableInvocationIdentity identity, string workerId, TimeSpan duration,
            bool allowExpiredLeaseReassignment, int maxAssignmentEpoch, CancellationToken cancellationToken = default)
        {
            AiDurableInvocationValidation.Text(workerId, nameof(workerId)); ValidateDuration(duration);
            if (maxAssignmentEpoch is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maxAssignmentEpoch));
            return ChangeAsync(scope, identity, (record, now) =>
            {
                if (AiDurableInvocationValidation.Terminal(record) || record.Lease?.ExpiresAtUtc > now ||
                    record.Lease is not null && !allowExpiredLeaseReassignment) return null;
                var epoch = checked((record.Lease?.Epoch ?? 0) + 1);
                if (epoch > maxAssignmentEpoch) return null;
                return Next(record, now) with
                {
                    Status = AiDurableInvocationStatus.Leased,
                    Lease = new AiDurableInvocationLease(workerId, epoch, Guid.NewGuid().ToString("N"), now.Add(duration))
                };
            }, cancellationToken);
        }
    }
}
