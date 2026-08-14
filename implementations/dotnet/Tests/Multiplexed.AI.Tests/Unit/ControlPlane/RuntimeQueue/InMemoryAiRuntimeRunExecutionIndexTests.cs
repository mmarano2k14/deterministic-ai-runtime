using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeQueue
{
    public sealed class InMemoryAiRuntimeRunExecutionIndexTests
    {
        [Fact]
        public async Task ListUnfinishedByRuntimeInstanceAsync_Should_Return_Only_Unfinished_Runs_For_RuntimeInstance()
        {
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await index.RegisterQueuedAsync(
                CreateEntry(
                    "run-queued-runtime-1",
                    "runtime-1",
                    "queued"));

            await index.RegisterQueuedAsync(
                CreateEntry(
                    "run-running-runtime-1",
                    "runtime-1",
                    "queued"));

            await index.MarkStartedAsync(
                "run-running-runtime-1",
                "execution-running-runtime-1");

            await index.RegisterQueuedAsync(
                CreateEntry(
                    "run-completed-runtime-1",
                    "runtime-1",
                    "queued"));

            await index.MarkCompletedAsync(
                "run-completed-runtime-1",
                "execution-completed-runtime-1");

            await index.RegisterQueuedAsync(
                CreateEntry(
                    "run-failed-runtime-1",
                    "runtime-1",
                    "queued"));

            await index.MarkFailedAsync(
                "run-failed-runtime-1",
                "execution-failed-runtime-1",
                "runtime failure");

            await index.RegisterQueuedAsync(
                CreateEntry(
                    "run-cancelled-runtime-1",
                    "runtime-1",
                    "queued"));

            await index.MarkCancelledAsync(
                "run-cancelled-runtime-1",
                "execution-cancelled-runtime-1",
                "cancelled");

            await index.RegisterQueuedAsync(
                CreateEntry(
                    "run-queued-runtime-2",
                    "runtime-2",
                    "queued"));

            var entries =
                await index.ListUnfinishedByRuntimeInstanceAsync("runtime-1");

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.RunId == "run-queued-runtime-1" && entry.Status == "queued");
            Assert.Contains(entries, entry => entry.RunId == "run-running-runtime-1" && entry.Status == "running");
            Assert.DoesNotContain(entries, entry => entry.RunId == "run-completed-runtime-1");
            Assert.DoesNotContain(entries, entry => entry.RunId == "run-failed-runtime-1");
            Assert.DoesNotContain(entries, entry => entry.RunId == "run-cancelled-runtime-1");
            Assert.DoesNotContain(entries, entry => entry.RuntimeInstanceId == "runtime-2");
        }

        /// <summary>
        /// Verifies that first-writer queued registration preserves the original runtime owner.
        /// </summary>
        [Fact]
        public async Task TryRegisterQueuedAsync_Should_Accept_Only_First_Runtime_Owner()
        {
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            var first = await index.TryRegisterQueuedAsync(
                new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = "deterministic-recovery-run",
                    ExecutionId = "execution-1",
                    RuntimeInstanceId = "runtime-winner",
                    Status = "queued",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["recovery.owner.id"] = "runtime-recovery:execution-1:shared-run-1:failed-run-1"
                    }
                });

            var second = await index.TryRegisterQueuedAsync(
                new AiRuntimeRunExecutionIndexEntry
                {
                    RunId = "deterministic-recovery-run",
                    ExecutionId = "execution-1",
                    RuntimeInstanceId = "runtime-loser",
                    Status = "queued",
                    CreatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                    Metadata = new Dictionary<string, string>
                    {
                        ["recovery.owner.id"] = "runtime-recovery:execution-1:shared-run-1:failed-run-1"
                    }
                });

            var entry = await index.GetAsync(
                "deterministic-recovery-run");

            Assert.True(first);
            Assert.False(second);
            Assert.NotNull(entry);
            Assert.Equal("runtime-winner", entry!.RuntimeInstanceId);
            Assert.Equal("execution-1", entry.ExecutionId);
        }

        /// <summary>
        /// Verifies that a running runtime run can be marked as requeued for recovery.
        /// </summary>
        [Fact]
        public async Task MarkRequeuedForRecoveryAsync_Should_Mark_Running_Run_As_Requeued_For_Recovery()
        {
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await index.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = "run-1",
                ExecutionId = "execution-1",
                RuntimeInstanceId = "runtime-1",
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot()
            });

            await index.MarkStartedAsync(
                "run-1",
                "execution-1");

            var changed = await index.MarkRequeuedForRecoveryAsync(
                "run-1",
                "execution-1",
                "runtime-execution-recovery-requeue");

            var entry = await index.GetAsync("run-1");
            var unfinished = await index.ListUnfinishedByRuntimeInstanceAsync("runtime-1");

            Assert.True(changed);
            Assert.NotNull(entry);
            Assert.Equal("requeued-for-recovery", entry!.Status);
            Assert.Equal("runtime-execution-recovery-requeue", entry.FailureReason);
            Assert.NotNull(entry.CompletedAtUtc);
            Assert.Empty(unfinished);
        }

        /// <summary>
        /// Verifies that requeued-for-recovery is idempotent.
        /// </summary>
        [Fact]
        public async Task MarkRequeuedForRecoveryAsync_Should_Return_False_When_Already_Requeued_For_Recovery()
        {
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await index.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = "run-1",
                ExecutionId = "execution-1",
                RuntimeInstanceId = "runtime-1",
                Status = "queued",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateExecutionContextSnapshot()
            });

            await index.MarkStartedAsync(
                "run-1",
                "execution-1");

            var first = await index.MarkRequeuedForRecoveryAsync(
                "run-1",
                "execution-1",
                "first-recovery");

            var second = await index.MarkRequeuedForRecoveryAsync(
                "run-1",
                "execution-1",
                "second-recovery");

            var entry = await index.GetAsync("run-1");

            Assert.True(first);
            Assert.False(second);
            Assert.NotNull(entry);
            Assert.Equal("requeued-for-recovery", entry!.Status);
            Assert.Equal("first-recovery", entry.FailureReason);
        }

        /// <summary>
        /// Verifies that terminal runtime run entries cannot be marked as requeued for recovery.
        /// </summary>
        [Theory]
        [InlineData("completed")]
        [InlineData("failed")]
        [InlineData("cancelled")]
        [InlineData("requeued-for-recovery")]
        public async Task MarkRequeuedForRecoveryAsync_Should_Return_False_When_Run_Is_Terminal(
            string terminalStatus)
        {
            var index = new InMemoryAiRuntimeRunExecutionIndex();

            await index.RegisterQueuedAsync(new AiRuntimeRunExecutionIndexEntry
            {
                RunId = "run-1",
                ExecutionId = "execution-1",
                RuntimeInstanceId = "runtime-1",
                Status = terminalStatus,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                FailureReason = "terminal",
                ExecutionContextSnapshot = CreateExecutionContextSnapshot()
            });

            var changed = await index.MarkRequeuedForRecoveryAsync(
                "run-1",
                "execution-1",
                "runtime-execution-recovery-requeue");

            var entry = await index.GetAsync("run-1");

            Assert.False(changed);
            Assert.NotNull(entry);
            Assert.Equal(terminalStatus, entry!.Status);
            Assert.Equal("terminal", entry.FailureReason);
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-tenant-1",
                Project = "runtime-run-index-tests",
                UserId = "test-user",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>(),
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.Now
            };
        }

        private static AiRuntimeRunExecutionIndexEntry CreateEntry(
            string runId,
            string runtimeInstanceId,
            string status)
        {
            return new AiRuntimeRunExecutionIndexEntry
            {
                RunId = runId,
                RuntimeInstanceId = runtimeInstanceId,
                Status = status,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExecutionContextSnapshot = CreateSnapshot("tenant-1"),
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "in-memory-runtime-run-index-test"
                }
            };
        }

        private static ExecutionContextSnapshot CreateSnapshot(
            string tenantId)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"test-context-{Guid.NewGuid():N}",
                TenantId = tenantId,
                TenantGroupId = "test-tenant-group",
                Project = "deterministic-ai-runtime-tests",
                UserId = $"user-{tenantId}",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "default",
                        Trns = new HashSet<string>
                        {
                            "trn:deterministic-ai-runtime-tests:runtime:run:read",
                            "trn:deterministic-ai-runtime-tests:runtime:run:write",
                            "trn:deterministic-ai-runtime-tests:runtime:execution:read"
                        }
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 300
            };
        }
    }
}