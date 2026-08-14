using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims
{
    /// <summary>
    /// Provides atomic in-memory recovery claims for exact runtime and membership failures.
    /// </summary>
    public sealed class InMemoryAiRuntimePoolRecoveryClaimStore :
        IAiRuntimePoolRecoveryClaimStore,
        IAiRuntimePoolRecoveryMembershipClaimStore
    {
        private readonly object syncRoot = new();

        private readonly Dictionary<string, ActiveRuntimeClaim>
            runtimeClaimsByFailureId =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, ActiveMembershipClaim>
            membershipClaimsByFailureId =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task<AiRuntimePoolRecoveryClaimAcquisition>
            TryAcquireAsync(
                AiRuntimePoolRecoveryClaimRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateRuntimeRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var normalized =
                NormalizeRuntimeRequest(request);

            lock (this.syncRoot)
            {
                if (this.membershipClaimsByFailureId.ContainsKey(
                        normalized.FailureId))
                {
                    throw new AiRuntimePoolRecoveryClaimConflictException(
                        normalized.FailureId);
                }

                if (this.runtimeClaimsByFailureId.TryGetValue(
                        normalized.FailureId,
                        out var existing))
                {
                    if (!HasSameRuntimeAuthority(
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
                        ClaimedAtUtc = DateTimeOffset.UtcNow
                    };

                var leaseId = CreateLeaseId();
                var releaseToken = CreateReleaseToken();

                this.runtimeClaimsByFailureId.Add(
                    claim.FailureId,
                    new ActiveRuntimeClaim(
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
                            new RuntimeLease(
                                this,
                                claim,
                                leaseId,
                                releaseToken)
                    });
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRecoveryMembershipClaimAcquisition>
            TryAcquireMembershipAsync(
                AiRuntimePoolRecoveryMembershipClaimRequest request,
                CancellationToken cancellationToken = default)
        {
            ValidateMembershipRequest(request);
            cancellationToken.ThrowIfCancellationRequested();

            var normalized =
                NormalizeMembershipRequest(request);

            lock (this.syncRoot)
            {
                if (this.runtimeClaimsByFailureId.ContainsKey(
                        normalized.FailureId))
                {
                    throw new AiRuntimePoolRecoveryClaimConflictException(
                        normalized.FailureId);
                }

                if (this.membershipClaimsByFailureId.TryGetValue(
                        normalized.FailureId,
                        out var existing))
                {
                    if (!HasSameMembershipAuthority(
                            existing.Claim,
                            normalized))
                    {
                        throw new AiRuntimePoolRecoveryClaimConflictException(
                            normalized.FailureId);
                    }

                    return Task.FromResult(
                        new AiRuntimePoolRecoveryMembershipClaimAcquisition
                        {
                            Status =
                                AiRuntimePoolRecoveryClaimAcquisitionStatus
                                    .AlreadyClaimed,
                            Claim = existing.Claim
                        });
                }

                var claim =
                    new AiRuntimePoolRecoveryMembershipClaim
                    {
                        ClaimId =
                            AiRuntimePoolRecoveryMembershipClaimIdentityFactory
                                .CreateClaimId(normalized),
                        FailureId = normalized.FailureId,
                        PoolId = normalized.PoolId,
                        HostId = normalized.HostId,
                        MembershipFingerprint =
                            normalized.MembershipFingerprint,
                        MemberCount = normalized.MemberCount,
                        InventoryFingerprint =
                            normalized.InventoryFingerprint,
                        CandidateCount = normalized.CandidateCount,
                        ClaimedBy = normalized.ClaimedBy,
                        ClaimedAtUtc = DateTimeOffset.UtcNow
                    };

                var leaseId = CreateLeaseId();
                var releaseToken = CreateReleaseToken();

                this.membershipClaimsByFailureId.Add(
                    claim.FailureId,
                    new ActiveMembershipClaim(
                        claim,
                        leaseId,
                        releaseToken));

                return Task.FromResult(
                    new AiRuntimePoolRecoveryMembershipClaimAcquisition
                    {
                        Status =
                            AiRuntimePoolRecoveryClaimAcquisitionStatus
                                .Acquired,
                        Claim = claim,
                        Lease =
                            new MembershipLease(
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
                if (!this.runtimeClaimsByFailureId.TryGetValue(
                        failureId.Trim(),
                        out var active))
                {
                    return Task.FromResult<
                        AiRuntimePoolRecoveryClaim?>(null);
                }

                return Task.FromResult<
                    AiRuntimePoolRecoveryClaim?>(active.Claim);
            }
        }

        /// <inheritdoc />
        public Task<AiRuntimePoolRecoveryMembershipClaim?>
            GetMembershipByFailureIdAsync(
                string failureId,
                CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                if (!this.membershipClaimsByFailureId.TryGetValue(
                        failureId.Trim(),
                        out var active))
                {
                    return Task.FromResult<
                        AiRuntimePoolRecoveryMembershipClaim?>(null);
                }

                return Task.FromResult<
                    AiRuntimePoolRecoveryMembershipClaim?>(active.Claim);
            }
        }

        /// <inheritdoc />
        public Task<bool> IsActiveLeaseAsync(
            string failureId,
            string claimId,
            string leaseId,
            CancellationToken cancellationToken = default)
        {
            ValidateLeaseIdentity(
                failureId,
                claimId,
                leaseId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                var active =
                    this.runtimeClaimsByFailureId.TryGetValue(
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

        /// <inheritdoc />
        public Task<bool> IsActiveMembershipLeaseAsync(
            string failureId,
            string claimId,
            string leaseId,
            CancellationToken cancellationToken = default)
        {
            ValidateLeaseIdentity(
                failureId,
                claimId,
                leaseId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                var active =
                    this.membershipClaimsByFailureId.TryGetValue(
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

        private ValueTask ReleaseRuntimeAsync(
            string failureId,
            string claimId,
            string leaseId,
            string releaseToken)
        {
            lock (this.syncRoot)
            {
                if (this.runtimeClaimsByFailureId.TryGetValue(
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
                    this.runtimeClaimsByFailureId.Remove(failureId);
                }
            }

            return ValueTask.CompletedTask;
        }

        private ValueTask ReleaseMembershipAsync(
            string failureId,
            string claimId,
            string leaseId,
            string releaseToken)
        {
            lock (this.syncRoot)
            {
                if (this.membershipClaimsByFailureId.TryGetValue(
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
                    this.membershipClaimsByFailureId.Remove(failureId);
                }
            }

            return ValueTask.CompletedTask;
        }

        private static void ValidateRuntimeRequest(
            AiRuntimePoolRecoveryClaimRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.RuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RouteId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.InventoryFingerprint);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ClaimedBy);

            if (request.CandidateCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "CandidateCount cannot be negative.");
            }
        }

        private static void ValidateMembershipRequest(
            AiRuntimePoolRecoveryMembershipClaimRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.FailureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.MembershipFingerprint);
            ArgumentException.ThrowIfNullOrWhiteSpace(
                request.InventoryFingerprint);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ClaimedBy);

            if (request.MemberCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "MemberCount must be greater than zero.");
            }

            if (request.CandidateCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "CandidateCount cannot be negative.");
            }
        }

        private static void ValidateLeaseIdentity(
            string failureId,
            string claimId,
            string leaseId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failureId);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimId);
            ArgumentException.ThrowIfNullOrWhiteSpace(leaseId);
        }

        private static AiRuntimePoolRecoveryClaimRequest
            NormalizeRuntimeRequest(
                AiRuntimePoolRecoveryClaimRequest request)
        {
            return request with
            {
                FailureId = request.FailureId.Trim(),
                PoolId = request.PoolId.Trim(),
                HostId = request.HostId.Trim(),
                RuntimeInstanceId = request.RuntimeInstanceId.Trim(),
                RouteId = request.RouteId.Trim(),
                InventoryFingerprint =
                    request.InventoryFingerprint.Trim()
                        .ToLowerInvariant(),
                ClaimedBy = request.ClaimedBy.Trim()
            };
        }

        private static AiRuntimePoolRecoveryMembershipClaimRequest
            NormalizeMembershipRequest(
                AiRuntimePoolRecoveryMembershipClaimRequest request)
        {
            return request with
            {
                FailureId = request.FailureId.Trim(),
                PoolId = request.PoolId.Trim(),
                HostId = request.HostId.Trim(),
                MembershipFingerprint =
                    request.MembershipFingerprint.Trim()
                        .ToLowerInvariant(),
                InventoryFingerprint =
                    request.InventoryFingerprint.Trim()
                        .ToLowerInvariant(),
                ClaimedBy = request.ClaimedBy.Trim()
            };
        }

        private static bool HasSameRuntimeAuthority(
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
                claim.CandidateCount == request.CandidateCount;
        }

        private static bool HasSameMembershipAuthority(
            AiRuntimePoolRecoveryMembershipClaim claim,
            AiRuntimePoolRecoveryMembershipClaimRequest request)
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
                    claim.MembershipFingerprint,
                    request.MembershipFingerprint) &&
                claim.MemberCount == request.MemberCount &&
                StringComparer.Ordinal.Equals(
                    claim.InventoryFingerprint,
                    request.InventoryFingerprint) &&
                claim.CandidateCount == request.CandidateCount;
        }

        private static string CreateLeaseId()
        {
            return string.Concat(
                "recovery-lease-",
                Guid.NewGuid().ToString("N"));
        }

        private static string CreateReleaseToken()
        {
            return Guid.NewGuid().ToString("N");
        }

        private sealed record ActiveRuntimeClaim(
            AiRuntimePoolRecoveryClaim Claim,
            string LeaseId,
            string ReleaseToken);

        private sealed record ActiveMembershipClaim(
            AiRuntimePoolRecoveryMembershipClaim Claim,
            string LeaseId,
            string ReleaseToken);

        private sealed class RuntimeLease :
            IAiRuntimePoolRecoveryClaimLease
        {
            private readonly InMemoryAiRuntimePoolRecoveryClaimStore owner;
            private readonly string releaseToken;
            private int disposed;

            public RuntimeLease(
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

            public AiRuntimePoolRecoveryClaim Claim { get; }

            public string LeaseId { get; }

            public bool IsReleased =>
                Volatile.Read(ref this.disposed) != 0;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(
                        ref this.disposed,
                        1) != 0)
                {
                    return ValueTask.CompletedTask;
                }

                return this.owner.ReleaseRuntimeAsync(
                    this.Claim.FailureId,
                    this.Claim.ClaimId,
                    this.LeaseId,
                    this.releaseToken);
            }
        }

        private sealed class MembershipLease :
            IAiRuntimePoolRecoveryMembershipClaimLease
        {
            private readonly InMemoryAiRuntimePoolRecoveryClaimStore owner;
            private readonly string releaseToken;
            private int disposed;

            public MembershipLease(
                InMemoryAiRuntimePoolRecoveryClaimStore owner,
                AiRuntimePoolRecoveryMembershipClaim claim,
                string leaseId,
                string releaseToken)
            {
                this.owner = owner;
                this.Claim = claim;
                this.LeaseId = leaseId;
                this.releaseToken = releaseToken;
            }

            public AiRuntimePoolRecoveryMembershipClaim Claim { get; }

            public string LeaseId { get; }

            public bool IsReleased =>
                Volatile.Read(ref this.disposed) != 0;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(
                        ref this.disposed,
                        1) != 0)
                {
                    return ValueTask.CompletedTask;
                }

                return this.owner.ReleaseMembershipAsync(
                    this.Claim.FailureId,
                    this.Claim.ClaimId,
                    this.LeaseId,
                    this.releaseToken);
            }
        }
    }
}
