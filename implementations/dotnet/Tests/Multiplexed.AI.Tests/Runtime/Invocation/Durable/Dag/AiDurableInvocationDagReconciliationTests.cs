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
        public async Task Oldest_Retained_Batch_Cannot_Starve_Other_Tenant_Candidates()
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
            var before = await fixture.JournalStore.ListContinuationCandidatesAsync(Scope, 100);
            var pass = await fixture.Reconciler.ReconcileAsync(Scope, 100);
            Assert.Equal(100, pass.Candidates);
            Assert.Equal(100, pass.Errors); // Parent absence is retained, never silently acknowledged.
            var after = await fixture.JournalStore.ListContinuationCandidatesAsync(Scope, 100);
            Assert.Equal(5, after.Take(5).Count(item => !before.Any(old => old.OperationId == item.OperationId)));
            Assert.All(after, item => Assert.Equal(AiDurableInvocationContinuationStatus.Pending, item.ContinuationStatus));
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
