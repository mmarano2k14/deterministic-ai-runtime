using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
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
    /// </remarks>
    public sealed class AiRuntimeInstanceReadinessWaiter : IAiRuntimeInstanceReadinessWaiter
    {
        private readonly IAiRuntimeInstanceRegistry runtimeInstanceRegistry;
        private readonly IAiRuntimeInstanceCapacityStore runtimeInstanceCapacityStore;

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

        private async Task<AiRuntimeInstanceReadinessResult> CheckReadinessOnceAsync(
            AiRuntimeInstanceReadinessRequest request,
            CancellationToken cancellationToken)
        {
            var snapshot = await runtimeInstanceRegistry
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

            var capacity = await runtimeInstanceCapacityStore
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
    }
}