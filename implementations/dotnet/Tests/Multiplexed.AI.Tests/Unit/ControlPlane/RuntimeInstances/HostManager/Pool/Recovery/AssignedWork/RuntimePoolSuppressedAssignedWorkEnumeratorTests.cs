using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Validates the shared exact-suppression assigned-work enumeration core.
    /// </summary>
    public sealed class RuntimePoolSuppressedAssignedWorkEnumeratorTests
    {
        /// <summary>
        /// Verifies that existing durable index semantics are reused without mutation.
        /// </summary>
        [Fact]
        public async Task EnumerateAsync_Should_Project_Only_Exact_Suppressed_Runtime_Work()
        {
            var suppression = CreateSuppression();
            var safety =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await safety.SuppressAsync(suppression);

            var index =
                new FakeRuntimeRunExecutionIndex(
                    new[]
                    {
                        CreateEntry(
                            "local-flight",
                            "runtime-a1",
                            "execution-a1",
                            "running",
                            new DateTimeOffset(
                                2026,
                                7,
                                28,
                                0,
                                0,
                                2,
                                TimeSpan.Zero)),
                        CreateEntry(
                            "local-queued",
                            "runtime-a1",
                            null,
                            "queued",
                            new DateTimeOffset(
                                2026,
                                7,
                                28,
                                0,
                                0,
                                1,
                                TimeSpan.Zero)),
                        CreateEntry(
                            "local-sibling",
                            "runtime-a2",
                            "execution-a2",
                            "running",
                            DateTimeOffset.UtcNow)
                    });

            var enumerator =
                new AiRuntimePoolSuppressedAssignedWorkEnumerator(
                    safety,
                    index);

            var inventory =
                await enumerator.EnumerateAsync(suppression);

            Assert.Equal(
                "runtime-a1",
                index.LastRequestedRuntimeInstanceId);
            Assert.Equal(
                new[]
                {
                    "local-flight",
                    "local-queued"
                },
                inventory.Candidates
                    .Select(candidate => candidate.LocalRunId)
                    .ToArray());
            Assert.Equal(
                new[]
                {
                    AiRuntimePoolAssignedWorkKind.InFlight,
                    AiRuntimePoolAssignedWorkKind.LocalQueued
                },
                inventory.Candidates
                    .Select(candidate => candidate.Kind)
                    .ToArray());
            Assert.All(
                inventory.Candidates,
                candidate =>
                    Assert.Equal(
                        suppression.FailureId,
                        candidate.FailureId));
            Assert.Equal(0, index.MutationCallCount);
        }

        /// <summary>
        /// Verifies that a non-authoritative route incarnation is rejected before index access.
        /// </summary>
        [Fact]
        public async Task EnumerateAsync_Should_Reject_NonAuthoritative_Route()
        {
            var suppression = CreateSuppression();
            var safety =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            await safety.SuppressAsync(suppression);

            var index =
                new FakeRuntimeRunExecutionIndex(
                    Array.Empty<AiRuntimeRunExecutionIndexEntry>());

            var enumerator =
                new AiRuntimePoolSuppressedAssignedWorkEnumerator(
                    safety,
                    index);

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolAssignedWorkAuthorityException>(
                    () =>
                        enumerator.EnumerateAsync(
                            suppression with
                            {
                                RouteId = "route-forged"
                            }));

            Assert.Equal(
                AiRuntimePoolAssignedWorkAuthorityFailure.RouteMismatch,
                exception.Reason);
            Assert.Null(index.LastRequestedRuntimeInstanceId);
            Assert.Equal(0, index.MutationCallCount);
        }

        private static AiRuntimePoolCapacitySuppression CreateSuppression()
        {
            return new AiRuntimePoolCapacitySuppression
            {
                FailureId = "failure-pod-01",
                PoolId = "pool-01",
                HostId = "pod-uid-01",
                RuntimeInstanceId = "runtime-a1",
                RouteId = "route-a1",
                SuppressedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        28,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)
            };
        }

        private static AiRuntimeRunExecutionIndexEntry CreateEntry(
            string runId,
            string runtimeInstanceId,
            string? executionId,
            string status,
            DateTimeOffset createdAtUtc)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runId,
                ExecutionId = executionId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = status,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey = "context-01",
                        Project = "runtime-pool",
                        UserId = "user-01",
                        TenantId = "tenant-01",
                        TenantGroupId = "tenant-group-01",
                        CurrentNamespace = "default",
                        Namespaces = new List<NamespaceEntry>(),
                        TtlSeconds = 300
                    },
                CreatedAtUtc = createdAtUtc,
                Metadata =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["sharedRunId"] = "shared-" + runId
                    }
            };
        }

        private sealed class FakeRuntimeRunExecutionIndex :
            RuntimeRunExecutionIndexTestFixture
        {
            private readonly IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry> entries;

            public FakeRuntimeRunExecutionIndex(
                IReadOnlyList<AiRuntimeRunExecutionIndexEntry> entries)
            {
                this.entries = entries;
            }

            public string? LastRequestedRuntimeInstanceId
            {
                get;
                private set;
            }

            public int MutationCallCount { get; private set; }

            public override Task RegisterQueuedAsync(
                AiRuntimeRunExecutionIndexEntry entry,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            public override Task MarkStartedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            public override Task MarkCompletedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            public override Task MarkFailedAsync(
                string runId,
                string? executionId,
                string failureReason,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            public override Task MarkCancelledAsync(
                string runId,
                string? executionId,
                string? reason,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            public override Task<bool> MarkRequeuedForRecoveryAsync(
                string runId,
                string executionId,
                string reason,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.FromResult(true);
            }

            public override Task<AiRuntimeRunExecutionIndexEntry?> GetAsync(
                string runId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    this.entries.SingleOrDefault(
                        entry =>
                            StringComparer.Ordinal.Equals(
                                entry.RunId,
                                runId)));
            }

            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
                ListUnfinishedByRuntimeInstanceAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<
                    IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(
                    this.entries
                        .Where(
                            entry =>
                                StringComparer.Ordinal.Equals(
                                    entry.RuntimeInstanceId,
                                    runtimeInstanceId))
                        .ToArray());
            }

            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
                ListUnfinishedAsync(
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.entries);
            }

            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
                ListRecoverableByRuntimeInstanceAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                this.LastRequestedRuntimeInstanceId = runtimeInstanceId;

                return Task.FromResult<
                    IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>(
                    this.entries
                        .Where(
                            entry =>
                                StringComparer.Ordinal.Equals(
                                    entry.RuntimeInstanceId,
                                    runtimeInstanceId))
                        .ToArray());
            }

            public override Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>>
                ListRecoverableAsync(
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.entries);
            }
        }
    }
}
