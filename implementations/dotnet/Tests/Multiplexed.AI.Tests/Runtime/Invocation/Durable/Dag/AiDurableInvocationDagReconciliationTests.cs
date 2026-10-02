using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Dag;
using Multiplexed.AI.Runtime.Invocation.Durable.DI;
using static Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag.DurableInvocationDagTestSupport;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable.Dag
{
    public sealed class AiDurableInvocationDagReconciliationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Deferral_Preserves_Result_Assignment_And_Continuation_State(bool scheduled)
        {
            using var fixture = await CreateAsync();
            var completed = await fixture.TerminalAsync();
            if (scheduled) completed = (await fixture.Journal.MarkContinuationScheduledAsync(Scope, Identity))!;
            var deferred = (await fixture.Journal.DeferContinuationAsync(Scope, Identity))!;
            Assert.Equal(completed.ContinuationStatus, deferred.ContinuationStatus);
            Assert.Equal(completed.Result, deferred.Result);
            Assert.Equal(completed.ResultSha256, deferred.ResultSha256);
            Assert.Equal(completed.Lease, deferred.Lease);
            Assert.Equal(completed.Definition, deferred.Definition);
            Assert.True(deferred.UpdatedAtUtc > completed.UpdatedAtUtc);
            Assert.Equal(completed.Revision + 1, deferred.Revision);
        }

        [Fact]
        public async Task Keyset_Fairness_Reaches_Candidates_Beyond_First_Batch_Without_Durable_Deferral_Writes()
        {
            using var fixture = await CreateAsync();
            for (var index = 0; index < 105; index++)
            {
                var definition = DurableInvocationTestSupport.Definition() with
                    { Identity = Identity with { ExecutionId = $"missing-parent-{index:D3}" } };
                await fixture.Journal.PrepareAsync(definition);
                var leased = (await fixture.Journal.TryAcquireLeaseAsync(Scope, definition.Identity, "worker", TimeSpan.FromSeconds(30)))!;
                await fixture.Journal.CompleteAsync(Scope, definition.Identity, leased.Lease!, new AiDurableInvocationResult(true, "{}"));
            }

            var writesBefore = fixture.JournalStore.CasCalls;
            var first = await fixture.Reconciler.ReconcilePageAsync(Scope, 100, null);
            Assert.Equal(100, first.Candidates);
            Assert.Equal(100, first.Errors); // Parent absence is retained, never silently acknowledged.
            Assert.NotNull(first.NextCursor);
            Assert.False(first.Wrapped);

            var second = await fixture.Reconciler.ReconcilePageAsync(Scope, 100, first.NextCursor);
            Assert.Equal(5, second.Candidates);
            Assert.Equal(5, second.Errors);
            Assert.NotNull(second.NextCursor);
            Assert.False(second.Wrapped);
            Assert.Equal(writesBefore, fixture.JournalStore.CasCalls);

            var wrapped = await fixture.Reconciler.ReconcilePageAsync(Scope, 100, second.NextCursor);
            Assert.Equal(100, wrapped.Candidates);
            Assert.True(wrapped.Wrapped);
            Assert.Equal(writesBefore, fixture.JournalStore.CasCalls);
            Assert.All(await fixture.JournalStore.ListContinuationCandidatesAsync(Scope, 100),
                item => Assert.Equal(AiDurableInvocationContinuationStatus.Pending, item.ContinuationStatus));
        }

        [Fact]
        public async Task Continuation_Page_Uses_Exclusive_UpdatedAt_OperationId_Cursor()
        {
            using var fixture = await CreateAsync();
            for (var index = 0; index < 3; index++)
            {
                var definition = DurableInvocationTestSupport.Definition() with
                    { Identity = Identity with { ExecutionId = $"cursor-{index:D3}" } };
                await fixture.Journal.PrepareAsync(definition);
                var leased = (await fixture.Journal.TryAcquireLeaseAsync(Scope, definition.Identity, "worker", TimeSpan.FromSeconds(30)))!;
                await fixture.Journal.CompleteAsync(Scope, definition.Identity, leased.Lease!, new AiDurableInvocationResult(true, "{}"));
            }
            var page = await fixture.JournalStore.ListContinuationPageAsync(Scope, 2);
            Assert.Equal(2, page.Count);
            var cursor = new AiDurableInvocationContinuationCursor(page[^1].UpdatedAtUtc, page[^1].OperationId);
            var tail = await fixture.JournalStore.ListContinuationPageAsync(Scope, 2, cursor);
            Assert.Single(tail);
            Assert.DoesNotContain(tail, item => page.Any(first => first.OperationId == item.OperationId));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public async Task Reconciliation_Rejects_Unbounded_Batches(int batch)
        {
            using var fixture = await CreateAsync();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Reconciler.ReconcileAsync(Scope, batch));
        }

        [Fact]
        public async Task Acknowledged_Continuation_Is_Not_Rotated_Or_Redispatched()
        {
            using var fixture = await CreateAsync();
            await fixture.TerminalAsync();
            await fixture.SetStateAsync(AiStepExecutionStatus.WaitingForExternal, AiExecutionStatus.Cancelled);
            var acknowledged = await fixture.ReconcileAsync();
            Assert.Null(await fixture.Journal.DeferContinuationAsync(Scope, Identity));
            Assert.Equal(acknowledged, await fixture.Journal.GetAsync(Scope, Identity));
            Assert.Equal(0, (await fixture.Reconciler.ReconcileAsync(Scope)).Candidates);
        }

        [Fact]
        public void Core_Registration_Is_Idempotent_And_Does_Not_Start_A_Hosted_Service()
        {
            var services = new ServiceCollection();
            services.AddAiDurableInvocationDag(); services.AddAiDurableInvocationDag();
            Assert.Equal(3, services.Count(item => item.ServiceType == typeof(IAiStepInvocationAdapterFactory)));
            Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService));
            Assert.DoesNotContain(services, item => item.ServiceType == typeof(IAiDurableInvocationTargetResolver));
            Assert.DoesNotContain(typeof(AiDurableInvocationStepAdapter).GetCustomAttributes(false),
                attribute => attribute.GetType().Name == "AiStepAttribute");
        }

        [Fact]
        public void Reconciler_Requires_Explicit_Trusted_Scopes()
        {
            using var provider = new ServiceCollection().BuildServiceProvider();
            Assert.Throws<ArgumentException>(() => new AiDurableInvocationDagReconcilerHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(), new AiDurableInvocationDagReconciliationOptions(),
                NullLogger<AiDurableInvocationDagReconcilerHostedService>.Instance));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(99)]
        [InlineData(300001)]
        public void Reconciler_Rejects_Unbounded_Or_Busy_Loop_Intervals(int milliseconds)
        {
            using var provider = new ServiceCollection().BuildServiceProvider();
            Assert.Throws<ArgumentException>(() => new AiDurableInvocationDagReconcilerHostedService(
                provider.GetRequiredService<IServiceScopeFactory>(), new AiDurableInvocationDagReconciliationOptions
                { Scopes = new[] { Scope }, Interval = TimeSpan.FromMilliseconds(milliseconds) },
                NullLogger<AiDurableInvocationDagReconcilerHostedService>.Instance));
        }

        [Fact]
        public void Hosted_Registration_Is_Explicit_And_Rejects_Duplicate_Configuration()
        {
            var services = new ServiceCollection();
            var options = new AiDurableInvocationDagReconciliationOptions { Scopes = new[] { Scope } };
            services.AddAiDurableInvocationDagReconciliation(options);
            Assert.Single(services.Where(item => item.ServiceType == typeof(IHostedService)));
            Assert.Throws<InvalidOperationException>(() => services.AddAiDurableInvocationDagReconciliation(options));
        }
    }
}
