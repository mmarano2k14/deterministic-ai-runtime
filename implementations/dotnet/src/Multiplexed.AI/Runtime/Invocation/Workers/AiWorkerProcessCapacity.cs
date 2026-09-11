namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>Process-wide launch bound. An unconfirmed cleanup permanently withholds its slot until host recovery.</summary>
    public sealed class AiWorkerProcessCapacity : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private int _quarantined;
        public AiWorkerProcessCapacity(AiWorkerSupervisionOptions options)
        { ArgumentNullException.ThrowIfNull(options); _slots = new(options.MaxConcurrentProcesses, options.MaxConcurrentProcesses); }
        public int Available => _slots.CurrentCount;
        public int Quarantined => Volatile.Read(ref _quarantined);
        public Lease? TryEnter() => _slots.Wait(0) ? new Lease(this) : null;
        public void Dispose() => _slots.Dispose();
        public sealed class Lease : IDisposable
        {
            private AiWorkerProcessCapacity? _owner;
            private bool _quarantine;
            internal Lease(AiWorkerProcessCapacity owner) => _owner = owner;
            public void Quarantine() => _quarantine = true;
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is null) return;
                if (_quarantine) Interlocked.Increment(ref owner._quarantined); else owner._slots.Release();
            }
        }
    }
}
