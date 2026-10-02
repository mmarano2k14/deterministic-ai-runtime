using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>
    /// Heartbeat is liveness, not authority. Only a confirmed journal CAS extends the local
    /// safety deadline; a lost renewal acknowledgement stops the process rather than guessing.
    /// </summary>
    internal sealed class AiWorkerLeaseGuard : IDisposable
    {
        private readonly AiDurableInvocationJournal _journal;
        private readonly AiDurableInvocationRecord _record;
        private readonly AiWorkerSupervisionOptions _options;
        private readonly TimeProvider _time;
        private readonly CancellationTokenSource _lost = new();
        private readonly ITimer _timer;
        private AiDurableInvocationLease _lease;
        private DateTimeOffset _renewAt;
        private bool _disposed;
        internal AiWorkerLeaseGuard(AiDurableInvocationJournal journal, AiDurableInvocationRecord record,
            AiWorkerSupervisionOptions options, TimeProvider time)
        {
            _journal = journal; _record = record; _options = options; _time = time;
            _lease = record.Lease ?? throw new InvalidOperationException("Worker supervision requires an acquired lease.");
            _renewAt = time.GetUtcNow() + options.RenewalInterval;
            _timer = time.CreateTimer(_ => OnSafetyTimer(), null, Remaining(), Timeout.InfiniteTimeSpan);
        }
        internal CancellationToken LostToken => _lost.Token;
        internal AiDurableInvocationLease Lease => _lease;
        internal bool IsLost => _lost.IsCancellationRequested;
        internal async Task HeartbeatAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || IsLost || Remaining() <= TimeSpan.Zero) { Lose(); throw new OperationCanceledException("Worker lease authority expired.", token); }
            if (_time.GetUtcNow() < _renewAt) return;
            try
            {
                var renewed = await _journal.TryRenewLeaseAsync(_record.Definition.Scope, _record.Definition.Identity,
                    _lease, _options.LeaseDuration, token).ConfigureAwait(false);
                if (renewed is null || _disposed || IsLost)
                    throw new InvalidOperationException("Worker lease renewal was not authoritatively confirmed.");
                _lease = renewed.Lease!;
                _renewAt = _time.GetUtcNow() + _options.RenewalInterval;
                var remaining = Remaining();
                if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("The confirmed worker lease is already unsafe.");
                _timer.Change(remaining, Timeout.InfiniteTimeSpan);
            }
            catch { Lose(); throw; }
        }
        private TimeSpan Remaining()
        {
            var duration = _lease.ExpiresAtUtc - _time.GetUtcNow() - _options.LeaseSafetyMargin;
            return duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
        }
        private void OnSafetyTimer()
        {
            if (_disposed || IsLost) return;

            var remaining = Remaining();
            if (remaining <= TimeSpan.Zero)
            {
                Lose();
                return;
            }

            // Timer delivery is only a wake-up signal. Lease authority is derived from
            // the configured TimeProvider, so an early/spurious callback must not revoke
            // a lease that is still inside its confirmed safety window.
            try { _timer.Change(remaining, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        private void Lose()
        {
            try { _lost.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
        }
        public void Dispose() { _disposed = true; _timer.Dispose(); _lost.Dispose(); }
    }
}
