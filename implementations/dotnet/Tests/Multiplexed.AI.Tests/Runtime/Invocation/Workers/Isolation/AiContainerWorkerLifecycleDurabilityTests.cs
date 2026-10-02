using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Durable lifecycle closure proving container execution remains subordinate to the existing journal lease authority.</summary>
    [Trait("Category", "ContainerProcess")]
    public sealed class AiContainerWorkerLifecycleDurabilityTests
    {
        [Fact]
        public async Task Cancelled_Container_Assignment_Can_Be_Redispatched_And_An_Old_Epoch_Cannot_Win()
        {
            var onceMarker = Temp("cancel-once");
            var cleanupMarker = Temp("cancel-cleanup");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_MODE"] = "hang-once-after-ready",
                    ["CONTAINER_ENGINE_PROBE_ONCE_MARKER"] = onceMarker,
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_MARKER"] = cleanupMarker
                }, containerOwnerScope: "test-host-cancel-recovery");
                var (journal, _, clock) = DurableInvocationTestSupport.Create();
                var prepared = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
                var options = RecoveryOptions();
                using var capacity = new AiWorkerProcessCapacity(options);
                var supervisor = Supervisor(journal, profile, capacity, options, clock);
                using var cancellation = new CancellationTokenSource();

                var first = supervisor.DispatchAsync(
                    DurableInvocationTestSupport.Scope,
                    DurableInvocationTestSupport.Identity,
                    cancellation.Token);

                await WaitForFileAsync(onceMarker);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(15)));

                var retained = await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
                Assert.NotNull(retained);
                Assert.Equal(AiDurableInvocationStatus.Leased, retained.Status);
                Assert.Null(retained.Result);
                Assert.NotNull(retained.Lease);
                var firstLease = retained.Lease;
                Assert.True(File.Exists(cleanupMarker));
                Assert.Equal(1, capacity.Available);
                Assert.Equal(0, capacity.Quarantined);

                clock.Advance(TimeSpan.FromSeconds(10));
                var recovered = await supervisor.DispatchAsync(
                    DurableInvocationTestSupport.Scope,
                    DurableInvocationTestSupport.Identity);

                Assert.Equal(AiWorkerDispatchDisposition.Accepted, recovered.Disposition);
                Assert.Equal(prepared.OperationId, recovered.OperationId);
                Assert.NotNull(recovered.Epoch);
                Assert.True(recovered.Epoch.Value > firstLease!.Epoch);

                var completed = await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
                Assert.NotNull(completed?.Result);
                Assert.NotNull(completed.Lease);
                Assert.Equal(recovered.Epoch, completed.Lease.Epoch);
                Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected,
                    await journal.CompleteAsync(
                        DurableInvocationTestSupport.Scope,
                        DurableInvocationTestSupport.Identity,
                        firstLease,
                        DurableInvocationTestSupport.Result(json: "{\"value\":999}")));
            }
            finally
            {
                Delete(onceMarker);
                Delete(cleanupMarker);
            }
        }

        [Fact]
        public async Task Failed_Container_Assignment_Retains_The_Operation_And_Duplicate_Replay_Converges()
        {
            var onceMarker = Temp("fail-once");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_MODE"] = "fail-once-after-ready",
                    ["CONTAINER_ENGINE_PROBE_ONCE_MARKER"] = onceMarker
                }, containerOwnerScope: "test-host-failure-recovery");
                var (journal, _, clock) = DurableInvocationTestSupport.Create();
                var prepared = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
                var options = RecoveryOptions();
                using var capacity = new AiWorkerProcessCapacity(options);
                var supervisor = Supervisor(journal, profile, capacity, options, clock);

                var failed = await supervisor.DispatchAsync(
                    DurableInvocationTestSupport.Scope,
                    DurableInvocationTestSupport.Identity);

                Assert.Equal(AiWorkerDispatchDisposition.TechnicalFailure, failed.Disposition);
                Assert.Equal(prepared.OperationId, failed.OperationId);
                Assert.NotNull(failed.Epoch);
                var retained = await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
                Assert.NotNull(retained);
                Assert.Equal(AiDurableInvocationStatus.Leased, retained.Status);
                Assert.Null(retained.Result);

                clock.Advance(TimeSpan.FromSeconds(10));
                var recovered = await supervisor.DispatchAsync(
                    DurableInvocationTestSupport.Scope,
                    DurableInvocationTestSupport.Identity);

                Assert.Equal(AiWorkerDispatchDisposition.Accepted, recovered.Disposition);
                Assert.Equal(prepared.OperationId, recovered.OperationId);
                Assert.NotNull(recovered.Epoch);
                Assert.True(recovered.Epoch.Value > failed.Epoch!.Value);

                var completed = await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
                Assert.NotNull(completed?.Lease);
                Assert.NotNull(completed.Result);
                Assert.Equal(AiDurableInvocationContinuationStatus.Pending, completed.ContinuationStatus);
                Assert.Equal(AiDurableInvocationCompletionStatus.AlreadyAccepted,
                    await journal.CompleteAsync(
                        DurableInvocationTestSupport.Scope,
                        DurableInvocationTestSupport.Identity,
                        completed.Lease,
                        completed.Result));
                Assert.Equal(1, capacity.Available);
                Assert.Equal(0, capacity.Quarantined);
            }
            finally
            {
                Delete(onceMarker);
            }
        }

        private static AiWorkerSupervisionOptions RecoveryOptions() => new(
            maxConcurrentProcesses: 1,
            leaseDuration: TimeSpan.FromSeconds(3),
            renewalInterval: TimeSpan.FromMilliseconds(500),
            leaseSafetyMargin: TimeSpan.FromMilliseconds(500),
            executionTimeout: TimeSpan.FromSeconds(30),
            allowExpiredLeaseReassignment: true,
            maxAssignmentEpoch: 4);

        private static AiWorkerInvocationSupervisor Supervisor(
            AiDurableInvocationJournal journal,
            AiContainerWorkerProfile profile,
            AiWorkerProcessCapacity capacity,
            AiWorkerSupervisionOptions options,
            TimeProvider clock) => new(
            journal,
            new StaticPreparer(ContainerWorkerTestSupport.Request(profile).Code),
            new AiWorkerInvocationTransportRouter(
                new AiWorkerProcessTransport(
                    new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new(), clock),
                new AiContainerWorkerTransport(
                    new AiConfiguredContainerWorkerCatalog(new[] { profile }), new(), clock)),
            new StaticAiControlPlaneIdResolver("control-a"),
            capacity,
            options,
            NullLogger<AiWorkerInvocationSupervisor>.Instance,
            clock);

        private static string Temp(string suffix) =>
            Path.Combine(Path.GetTempPath(), "multiplexed-container-" + suffix + "-" + Guid.NewGuid().ToString("N") + ".txt");

        private static async Task WaitForFileAsync(string path)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(path)) await Task.Delay(20, timeout.Token);
        }

        private static void Delete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private sealed class StaticPreparer(AiWorkerCodeBundle code) : IAiWorkerInvocationPreparer
        {
            public Task<AiWorkerCodeBundle> PrepareAsync(
                AiDurableInvocationRecord invocation,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(code with { Target = invocation.Definition.Target });
            }
        }
    }
}
