using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity
{
    /// <summary>
    /// Provides a deterministic thread-safe in-memory exact capacity suppression registry.
    /// </summary>
    public sealed class InMemoryAiRuntimePoolCapacitySafetyRegistry :
        IAiRuntimePoolCapacitySafetyRegistry,
        IAiRuntimePoolCapacitySafetyBatchWriter
    {
        private readonly object syncRoot = new();

        private readonly Dictionary<string, AiRuntimePoolCapacitySuppression>
            suppressionsByRuntimeInstanceId =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public async Task<AiRuntimePoolCapacitySuppression> SuppressAsync(
            AiRuntimePoolCapacitySuppression suppression,
            CancellationToken cancellationToken = default)
        {
            var suppressions =
                await this.SuppressBatchAsync(
                        new[]
                        {
                            suppression
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

            return suppressions[0];
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
            SuppressBatchAsync(
                IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(suppressions);
            cancellationToken.ThrowIfCancellationRequested();

            if (suppressions.Count == 0)
            {
                throw new ArgumentException(
                    "At least one capacity suppression is required.",
                    nameof(suppressions));
            }

            var normalized =
                suppressions
                    .Select(
                        suppression =>
                        {
                            ValidateSuppression(suppression);
                            return Normalize(suppression);
                        })
                    .OrderBy(
                        suppression => suppression.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ToArray();

            var runtimeInstanceIds =
                new HashSet<string>(StringComparer.Ordinal);

            foreach (var suppression in normalized)
            {
                if (!runtimeInstanceIds.Add(
                        suppression.RuntimeInstanceId))
                {
                    throw new ArgumentException(
                        string.Concat(
                            "The batch contains duplicate RuntimeInstanceId '",
                            suppression.RuntimeInstanceId,
                            "'."),
                        nameof(suppressions));
                }
            }

            lock (this.syncRoot)
            {
                foreach (var suppression in normalized)
                {
                    if (this.suppressionsByRuntimeInstanceId.TryGetValue(
                            suppression.RuntimeInstanceId,
                            out var existing) &&
                        existing != suppression)
                    {
                        throw new AiRuntimePoolCapacitySuppressionConflictException(
                            suppression.RuntimeInstanceId);
                    }
                }

                var authoritative =
                    new List<AiRuntimePoolCapacitySuppression>(
                        normalized.Length);

                foreach (var suppression in normalized)
                {
                    if (!this.suppressionsByRuntimeInstanceId.TryGetValue(
                            suppression.RuntimeInstanceId,
                            out var stored))
                    {
                        this.suppressionsByRuntimeInstanceId.Add(
                            suppression.RuntimeInstanceId,
                            suppression);

                        stored = suppression;
                    }

                    authoritative.Add(stored);
                }

                return Task.FromResult<
                    IReadOnlyList<AiRuntimePoolCapacitySuppression>>(
                    authoritative.ToArray());
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolCapacitySuppression?> GetSuppressionAsync(
            string poolId,
            string hostId,
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.suppressionsByRuntimeInstanceId.TryGetValue(
                        runtimeInstanceId.Trim(),
                        out var suppression) ||
                    !StringComparer.Ordinal.Equals(
                        suppression.PoolId,
                        poolId.Trim()) ||
                    !StringComparer.Ordinal.Equals(
                        suppression.HostId,
                        hostId.Trim()))
                {
                    return Task.FromResult<
                        AiRuntimePoolCapacitySuppression?>(
                        null);
                }

                return Task.FromResult<
                    AiRuntimePoolCapacitySuppression?>(
                    suppression);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimePoolCapacitySuppression>>
            ListByHostIdAsync(
                string hostId,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                IReadOnlyList<AiRuntimePoolCapacitySuppression> suppressions =
                    this.suppressionsByRuntimeInstanceId
                        .Values
                        .Where(
                            suppression =>
                                StringComparer.Ordinal.Equals(
                                    suppression.HostId,
                                    hostId.Trim()))
                        .OrderBy(
                            suppression =>
                                suppression.SuppressedAtUtc)
                        .ThenBy(
                            suppression =>
                                suppression.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .ToArray();

                return Task.FromResult(suppressions);
            }
        }

        /// <summary>
        /// Validates one exact immutable capacity suppression.
        /// </summary>
        private static void ValidateSuppression(
            AiRuntimePoolCapacitySuppression suppression)
        {
            ArgumentNullException.ThrowIfNull(suppression);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                suppression.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                suppression.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                suppression.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                suppression.RuntimeInstanceId);
            if (suppression.Scope ==
                    AiRuntimePoolCapacitySuppressionScope.RuntimeInstanceRoute &&
                string.IsNullOrWhiteSpace(suppression.RouteId))
            {
                throw new ArgumentException(
                    "Route-scoped capacity suppression requires RouteId.",
                    nameof(suppression));
            }

            if (suppression.Scope ==
                    AiRuntimePoolCapacitySuppressionScope.HostMembership &&
                !string.IsNullOrWhiteSpace(suppression.RouteId))
            {
                throw new ArgumentException(
                    "Host-membership capacity suppression must not carry a local RouteId.",
                    nameof(suppression));
            }

            if (suppression.SuppressedAtUtc == default)
            {
                throw new ArgumentException(
                    "SuppressedAtUtc is required.",
                    nameof(suppression));
            }
        }

        /// <summary>
        /// Normalizes authoritative string identities before storage.
        /// </summary>
        private static AiRuntimePoolCapacitySuppression Normalize(
            AiRuntimePoolCapacitySuppression suppression)
        {
            return suppression with
            {
                FailureId = suppression.FailureId.Trim(),
                PoolId = suppression.PoolId.Trim(),
                HostId = suppression.HostId.Trim(),
                RuntimeInstanceId =
                    suppression.RuntimeInstanceId.Trim(),
                RouteId = string.IsNullOrWhiteSpace(suppression.RouteId)
                    ? null
                    : suppression.RouteId.Trim()
            };
        }
    }
}
