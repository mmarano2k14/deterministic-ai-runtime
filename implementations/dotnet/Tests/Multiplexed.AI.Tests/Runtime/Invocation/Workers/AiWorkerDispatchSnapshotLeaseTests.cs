using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    public sealed class AiWorkerDispatchSnapshotLeaseTests
    {
        [Fact]
        public async Task Dispatch_Candidate_Can_Acquire_The_First_Worker_Lease_Without_A_Point_Read()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var candidate = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            store.GetCalls = 0;

            var leased = await journal.TryAcquireWorkerLeaseAsync(
                DurableInvocationTestSupport.Scope,
                candidate,
                "worker-a",
                TimeSpan.FromSeconds(30),
                allowExpiredLeaseReassignment: false,
                maxAssignmentEpoch: 8);

            Assert.NotNull(leased);
            Assert.Equal(0, store.GetCalls);
            Assert.Equal(1, store.CasCalls);
            Assert.Equal(1, leased!.Lease!.Epoch);
        }

        [Fact]
        public async Task Classified_First_Candidate_Cas_Conflict_Reuses_Durable_Truth_Without_Point_Read()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var candidate = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            store.GetCalls = 0;
            store.RejectCasCount = 1;

            var leased = await journal.TryAcquireWorkerLeaseAsync(
                DurableInvocationTestSupport.Scope,
                candidate,
                "worker-a",
                TimeSpan.FromSeconds(30),
                allowExpiredLeaseReassignment: false,
                maxAssignmentEpoch: 8);

            Assert.NotNull(leased);
            Assert.Equal(0, store.GetCalls);
            Assert.Equal(2, store.CasCalls);
            Assert.Equal(1, leased!.Lease!.Epoch);
        }

        [Fact]
        public async Task Supervisor_Page_Candidate_Reaches_Preparation_Without_A_PreLease_Point_Read()
        {
            using var fixture = new WorkerTestSupport.Fixture();
            var candidate = await fixture.PrepareAsync();
            fixture.Store.GetCalls = 0;
            fixture.Preparer.Body = (record, token) =>
            {
                Assert.Equal(0, fixture.Store.GetCalls);
                return Task.FromResult(WorkerTestSupport.Bundle(record.Definition.Target));
            };

            var result = await fixture.Supervisor.DispatchAsync(DurableInvocationTestSupport.Scope, candidate);

            Assert.Equal(AiWorkerDispatchDisposition.Accepted, result.Disposition);
        }

        [Fact]
        public async Task Identity_Dispatch_Performs_One_PreLease_Point_Read_Instead_Of_Two()
        {
            using var fixture = new WorkerTestSupport.Fixture();
            await fixture.PrepareAsync();
            fixture.Store.GetCalls = 0;
            fixture.Preparer.Body = (record, token) =>
            {
                Assert.Equal(1, fixture.Store.GetCalls);
                return Task.FromResult(WorkerTestSupport.Bundle(record.Definition.Target));
            };

            var result = await fixture.DispatchAsync();

            Assert.Equal(AiWorkerDispatchDisposition.Accepted, result.Disposition);
        }
    }
}
