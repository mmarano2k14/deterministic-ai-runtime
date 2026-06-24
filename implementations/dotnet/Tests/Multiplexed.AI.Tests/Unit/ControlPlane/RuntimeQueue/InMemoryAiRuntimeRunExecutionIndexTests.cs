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