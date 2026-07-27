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
        IAiRuntimePoolCapacitySafetyRegistry
    {
        private readonly object syncRoot = new();

        private readonly Dictionary<string, AiRuntimePoolCapacitySuppression>
            suppressionsByRuntimeInstanceId =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimePoolCapacitySuppression> SuppressAsync(
            AiRuntimePoolCapacitySuppression suppression,
            CancellationToken cancellationToken = default)
        {
            ValidateSuppression(suppression);
            cancellationToken.ThrowIfCancellationRequested();

            var normalized =
                Normalize(suppression);

            lock (this.syncRoot)
            {
                if (this.suppressionsByRuntimeInstanceId.TryGetValue(
                        normalized.RuntimeInstanceId,
                        out var existing))
                {
                    if (existing == normalized)
                    {
                        return Task.FromResult(existing);
                    }

                    throw new AiRuntimePoolCapacitySuppressionConflictException(
                        normalized.RuntimeInstanceId);
                }

                this.suppressionsByRuntimeInstanceId.Add(
                    normalized.RuntimeInstanceId,
                    normalized);

                return Task.FromResult(normalized);
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
            ArgumentException.ThrowIfNullOrWhiteSpace(
                suppression.RouteId);

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
                RouteId = suppression.RouteId.Trim()
            };
        }
    }
}
