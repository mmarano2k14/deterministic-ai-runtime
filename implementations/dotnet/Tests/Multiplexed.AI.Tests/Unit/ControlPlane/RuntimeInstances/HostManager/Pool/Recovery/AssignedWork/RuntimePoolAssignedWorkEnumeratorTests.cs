using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Failure;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork;
using Multiplexed.AI.Tests.Fixtures;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Recovery.AssignedWork
{
    /// <summary>
    /// Validates exact read-only work enumeration for one failed runtime instance.
    /// </summary>
    public sealed class RuntimePoolAssignedWorkEnumeratorTests
    {
        /// <summary>
        /// Verifies that only A1 work is returned in deterministic recovery order.
        /// </summary>
        [Fact]
        public async Task EnumerateAsync_Should_Return_Only_A1_Work_In_Recovery_Order()
        {
            var authority =
                await CreateAuthorityAsync();

            var index =
                new FakeRuntimeRunExecutionIndex(
                    new[]
                    {
                        CreateEntry(
                            runId: "local-a1-other",
                            runtimeInstanceId: "runtime-a1",
                            executionId: null,
                            status: "failed",
                            createdAtUtc:
                                new DateTimeOffset(
                                    2026,
                                    7,
                                    26,
                                    0,
                                    0,
                                    1,
                                    TimeSpan.Zero)),
                        CreateEntry(
                            runId: "local-a1-queued",
                            runtimeInstanceId: "runtime-a1",
                            executionId: null,
                            status: "queued",
                            createdAtUtc:
                                new DateTimeOffset(
                                    2026,
                                    7,
                                    26,
                                    0,
                                    0,
                                    2,
                                    TimeSpan.Zero)),
                        CreateEntry(
                            runId: "local-a1-flight",
                            runtimeInstanceId: "runtime-a1",
                            executionId: "execution-a1",
                            status: "running",
                            createdAtUtc:
                                new DateTimeOffset(
                                    2026,
                                    7,
                                    26,
                                    0,
                                    0,
                                    3,
                                    TimeSpan.Zero)),
                        CreateEntry(
                            runId: "local-a2-flight",
                            runtimeInstanceId: "runtime-a2",
                            executionId: "execution-a2",
                            status: "running",
                            createdAtUtc:
                                new DateTimeOffset(
                                    2026,
                                    7,
                                    26,
                                    0,
                                    0,
                                    4,
                                    TimeSpan.Zero))
                    });

            var enumerator =
                new AiRuntimePoolAssignedWorkEnumerator(
                    authority.Journal,
                    authority.Safety,
                    index);

            var inventory =
                await enumerator.EnumerateAsync(
                    authority.Failure.FailureId);

            Assert.Equal(
                "runtime-a1",
                index.LastRequestedRuntimeInstanceId);

            Assert.Equal(
                new[]
                {
                    AiRuntimePoolAssignedWorkKind.InFlight,
                    AiRuntimePoolAssignedWorkKind.LocalQueued,
                    AiRuntimePoolAssignedWorkKind.OtherRecoverable
                },
                inventory.Candidates
                    .Select(candidate => candidate.Kind)
                    .ToArray());

            Assert.Equal(
                new[]
                {
                    "local-a1-flight",
                    "local-a1-queued",
                    "local-a1-other"
                },
                inventory.Candidates
                    .Select(candidate => candidate.LocalRunId)
                    .ToArray());

            Assert.All(
                inventory.Candidates,
                candidate =>
                {
                    Assert.Equal(
                        authority.Failure.FailureId,
                        candidate.FailureId);

                    Assert.Equal(
                        "runtime-a1",
                        candidate.RuntimeInstanceId);

                    Assert.Equal(
                        "tenant-01",
                        candidate.TenantId);

                    Assert.Equal(
                        "tenant-group-01",
                        candidate.TenantGroupId);

                    Assert.Equal(
                        "shared-run-01",
                        candidate.SharedRunId);
                });

            Assert.DoesNotContain(
                inventory.Candidates,
                candidate =>
                    StringComparer.Ordinal.Equals(
                        candidate.RuntimeInstanceId,
                        "runtime-a2"));

            Assert.Equal(
                0,
                index.MutationCallCount);
        }

        /// <summary>
        /// Verifies that enumeration is rejected until exact capacity suppression exists.
        /// </summary>
        [Fact]
        public async Task EnumerateAsync_Should_Reject_Missing_Suppression()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var failure =
                CreateFailure();

            await journal.RecordAsync(failure);

            var enumerator =
                new AiRuntimePoolAssignedWorkEnumerator(
                    journal,
                    new InMemoryAiRuntimePoolCapacitySafetyRegistry(),
                    new FakeRuntimeRunExecutionIndex(
                        Array.Empty<AiRuntimeRunExecutionIndexEntry>()));

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolAssignedWorkAuthorityException>(
                    () =>
                        enumerator.EnumerateAsync(
                            failure.FailureId));

            Assert.Equal(
                AiRuntimePoolAssignedWorkAuthorityFailure
                    .SuppressionMissing,
                exception.Reason);
        }

        /// <summary>
        /// Verifies that a sibling runtime entry from the durable index is rejected.
        /// </summary>
        [Fact]
        public async Task EnumerateAsync_Should_Reject_Runtime_Boundary_Violation()
        {
            var authority =
                await CreateAuthorityAsync();

            var index =
                new FakeRuntimeRunExecutionIndex(
                    Array.Empty<AiRuntimeRunExecutionIndexEntry>())
                {
                    ForcedRecoverableResult =
                        new[]
                        {
                            CreateEntry(
                                runId: "local-a2-flight",
                                runtimeInstanceId: "runtime-a2",
                                executionId: "execution-a2",
                                status: "running",
                                createdAtUtc:
                                    DateTimeOffset.UtcNow)
                        }
                };

            var enumerator =
                new AiRuntimePoolAssignedWorkEnumerator(
                    authority.Journal,
                    authority.Safety,
                    index);

            var exception =
                await Assert.ThrowsAsync<
                    AiRuntimePoolAssignedWorkAuthorityException>(
                    () =>
                        enumerator.EnumerateAsync(
                            authority.Failure.FailureId));

            Assert.Equal(
                AiRuntimePoolAssignedWorkAuthorityFailure
                    .RuntimeBoundaryViolation,
                exception.Reason);

            Assert.Equal(
                0,
                index.MutationCallCount);
        }

        /// <summary>
        /// Creates one exact journaled and suppressed A1 authority.
        /// </summary>
        private static async Task<AuthorityFixture>
            CreateAuthorityAsync()
        {
            var journal =
                new InMemoryAiRuntimePoolFailureJournal();

            var safety =
                new InMemoryAiRuntimePoolCapacitySafetyRegistry();

            var observer =
                new AiRuntimePoolFailureSafetyObserver(
                    journal,
                    safety);

            var failure =
                CreateFailure();

            await observer.RecordAsync(failure);

            return new AuthorityFixture(
                journal,
                safety,
                failure);
        }

        /// <summary>
        /// Creates one exact A1 failure observation.
        /// </summary>
        private static AiRuntimePoolFailureObservation
            CreateFailure()
        {
            return new AiRuntimePoolFailureObservation
            {
                FailureId = "failure-a1",
                Scope =
                    AiRuntimePoolFailureScope.RuntimeInstance,
                PoolId = "pool-01",
                HostId = "host-01",
                RuntimeInstanceId = "runtime-a1",
                RouteId = "route-a1",
                Kind =
                    AiRuntimePoolFailureKind
                        .UnexpectedProcessExit,
                ExitCode = 137,
                ObservedAtUtc =
                    new DateTimeOffset(
                        2026,
                        7,
                        26,
                        0,
                        0,
                        0,
                        TimeSpan.Zero)
            };
        }

        /// <summary>
        /// Creates one durable runtime-run index entry.
        /// </summary>
        private static AiRuntimeRunExecutionIndexEntry
            CreateEntry(
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
                RuntimeInstanceId =
                    runtimeInstanceId,
                Status = status,
                ExecutionContextSnapshot =
                    CreateExecutionContextSnapshot(),
                CreatedAtUtc = createdAtUtc,
                Metadata =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["sharedRunId"] =
                            "shared-run-01"
                    }
            };
        }

        /// <summary>
        /// Creates one durable tenant execution context.
        /// </summary>
        private static ExecutionContextSnapshot
            CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "context-01",
                Project = "runtime-pool",
                UserId = "user-01",
                TenantId = "tenant-01",
                TenantGroupId =
                    "tenant-group-01",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                TtlSeconds = 300
            };
        }

        /// <summary>
        /// Groups exact failure and suppression authority for tests.
        /// </summary>
        private sealed record AuthorityFixture(
            InMemoryAiRuntimePoolFailureJournal Journal,
            InMemoryAiRuntimePoolCapacitySafetyRegistry Safety,
            AiRuntimePoolFailureObservation Failure);

        /// <summary>
        /// Provides a deterministic existing durable runtime-run index.
        /// </summary>
        private sealed class FakeRuntimeRunExecutionIndex :
            RuntimeRunExecutionIndexTestFixture
        {
            private readonly IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry> entries;

            /// <summary>
            /// Initializes a new instance of the
            /// <see cref="FakeRuntimeRunExecutionIndex"/> class.
            /// </summary>
            public FakeRuntimeRunExecutionIndex(
                IReadOnlyList<
                    AiRuntimeRunExecutionIndexEntry> entries)
            {
                this.entries = entries;
            }

            /// <summary>
            /// Gets or sets a raw result used to prove boundary validation.
            /// </summary>
            public IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>?
                ForcedRecoverableResult { get; init; }

            /// <summary>
            /// Gets the last exact runtime requested by enumeration.
            /// </summary>
            public string? LastRequestedRuntimeInstanceId
            {
                get;
                private set;
            }

            /// <summary>
            /// Gets the number of mutation methods invoked.
            /// </summary>
            public int MutationCallCount { get; private set; }

            /// <inheritdoc />
            public override Task RegisterQueuedAsync(
                AiRuntimeRunExecutionIndexEntry entry,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkStartedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkCompletedAsync(
                string runId,
                string executionId,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkFailedAsync(
                string runId,
                string? executionId,
                string failureReason,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task MarkCancelledAsync(
                string runId,
                string? executionId,
                string? reason,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public override Task<bool> MarkRequeuedForRecoveryAsync(
                string runId,
                string executionId,
                string reason,
                CancellationToken cancellationToken = default)
            {
                this.MutationCallCount++;
                return Task.FromResult(true);
            }

            /// <inheritdoc />
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

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListUnfinishedByRuntimeInstanceAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult<
                    IReadOnlyList<
                        AiRuntimeRunExecutionIndexEntry>>(
                    this.entries
                        .Where(
                            entry =>
                                StringComparer.Ordinal.Equals(
                                    entry.RuntimeInstanceId,
                                    runtimeInstanceId))
                        .ToArray());
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListUnfinishedAsync(
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.entries);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListRecoverableByRuntimeInstanceAsync(
                    string runtimeInstanceId,
                    CancellationToken cancellationToken = default)
            {
                this.LastRequestedRuntimeInstanceId =
                    runtimeInstanceId;

                if (this.ForcedRecoverableResult is not null)
                {
                    return Task.FromResult(
                        this.ForcedRecoverableResult);
                }

                IReadOnlyList<
                    AiRuntimeRunExecutionIndexEntry> result =
                    this.entries
                        .Where(
                            entry =>
                                StringComparer.Ordinal.Equals(
                                    entry.RuntimeInstanceId,
                                    runtimeInstanceId))
                        .ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public override Task<IReadOnlyList<
                AiRuntimeRunExecutionIndexEntry>>
                ListRecoverableAsync(
                    CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.entries);
            }
        }
    }
}
