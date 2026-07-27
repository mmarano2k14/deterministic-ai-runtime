using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Provides atomic in-memory recovery claims for the local process-host Runtime Pool.
    /// </summary>
    public sealed class InMemoryAiRuntimePoolRecoveryClaimStore :
        IAiRuntimePoolRecoveryClaimStore
    {
        private readonly object syncRoot = new();

        private readonly Dictionary<string, ActiveClaim>
            claimsByFailureId =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimePoolRecoveryClaimAcquisition>
            TryAcquireAsync(
                AiRuntimePoolRecoveryClaimRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var normalized =
                Normalize(request);

            lock (this.syncRoot)
            {
                if (this.claimsByFailureId.TryGetValue(
                        normalized.FailureId,
                        out var existing))
                {
                    if (!HasSameAuthority(
                            existing.Claim,
                            normalized))
                    {
                        throw new AiRuntimePoolRecoveryClaimConflictException(
                            normalized.FailureId);
                    }

                    return Task.FromResult(
                        new AiRuntimePoolRecoveryClaimAcquisition
                        {
                            Status =
                                AiRuntimePoolRecoveryClaimAcquisitionStatus
                                    .AlreadyClaimed,
                            Claim = existing.Claim
                        });
                }

                var claim =
                    new AiRuntimePoolRecoveryClaim
                    {
                        ClaimId =
                            AiRuntimePoolRecoveryClaimIdentityFactory
                                .CreateClaimId(normalized),
                        FailureId = normalized.FailureId,
                        PoolId = normalized.PoolId,
                        HostId = normalized.HostId,
                        RuntimeInstanceId =
                            normalized.RuntimeInstanceId,
                        RouteId = normalized.RouteId,
                        InventoryFingerprint =
                            normalized.InventoryFingerprint,
                        CandidateCount =
                            normalized.CandidateCount,
                        ClaimedBy = normalized.ClaimedBy,
                        ClaimedAtUtc =
                            DateTimeOffset.UtcNow
                    };

                var leaseId =
                    string.Concat(
                        "recovery-lease-",
                        Guid.NewGuid().ToString("N"));

                var releaseToken =
                    Guid.NewGuid()
                        .ToString("N");

                this.claimsByFailureId.Add(
                    claim.FailureId,
                    new ActiveClaim(
                        claim,
                        leaseId,
                        releaseToken));

                return Task.FromResult(
                    new AiRuntimePoolRecoveryClaimAcquisition
                    {
                        Status =
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .Acquired,
                        Claim = claim,
                        Lease =
                            new Lease(
                                this,
                                claim,
                                leaseId,
                                releaseToken)
                    });
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRecoveryClaim?> GetByFailureIdAsync(
            string failureId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.claimsByFailureId.TryGetValue(
                        failureId.Trim(),
                        out var active))
                {
                    return Task.FromResult<
                        AiRuntimePoolRecoveryClaim?>(
                        null);
                }

                return Task.FromResult<
                    AiRuntimePoolRecoveryClaim?>(
                    active.Claim);
            }
        }

        /// <inheritdoc />
        public Task<bool> IsActiveLeaseAsync(
            string failureId,
            string claimId,
            string leaseId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimId);
            ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                var active =
                    this.claimsByFailureId.TryGetValue(
                        failureId.Trim(),
                        out var existing) &&
                    StringComparer.Ordinal.Equals(
                        existing.Claim.ClaimId,
                        claimId.Trim()) &&
                    StringComparer.Ordinal.Equals(
                        existing.LeaseId,
                        leaseId.Trim());

                return Task.FromResult(active);
            }
        }

        /// <summary>
        /// Releases one claim only when the private lease token and public lease incarnation still
        /// own it.
        /// </summary>
        private ValueTask ReleaseAsync(
            string failureId,
            string claimId,
            string leaseId,
            string releaseToken)
        {
            lock (this.syncRoot)
            {
                if (this.claimsByFailureId.TryGetValue(
                        failureId,
                        out var existing) &&
                    StringComparer.Ordinal.Equals(
                        existing.Claim.ClaimId,
                        claimId) &&
                    StringComparer.Ordinal.Equals(
                        existing.LeaseId,
                        leaseId) &&
                    StringComparer.Ordinal.Equals(
                        existing.ReleaseToken,
                        releaseToken))
                {
                    this.claimsByFailureId.Remove(
                        failureId);
                }
            }

            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Validates one exact claim request.
        /// </summary>
        private static void ValidateRequest(
            AiRuntimePoolRecoveryClaimRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.RuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.RouteId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.InventoryFingerprint);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.ClaimedBy);

            if (request.CandidateCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "CandidateCount cannot be negative.");
            }
        }

        /// <summary>
        /// Normalizes authoritative string values.
        /// </summary>
        private static AiRuntimePoolRecoveryClaimRequest Normalize(
            AiRuntimePoolRecoveryClaimRequest request)
        {
            return request with
            {
                FailureId = request.FailureId.Trim(),
                PoolId = request.PoolId.Trim(),
                HostId = request.HostId.Trim(),
                RuntimeInstanceId =
                    request.RuntimeInstanceId.Trim(),
                RouteId = request.RouteId.Trim(),
                InventoryFingerprint =
                    request.InventoryFingerprint.Trim()
                        .ToLowerInvariant(),
                ClaimedBy = request.ClaimedBy.Trim()
            };
        }

        /// <summary>
        /// Determines whether an active claim has the exact requested authority.
        /// </summary>
        private static bool HasSameAuthority(
            AiRuntimePoolRecoveryClaim claim,
            AiRuntimePoolRecoveryClaimRequest request)
        {
            return
                StringComparer.Ordinal.Equals(
                    claim.FailureId,
                    request.FailureId) &&
                StringComparer.Ordinal.Equals(
                    claim.PoolId,
                    request.PoolId) &&
                StringComparer.Ordinal.Equals(
                    claim.HostId,
                    request.HostId) &&
                StringComparer.Ordinal.Equals(
                    claim.RuntimeInstanceId,
                    request.RuntimeInstanceId) &&
                StringComparer.Ordinal.Equals(
                    claim.RouteId,
                    request.RouteId) &&
                StringComparer.Ordinal.Equals(
                    claim.InventoryFingerprint,
                    request.InventoryFingerprint) &&
                claim.CandidateCount ==
                    request.CandidateCount;
        }

        /// <summary>
        /// Stores one active claim and its release authority.
        /// </summary>
        private sealed record ActiveClaim(
            AiRuntimePoolRecoveryClaim Claim,
            string LeaseId,
            string ReleaseToken);

        /// <summary>
        /// Owns the private release token for one active claim.
        /// </summary>
        private sealed class Lease :
            IAiRuntimePoolRecoveryClaimLease
        {
            private readonly InMemoryAiRuntimePoolRecoveryClaimStore owner;
            private readonly string releaseToken;
            private int disposed;

            /// <summary>
            /// Initializes a new instance of the <see cref="Lease"/> class.
            /// </summary>
            public Lease(
                InMemoryAiRuntimePoolRecoveryClaimStore owner,
                AiRuntimePoolRecoveryClaim claim,
                string leaseId,
                string releaseToken)
            {
                this.owner = owner;
                this.Claim = claim;
                this.LeaseId = leaseId;
                this.releaseToken = releaseToken;
            }

            /// <inheritdoc />
            public AiRuntimePoolRecoveryClaim Claim { get; }

            /// <inheritdoc />
            public string LeaseId { get; }

            /// <inheritdoc />
            public bool IsReleased =>
                Volatile.Read(ref this.disposed) != 0;

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(
                        ref this.disposed,
                        1) != 0)
                {
                    return ValueTask.CompletedTask;
                }

                return this.owner.ReleaseAsync(
                    this.Claim.FailureId,
                    this.Claim.ClaimId,
                    this.LeaseId,
                    this.releaseToken);
            }
        }
    }
}
