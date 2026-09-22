using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    public sealed class AiDurableInvocationCasOutcomeTests
    {
        [Fact]
        public async Task Authority_Predicate_Rejection_Stops_Lease_Cas_Immediately()
        {
            var authorityClock = new DurableInvocationTestSupport.Clock();
            authorityClock.Advance(TimeSpan.FromSeconds(10));
            var callerClock = new DurableInvocationTestSupport.Clock();
            var store = new DurableInvocationTestSupport.MemoryStore(authorityClock);
            var journal = new AiDurableInvocationJournal(store, callerClock);

            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var beforeCas = store.CasCalls;

            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity,
                "worker-a",
                TimeSpan.FromSeconds(1)));
            Assert.Equal(beforeCas + 1, store.CasCalls);
            Assert.Equal(1, store.GetCalls);
        }

        [Fact]
        public async Task Revision_Conflict_Reuses_Classified_Current_Record_Without_Another_Get()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            store.RejectCasCount = 1;

            var leased = await journal.TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity,
                "worker-a",
                TimeSpan.FromSeconds(30));

            Assert.NotNull(leased);
            Assert.Equal(2, store.CasCalls);
            Assert.Equal(1, store.GetCalls);
        }

        [Fact]
        public async Task Authoritative_Lease_Expiry_Rejects_Completion_Without_Contention_Retries()
        {
            var authorityClock = new DurableInvocationTestSupport.Clock();
            var store = new DurableInvocationTestSupport.MemoryStore(authorityClock);
            var authorityJournal = new AiDurableInvocationJournal(store, authorityClock);
            await authorityJournal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var leased = (await authorityJournal.TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity,
                "worker-a",
                TimeSpan.FromSeconds(2)))!;

            var staleCallerClock = new DurableInvocationTestSupport.Clock();
            staleCallerClock.Set(leased.UpdatedAtUtc);
            authorityClock.Advance(TimeSpan.FromSeconds(3));
            var staleCaller = new AiDurableInvocationJournal(store, staleCallerClock);
            var beforeCas = store.CasCalls;

            var status = await staleCaller.CompleteAsync(
                DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity,
                leased.Lease!,
                DurableInvocationTestSupport.Result());

            Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected, status);
            Assert.Equal(beforeCas + 1, store.CasCalls);
        }
    }
}
