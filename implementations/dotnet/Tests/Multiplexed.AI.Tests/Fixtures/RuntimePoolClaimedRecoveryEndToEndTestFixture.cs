using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery.Transition;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Ownership;
using Multiplexed.Abstractions.Core.ExecutionContext;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Provides centralized strongly typed assigned-work and transition services for the final
    /// real process-host claimed-recovery proof.
    /// </summary>
    public sealed class RuntimePoolClaimedRecoveryEndToEndTestFixture
    {
        /// <summary>
        /// Registers fixture-owned deterministic recovery boundaries after the normal control-plane
        /// composition and before the opt-in Runtime Pool composition.
        /// </summary>
        /// <param name="services">The test host service collection.</param>
        public void RegisterServices(
            IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);

            services.AddSingleton<
                RuntimePoolClaimedRecoveryEndToEndState>();

            services.AddSingleton<
                IAiRuntimeRunExecutionIndex>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        RuntimePoolClaimedRecoveryEndToEndState>());

            services.AddSingleton<
                IAiSharedRunOwnershipResolver>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        RuntimePoolClaimedRecoveryEndToEndState>());

            services.AddSingleton<
                IAiRuntimeExecutionRecoveryTransitionService>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        RuntimePoolClaimedRecoveryEndToEndState>());
        }

        /// <summary>
        /// Seeds three exact A1 recovery candidates and sibling A2/A3 controls.
        /// </summary>
        public void SeedAssignedWork(
            RuntimePoolClaimedRecoveryEndToEndState state,
            string runtimeA1,
            string runtimeA2,
            string runtimeA3)
        {
            ArgumentNullException.ThrowIfNull(state);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeA1);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeA2);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeA3);

            state.Seed(
                new[]
                {
                    CreateEntry(
                        localRunId: "local-a1-flight",
                        runtimeInstanceId: runtimeA1,
                        executionId: "execution-a1",
                        status: "running",
                        tenantId: "tenant-a1",
                        sharedRunId: "shared-a1-flight",
                        createdSecond: 1),
                    CreateEntry(
                        localRunId: "local-a1-queued-01",
                        runtimeInstanceId: runtimeA1,
                        executionId: null,
                        status: "queued",
                        tenantId: "tenant-a1",
                        sharedRunId: "shared-a1-queued-01",
                        createdSecond: 2),
                    CreateEntry(
                        localRunId: "local-a1-queued-02",
                        runtimeInstanceId: runtimeA1,
                        executionId: null,
                        status: "queued",
                        tenantId: "tenant-a1",
                        sharedRunId: "shared-a1-queued-02",
                        createdSecond: 3),
                    CreateEntry(
                        localRunId: "local-a2-flight-control",
                        runtimeInstanceId: runtimeA2,
                        executionId: "execution-a2-control",
                        status: "running",
                        tenantId: "tenant-a2",
                        sharedRunId: "shared-a2-control",
                        createdSecond: 4),
                    CreateEntry(
                        localRunId: "local-a3-queued-control",
                        runtimeInstanceId: runtimeA3,
                        executionId: null,
                        status: "queued",
                        tenantId: "tenant-a3",
                        sharedRunId: "shared-a3-control",
                        createdSecond: 5)
                });
        }

        /// <summary>
        /// Creates one durable runtime-run index entry.
        /// </summary>
        private static AiRuntimeRunExecutionIndexEntry CreateEntry(
            string localRunId,
            string runtimeInstanceId,
            string? executionId,
            string status,
            string tenantId,
            string sharedRunId,
            int createdSecond)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = localRunId,
                RuntimeInstanceId = runtimeInstanceId,
                ExecutionId = executionId,
                Status = status,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "context-",
                                localRunId),
                        Project = "runtime-pool-claimed-recovery-e2e",
                        UserId = "system",
                        TenantId = tenantId,
                        TenantGroupId =
                            "runtime-pool-recovery-group",
                        CurrentNamespace = "default",
                        Namespaces =
                            new List<NamespaceEntry>(),
                        TtlSeconds = 300
                    },
                CreatedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        27,
                        0,
                        0,
                        createdSecond,
                        TimeSpan.Zero),
                Metadata =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["sharedRunId"] = sharedRunId
                    }
            };
        }
    }

    /// <summary>
    /// Implements the existing durable index, ownership resolver, and transition boundary for the
    /// final infrastructure proof while recording every exact request.
    /// </summary>
    public sealed class RuntimePoolClaimedRecoveryEndToEndState :
        IAiRuntimeRunExecutionIndex,
        IAiSharedRunOwnershipResolver,
        IAiRuntimeExecutionRecoveryTransitionService
    {
        private readonly object syncRoot = new();

        private IReadOnlyList<AiRuntimeRunExecutionIndexEntry> entries =
            Array.Empty<AiRuntimeRunExecutionIndexEntry>();

        private readonly List<AiSharedRunOwnershipResolutionRequest>
            ownershipRequests = new();

        private readonly List<AiRuntimeExecutionRecoveryTransitionRequest>
            transitionRequests = new();

        /// <summary>
        /// Gets a stable snapshot of ownership requests.
        /// </summary>
        public IReadOnlyList<AiSharedRunOwnershipResolutionRequest>
            OwnershipRequests
        {
            get
            {
                lock (this.syncRoot)
                {
                    return this.ownershipRequests.ToArray();
                }
            }
        }

        /// <summary>
        /// Gets a stable snapshot of transition requests.
        /// </summary>
        public IReadOnlyList<AiRuntimeExecutionRecoveryTransitionRequest>
            TransitionRequests
        {
            get
            {
                lock (this.syncRoot)
                {
                    return this.transitionRequests.ToArray();
                }
            }
        }

        /// <summary>
        /// Replaces the deterministic durable test inventory.
        /// </summary>
        public void Seed(
            IReadOnlyList<AiRuntimeRunExecutionIndexEntry> seededEntries)
        {
            ArgumentNullException.ThrowIfNull(seededEntries);

            lock (this.syncRoot)
            {
                this.entries = seededEntries.ToArray();
                this.ownershipRequests.Clear();
                this.transitionRequests.Clear();
            }
        }

        /// <inheritdoc />
        public Task RegisterQueuedAsync(
            AiRuntimeRunExecutionIndexEntry entry,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The final Runtime Pool recovery proof seeds its read-only inventory explicitly.");
        }

        /// <inheritdoc />
        public Task MarkStartedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The claimed-recovery executor delegates mutations to the transition service.");
        }

        /// <inheritdoc />
        public Task MarkCompletedAsync(
            string runId,
            string executionId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The claimed-recovery executor delegates mutations to the transition service.");
        }

        /// <inheritdoc />
        public Task MarkFailedAsync(
            string runId,
            string? executionId,
            string failureReason,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The claimed-recovery executor delegates mutations to the transition service.");
        }

        /// <inheritdoc />
        public Task MarkCancelledAsync(
            string runId,
            string? executionId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The claimed-recovery executor delegates mutations to the transition service.");
        }

        /// <inheritdoc />
        public Task<bool> MarkRequeuedForRecoveryAsync(
            string runId,
            string executionId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(
                "The claimed-recovery executor delegates mutations to the transition service.");
        }

        /// <inheritdoc />
        public Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(
                    this.entries.SingleOrDefault(
                        entry =>
                            StringComparer.Ordinal.Equals(
                                entry.RunId,
                                runId)));
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListUnfinishedByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
        {
            return this.ListByRuntimeInstanceAsync(
                runtimeInstanceId,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListUnfinishedAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.entries);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListRecoverableByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
        {
            return this.ListByRuntimeInstanceAsync(
                runtimeInstanceId,
                cancellationToken);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListRecoverableAsync(
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                return Task.FromResult(this.entries);
            }
        }

        /// <inheritdoc />
        public Task<AiSharedRunOwnershipResolutionResult> ResolveAsync(
            AiSharedRunOwnershipResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.ownershipRequests.Add(request);
            }

            return Task.FromResult(
                new AiSharedRunOwnershipResolutionResult
                {
                    Resolved = true,
                    CanRecover = true,
                    SharedRunId = request.SharedRunId,
                    RuntimeInstanceId =
                        request.RuntimeInstanceId,
                    LocalRunId = request.LocalRunId,
                    ExecutionId = request.ExecutionId,
                    TenantId = request.TenantId,
                    TenantGroupId =
                        request.TenantGroupId,
                    ClaimToken =
                        string.Concat(
                            "fixture-claim-",
                            request.LocalRunId),
                    Reason =
                        "runtime-pool-final-e2e-resolved"
                });
        }

        /// <inheritdoc />
        public Task<AiRuntimeExecutionRecoveryTransitionResult>
            ApplyAsync(
                AiRuntimeExecutionRecoveryTransitionRequest request,
                CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                this.transitionRequests.Add(request);
            }

            return Task.FromResult(
                new AiRuntimeExecutionRecoveryTransitionResult
                {
                    Accepted = true,
                    Changed = true,
                    SharedRunId =
                        request.Ownership.SharedRunId,
                    RuntimeInstanceId =
                        request.Ownership.RuntimeInstanceId,
                    LocalRunId =
                        request.Ownership.LocalRunId,
                    ExecutionId =
                        request.Ownership.ExecutionId,
                    Action =
                        string.IsNullOrWhiteSpace(
                            request.Ownership.ExecutionId)
                            ? "redispatch-local-queued"
                            : "resume-existing-execution",
                    Reason =
                        request.Reason ??
                        "runtime-pool-final-e2e"
                });
        }

        /// <summary>
        /// Lists exact entries for one runtime without widening the boundary.
        /// </summary>
        private Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
            ListByRuntimeInstanceAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (this.syncRoot)
            {
                IReadOnlyList<AiRuntimeRunExecutionIndexEntry> result =
                    this.entries
                        .Where(
                            entry =>
                                StringComparer.Ordinal.Equals(
                                    entry.RuntimeInstanceId,
                                    runtimeInstanceId))
                        .ToArray();

                return Task.FromResult(result);
            }
        }
    }
}
