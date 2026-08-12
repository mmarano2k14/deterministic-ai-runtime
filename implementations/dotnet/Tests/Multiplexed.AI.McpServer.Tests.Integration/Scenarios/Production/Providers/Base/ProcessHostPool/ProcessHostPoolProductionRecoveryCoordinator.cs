using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Claims;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.Execution;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Helpers;
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
        private readonly IAiRuntimePoolCapacitySafetyWriter safetyWriter;
        private readonly IAiRuntimePoolCapacitySafetyReader safetyReader;
        private readonly IAiRuntimePoolSuppressedAssignedWorkEnumerator
            assignedWorkEnumerator;
        private readonly IAiRuntimePoolRecoveryMembershipClaimStore claimStore;
        private readonly IAiRuntimePoolRecoveryClaimCoordinator
            runtimeClaimCoordinator;
        private readonly IAiRuntimePoolClaimedRecoveryExecutor
            runtimeClaimedRecoveryExecutor;
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
            this.safetyWriter =
                services.GetRequiredService<IAiRuntimePoolCapacitySafetyWriter>();
            this.safetyReader =
                services.GetRequiredService<IAiRuntimePoolCapacitySafetyReader>();
            this.assignedWorkEnumerator =
                services.GetRequiredService<IAiRuntimePoolSuppressedAssignedWorkEnumerator>();
            this.claimStore =
                services.GetRequiredService<IAiRuntimePoolRecoveryMembershipClaimStore>();
            this.runtimeClaimCoordinator =
                services.GetRequiredService<IAiRuntimePoolRecoveryClaimCoordinator>();
            this.runtimeClaimedRecoveryExecutor =
                services.GetRequiredService<IAiRuntimePoolClaimedRecoveryExecutor>();
            this.transitionExecutor =
                services.GetRequiredService<IAiRuntimePoolRecoveryCandidateTransitionExecutor>();
        }

        /// <summary>
        /// Reads the exact child failure from the shared durable failure journal, projects that
        /// immutable authority into control-plane capacity safety, claims its assigned work once,
        /// and executes the existing transition.
        /// </summary>
        public async Task RecoverChildRuntimeAsync(
            ProcessHostPoolProductionCluster cluster,
            ProcessHostPoolProductionHostProcess host,
            string failedRuntimeInstanceId,
            string expectedSharedRunId,
            string expectedLocalRunId,
            string expectedExecutionId,
            int cycleNumber,
            string claimedBy,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(cluster);
            ArgumentNullException.ThrowIfNull(host);
            ArgumentException.ThrowIfNullOrWhiteSpace(failedRuntimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedSharedRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedLocalRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutionId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            host.AssertRunning();
            Assert.Equal(cluster.PoolId, host.PoolId);

            AiRuntimePoolFailureObservation parentFailure;

            try
            {
                parentFailure =
                    await this.WaitForSharedRuntimeFailureAsync(
                            cluster.PoolId,
                            host.HostId,
                            failedRuntimeInstanceId,
                            timeout)
                        .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    string.Concat(
                        exception.Message,
                        Environment.NewLine,
                        "Parent ProcessHost diagnostics:",
                        Environment.NewLine,
                        host.BuildDiagnostics()),
                    exception);
            }

            Assert.Equal(
                AiRuntimePoolFailureScope.RuntimeInstance,
                parentFailure.Scope);
            Assert.Equal(
                AiRuntimePoolFailureKind.UnexpectedProcessExit,
                parentFailure.Kind);
            Assert.Equal(cluster.PoolId, parentFailure.PoolId);
            Assert.Equal(host.HostId, parentFailure.HostId);
            Assert.Equal(
                failedRuntimeInstanceId,
                parentFailure.RuntimeInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(parentFailure.RouteId));

            var storedFailure =
                await this.failureReader
                    .GetByFailureIdAsync(parentFailure.FailureId)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.NotNull(storedFailure);
            Assert.Equal(parentFailure, storedFailure);

            /*
             * Failure persistence is shared, but capacity suppression is deliberately local to
             * each control-plane composition. Project the exact persisted failure into this
             * control plane without recording or manufacturing a second failure observation.
             */
            var suppression =
                await this.safetyWriter
                    .SuppressAsync(
                        new AiRuntimePoolCapacitySuppression
                        {
                            FailureId = parentFailure.FailureId,
                            Scope =
                                AiRuntimePoolCapacitySuppressionScope
                                    .RuntimeInstanceRoute,
                            PoolId = parentFailure.PoolId,
                            HostId = parentFailure.HostId,
                            RuntimeInstanceId = failedRuntimeInstanceId,
                            RouteId = parentFailure.RouteId,
                            SuppressedAtUtc = parentFailure.ObservedAtUtc
                        })
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(parentFailure.FailureId, suppression.FailureId);
            Assert.Equal(
                AiRuntimePoolCapacitySuppressionScope.RuntimeInstanceRoute,
                suppression.Scope);
            Assert.Equal(parentFailure.RouteId, suppression.RouteId);

            var claimedWork =
                await this.runtimeClaimCoordinator
                    .TryAcquireAsync(
                        parentFailure.FailureId,
                        claimedBy.Trim())
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            Assert.Equal(
                AiRuntimePoolRecoveryClaimAcquisitionStatus.Acquired,
                claimedWork.Status);
            Assert.NotNull(claimedWork.Lease);

            var inventory = claimedWork.Inventory;
            var candidates =
                inventory.Candidates
                    .OrderBy(candidate => candidate.Kind)
                    .ThenBy(candidate => candidate.CreatedAtUtc)
                    .ThenBy(
                        candidate => candidate.LocalRunId,
                        StringComparer.Ordinal)
                    .ToArray();

            Assert.Equal(parentFailure.FailureId, inventory.FailureId);
            Assert.Equal(cluster.PoolId, inventory.PoolId);
            Assert.Equal(host.HostId, inventory.HostId);
            Assert.Equal(failedRuntimeInstanceId, inventory.RuntimeInstanceId);
            Assert.Equal(parentFailure.RouteId, inventory.RouteId);

            var expectedCandidate =
                Assert.Single(
                    candidates.Where(
                        candidate =>
                            StringComparer.Ordinal.Equals(
                                candidate.LocalRunId,
                                expectedLocalRunId)));

            Assert.Equal(
                AiRuntimePoolAssignedWorkKind.InFlight,
                expectedCandidate.Kind);
            Assert.Equal(expectedSharedRunId, expectedCandidate.SharedRunId);
            Assert.Equal(expectedExecutionId, expectedCandidate.ExecutionId);

            await using var lease = claimedWork.Lease!;

            var execution =
                await this.runtimeClaimedRecoveryExecutor
                    .ExecuteAsync(claimedWork)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);
            var outcomes = execution.Outcomes;

            Assert.Equal(parentFailure.FailureId, execution.FailureId);
            Assert.Equal(failedRuntimeInstanceId, execution.RuntimeInstanceId);
            Assert.Equal(candidates.Length, execution.CandidateCount);

            var expectedOutcome =
                Assert.Single(
                    outcomes.Where(
                        outcome =>
                            StringComparer.Ordinal.Equals(
                                outcome.Candidate.LocalRunId,
                                expectedLocalRunId)));

            Assert.True(
                expectedOutcome.Transition.Accepted,
                expectedOutcome.Transition.Reason);
            Assert.True(
                expectedOutcome.Transition.Changed,
                expectedOutcome.Transition.Reason);
            Assert.Equal(expectedSharedRunId, expectedOutcome.Transition.SharedRunId);
            Assert.Equal(expectedExecutionId, expectedOutcome.Transition.ExecutionId);
            Assert.Equal(1, execution.AcceptedCount);
            Assert.Equal(1, execution.ChangedCount);
            Assert.All(
                outcomes.Where(
                    outcome =>
                        !StringComparer.Ordinal.Equals(
                            outcome.Candidate.LocalRunId,
                            expectedLocalRunId)),
                outcome =>
                {
                    Assert.False(outcome.Transition.Accepted);
                    Assert.False(outcome.Transition.Changed);
                });

            this.output.WriteLine(
                $"[{this.logPrefix} CHILD RUNTIME RECOVERY] Cycle='{cycleNumber}', FailureId='{parentFailure.FailureId}', ParentProcessId='{host.ProcessId}', HostId='{host.HostId}', FailedRuntimeInstanceId='{failedRuntimeInstanceId}', FailedRouteId='{parentFailure.RouteId}', ClaimId='{claimedWork.Claim.ClaimId}', CandidateCount='{execution.CandidateCount}', AcceptedCount='{execution.AcceptedCount}', RejectedCount='{execution.RejectedCount}', RecoveredSharedRunId='{expectedSharedRunId}', RecoveredExecutionId='{expectedExecutionId}', Authority='shared-mongo-failure-journal'.");
        }

        private async Task<AiRuntimePoolFailureObservation>
            WaitForSharedRuntimeFailureAsync(
                string poolId,
                string hostId,
                string runtimeInstanceId,
                TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            IReadOnlyList<AiRuntimePoolFailureObservation> lastObservations =
                Array.Empty<AiRuntimePoolFailureObservation>();

            while (DateTimeOffset.UtcNow < deadline)
            {
                lastObservations =
                    await this.failureReader
                        .ListByRuntimeInstanceIdAsync(runtimeInstanceId)
                        .ConfigureAwait(false);

                var exact =
                    lastObservations
                        .Where(
                            failure =>
                                failure.Scope ==
                                    AiRuntimePoolFailureScope.RuntimeInstance &&
                                StringComparer.Ordinal.Equals(
                                    failure.PoolId,
                                    poolId) &&
                                StringComparer.Ordinal.Equals(
                                    failure.HostId,
                                    hostId) &&
                                StringComparer.Ordinal.Equals(
                                    failure.RuntimeInstanceId,
                                    runtimeInstanceId))
                        .OrderBy(failure => failure.ObservedAtUtc)
                        .ThenBy(
                            failure => failure.FailureId,
                            StringComparer.Ordinal)
                        .ToArray();

                if (exact.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Shared failure journal exposed multiple immutable failures for exact runtime '{runtimeInstanceId}'. FailureIds='{string.Join(",", exact.Select(failure => failure.FailureId))}'.");
                }

                if (exact.Length == 1)
                {
                    this.output.WriteLine(
                        $"[{this.logPrefix} SHARED CHILD FAILURE AUTHORITY] FailureId='{exact[0].FailureId}', PoolId='{poolId}', HostId='{hostId}', RuntimeInstanceId='{runtimeInstanceId}', RouteId='{exact[0].RouteId}', Journal='shared-durable'.");

                    return exact[0];
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Shared Runtime Pool failure journal did not expose an exact child failure within '{timeout}'. PoolId='{poolId}', HostId='{hostId}', RuntimeInstanceId='{runtimeInstanceId}', ObservedFailureIds='{string.Join(",", lastObservations.Select(failure => failure.FailureId))}'.");
        }

        public async Task<ProcessHostPoolProductionRecoveryProof> RecoverAsync(
            ProcessHostPoolProductionCluster cluster,
            ProcessHostPoolProductionFailureTarget target,
            int cycleNumber,
            string claimedBy,
            TimeSpan timeout,
            ProductionCrashCheckpointGate? boundaryFailureCrashGate = null)
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

            try
            {
                await cluster
                    .CrashHostAsync(target.Host.HostId, timeout)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (boundaryFailureCrashGate is not null)
                {
                    // The deferred boundary wave is intentionally frozen only
                    // until the exact parent boundary has terminated. Release
                    // surviving siblings immediately after the kill; recovered
                    // executions observe the durable released state as well.
                    await boundaryFailureCrashGate
                        .ReleaseAsync()
                        .WaitAsync(timeout)
                        .ConfigureAwait(false);
                }
            }

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

            var hostSuppressionHistory =
                await this.safetyReader
                    .ListByHostIdAsync(target.Host.HostId)
                    .WaitAsync(timeout)
                    .ConfigureAwait(false);

            // Capacity safety is a durable history. A host can already contain a
            // runtime-scoped suppression from an earlier child crash, so a host-level
            // recovery must prove and enumerate only the suppressions created by the
            // current failure authority rather than treating the whole host history
            // as current failed membership.
            var persistedSuppressions =
                hostSuppressionHistory
                    .Where(
                        item =>
                            string.Equals(
                                item.FailureId,
                                failureId,
                                StringComparison.Ordinal) &&
                            item.Scope ==
                                AiRuntimePoolCapacitySuppressionScope.HostMembership)
                    .OrderBy(
                        item => item.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ToArray();

            Assert.Equal(cluster.RuntimeCountPerHost, persistedSuppressions.Length);
            Assert.True(
                failedRuntimeInstanceIds.SetEquals(
                    persistedSuppressions.Select(item => item.RuntimeInstanceId)),
                "The current failure's durable host-membership suppressions do not match the exact failed runtime membership.");
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
                    persistedSuppressions.Length);

            foreach (var suppression in persistedSuppressions)
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
