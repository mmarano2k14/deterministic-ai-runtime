using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Reuses the durable runtime-run index for one exact suppressed runtime member.
    /// </summary>
    public sealed class AiRuntimePoolSuppressedAssignedWorkEnumerator :
        IAiRuntimePoolSuppressedAssignedWorkEnumerator
    {
        private static readonly string[] SharedRunMetadataKeys =
        {
            "sharedRunId",
            "shared.run.id",
            "sharedRun.id",
            "recovery.sharedRunId",
            "recovery.shared.run.id"
        };

        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex;

        public AiRuntimePoolSuppressedAssignedWorkEnumerator(
            IAiRuntimePoolCapacitySafetyReader safetyReader,
            IAiRuntimeRunExecutionIndex runtimeRunExecutionIndex)
        {
            this.safetyReader =
                safetyReader
                ?? throw new ArgumentNullException(nameof(safetyReader));
            this.runtimeRunExecutionIndex =
                runtimeRunExecutionIndex
                ?? throw new ArgumentNullException(
                    nameof(runtimeRunExecutionIndex));
        }

        public async Task<AiRuntimePoolAssignedWorkInventory> EnumerateAsync(
            AiRuntimePoolCapacitySuppression suppression,
            CancellationToken cancellationToken = default)
        {
            ValidateSuppression(suppression);
            cancellationToken.ThrowIfCancellationRequested();

            var authoritative =
                await this.safetyReader
                    .GetSuppressionAsync(
                        suppression.PoolId,
                        suppression.HostId,
                        suppression.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (authoritative is null)
            {
                throw CreateAuthorityException(
                    suppression.FailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .SuppressionMissing,
                    $"Runtime instance '{suppression.RuntimeInstanceId}' is not authoritatively suppressed for failure '{suppression.FailureId}'.");
            }

            if (!StringComparer.Ordinal.Equals(
                    authoritative.FailureId,
                    suppression.FailureId))
            {
                throw CreateAuthorityException(
                    suppression.FailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .FailureMismatch,
                    $"Runtime instance '{suppression.RuntimeInstanceId}' suppression belongs to failure '{authoritative.FailureId}' instead of '{suppression.FailureId}'.");
            }

            if (authoritative.Scope != suppression.Scope ||
                !StringComparer.Ordinal.Equals(
                    authoritative.RouteId,
                    suppression.RouteId))
            {
                throw CreateAuthorityException(
                    suppression.FailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure.RouteMismatch,
                    $"Runtime instance '{suppression.RuntimeInstanceId}' suppression authority does not match the requested scope and route.");
            }

            var recoverableEntries =
                await this.runtimeRunExecutionIndex
                    .ListRecoverableByRuntimeInstanceAsync(
                        authoritative.RuntimeInstanceId,
                        cancellationToken)
                    .ConfigureAwait(false);

            var candidates =
                recoverableEntries
                    .Select(
                        entry =>
                            CreateCandidate(
                                authoritative,
                                entry))
                    .OrderBy(candidate => candidate.Kind)
                    .ThenBy(candidate => candidate.CreatedAtUtc)
                    .ThenBy(
                        candidate => candidate.LocalRunId,
                        StringComparer.Ordinal)
                    .ToArray();

            return new AiRuntimePoolAssignedWorkInventory
            {
                FailureId = authoritative.FailureId,
                PoolId = authoritative.PoolId,
                HostId = authoritative.HostId,
                RuntimeInstanceId = authoritative.RuntimeInstanceId,
                RouteId = authoritative.RouteId,
                EnumeratedAtUtc = DateTimeOffset.UtcNow,
                Candidates = candidates
            };
        }

        private static AiRuntimePoolAssignedWorkCandidate CreateCandidate(
            AiRuntimePoolCapacitySuppression suppression,
            AiRuntimeRunExecutionIndexEntry entry)
        {
            ArgumentNullException.ThrowIfNull(suppression);
            ArgumentNullException.ThrowIfNull(entry);

            if (string.IsNullOrWhiteSpace(entry.RuntimeInstanceId) ||
                !StringComparer.Ordinal.Equals(
                    entry.RuntimeInstanceId,
                    suppression.RuntimeInstanceId))
            {
                throw CreateAuthorityException(
                    suppression.FailureId,
                    AiRuntimePoolAssignedWorkAuthorityFailure
                        .RuntimeBoundaryViolation,
                    $"Runtime-run '{entry.RunId}' belongs to runtime '{entry.RuntimeInstanceId}' instead of suppressed runtime '{suppression.RuntimeInstanceId}'.");
            }

            return new AiRuntimePoolAssignedWorkCandidate
            {
                FailureId = suppression.FailureId,
                PoolId = suppression.PoolId,
                HostId = suppression.HostId,
                RuntimeInstanceId = suppression.RuntimeInstanceId,
                RouteId = suppression.RouteId,
                LocalRunId = entry.RunId,
                ExecutionId = entry.ExecutionId,
                Status = entry.Status,
                TenantId = entry.ExecutionContextSnapshot.TenantId,
                TenantGroupId = entry.ExecutionContextSnapshot.TenantGroupId,
                SharedRunId =
                    TryGetMetadataValue(
                        entry.Metadata,
                        SharedRunMetadataKeys),
                Kind = ResolveKind(entry),
                CreatedAtUtc = entry.CreatedAtUtc,
                Metadata =
                    new Dictionary<string, string>(
                        entry.Metadata,
                        StringComparer.OrdinalIgnoreCase)
            };
        }

        private static AiRuntimePoolAssignedWorkKind ResolveKind(
            AiRuntimeRunExecutionIndexEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.ExecutionId))
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
                if (metadata.TryGetValue(key, out var value) &&
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

                if (!string.IsNullOrWhiteSpace(match.Value))
                {
                    return match.Value;
                }
            }

            return null;
        }

        private static void ValidateSuppression(
            AiRuntimePoolCapacitySuppression suppression)
        {
            ArgumentNullException.ThrowIfNull(suppression);
            ArgumentException.ThrowIfNullOrWhiteSpace(suppression.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(suppression.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(suppression.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                suppression.RuntimeInstanceId);

            if (suppression.Scope ==
                    AiRuntimePoolCapacitySuppressionScope.RuntimeInstanceRoute &&
                string.IsNullOrWhiteSpace(suppression.RouteId))
            {
                throw new ArgumentException(
                    "Route-scoped suppression requires RouteId.",
                    nameof(suppression));
            }

            if (suppression.Scope ==
                    AiRuntimePoolCapacitySuppressionScope.HostMembership &&
                !string.IsNullOrWhiteSpace(suppression.RouteId))
            {
                throw new ArgumentException(
                    "Host-membership suppression must not carry RouteId.",
                    nameof(suppression));
            }

            if (suppression.SuppressedAtUtc == default)
            {
                throw new ArgumentException(
                    "SuppressedAtUtc is required.",
                    nameof(suppression));
            }
        }

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
