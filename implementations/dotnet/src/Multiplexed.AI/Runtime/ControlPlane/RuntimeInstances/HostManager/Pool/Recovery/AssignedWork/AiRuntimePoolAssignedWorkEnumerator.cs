using System;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Enumerates existing durable recovery candidates for one exact failed runtime instance.
    /// </summary>
    public sealed class AiRuntimePoolAssignedWorkEnumerator :
        IAiRuntimePoolAssignedWorkEnumerator
    {
        private readonly IAiRuntimePoolFailureReader failureReader;
        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly IAiRuntimePoolSuppressedAssignedWorkEnumerator
            suppressedAssignedWorkEnumerator;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimePoolAssignedWorkEnumerator"/> class.
        /// </summary>
        /// <param name="failureReader">The exact failure journal reader.</param>
        /// <param name="safetyReader">The exact capacity suppression reader.</param>
        /// <param name="runtimeRunExecutionIndex">The existing durable runtime-run index.</param>
        public AiRuntimePoolAssignedWorkEnumerator(
            IAiRuntimePoolFailureReader failureReader,
            IAiRuntimePoolCapacitySafetyReader safetyReader,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex)
        {
            this.failureReader =
                failureReader
                ?? throw new ArgumentNullException(nameof(failureReader));

            this.safetyReader =
                safetyReader
                ?? throw new ArgumentNullException(nameof(safetyReader));

            ArgumentNullException.ThrowIfNull(runtimeRunExecutionIndex);

            this.suppressedAssignedWorkEnumerator =
                new AiRuntimePoolSuppressedAssignedWorkEnumerator(
                    this.safetyReader,
                    runtimeRunExecutionIndex);
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolAssignedWorkInventory> EnumerateAsync(
            string failureId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedFailureId = failureId.Trim();

            var failure =
                await this.failureReader
                    .GetByFailureIdAsync(
                        normalizedFailureId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (failure is null)
            {
                throw CreateAuthorityException(
                    normalizedFailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .FailureNotFound,
                    $"Runtime Pool failure '{normalizedFailureId}' does not exist.");
            }

            if (failure.Scope !=
                AiRuntimePoolFailureScope.RuntimeInstance)
            {
                throw CreateAuthorityException(
                    normalizedFailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .UnsupportedFailureScope,
                    $"Runtime Pool failure '{normalizedFailureId}' has unsupported scope '{failure.Scope}'.");
            }

            var suppression =
                await this.safetyReader
                    .GetSuppressionAsync(
                        failure.PoolId,
                        failure.HostId,
                        failure.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (suppression is null)
            {
                throw CreateAuthorityException(
                    normalizedFailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .SuppressionMissing,
                    $"Runtime instance '{failure.RuntimeInstanceId}' is not suppressed for failure '{normalizedFailureId}'.");
            }

            if (!StringComparer.Ordinal.Equals(
                    suppression.FailureId,
                    failure.FailureId))
            {
                throw CreateAuthorityException(
                    normalizedFailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .FailureMismatch,
                    $"Runtime instance '{failure.RuntimeInstanceId}' suppression belongs to failure '{suppression.FailureId}' instead of '{failure.FailureId}'.");
            }

            if (suppression.Scope !=
                    AiRuntimePoolCapacitySuppressionScope.RuntimeInstanceRoute ||
                !StringComparer.Ordinal.Equals(
                    suppression.RouteId,
                    failure.RouteId))
            {
                throw CreateAuthorityException(
                    normalizedFailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .RouteMismatch,
                    $"Runtime instance '{failure.RuntimeInstanceId}' suppression route '{suppression.RouteId}' does not match failed route '{failure.RouteId}'.");
            }

            return await this.suppressedAssignedWorkEnumerator
                .EnumerateAsync(
                    suppression,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates one typed authority exception.
        /// </summary>
        private static AiRuntimePoolAssignedWorkAuthorityException
            CreateAuthorityException(
                string failureId,
                AiRuntimePoolAssignedWorkAuthorityFailure reason,
                string message)
        {
            return new AiRuntimePoolAssignedWorkAuthorityException(
                failureId,
                reason,
                message);
        }
    }
}
