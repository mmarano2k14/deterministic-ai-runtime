using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;
using Multiplexed.AI.Tests.Runtime.Publication;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers
{
    /// <summary>Bounded keyset traversal and explicit host registration without enabling any existing runtime mode.</summary>
    public sealed class AiWorkerDispatchPollingTests
    {
        [Fact]
        public async Task Keyset_Pages_Visit_All_Tied_Timestamps_Without_Duplicates()
        {
            var (journal, memory, clock) = DurableInvocationTestSupport.Create(); var store = new WorkerTestSupport.PagedStore(memory);
            var reader = new AiWorkerDispatchPageReader(store, new StaticAiControlPlaneIdResolver("control-a"), clock);
            for (var i = 0; i < 7; i++) await journal.PrepareAsync(DurableInvocationTestSupport.Definition() with
                { Identity = DurableInvocationTestSupport.Identity with { StepName = "call-" + i } });
            var visited = new List<string>(); AiWorkerDispatchCursor? cursor = null;
            while (true)
            {
                var page = await reader.ReadAsync(DurableInvocationTestSupport.Scope, "python", 2, cursor);
                if (page.Count == 0) break;
                visited.AddRange(page.Select(r => r.OperationId)); cursor = new(page[^1].UpdatedAtUtc, page[^1].OperationId);
            }
            Assert.Equal(7, visited.Count); Assert.Equal(7, visited.Distinct().Count());
            Assert.Equal(visited.OrderBy(x => x, StringComparer.Ordinal), visited);
        }
        [Fact]
        public async Task Unreassignable_Expired_Work_Does_Not_Hide_Later_Prepared_Candidates()
        {
            var (journal, memory, clock) = DurableInvocationTestSupport.Create(); var store = new WorkerTestSupport.PagedStore(memory);
            await DurableInvocationTestSupport.LeaseAsync(journal); clock.Advance(TimeSpan.FromMinutes(1));
            await journal.PrepareAsync(DurableInvocationTestSupport.Definition() with { Identity = DurableInvocationTestSupport.Identity with { StepName = "later" } });
            var reader = new AiWorkerDispatchPageReader(store, new StaticAiControlPlaneIdResolver("control-a"), clock);
            var first = Assert.Single(await reader.ReadAsync(DurableInvocationTestSupport.Scope, "python", 1));
            Assert.Equal(AiDurableInvocationStatus.Leased, first.Status);
            var next = Assert.Single(await reader.ReadAsync(DurableInvocationTestSupport.Scope, "python", 1, new(first.UpdatedAtUtc, first.OperationId)));
            Assert.Equal("later", next.Definition.Identity.StepName); Assert.Equal(AiDurableInvocationStatus.Prepared, next.Status);
        }
        [Theory]
        [InlineData("tenant")]
        [InlineData("group")]
        [InlineData("language")]
        public async Task Query_Does_Not_Expose_Another_Partition_Or_Language(string field)
        {
            var (journal, memory, clock) = DurableInvocationTestSupport.Create(); await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var reader = new AiWorkerDispatchPageReader(new WorkerTestSupport.PagedStore(memory), new StaticAiControlPlaneIdResolver("control-a"), clock);
            var scope = field switch { "tenant" => DurableInvocationTestSupport.Scope with { TenantId = "other" },
                "group" => DurableInvocationTestSupport.Scope with { TenantGroupId = "other" }, _ => DurableInvocationTestSupport.Scope };
            Assert.Empty(await reader.ReadAsync(scope, field == "language" ? "typescript" : "python", 10));
        }
        [Fact]
        public async Task Foreign_Control_Plane_Is_Rejected_Before_The_Query()
        {
            var (_, memory, clock) = DurableInvocationTestSupport.Create();
            var reader = new AiWorkerDispatchPageReader(new WorkerTestSupport.PagedStore(memory), new StaticAiControlPlaneIdResolver("other"), clock);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(DurableInvocationTestSupport.Scope, "python", 10));
        }
        [Fact]
        public async Task A_Store_Without_Paging_Cannot_Silently_Use_The_Oldest_Batch_Forever()
        {
            var (_, memory, clock) = DurableInvocationTestSupport.Create();
            var reader = new AiWorkerDispatchPageReader(memory, new StaticAiControlPlaneIdResolver("control-a"), clock);
            await Assert.ThrowsAsync<NotSupportedException>(() => reader.ReadAsync(DurableInvocationTestSupport.Scope, "python", 10));
        }
        [Theory]
        [InlineData("duplicate")]
        [InlineData("oversize")]
        [InlineData("foreign")]
        public async Task A_Broken_Page_Contract_Is_Refused(string defect)
        {
            var (journal, memory, clock) = DurableInvocationTestSupport.Create(); await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
            var store = new WorkerTestSupport.PagedStore(memory);
            store.Mutate = page => defect == "foreign" ? new[] { page[0] with { Definition = page[0].Definition with
                { Scope = page[0].Definition.Scope with { TenantGroupId = "foreign" } } } } : new[] { page[0], page[0] };
            var reader = new AiWorkerDispatchPageReader(store, new StaticAiControlPlaneIdResolver("control-a"), clock);
            await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(DurableInvocationTestSupport.Scope, "python", defect == "oversize" ? 1 : 2));
        }
        [Fact]
        public void Mongo_Store_Exposes_The_Optional_Paging_Capability()
        { Assert.True(typeof(IAiDurableInvocationDispatchPageStore).IsAssignableFrom(typeof(MongoAiDurableInvocationStore))); }
        [Fact]
        public void Worker_Service_Registration_Does_Not_Start_A_Poller_Or_Replace_The_Dag_Adapter()
        {
            var services = new ServiceCollection();
            services.AddAiHostedInvocationWorkers(new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
            Assert.DoesNotContain(services, s => s.ServiceType == typeof(IHostedService));
            Assert.DoesNotContain(services, s => s.ServiceType.Name is "IAiStep" or "IAiPolicy" or "IAiStepInvocationAdapterFactory");
        }
        [Fact]
        public void Duplicate_Worker_Configuration_Is_Refused()
        {
            var services = new ServiceCollection(); var catalog = new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>());
            services.AddAiHostedInvocationWorkers(catalog, new());
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedInvocationWorkers(catalog, new()));
        }
        [Fact]
        public void Polling_Cannot_Be_Enabled_Before_Its_Hosted_Transport_Is_Configured()
        {
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAiHostedInvocationWorkerPolling(
                new(new[] { DurableInvocationTestSupport.Scope }, new[] { "python" })));
        }
        [Fact]
        public async Task Explicit_Host_Poller_Starts_Dispatches_And_Stops_Without_External_Infrastructure()
        {
            var (journal, memory, clock) = DurableInvocationTestSupport.Create(); var store = new WorkerTestSupport.PagedStore(memory);
            for (var i = 0; i < 3; i++) await journal.PrepareAsync(DurableInvocationTestSupport.Definition() with
                { Identity = DurableInvocationTestSupport.Identity with { StepName = "host-" + i } });
            var transport = new WorkerTestSupport.Transport();
            using var host = new HostBuilder().UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; })
                .ConfigureServices(services =>
                {
                    services.AddLogging(); services.AddSingleton<IAiDurableInvocationStore>(store); services.AddSingleton(journal);
                    services.AddSingleton<TimeProvider>(clock); services.AddSingleton<IAiControlPlaneIdResolver>(new StaticAiControlPlaneIdResolver("control-a"));
                    services.AddSingleton<IAiWorkerInvocationPreparer>(new WorkerTestSupport.Preparer());
                    services.AddSingleton<IAiWorkerInvocationTransport>(transport);
                    services.AddSingleton<IAiPayloadStoreResolver>(new PublicationTestSupport.PayloadResolver(new PublicationTestSupport.PayloadStore()));
                    services.AddAiHostedInvocationWorkers(new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new(maxConcurrentProcesses: 2));
                    services.AddAiHostedInvocationWorkerPolling(new(new[] { DurableInvocationTestSupport.Scope }, new[] { "python" },
                        pageSize: 2, interval: TimeSpan.FromMilliseconds(100)));
                }).Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await host.StartAsync(timeout.Token);
            try
            {
                while (System.Text.Json.JsonSerializer.Deserialize<AiDurableInvocationRecord[]>(memory.Export())!.Count(r => r.Result is not null) != 3)
                    await Task.Delay(20, timeout.Token);
            }
            finally { await host.StopAsync(CancellationToken.None); }
            Assert.Equal(3, transport.Requests.Count); Assert.Equal(3, transport.Requests.Select(r => r.OperationId).Distinct().Count());
        }
    }
}
