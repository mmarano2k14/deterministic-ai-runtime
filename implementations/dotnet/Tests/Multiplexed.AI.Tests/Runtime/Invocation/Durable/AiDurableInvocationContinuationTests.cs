using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    /// <summary>Early-result persistence, nonterminal scheduling and correlated acknowledgement.</summary>
    public sealed class AiDurableInvocationContinuationTests
    {
        [Fact]
        public async Task Scheduling_And_Rehydration_Do_Not_Consume_Continuation_Intent()
        {
            var (journal, store, clock) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            Assert.Equal(completed, Assert.Single(await store.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10)));
            var scheduled = await journal.MarkContinuationScheduledAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
            Assert.Equal(AiDurableInvocationContinuationStatus.Scheduled, scheduled!.ContinuationStatus);
            var reloadedStore = new DurableInvocationTestSupport.MemoryStore(clock); reloadedStore.Restore(store.Export());
            var reloaded = new AiDurableInvocationJournal(reloadedStore, clock);
            Assert.Equal(scheduled, Assert.Single(await reloadedStore.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10)));
            Assert.Equal(scheduled, await reloaded.MarkContinuationScheduledAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
            Assert.Equal(completed.Result, scheduled.Result);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Only_Actual_Application_Acknowledgement_Removes_A_Candidate(bool scheduledFirst)
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            if (scheduledFirst) await journal.MarkContinuationScheduledAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
            var ack = DurableInvocationTestSupport.Ack(completed);
            var applied = await journal.AcknowledgeContinuationAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, ack);
            Assert.Equal(AiDurableInvocationContinuationStatus.Applied, applied!.ContinuationStatus);
            Assert.Empty(await store.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10));
            Assert.Equal(applied, await journal.AcknowledgeContinuationAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, ack));
            Assert.Null(await journal.MarkContinuationScheduledAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task Parent_Terminal_Suppression_Is_Distinct_From_Application()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            var ack = DurableInvocationTestSupport.Ack(completed, AiDurableInvocationContinuationStatus.Suppressed);
            var suppressed = await journal.AcknowledgeContinuationAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, ack);
            Assert.Equal(AiDurableInvocationContinuationStatus.Suppressed, suppressed!.ContinuationStatus);
            Assert.Equal(completed.Result, suppressed.Result);
            Assert.Empty(await store.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AcknowledgeContinuationAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, DurableInvocationTestSupport.Ack(completed)));
        }

        [Theory]
        [InlineData("operation")]
        [InlineData("result")]
        [InlineData("status")]
        public async Task Wrong_Correlation_Or_Queue_Only_Acknowledgement_Is_Refused(string field)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            var ack = DurableInvocationTestSupport.Ack(completed);
            ack = field switch
            {
                "operation" => ack with { OperationId = "other" },
                "result" => ack with { ResultSha256 = DurableInvocationTestSupport.Digest('f') },
                _ => ack with { Status = AiDurableInvocationContinuationStatus.Scheduled }
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AcknowledgeContinuationAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, ack));
            Assert.Equal(completed, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task Scheduling_Is_Not_A_Completion_Path_For_Unfinished_Work()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            await DurableInvocationTestSupport.LeaseAsync(journal);
            Assert.Null(await journal.MarkContinuationScheduledAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
            Assert.Empty(await store.ListContinuationCandidatesAsync(DurableInvocationTestSupport.Scope, 10));
        }

        [Fact]
        public async Task Acknowledgement_Must_Keep_An_Explicit_Reason()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            await Assert.ThrowsAsync<ArgumentException>(() => journal.AcknowledgeContinuationAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, DurableInvocationTestSupport.Ack(completed) with { Reason = " " }));
        }

        [Theory]
        [InlineData("group")]
        [InlineData("control-plane")]
        public async Task Other_Ownership_Scopes_Cannot_Read_Or_Advance_The_Result(string field)
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            var scope = DurableInvocationTestSupport.Scope;
            scope = field == "group" ? scope with { TenantGroupId = "other" } : scope with { ControlPlaneId = "other" };
            Assert.Null(await journal.GetAsync(scope, DurableInvocationTestSupport.Identity));
            Assert.Empty(await store.ListContinuationCandidatesAsync(scope, 10));
            Assert.Null(await journal.AcknowledgeContinuationAsync(scope, DurableInvocationTestSupport.Identity, DurableInvocationTestSupport.Ack(completed)));
            Assert.Equal(completed, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task Reading_A_Terminal_Result_Does_Not_Lease_Or_Emit_An_Effect()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal);
            var writes = store.CasCalls;
            for (var count = 0; count < 10; count++) Assert.Equal(completed,
                await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
            Assert.Equal(writes, store.CasCalls);
        }
    }
}
