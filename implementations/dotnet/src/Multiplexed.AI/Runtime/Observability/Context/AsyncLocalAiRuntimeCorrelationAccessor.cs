using System.Threading;

using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;

namespace Multiplexed.AI.Runtime.Observability.Context
{
    /// <summary>
    /// Provides an <see cref="AsyncLocal{T}"/>-backed implementation of
    /// <see cref="IAiRuntimeCorrelationAccessor"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This accessor stores the current runtime execution correlation context inside the
    /// current asynchronous execution flow.
    /// </para>
    ///
    /// <para>
    /// The accessor itself is intended to be registered as a singleton. The current
    /// correlation context remains isolated per asynchronous flow by <see cref="AsyncLocal{T}"/>.
    /// </para>
    ///
    /// <para>
    /// When no ambient correlation context has been pushed, this implementation returns
    /// a fallback runtime-level correlation context built from the current
    /// <see cref="IAiRuntimeInstanceIdentityDescriptor"/>.
    /// </para>
    ///
    /// <para>
    /// This implementation does not provide distributed propagation. Correlation values such as
    /// <c>RunId</c>, <c>CorrelationId</c>, and <c>ExecutionId</c> must still be propagated explicitly
    /// through queued run metadata, execution metadata, or durable state when crossing process,
    /// worker, or runtime instance boundaries.
    /// </para>
    /// </remarks>
    public sealed class AsyncLocalAiRuntimeCorrelationAccessor : IAiRuntimeCorrelationAccessor
    {
        private const string FallbackWorkerId = "runtime-host";

        private static readonly AsyncLocal<AiRuntimeExecutionCorrelationContext?> CurrentContext = new();

        private readonly IAiRuntimeInstanceIdentityDescriptor _runtimeInstanceIdentity;
        private readonly Lazy<AiRuntimeExecutionCorrelationContext> _fallbackContext;

        /// <summary>
        /// Initializes a new instance of the <see cref="AsyncLocalAiRuntimeCorrelationAccessor"/> class.
        /// </summary>
        /// <param name="runtimeInstanceIdentity">The runtime instance identity used to create fallback correlation context.</param>
        public AsyncLocalAiRuntimeCorrelationAccessor(
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity)
        {
            _runtimeInstanceIdentity = runtimeInstanceIdentity
                ?? throw new ArgumentNullException(nameof(runtimeInstanceIdentity));

            _fallbackContext = new Lazy<AiRuntimeExecutionCorrelationContext>(
                CreateFallbackContext,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <inheritdoc />
        public AiRuntimeExecutionCorrelationContext? Current =>
            CurrentContext.Value ?? _fallbackContext.Value;

        /// <inheritdoc />
        public IDisposable Push(
            AiRuntimeExecutionCorrelationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            var previous = CurrentContext.Value;
            CurrentContext.Value = context;

            return new RestoreCorrelationScope(previous);
        }

        /// <summary>
        /// Creates the fallback correlation context used when no ambient context is available.
        /// </summary>
        /// <returns>The fallback runtime execution correlation context.</returns>
        private AiRuntimeExecutionCorrelationContext CreateFallbackContext()
        {
            return new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = _runtimeInstanceIdentity.RuntimeInstanceId,
                RuntimeInstanceId = _runtimeInstanceIdentity.RuntimeInstanceId,
                WorkerId = FallbackWorkerId
            };
        }

        private sealed class RestoreCorrelationScope : IDisposable
        {
            private readonly AiRuntimeExecutionCorrelationContext? _previous;
            private bool _disposed;

            /// <summary>
            /// Initializes a new instance of the <see cref="RestoreCorrelationScope"/> class.
            /// </summary>
            /// <param name="previous">The previous correlation context to restore on disposal.</param>
            public RestoreCorrelationScope(
                AiRuntimeExecutionCorrelationContext? previous)
            {
                _previous = previous;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                CurrentContext.Value = _previous;
                _disposed = true;
            }
        }
    }
}