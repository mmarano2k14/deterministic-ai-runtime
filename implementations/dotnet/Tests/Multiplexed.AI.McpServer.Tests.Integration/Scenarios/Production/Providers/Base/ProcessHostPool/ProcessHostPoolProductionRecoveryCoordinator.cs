using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    /// <summary>
    /// Executes exact parent-host membership suppression, replacement, and claimed recovery by
    /// composing the provider-neutral Runtime Pool failure primitives.
    /// </summary>
    internal sealed class ProcessHostPoolProductionRecoveryCoordinator
    {
        private readonly IAiRuntimePoolFailureObserver failureObserver;
        private readonly IAiRuntimePoolFailureReader failureReader;
        private readonly IAiRuntimePoolCapacitySafetyBatchWriter safetyBatchWriter;
        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly IAiRuntimePoolSuppressedAssignedWorkEnumerator
            assignedWorkEnumerator;
        private readonly IAiRuntimePoolRecoveryMembershipClaimStore claimStore;
        private readonly IAiRuntimePoolRecoveryCandidateTransitionExecutor
            transitionExecutor;
        private readonly ITestOutputHelper output;
        private readonly string logPrefix;

        public ProcessHostPoolProductionRecoveryCoordinator(
            IServiceProvider services,
            ITestOutputHelper output,
            string logPrefix)
        {
            ArgumentNullException.ThrowIfNull(services);
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.logPrefix = string.IsNullOrWhiteSpace(logPrefix)
                ? throw new ArgumentException("A log prefix is required.", nameof(logPrefix))
                : logPrefix;

            this.failureObserver =
                services.GetRequiredService<IAiRuntimePoolFailureObserver>();
            this.failureReader =
                services.GetRequiredService<IAiRuntimePoolFailureReader>();
            this.safetyBatchWriter =
                services.GetRequiredService<IAiRuntimePoolCapacitySafetyBatchWriter>();
            this.safetyReader =
                services.GetRequiredService<IAiRuntimePoolCapacitySafetyReader>();
            this.assignedWorkEnumerator =
                services.GetRequiredService<IAiRuntimePoolSuppressedAssignedWorkEnumerator>();
            this.claimStore =
                services.GetRequiredService<IAiRuntimePoolRecoveryMembershipClaimStore>();
            this.transitionExecutor =
                services.GetRequiredService<IAiRuntimePoolRecoveryCandidateTransitionExecutor>();
        }

        public async Task<ProcessHostPoolProductionRecoveryProof> RecoverAsync(
            ProcessHostPoolProductionCluster cluster,
            ProcessHostPoolProductionFailureTarget target,
            int cycleNumber,
            string claimedBy,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(cluster);
            ArgumentNullException.ThrowIfNull(target);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

            var failedRuntimeInstanceIds =
                target.Members
                    .Select(member => member.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);
            var impactedLocalRunIds =
                target.ActiveRuns
                    .Select(run => run.LocalRunId)
                    .ToHashSet(StringComparer.Ordinal);
            var impactedSharedRunIds =
                target.ActiveRuns
                    .Select(run => run.SharedRunId)
                    .ToHashSet(StringComparer.Ordinal);
            var impactedExecutionIds =
                target.ActiveRuns
                    .Select(run => run.ExecutionId)
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(cluster.RuntimeCountPerHost, failedRuntimeInstanceIds.Count);
            Assert.Equal(cluster.RuntimeCountPerHost, impactedLocalRunIds.Count);
            Assert.Equal(cluster.RuntimeCountPerHost, impactedSharedRunIds.Count);
            Assert.Equal(cluster.RuntimeCountPerHost, impactedExecutionIds.Count);

            this.output.WriteLine(
                $"[{this.logPrefix} PARENT HOST FAILURE TARGET] Cycle='{cycleNumber}', HostOrdinal='{target.Host.Ordinal}', ParentProcessId='{target.Host.ProcessId}', HostId='{target.Host.HostId}', RuntimeCount='{failedRuntimeInstanceIds.Count}', ActiveRunCount='{target.ActiveRuns.Count}', SurvivingHostCount='{target.SurvivingHostIds.Count}'.");

            await cluster
                .CrashHostAsync(target.Host.HostId, timeout)
                .ConfigureAwait(false);

            var failureId = AiRuntimePoolFailureIdentityFactory.CreateFailureId();
            var observedAtUtc = DateTimeOffset.UtcNow;

            var failure =
                await this.failureObserver
                    .RecordAsync(
                        new AiRuntimePoolFailureObservation
                        {
                            FailureId = failureId,
                            Scope = AiRuntimePoolFailureScope.Host,
                            PoolId = cluster.PoolId,
                            HostId = target.Host.HostId,
                            RuntimeInstanceId = null,
                            RouteId = null,
                            Kind = AiRuntimePoolFailureKind.UnexpectedProcessExit,
                            ExitCode = null,
                            ObservedAtUtc = observedAtUtc,
                            FailureMessage =
                                "Forced busy parent Process Host termination in the multi-host Runtime Pool production proof."
                        })
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            var storedFailure =
                await this.failureReader
                    .GetByFailureIdAsync(failureId)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.NotNull(storedFailure);
            Assert.Equal(failure, storedFailure);
            Assert.Equal(AiRuntimePoolFailureScope.Host, failure.Scope);
            Assert.Equal(AiRuntimePoolFailureKind.UnexpectedProcessExit, failure.Kind);
            Assert.Equal(cluster.PoolId, failure.PoolId);
            Assert.Equal(target.Host.HostId, failure.HostId);
            Assert.Null(failure.RuntimeInstanceId);
            Assert.Null(failure.RouteId);

            var plannedSuppressions =
                target.Members
                    .OrderBy(member => member.RuntimeInstanceId, StringComparer.Ordinal)
                    .Select(
                        member =>
                            new AiRuntimePoolCapacitySuppression
                            {
                                FailureId = failureId,
                                Scope = AiRuntimePoolCapacitySuppressionScope.HostMembership,
                                PoolId = cluster.PoolId,
                                HostId = target.Host.HostId,
                                RuntimeInstanceId = member.RuntimeInstanceId,
                                RouteId = null,
                                SuppressedAtUtc = observedAtUtc
                            })
                    .ToArray();

            var suppressions =
                await this.safetyBatchWriter
                    .SuppressBatchAsync(plannedSuppressions)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(cluster.RuntimeCountPerHost, suppressions.Count);
            Assert.True(
                failedRuntimeInstanceIds.SetEquals(
                    suppressions
                        .Select(item => item.RuntimeInstanceId)),
                "The persisted host-membership suppressions do not match the exact failed runtime membership.");

            var persistedSuppressions =
                await this.safetyReader
                    .ListByHostIdAsync(target.Host.HostId)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(cluster.RuntimeCountPerHost, persistedSuppressions.Count);
            Assert.All(
                persistedSuppressions,
                item =>
                {
                    Assert.Equal(failureId, item.FailureId);
                    Assert.Equal(
                        AiRuntimePoolCapacitySuppressionScope.HostMembership,
                        item.Scope);
                    Assert.Null(item.RouteId);
                    Assert.Contains(item.RuntimeInstanceId, failedRuntimeInstanceIds);
                });

            var runtimeInventories =
                new List<AiRuntimePoolAssignedWorkInventory>(
                    persistedSuppressions.Count);

            foreach (var suppression in persistedSuppressions
                         .OrderBy(
                             item => item.RuntimeInstanceId,
                             StringComparer.Ordinal))
            {
                runtimeInventories.Add(
                    await this.assignedWorkEnumerator
                        .EnumerateAsync(suppression)
                        .WaitAsync(timeout)
                        .ConfigureAwait(false));
            }

            var candidates =
                runtimeInventories
                    .SelectMany(inventory => inventory.Candidates)
                    .OrderBy(candidate => candidate.Kind)
                    .ThenBy(candidate => candidate.CreatedAtUtc)
                    .ThenBy(
                        candidate => candidate.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ThenBy(
                        candidate => candidate.LocalRunId,
                        StringComparer.Ordinal)
                    .ToArray();

            Assert.All(
                candidates,
                candidate =>
                {
                    Assert.Equal(failureId, candidate.FailureId);
                    Assert.Equal(cluster.PoolId, candidate.PoolId);
                    Assert.Equal(target.Host.HostId, candidate.HostId);
                    Assert.Contains(candidate.RuntimeInstanceId, failedRuntimeInstanceIds);
                    Assert.Null(candidate.RouteId);
                });

            Assert.All(
                impactedLocalRunIds,
                localRunId => Assert.Contains(
                    candidates,
                    candidate => StringComparer.Ordinal.Equals(
                        candidate.LocalRunId,
                        localRunId)));

            var acquisition =
                await this.claimStore
                    .TryAcquireMembershipAsync(
                        new AiRuntimePoolRecoveryMembershipClaimRequest
                        {
                            FailureId = failureId,
                            PoolId = cluster.PoolId,
                            HostId = target.Host.HostId,
                            MembershipFingerprint =
                                CalculateMembershipFingerprint(
                                    cluster.PoolId,
                                    target.Host.HostId,
                                    failedRuntimeInstanceIds),
                            MemberCount = failedRuntimeInstanceIds.Count,
                            InventoryFingerprint =
                                CalculateInventoryFingerprint(candidates),
                            CandidateCount = candidates.Length,
                            ClaimedBy = claimedBy.Trim()
                        })
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                acquisition.Status);
            Assert.NotNull(acquisition.Lease);

            await using var lease = acquisition.Lease!;

            var replacement =
                await cluster
                    .ReplaceCrashedHostAsync(target.Host.HostId)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            var outcomes =
                await this.transitionExecutor
                    .ExecuteAsync(
                        failureId,
                        candidates,
                        candidate =>
                            StringComparer.Ordinal.Equals(
                                candidate.FailureId,
                                failureId) &&
                            StringComparer.Ordinal.Equals(
                                candidate.PoolId,
                                cluster.PoolId) &&
                            StringComparer.Ordinal.Equals(
                                candidate.HostId,
                                target.Host.HostId) &&
                            failedRuntimeInstanceIds.Contains(
                                candidate.RuntimeInstanceId) &&
                            candidate.RouteId is null,
                        async cancellationToken =>
                        {
                            var active =
                                await this.claimStore
                                    .IsActiveMembershipLeaseAsync(
                                        failureId,
                                        acquisition.Claim.ClaimId,
                                        lease.LeaseId,
                                        cancellationToken)
                                    .ConfigureAwait(false);

                            if (!active)
                            {
                                throw new InvalidOperationException(
                                    $"Recovery membership lease '{lease.LeaseId}' is no longer active for failure '{failureId}'.");
                            }
                        })
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(candidates.Length, outcomes.Count);

            var currentFailureOutcomes =
                outcomes
                    .Where(
                        outcome => impactedLocalRunIds.Contains(
                            outcome.Candidate.LocalRunId))
                    .ToArray();
            var supersededFailedOutcomes =
                outcomes
                    .Where(
                        outcome => !impactedLocalRunIds.Contains(
                            outcome.Candidate.LocalRunId))
                    .ToArray();

            Assert.Equal(cluster.RuntimeCountPerHost, currentFailureOutcomes.Length);
            Assert.All(
                currentFailureOutcomes,
                outcome =>
                {
                    Assert.True(
                        outcome.Transition.Accepted,
                        outcome.Transition.Reason);
                    Assert.True(
                        outcome.Transition.Changed,
                        outcome.Transition.Reason);
                    Assert.Contains(
                        outcome.Candidate.SharedRunId!,
                        impactedSharedRunIds);
                    Assert.Contains(
                        outcome.Candidate.ExecutionId!,
                        impactedExecutionIds);
                });

            Assert.All(
                supersededFailedOutcomes,
                outcome =>
                {
                    Assert.True(
                        string.Equals(
                            outcome.Candidate.Status,
                            "failed",
                            StringComparison.OrdinalIgnoreCase));
                    Assert.NotNull(outcome.Ownership);
                    Assert.False(outcome.Ownership!.CanRecover);
                    Assert.False(outcome.Transition.Accepted);
                    Assert.False(outcome.Transition.Changed);
                    Assert.Equal("none", outcome.Transition.Action);
                    Assert.True(
                        StringComparer.Ordinal.Equals(
                            outcome.Transition.Reason,
                            "ownership-not-resolved") ||
                        StringComparer.Ordinal.Equals(
                            outcome.Transition.Reason,
                            "ownership-not-recoverable"));
                });

            Assert.Equal(
                cluster.RuntimeCountPerHost,
                outcomes.Count(outcome => outcome.Transition.Accepted));
            Assert.Equal(
                cluster.RuntimeCountPerHost,
                outcomes.Count(outcome => outcome.Transition.Changed));
            Assert.Equal(
                supersededFailedOutcomes.Length,
                outcomes.Count(outcome => !outcome.Transition.Accepted));

            var replacementRuntimeInstanceIds =
                replacement.ReplacementHost.RuntimeInstanceIds
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(cluster.RuntimeCountPerHost, replacementRuntimeInstanceIds.Count);
            Assert.Empty(
                failedRuntimeInstanceIds.Intersect(
                    replacementRuntimeInstanceIds,
                    StringComparer.Ordinal));

            this.output.WriteLine(
                $"[{this.logPrefix} PARENT HOST RECOVERY] Cycle='{cycleNumber}', FailureId='{failureId}', FailedParentProcessId='{replacement.FailedHost.ProcessId}', ReplacementParentProcessId='{replacement.ReplacementHost.ProcessId}', FailedHostId='{replacement.FailedHost.HostId}', ReplacementHostId='{replacement.ReplacementHost.HostId}', FailedRuntimeCount='{failedRuntimeInstanceIds.Count}', ReplacementRuntimeCount='{replacementRuntimeInstanceIds.Count}', CandidateCount='{outcomes.Count}', AcceptedCount='{currentFailureOutcomes.Length}', RejectedCount='{supersededFailedOutcomes.Length}', RecoveredSharedRunCount='{impactedSharedRunIds.Count}'.");

            return new ProcessHostPoolProductionRecoveryProof(
                failureId,
                replacement.FailedHost,
                replacement.ReplacementHost,
                failedRuntimeInstanceIds,
                replacementRuntimeInstanceIds,
                target.SurvivingHostIds,
                target.SurvivingParentProcessIds,
                target.SurvivingRuntimeInstanceIds,
                impactedSharedRunIds,
                impactedExecutionIds,
                impactedLocalRunIds,
                target.ActiveRuns
                    .Select(
                        run => string.Join(
                            ":",
                            "runtime-recovery",
                            run.ExecutionId,
                            run.SharedRunId,
                            run.LocalRunId))
                    .ToHashSet(StringComparer.Ordinal),
                supersededFailedOutcomes.Length);
        }

        private static string CalculateMembershipFingerprint(
            string poolId,
            string hostId,
            IReadOnlySet<string> runtimeInstanceIds)
        {
            return CalculateSha256(
                string.Join(
                    "\n",
                    new[] { poolId, hostId }
                        .Concat(
                            runtimeInstanceIds.OrderBy(
                                value => value,
                                StringComparer.Ordinal))));
        }

        private static string CalculateInventoryFingerprint(
            IReadOnlyList<AiRuntimePoolAssignedWorkCandidate> candidates)
        {
            return CalculateSha256(
                string.Join(
                    "\n",
                    candidates
                        .OrderBy(candidate => candidate.Kind)
                        .ThenBy(candidate => candidate.CreatedAtUtc)
                        .ThenBy(
                            candidate => candidate.RuntimeInstanceId,
                            StringComparer.Ordinal)
                        .ThenBy(
                            candidate => candidate.LocalRunId,
                            StringComparer.Ordinal)
                        .Select(
                            candidate => string.Join(
                                "|",
                                candidate.FailureId,
                                candidate.PoolId,
                                candidate.HostId,
                                candidate.RuntimeInstanceId,
                                candidate.RouteId ?? string.Empty,
                                candidate.LocalRunId,
                                candidate.ExecutionId ?? string.Empty,
                                candidate.SharedRunId ?? string.Empty,
                                candidate.Status ?? string.Empty,
                                candidate.Kind,
                                candidate.CreatedAtUtc.ToString("O")))));
        }

        private static string CalculateSha256(string value)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    internal sealed record ProcessHostPoolProductionFailureTarget(
        ProcessHostPoolProductionHostProcess Host,
        IReadOnlyList<AiRuntimeInstanceSnapshot> Members,
        IReadOnlyList<ProcessHostPoolProductionActiveRun> ActiveRuns,
        IReadOnlySet<string> SurvivingHostIds,
        IReadOnlySet<int> SurvivingParentProcessIds,
        IReadOnlySet<string> SurvivingRuntimeInstanceIds);

    internal sealed record ProcessHostPoolProductionActiveRun(
        string RuntimeInstanceId,
        string SharedRunId,
        string LocalRunId,
        string ExecutionId,
        string? Status);

    internal sealed record ProcessHostPoolProductionRecoveryProof(
        string FailureId,
        ProcessHostPoolProductionHostProcess FailedHost,
        ProcessHostPoolProductionHostProcess ReplacementHost,
        IReadOnlySet<string> FailedRuntimeInstanceIds,
        IReadOnlySet<string> ReplacementRuntimeInstanceIds,
        IReadOnlySet<string> SurvivingHostIds,
        IReadOnlySet<int> SurvivingParentProcessIds,
        IReadOnlySet<string> SurvivingRuntimeInstanceIds,
        IReadOnlySet<string> RecoveredSharedRunIds,
        IReadOnlySet<string> RecoveredExecutionIds,
        IReadOnlySet<string> RecoveredLocalRunIds,
        IReadOnlySet<string> RecoveryForensicsIds,
        int SupersededFailedCandidateCount);
}
