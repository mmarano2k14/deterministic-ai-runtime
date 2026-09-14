using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Worker replay admission is checked inside journal CAS, including competing snapshot changes.</summary>
    public sealed class AiWorkerLeaseAdmissionTests
    {
        [Fact]
        public async Task First_Assignment_Does_Not_Require_Expired_Replay_OptIn()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create(); await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var lease = await journal.TryAcquireWorkerLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-a", TimeSpan.FromSeconds(30), false, 1);
            Assert.NotNull(lease); Assert.Equal(1, lease.Lease!.Epoch);
        }
        [Theory]
        [InlineData(false, 2, false)]
        [InlineData(true, 1, false)]
        [InlineData(true, 2, true)]
        public async Task Expired_Admission_Respects_OptIn_And_Epoch_Cap(bool allow, int cap, bool expected)
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create(); await DurableInvocationTestSupport.LeaseAsync(journal);
            clock.Advance(TimeSpan.FromMinutes(1));
            var leased = await journal.TryAcquireWorkerLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-b", TimeSpan.FromSeconds(30), allow, cap);
            Assert.Equal(expected, leased is not null); if (leased is not null) Assert.Equal(2, leased.Lease!.Epoch);
        }
        [Theory]
        [InlineData(false, 10)]
        [InlineData(true, 1)]
        public async Task Cas_Retry_Does_Not_Use_Stale_Replay_Admission(bool allow, int cap)
        {
            var (journal, store, clock) = DurableInvocationTestSupport.Create();
            var initial = await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            store.BeforeNextCas = () =>
            {
                var raced = initial with { Revision = 1, Status = AiDurableInvocationStatus.Leased,
                    Lease = new AiDurableInvocationLease("other", 1, "other-token", clock.GetUtcNow().AddSeconds(1)) };
                store.Restore(JsonSerializer.Serialize(new[] { raced })); clock.Advance(TimeSpan.FromSeconds(2));
            };
            var lease = await journal.TryAcquireWorkerLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-a", TimeSpan.FromSeconds(30), allow, cap);
            Assert.Null(lease);
            Assert.Equal("other", (await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Lease!.WorkerId);
        }
        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public async Task Invalid_Worker_Epoch_Cap_Is_Refused(int cap)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await journal.TryAcquireWorkerLeaseAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, "worker-a", TimeSpan.FromSeconds(30), true, cap));
        }
        [Fact]
        public async Task General_Journal_Lease_Api_Retains_Its_Existing_Contract()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create(); var first = await DurableInvocationTestSupport.LeaseAsync(journal);
            clock.Advance(TimeSpan.FromMinutes(1));
            var next = await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "other", TimeSpan.FromSeconds(30));
            Assert.Equal(2, next!.Lease!.Epoch); Assert.Equal(first.OperationId, next.OperationId);
        }
    }
}
