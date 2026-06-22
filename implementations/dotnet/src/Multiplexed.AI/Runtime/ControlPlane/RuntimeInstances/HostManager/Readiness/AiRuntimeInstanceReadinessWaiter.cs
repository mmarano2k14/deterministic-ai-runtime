using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Readiness
{
    /// <summary>
    /// Provides a provider-agnostic readiness waiter for runtime instances created through scale-out.
    /// </summary>
    /// <remarks>
    /// This waiter validates runtime instance visibility and capacity before a scale-out request can be fulfilled.
    /// It does not dispatch runs, mutate execution state, or bypass runtime queues.
    ///
    /// IMPORTANT:
    /// - Readiness is evaluated using the execution context carried by the scale-out request.
    /// - This is required for dedicated tenant runtime instances because registry and capacity stores are tenant-visible.
    /// - Without the request execution context, a background scale-out watcher may not see a dedicated runtime instance.
    /// </remarks>
    public sealed class AiRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
    {
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore;
        private readonly IConnectionMultiplexer? redis;
        private readonly IOptions<AiRuntimeInstanceRegistrationOptions>? registrationOptions;
        private readonly IAiControlPlaneIdResolver? controlPlaneIdResolver;
        private readonly IAiRuntimeInstanceVisibilityEvaluator? visibilityEvaluator;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceReadinessWaiter"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        public AiRuntimeInstanceReadinessWaiter(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore)
        {
            this.runtimeInstanceRegistry = runtimeInstanceRegistry ?? throw new ArgumentNullException(nameof(runtimeInstanceRegistry));
            this.runtimeInstanceCapacityStore = runtimeInstanceCapacityStore ?? throw new ArgumentNullException(nameof(runtimeInstanceCapacityStore));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeInstanceReadinessWaiter"/> class.
        /// </summary>
        /// <param name="runtimeInstanceRegistry">The runtime instance registry.</param>
        /// <param name="runtimeInstanceCapacityStore">The runtime instance capacity store.</param>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="registrationOptions">The runtime instance registration options.</param>
        /// <param name="controlPlaneIdResolver">The control-plane id resolver.</param>
        /// <param name="visibilityEvaluator">The runtime instance visibility evaluator.</param>
        public AiRuntimeInstanceReadinessWaiter(
            IAiRuntimeInstanceRegistry runtimeInstanceRegistry,
            IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore,
            IConnectionMultiplexer redis,
            IOptions<AiRuntimeInstanceRegistrationOptions> registrationOptions,
            IAiControlPlaneIdResolver controlPlaneIdResolver,
            IAiRuntimeInstanceVisibilityEvaluator visibilityEvaluator)
            : this(runtimeInstanceRegistry, runtimeInstanceCapacityStore)
        {
            this.redis = redis ?? throw new ArgumentNullException(nameof(redis));
            this.registrationOptions = registrationOptions ?? throw new ArgumentNullException(nameof(registrationOptions));
            this.controlPlaneIdResolver = controlPlaneIdResolver ?? throw new ArgumentNullException(nameof(controlPlaneIdResolver));
            this.visibilityEvaluator = visibilityEvaluator ?? throw new ArgumentNullException(nameof(visibilityEvaluator));
        }

        /// <inheritdoc />
        public async Task<AiRuntimeInstanceReadinessResult> WaitUntilReadyAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var timeout = request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : request.Timeout;
            var pollInterval = request.PollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : request.PollInterval;
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            string? lastFailureReason = null;

            try
            {
                while (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var checkResult = await CheckReadinessOnceAsync(request, cancellationToken).ConfigureAwait(false);

                    if (checkResult.Success)
                    {
                        return checkResult;
                    }

                    lastFailureReason = checkResult.FailureReason;

                    var remaining = deadline - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var delay = remaining < pollInterval ? remaining : pollInterval;

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                return CreateFailure(request, lastFailureReason ?? "runtime-readiness-timeout", timedOut: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CreateFailure(request, "runtime-readiness-cancelled", timedOut: false);
            }
            catch
            {
                return CreateFailure(request, "runtime-readiness-exception", timedOut: false);
            }
        }

        /// <summary>
        /// Checks runtime readiness once.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The readiness result.</returns>
        private async Task<AiRuntimeInstanceReadinessResult> CheckReadinessOnceAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken)
        {
            var stores = CreateRequestScopedStores(request);

            var snapshot = await stores.Registry
                .GetAsync(request.RuntimeInstanceId, cancellationToken)
                .ConfigureAwait(false);

            if (snapshot is null)
            {
                return CreateFailure(request, "runtime-readiness-registry-missing", timedOut: false);
            }

            if (!string.IsNullOrWhiteSpace(request.ControlPlaneId) &&
                !string.Equals(snapshot.ControlPlaneId, request.ControlPlaneId, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailure(request, "runtime-readiness-control-plane-mismatch", timedOut: false);
            }

            var capacity = await stores.CapacityStore
                .GetAsync(request.RuntimeInstanceId, cancellationToken)
                .ConfigureAwait(false);

            if (capacity is null)
            {
                return CreateFailure(request, "runtime-readiness-capacity-missing", timedOut: false);
            }

            if (snapshot.Status != AiRuntimeInstanceStatus.Ready)
            {
                return CreateFailure(request, "runtime-readiness-not-ready", timedOut: false);
            }

            if (!snapshot.CanAcceptRun)
            {
                return CreateFailure(request, "runtime-readiness-cannot-accept-run", timedOut: false);
            }

            if (snapshot.AvailableRunSlots is <= 0)
            {
                return CreateFailure(request, "runtime-readiness-capacity-unavailable", timedOut: false);
            }

            return new AiRuntimeInstanceReadinessResult
            {
                Success = true,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName
            };
        }

        /// <summary>
        /// Creates request-scoped registry and capacity stores when Redis dependencies are available.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <returns>The registry and capacity store to use for readiness checks.</returns>
        private (IAiRuntimeInstanceRegistry Registry, IAiRuntimeInstanceCapacityStore CapacityStore) CreateRequestScopedStores(
            AiRuntimeInstanceReadinessRequest request)
        {
            if (request.ExecutionContextSnapshot is null ||
                this.redis is null ||
                this.registrationOptions is null ||
                this.controlPlaneIdResolver is null ||
                this.visibilityEvaluator is null)
            {
                return (this.runtimeInstanceRegistry, this.runtimeInstanceCapacityStore);
            }

            var executionContextProvider = new FixedExecutionContextSnapshotProvider(request.ExecutionContextSnapshot);

            return (
                new RedisAiRuntimeInstanceRegistry(
                    this.redis,
                    this.registrationOptions,
                    this.controlPlaneIdResolver,
                    this.visibilityEvaluator,
                    executionContextProvider),
                new RedisAiRuntimeInstanceCapacityStore(
                    this.redis,
                    this.registrationOptions,
                    this.controlPlaneIdResolver,
                    this.visibilityEvaluator,
                    executionContextProvider));
        }

        /// <summary>
        /// Creates a readiness failure result.
        /// </summary>
        /// <param name="request">The readiness request.</param>
        /// <param name="failureReason">The failure reason.</param>
        /// <param name="timedOut">A value indicating whether the readiness wait timed out.</param>
        /// <returns>The readiness failure result.</returns>
        private static AiRuntimeInstanceReadinessResult CreateFailure(
            AiRuntimeInstanceReadinessRequest request,
            string failureReason,
            bool timedOut)
        {
            return new AiRuntimeInstanceReadinessResult
            {
                Success = false,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                FailureReason = failureReason,
                TimedOut = timedOut
            };
        }

        /// <summary>
        /// Provides a fixed execution context snapshot for request-scoped readiness store reads.
        /// </summary>
        private sealed class FixedExecutionContextSnapshotProvider : IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="FixedExecutionContextSnapshotProvider"/> class.
            /// </summary>
            /// <param name="snapshot">The fixed execution context snapshot.</param>
            public FixedExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return this.snapshot;
            }
        }
    }
}