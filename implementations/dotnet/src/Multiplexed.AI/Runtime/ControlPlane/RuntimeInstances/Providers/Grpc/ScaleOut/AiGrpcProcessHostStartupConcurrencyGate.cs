using System.Collections.Concurrent;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Providers.Grpc.ScaleOut
{
    /// <summary>
    /// Coordinates process-host startup and readiness across all gRPC scale-out
    /// provisioners in the current process.
    /// </summary>
    /// <remarks>
    /// Each concurrency key owns one process-wide semaphore. The first positive
    /// limit observed for a key becomes the effective limit for the lifetime of
    /// the current process.
    ///
    /// Callers using the same key should therefore provide the same configured
    /// concurrency limit.
    /// </remarks>
    public static class AiGrpcProcessHostStartupConcurrencyGate
    {
        private static readonly ConcurrentDictionary<string, GateState> Gates =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Acquires a process-host startup slot.
        /// </summary>
        /// <param name="concurrencyKey">
        /// The process-wide concurrency key.
        /// </param>
        /// <param name="maxConcurrency">
        /// The requested maximum concurrency.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The acquired gate lease.
        /// </returns>
        public static async Task<Lease> AcquireAsync(
            string concurrencyKey,
            int maxConcurrency,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(concurrencyKey);

            if (maxConcurrency <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxConcurrency),
                    maxConcurrency,
                    "Process-host startup concurrency must be greater than zero when the gate is enabled.");
            }

            var normalizedKey = concurrencyKey.Trim();

            var state = Gates.GetOrAdd(
                normalizedKey,
                static (_, requestedLimit) => new GateState(requestedLimit),
                maxConcurrency);

            var waitStartedAtUtc = DateTimeOffset.UtcNow;

            Interlocked.Increment(ref state.WaitingCount);

            try
            {
                await state.Semaphore
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref state.WaitingCount);
            }

            Interlocked.Increment(ref state.ActiveCount);

            return new Lease(
                normalizedKey,
                maxConcurrency,
                state,
                DateTimeOffset.UtcNow - waitStartedAtUtc);
        }

        /// <summary>
        /// Represents an acquired process-host startup slot.
        /// </summary>
        public sealed class Lease : IDisposable
        {
            private readonly GateState state;
            private int disposed;

            /// <summary>
            /// Initializes a new instance of the <see cref="Lease"/> class.
            /// </summary>
            internal Lease(
                string concurrencyKey,
                int requestedMaxConcurrency,
                GateState state,
                TimeSpan waitDuration)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(concurrencyKey);
                ArgumentNullException.ThrowIfNull(state);

                ConcurrencyKey = concurrencyKey;
                RequestedMaxConcurrency = requestedMaxConcurrency;
                this.state = state;
                WaitDuration = waitDuration;
            }

            /// <summary>
            /// Gets the process-wide concurrency key.
            /// </summary>
            public string ConcurrencyKey { get; }

            /// <summary>
            /// Gets the requested maximum concurrency.
            /// </summary>
            public int RequestedMaxConcurrency { get; }

            /// <summary>
            /// Gets the effective maximum concurrency established for the key.
            /// </summary>
            public int EffectiveMaxConcurrency => state.MaxConcurrency;

            /// <summary>
            /// Gets the time spent waiting for the slot.
            /// </summary>
            public TimeSpan WaitDuration { get; }

            /// <summary>
            /// Gets the current number of acquired slots.
            /// </summary>
            public int ActiveCount =>
                Volatile.Read(ref state.ActiveCount);

            /// <summary>
            /// Gets the current number of waiting callers.
            /// </summary>
            public int WaitingCount =>
                Volatile.Read(ref state.WaitingCount);

            /// <inheritdoc />
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                Interlocked.Decrement(ref state.ActiveCount);
                state.Semaphore.Release();
            }
        }

        /// <summary>
        /// Holds the shared semaphore and its diagnostic counters for one gate.
        /// </summary>
        internal sealed class GateState
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="GateState"/> class.
            /// </summary>
            /// <param name="maxConcurrency">
            /// The maximum number of concurrently acquired slots.
            /// </param>
            public GateState(int maxConcurrency)
            {
                MaxConcurrency = maxConcurrency;
                Semaphore = new SemaphoreSlim(
                    maxConcurrency,
                    maxConcurrency);
            }

            /// <summary>
            /// Gets the effective maximum concurrency.
            /// </summary>
            public int MaxConcurrency { get; }

            /// <summary>
            /// Gets the semaphore controlling admission.
            /// </summary>
            public SemaphoreSlim Semaphore { get; }

            /// <summary>
            /// Stores the current number of acquired slots.
            /// </summary>
            public int ActiveCount;

            /// <summary>
            /// Stores the current number of waiting callers.
            /// </summary>
            public int WaitingCount;
        }
    }
}