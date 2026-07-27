using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Enumerates existing durable recovery candidates for one exact suppressed runtime instance.
    /// </summary>
    public sealed class AiRuntimePoolAssignedWorkEnumerator :
        IAiRuntimePoolAssignedWorkEnumerator
    {
        private static readonly string[] SharedRunMetadataKeys =
        {
            "sharedRunId",
            "shared.run.id",
            "sharedRun.id",
            "recovery.sharedRunId",
            "recovery.shared.run.id"
        };

        private readonly IAiRuntimePoolFailureReader failureReader;
        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;

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

            this.runtimeRunExecutionIndex =
                runtimeRunExecutionIndex
                ?? throw new ArgumentNullException(
                    nameof(runtimeRunExecutionIndex));
        }

        /// <inheritdoc />
        public async Task<AiRuntimePoolAssignedWorkInventory> EnumerateAsync(
            string failureId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedFailureId =
                failureId.Trim();

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

            if (!StringComparer.Ordinal.Equals(
                    suppression.RouteId,
                    failure.RouteId))
            {
                throw CreateAuthorityException(
                    normalizedFailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .RouteMismatch,
                    $"Runtime instance '{failure.RuntimeInstanceId}' suppression route '{suppression.RouteId}' does not match failed route '{failure.RouteId}'.");
            }

            var recoverableEntries =
                await this.runtimeRunExecutionIndex
                    .ListRecoverableByRuntimeInstanceAsync(
                        failure.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var candidates =
                recoverableEntries
                    .Select(
                        entry =>
                            CreateCandidate(
                                failure,
                                entry))
                    .OrderBy(
                        candidate =>
                            candidate.Kind)
                    .ThenBy(
                        candidate =>
                            candidate.CreatedAtUtc)
                    .ThenBy(
                        candidate =>
                            candidate.LocalRunId,
                        StringComparer.Ordinal)
                    .ToArray();

            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = failure.FailureId,
                PoolId = failure.PoolId,
                HostId = failure.HostId,
                RuntimeInstanceId =
                    failure.RuntimeInstanceId,
                RouteId = failure.RouteId,
                EnumeratedAtUtc =
                    DateTimeOffset.UtcNow,
                Candidates = candidates
            };
        }

        /// <summary>
        /// Projects one exact durable index entry into a recovery candidate.
        /// </summary>
        private static AiRuntimePoolAssignedWorkCandidate
            CreateCandidate(
                AiRuntimePoolFailureObservation failure,
                AiRuntimeRunExecutionIndexEntry entry)
        {
            ArgumentNullException.ThrowIfNull(failure);
            ArgumentNullException.ThrowIfNull(entry);

            if (string.IsNullOrWhiteSpace(
                    entry.RuntimeInstanceId) ||
                !StringComparer.Ordinal.Equals(
                    entry.RuntimeInstanceId,
                    failure.RuntimeInstanceId))
            {
                throw CreateAuthorityException(
                    failure.FailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .RuntimeBoundaryViolation,
                    $"Runtime-run '{entry.RunId}' belongs to runtime '{entry.RuntimeInstanceId}' instead of failed runtime '{failure.RuntimeInstanceId}'.");
            }

            return new AiRuntimePoolAssignedWorkCandidate
            {
                FailureId = failure.FailureId,
                PoolId = failure.PoolId,
                HostId = failure.HostId,
                RuntimeInstanceId =
                    failure.RuntimeInstanceId,
                RouteId = failure.RouteId,
                LocalRunId = entry.RunId,
                ExecutionId = entry.ExecutionId,
                Status = entry.Status,
                TenantId =
                    entry.ExecutionContextSnapshot.TenantId,
                TenantGroupId =
                    entry.ExecutionContextSnapshot.TenantGroupId,
                SharedRunId =
                    TryGetMetadataValue(
                        entry.Metadata,
                        SharedRunMetadataKeys),
                Kind = ResolveKind(entry),
                CreatedAtUtc =
                    entry.CreatedAtUtc,
                Metadata =
                    new Dictionary<string, string>(
                        entry.Metadata,
                        StringComparer.OrdinalIgnoreCase)
            };
        }

        /// <summary>
        /// Resolves the same deterministic candidate priority used by runtime recovery.
        /// </summary>
        private static AiRuntimePoolAssignedWorkKind ResolveKind(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(
                    entry.ExecutionId))
            {
                return AiRuntimePoolAssignedWorkKind.InFlight;
            }

            if (string.Equals(
                    entry.Status,
                    "queued",
                    StringComparison.OrdinalIgnoreCase))
            {
                return AiRuntimePoolAssignedWorkKind.LocalQueued;
            }

            return AiRuntimePoolAssignedWorkKind.OtherRecoverable;
        }

        /// <summary>
        /// Reads the first non-empty metadata value matching one of the existing keys.
        /// </summary>
        private static string? TryGetMetadataValue(
            IReadOnlyDictionary<string, string>? metadata,
            IEnumerable<string> keys)
        {
            if (metadata is null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (metadata.TryGetValue(
                        key,
                        out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var key in keys)
            {
                var match =
                    metadata.FirstOrDefault(
                        pair =>
                            string.Equals(
                                pair.Key,
                                key,
                                StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(
                        match.Value))
                {
                    return match.Value;
                }
            }

            return null;
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
