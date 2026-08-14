using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;

namespace Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling
{
    /// <summary>
    /// Single-process Runtime Pool Pod creation reservation authority.
    /// </summary>
    public sealed class InMemoryAiRuntimePoolPodCreationReservationStore :
        IAiRuntimePoolPodCreationReservationStore
    {
        private readonly object sync = new();
        private readonly Dictionary<string, Dictionary<string, DateTimeOffset>>
            reservations = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimePoolPodCreationReservationAttemptResult> TryAcquireAsync(
            string controlPlaneId,
            string poolId,
            string reservationId,
            int activePodCount,
            int maximumPodCount,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken = default)
        {
            Validate(
                controlPlaneId,
                poolId,
                reservationId,
                activePodCount,
                maximumPodCount,
                expiresAtUtc);

            cancellationToken.ThrowIfCancellationRequested();

            var authorityKey =
                CreateAuthorityKey(controlPlaneId, poolId);

            lock (this.sync)
            {
                var now = DateTimeOffset.UtcNow;

                if (!this.reservations.TryGetValue(
                        authorityKey,
                        out var authorityReservations))
                {
                    authorityReservations =
                        new Dictionary<string, DateTimeOffset>(
                            StringComparer.Ordinal);
                    this.reservations[authorityKey] =
                        authorityReservations;
                }

                foreach (var expired in authorityReservations
                    .Where(item => item.Value <= now)
                    .Select(item => item.Key)
                    .ToArray())
                {
                    authorityReservations.Remove(expired);
                }

                if (authorityReservations.ContainsKey(reservationId))
                {
                    authorityReservations[reservationId] = expiresAtUtc;

                    return Task.FromResult(
                        CreateResult(
                            acquired: true,
                            activePodCount,
                            authorityReservations.Count,
                            maximumPodCount));
                }

                if ((long)activePodCount +
                    authorityReservations.Count >=
                    maximumPodCount)
                {
                    return Task.FromResult(
                        CreateResult(
                            acquired: false,
                            activePodCount,
                            authorityReservations.Count,
                            maximumPodCount));
                }

                authorityReservations[reservationId] = expiresAtUtc;

                return Task.FromResult(
                    CreateResult(
                        acquired: true,
                        activePodCount,
                        authorityReservations.Count,
                        maximumPodCount));
            }
        }

        /// <inheritdoc />
        public Task ReleaseAsync(
            string controlPlaneId,
            string poolId,
            string reservationId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
            cancellationToken.ThrowIfCancellationRequested();

            var authorityKey =
                CreateAuthorityKey(controlPlaneId, poolId);

            lock (this.sync)
            {
                if (this.reservations.TryGetValue(
                        authorityKey,
                        out var authorityReservations))
                {
                    authorityReservations.Remove(reservationId);

                    if (authorityReservations.Count == 0)
                    {
                        this.reservations.Remove(authorityKey);
                    }
                }
            }

            return Task.CompletedTask;
        }

        private static void Validate(
            string controlPlaneId,
            string poolId,
            string reservationId,
            int activePodCount,
            int maximumPodCount,
            DateTimeOffset expiresAtUtc)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
            ArgumentOutOfRangeException.ThrowIfNegative(activePodCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPodCount);

            if (expiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expiresAtUtc),
                    expiresAtUtc,
                    "Pod creation reservation expiration must be in the future.");
            }
        }

        private static string CreateAuthorityKey(
            string controlPlaneId,
            string poolId)
        {
            return string.Concat(
                controlPlaneId.Trim(),
                "|",
                poolId.Trim());
        }

        private static AiRuntimePoolPodCreationReservationAttemptResult
            CreateResult(
                bool acquired,
                int activePodCount,
                int reservedPodCount,
                int maximumPodCount)
        {
            return new AiRuntimePoolPodCreationReservationAttemptResult
            {
                Acquired = acquired,
                ActivePodCount = activePodCount,
                ReservedPodCount = reservedPodCount,
                MaximumPodCount = maximumPodCount
            };
        }
    }
}
