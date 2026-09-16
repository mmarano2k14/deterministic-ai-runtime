using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Workers.DI;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.AI.Tests.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Workers.Isolation
{
    /// <summary>Provider routing and lifecycle ownership remain behind the existing durable supervisor.</summary>
    [Trait("Category", "ContainerProcess")]
    public sealed class AiContainerWorkerProviderIntegrationTests
    {
        [Fact]
        public void Descriptor_Free_Compatibility_Remains_On_The_Trusted_Process_Provider()
        {
            Assert.Equal(
                AiWorkerInvocationProviderKind.TrustedProcess,
                AiWorkerInvocationTransportRouter.SelectProvider(WorkerTestSupport.Bundle()));
        }

        [Fact]
        public void Oci_Execution_Descriptor_Selects_The_Isolated_Provider()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            Assert.Equal(
                AiWorkerInvocationProviderKind.IsolatedContainer,
                AiWorkerInvocationTransportRouter.SelectProvider(ContainerWorkerTestSupport.Request(profile).Code));
        }

        [Fact]
        public async Task Missing_Isolated_Profile_Does_Not_Downgrade_To_Trusted_Process()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var router = new AiWorkerInvocationTransportRouter(
                new AiWorkerProcessTransport(
                    new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new()),
                new AiContainerWorkerTransport(
                    new AiConfiguredContainerWorkerCatalog(Array.Empty<AiContainerWorkerProfile>()), new()));

            await Assert.ThrowsAsync<NotSupportedException>(() => router.InvokeAsync(
                ContainerWorkerTestSupport.Request(profile), _ => Task.CompletedTask));
        }

        [Fact]
        public void Explicit_Container_Registration_Installs_One_Router_Behind_The_Existing_Boundary()
        {
            var services = new ServiceCollection();
            services.AddAiHostedInvocationWorkers(
                new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
            services.AddAiHostedInvocationContainerWorkers(
                new AiConfiguredContainerWorkerCatalog(new[] { ContainerWorkerTestSupport.Profile() }));

            Assert.Single(services.Where(service => service.ServiceType == typeof(IAiWorkerInvocationTransport)));
            using var provider = services.BuildServiceProvider();
            Assert.IsType<AiWorkerInvocationTransportRouter>(provider.GetRequiredService<IAiWorkerInvocationTransport>());
            Assert.NotNull(provider.GetRequiredService<AiWorkerProcessCapacity>());
        }

        [Fact]
        public void Container_Provider_Cannot_Be_Configured_Before_Hosted_Workers()
        {
            var services = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedInvocationContainerWorkers(
                new AiConfiguredContainerWorkerCatalog(Array.Empty<AiContainerWorkerProfile>())));
        }

        [Fact]
        public void Duplicate_Container_Provider_Configuration_Is_Refused()
        {
            var services = new ServiceCollection();
            services.AddAiHostedInvocationWorkers(
                new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
            var catalog = new AiConfiguredContainerWorkerCatalog(Array.Empty<AiContainerWorkerProfile>());
            services.AddAiHostedInvocationContainerWorkers(catalog);
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedInvocationContainerWorkers(catalog));
        }

        [Fact]
        public void Unknown_Preconfigured_Transport_Is_Not_Silently_Replaced_By_The_Router()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAiWorkerInvocationTransport>(new WorkerTestSupport.Transport());
            services.AddAiHostedInvocationWorkers(
                new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new());
            Assert.Throws<InvalidOperationException>(() => services.AddAiHostedInvocationContainerWorkers(
                new AiConfiguredContainerWorkerCatalog(Array.Empty<AiContainerWorkerProfile>())));
        }

        [Fact]
        public async Task Isolated_Provider_Uses_The_Existing_Journal_And_Result_Acceptance_Path()
        {
            var profile = ContainerWorkerTestSupport.Profile();
            var (journal, _, clock) = DurableInvocationTestSupport.Create();
            var definition = DurableInvocationTestSupport.Definition();
            var prepared = await journal.PrepareAsync(definition);
            var options = new AiWorkerSupervisionOptions(maxConcurrentProcesses: 1);
            using var capacity = new AiWorkerProcessCapacity(options);
            var supervisor = new AiWorkerInvocationSupervisor(
                journal,
                new StaticPreparer(ContainerWorkerTestSupport.Request(profile).Code),
                Router(profile, clock),
                new StaticAiControlPlaneIdResolver("control-a"),
                capacity,
                options,
                NullLogger<AiWorkerInvocationSupervisor>.Instance,
                clock);

            var result = await supervisor.DispatchAsync(
                DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
            Assert.Equal(AiWorkerDispatchDisposition.Accepted, result.Disposition);
            Assert.Equal(prepared.OperationId, result.OperationId);
            var stored = await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
            Assert.NotNull(stored?.Result);
            Assert.Equal(0, capacity.Quarantined);
            Assert.Equal(1, capacity.Available);
        }

        [Fact]
        public async Task Unconfirmed_Container_Cleanup_Quarantines_The_Shared_Supervisor_Capacity()
        {
            var marker = Path.Combine(Path.GetTempPath(), "multiplexed-container-provider-request-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var profile = ContainerWorkerTestSupport.Profile(new Dictionary<string, string>
                {
                    ["CONTAINER_ENGINE_PROBE_MODE"] = "hang-after-ready",
                    ["CONTAINER_ENGINE_PROBE_CLEANUP_FAIL"] = "1",
                    ["CONTAINER_ENGINE_PROBE_REQUEST_MARKER"] = marker
                });
                var (journal, _, clock) = DurableInvocationTestSupport.Create();
                await journal.PrepareAsync(DurableInvocationTestSupport.Definition());
                var options = new AiWorkerSupervisionOptions(maxConcurrentProcesses: 1);
                using var capacity = new AiWorkerProcessCapacity(options);
                var supervisor = new AiWorkerInvocationSupervisor(
                    journal,
                    new StaticPreparer(ContainerWorkerTestSupport.Request(profile).Code),
                    Router(profile, clock),
                    new StaticAiControlPlaneIdResolver("control-a"),
                    capacity,
                    options,
                    NullLogger<AiWorkerInvocationSupervisor>.Instance,
                    clock);
                using var cancellation = new CancellationTokenSource();
                var dispatch = supervisor.DispatchAsync(
                    DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity, cancellation.Token);

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(marker)) await Task.Delay(20, timeout.Token);
                cancellation.Cancel();

                var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(AiWorkerDispatchDisposition.CapacityQuarantined, result.Disposition);
                Assert.Equal(0, capacity.Available);
                Assert.Equal(1, capacity.Quarantined);
                var stored = await journal.GetAsync(DurableInvocationTestSupport.Scope, DurableInvocationTestSupport.Identity);
                Assert.Null(stored?.Result);
            }
            finally { try { File.Delete(marker); } catch { } }
        }

        private static AiWorkerInvocationTransportRouter Router(
            AiContainerWorkerProfile profile,
            TimeProvider timeProvider) => new(
            new AiWorkerProcessTransport(
                new AiConfiguredWorkerProcessCatalog(Array.Empty<AiWorkerProcessProfile>()), new(), timeProvider),
            new AiContainerWorkerTransport(
                new AiConfiguredContainerWorkerCatalog(new[] { profile }), new(), timeProvider));

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
