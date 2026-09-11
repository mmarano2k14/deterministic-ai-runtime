using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    /// <summary>Assignment fencing, atomic outcomes, contention and ambiguous acknowledgements.</summary>
    public sealed class AiDurableInvocationLeaseTests
    {
        [Fact]
        public async Task Concurrent_Lease_Requests_Have_One_Winner()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => journal.TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, "worker-" + index, TimeSpan.FromSeconds(30))));
            var winner = Assert.Single(results.Where(item => item is not null))!;
            Assert.Equal(1, winner!.Lease!.Epoch);
            Assert.Equal(1, winner.Revision);
        }

        [Theory]
        [InlineData("worker-a")]
        [InlineData("worker-b")]
        public async Task Expired_Reassignment_Changes_Only_Assignment_Authority(string nextWorker)
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var first = await DurableInvocationTestSupport.LeaseAsync(journal);
            Assert.Null(await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                nextWorker, TimeSpan.FromSeconds(30)));
            clock.Advance(TimeSpan.FromSeconds(30));
            var replacement = (await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                nextWorker, TimeSpan.FromSeconds(30)))!;
            Assert.Equal(2, replacement.Lease!.Epoch);
            Assert.NotEqual(first.Lease!.Token, replacement.Lease.Token);
            Assert.Equal(first.OperationId, replacement.OperationId);
            Assert.Equal(first.EffectIdempotencyKey, replacement.EffectIdempotencyKey);
            Assert.Equal(first.Definition, replacement.Definition);
            Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected, await journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, first.Lease, DurableInvocationTestSupport.Result()));
        }

        [Theory]
        [InlineData("worker")]
        [InlineData("epoch")]
        [InlineData("token")]
        public async Task A_Result_Must_Match_All_Assignment_Authority_Fields(string field)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            var forged = field switch
            {
                "worker" => record.Lease! with { WorkerId = "foreign" },
                "epoch" => record.Lease! with { Epoch = 999 },
                _ => record.Lease! with { Token = "foreign" }
            };
            Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected, await journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, forged, DurableInvocationTestSupport.Result()));
        }

        [Fact]
        public async Task Expiry_In_A_Callback_Cannot_Extend_The_Stored_Lease()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            clock.Advance(TimeSpan.FromSeconds(30));
            var forged = record.Lease! with { ExpiresAtUtc = clock.GetUtcNow().AddDays(1) };
            Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected, await journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, forged, DurableInvocationTestSupport.Result()));
        }

        [Fact]
        public async Task Renewal_Extends_Without_Changing_Identity_Or_Fence()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            clock.Advance(TimeSpan.FromSeconds(10));
            var renewed = (await journal.TryRenewLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                record.Lease!, TimeSpan.FromSeconds(30)))!;
            Assert.Equal(record.Lease!.Token, renewed.Lease!.Token);
            Assert.Equal(record.Lease.Epoch, renewed.Lease.Epoch);
            Assert.Equal(record.OperationId, renewed.OperationId);
            Assert.Equal(record.Lease.ExpiresAtUtc.AddSeconds(10), renewed.Lease.ExpiresAtUtc);
            Assert.Null(await journal.TryRenewLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                record.Lease, TimeSpan.FromSeconds(1)));
            Assert.Equal(renewed, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task Expired_Lease_Cannot_Be_Renewed()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Null(await journal.TryRenewLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                record.Lease!, TimeSpan.FromSeconds(30)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Result_And_Pending_Continuation_Are_Accepted_Together(bool success)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var completed = await DurableInvocationTestSupport.CompleteAsync(journal, success);
            Assert.Equal(success ? AiDurableInvocationStatus.Succeeded : AiDurableInvocationStatus.Failed, completed.Status);
            Assert.Equal(DurableInvocationTestSupport.Result(success), completed.Result);
            Assert.Equal(AiDurableInvocationContinuationStatus.Pending, completed.ContinuationStatus);
            Assert.NotNull(completed.ResultSha256);
            Assert.NotNull(completed.CompletedAtUtc);
            Assert.Null(await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-b", TimeSpan.FromSeconds(30)));
        }

        [Fact]
        public async Task Concurrent_Identical_Completions_Accept_One_Result()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, record.Lease!, DurableInvocationTestSupport.Result())));
            Assert.Equal(1, outcomes.Count(value => value == AiDurableInvocationCompletionStatus.Accepted));
            Assert.Equal(63, outcomes.Count(value => value == AiDurableInvocationCompletionStatus.AlreadyAccepted));
            Assert.Equal(2, (await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Revision);
        }

        [Theory]
        [InlineData(true, "{\"value\":99}")]
        [InlineData(false, "{\"value\":42}")]
        public async Task A_Second_Conflicting_Result_Is_Never_An_Upsert(bool success, string json)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.CompleteAsync(journal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, record.Lease!, new(success, json)));
            Assert.Equal(record, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task An_Accepted_Duplicate_Remains_Acknowledged_After_Expiry()
        {
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.CompleteAsync(journal);
            clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(AiDurableInvocationCompletionStatus.AlreadyAccepted, await journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, record.Lease!, new(true, " { \"value\" : 42 } ")));
            Assert.Equal(record, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task Expiry_Between_Read_And_Cas_Rejects_The_Result()
        {
            var (journal, store, clock) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            store.BeforeNextCas = () => clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(AiDurableInvocationCompletionStatus.LeaseRejected, await journal.CompleteAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, record.Lease!, DurableInvocationTestSupport.Result()));
            Assert.Null((await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Result);
        }

        [Fact]
        public async Task Lost_Result_Acknowledgement_Is_Reconciled_Without_New_Work()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            store.ThrowAfterNextWrite = true;
            await Assert.ThrowsAsync<IOException>(() => journal.CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, record.Lease!, DurableInvocationTestSupport.Result()));
            Assert.Equal(AiDurableInvocationCompletionStatus.AlreadyAccepted, await journal.CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, record.Lease!, DurableInvocationTestSupport.Result()));
            Assert.Equal(2, (await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Revision);
        }

        [Fact]
        public async Task Cas_Contention_Retries_The_Same_Logical_Operation()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            store.RejectCasCount = 3;
            var record = await journal.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity,
                "worker-a", TimeSpan.FromSeconds(30));
            Assert.Equal(4, store.CasCalls);
            Assert.Equal(1, record!.Lease!.Epoch);
        }

        [Fact]
        public async Task Persistent_Cas_Rejection_Is_Bounded_And_Not_Success()
        {
            var (journal, store, _) = DurableInvocationTestSupport.Create();
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            store.RejectCasCount = int.MaxValue;
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, "worker-a", TimeSpan.FromSeconds(30)));
            Assert.Equal(16, store.CasCalls);
            Assert.Null((await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity))!.Lease);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(300001)]
        public async Task Lease_Duration_Is_Server_Bounded(int milliseconds)
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => journal.TryAcquireLeaseAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, "worker-a", TimeSpan.FromMilliseconds(milliseconds)));
        }

        [Fact]
        public async Task Cancellation_Does_Not_Write_An_Outcome()
        {
            var (journal, _, _) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.CompleteAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, record.Lease!, DurableInvocationTestSupport.Result(), cancellation.Token));
            Assert.Equal(record, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }

        [Fact]
        public async Task Clock_Ahead_Of_Storage_Cannot_Replace_A_Live_Assignment()
        {
            var (journal, store, clock) = DurableInvocationTestSupport.Create();
            var record = await DurableInvocationTestSupport.LeaseAsync(journal);
            var ahead = new DurableInvocationTestSupport.Clock(); ahead.Set(clock.GetUtcNow().AddMinutes(1));
            var other = new AiDurableInvocationJournal(store, ahead);
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.TryAcquireLeaseAsync(DurableInvocationTestSupport.Scope,
                DurableInvocationTestSupport.Identity, "worker-b", TimeSpan.FromSeconds(30)));
            Assert.Equal(record, await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity));
        }
    }
}
