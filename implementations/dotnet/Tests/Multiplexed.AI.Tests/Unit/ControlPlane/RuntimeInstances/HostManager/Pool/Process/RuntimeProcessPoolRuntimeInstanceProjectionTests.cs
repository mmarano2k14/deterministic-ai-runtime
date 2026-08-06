using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Validates RuntimeInstanceOnly process projection and authoritative identity configuration.
    /// </summary>
    public sealed class RuntimeProcessPoolRuntimeInstanceProjectionTests
    {
        /// <summary>
        /// Verifies exact RuntimeInstanceOnly launch, first-class identities, and readiness routing.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Project_Authoritative_RuntimeInstanceOnly_Configuration()
        {
            var portAllocator = new FakePortAllocator(5931);
            var options = CreateOptions();

            options.EnvironmentVariables["AiMcpHost__Mode"] = "wrong-mode";
            options.EnvironmentVariables["AiRuntimeInstanceRegistration__PoolId"] = "wrong-pool";
            options.EnvironmentVariables["AiRuntimeInstanceRegistration__HostId"] = "wrong-host";
            options.EnvironmentVariables[
                "AiRuntimeInstanceRegistration__Metadata__hostType"] =
                "runtime-instance-kubernetes-pool";
            options.EnvironmentVariables[
                "AiRuntimeInstanceRegistration__Metadata__deployment"] =
                "kubernetes-pool";

            var factory =
                new AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory(
                    options,
                    portAllocator);

            var request = CreateRequest();
            var plan = await factory.CreateAsync(request);
            var environment = plan.ProcessOptions.EnvironmentVariables;

            Assert.Equal("RuntimeInstanceOnly", environment["AiMcpHost__Mode"]);
            Assert.Equal(request.PoolId, environment["AiRuntimeInstanceRegistration__PoolId"]);
            Assert.Equal(request.HostId, environment["AiRuntimeInstanceRegistration__HostId"]);
            Assert.Equal(request.RuntimeInstanceId, environment["AiRuntimeInstanceRegistration__RuntimeInstanceId"]);
            Assert.Equal("http://127.0.0.1:5931", plan.TransportEndpoint);
            Assert.Equal(plan.TransportEndpoint, plan.ReadinessRequest.TransportEndpoint);
            Assert.Equal(request.RuntimeInstanceId, plan.ReadinessRequest.RuntimeInstanceId);
            Assert.True(plan.ReadinessRequest.RequireExactRuntimeInstanceId);
            Assert.True(plan.ReadinessRequest.RequireTransportEndpoint);
            Assert.Equal(
                "runtime-instance-kubernetes-pool",
                environment[
                    "AiRuntimeInstanceRegistration__Metadata__hostType"]);
            Assert.Equal(
                "kubernetes-pool",
                environment[
                    "AiRuntimeInstanceRegistration__Metadata__deployment"]);
            Assert.Equal(
                "True",
                environment["AiEngine__ControlPlane__EnableDiscovery"]);
            Assert.Equal(
                "True",
                environment["AiEngine__ControlPlane__RequireDiscovery"]);
            Assert.Equal(
                "false",
                environment["AiRuntimeProcessPool__Enabled"]);
            Assert.Equal(
                "false",
                environment["AiKubernetesRuntimePoolInPod__Enabled"]);
            Assert.DoesNotContain(
                environment.Keys,
                key => key.Contains("Metadata__pool", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                environment.Keys,
                key => key.Contains("Metadata__host.id", StringComparison.OrdinalIgnoreCase));

            await plan.PortLease.DisposeAsync();
            Assert.True(portAllocator.LeaseReleased);
        }

        /// <summary>
        /// Verifies that a child binds and is probed locally while publishing the stable parent
        /// Runtime Pool router endpoint to remote control planes.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Separate_Local_Route_From_Published_Pool_Endpoint()
        {
            var portAllocator = new FakePortAllocator(6031);
            var options = CreateOptions();
            options.PublishedTransportEndpoint =
                "http://127.0.0.1:7100/";

            var factory =
                new AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory(
                    options,
                    portAllocator);

            var plan =
                await factory.CreateAsync(
                    CreateRequest());

            var environment =
                plan.ProcessOptions.EnvironmentVariables;

            Assert.Equal(
                "http://127.0.0.1:6031",
                plan.TransportEndpoint);
            Assert.Equal(
                plan.TransportEndpoint,
                plan.ReadinessRequest.TransportEndpoint);
            Assert.Equal(
                "http://127.0.0.1:7100",
                environment[
                    "AiRuntimeInstanceRegistration__ProviderMetadata__transport.endpoint"]);
            Assert.Equal(
                "http://127.0.0.1:7100",
                environment[
                    "AiRuntimeInstanceRegistration__Metadata__transport.endpoint"]);
            Assert.Equal(
                "http://127.0.0.1:6031",
                environment["ASPNETCORE_URLS"]);

            await plan.PortLease.DisposeAsync();
        }

        /// <summary>
        /// Verifies that a gRPC child receives authoritative HTTP/2 Kestrel settings on its exact
        /// allocated endpoint.
        /// </summary>
        [Fact]
        public async Task CreateAsync_Should_Project_Grpc_Http2_Endpoint()
        {
            var portAllocator =
                new FakePortAllocator(6231);

            var options =
                CreateOptions();

            options.ProviderName = "grpc";
            options.TransportName = "grpc";

            var factory =
                new AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory(
                    options,
                    portAllocator);

            var plan =
                await factory.CreateAsync(
                    CreateRequest());

            var environment =
                plan.ProcessOptions.EnvironmentVariables;

            Assert.Equal(
                "grpc",
                plan.ReadinessRequest.TransportName);

            Assert.Equal(
                "http://127.0.0.1:6231",
                plan.TransportEndpoint);

            Assert.Equal(
                "Http2",
                environment[
                    "Kestrel__EndpointDefaults__Protocols"]);

            Assert.Equal(
                plan.TransportEndpoint,
                environment[
                    "Kestrel__Endpoints__Grpc__Url"]);

            Assert.Equal(
                "Http2",
                environment[
                    "Kestrel__Endpoints__Grpc__Protocols"]);

            await plan.PortLease.DisposeAsync();
        }

        /// <summary>
        /// Creates valid RuntimeInstanceOnly options for focused projection tests.
        /// </summary>
        internal static AiRuntimeProcessPoolRuntimeInstanceOptions CreateOptions()
        {
            return new AiRuntimeProcessPoolRuntimeInstanceOptions
            {
                DotnetExecutablePath = "dotnet",
                RuntimeHostAssemblyPath = "runtime-host.dll",
                BasePort = 5931,
                MaxPort = 5931,
                EndpointHost = "127.0.0.1",
                ControlPlaneId = "control-plane-01",
                ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                ProviderName = "http",
                TransportName = "http",
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 8,
                StartupTimeout = TimeSpan.FromSeconds(10),
                ReadinessPollInterval = TimeSpan.FromMilliseconds(50)
            };
        }

        /// <summary>
        /// Creates one authoritative process-pool child start request.
        /// </summary>
        internal static AiRuntimeProcessPoolChildStartRequest CreateRequest()
        {
            return new AiRuntimeProcessPoolChildStartRequest
            {
                PoolId = "pool-01",
                HostId = "pool-host-incarnation-01",
                RuntimeInstanceId = "runtime-a1",
                Ordinal = 1
            };
        }

        /// <summary>
        /// Creates the execution context carried through readiness.
        /// </summary>
        internal static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "runtime-pool-readiness",
                Project = "runtime-pool-tests",
                UserId = "system",
                TenantId = "tenant-a",
                TenantGroupId = "group-a",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>()
            };
        }

        /// <summary>
        /// Provides one deterministic port lease for projection tests.
        /// </summary>
        internal sealed class FakePortAllocator : IAiRuntimeProcessPoolPortAllocator
        {
            private readonly int port;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakePortAllocator"/> class.
            /// </summary>
            public FakePortAllocator(
                int port)
            {
                this.port = port;
            }

            /// <summary>
            /// Gets a value indicating whether the lease was released.
            /// </summary>
            public bool LeaseReleased { get; private set; }

            /// <inheritdoc />
            public Task<IAiRuntimeProcessPoolPortLease> ReserveAsync(
                int basePort,
                int maxPort,
                CancellationToken cancellationToken = default)
            {
                IAiRuntimeProcessPoolPortLease lease =
                    new FakePortLease(
                        this.port,
                        () => this.LeaseReleased = true);

                return Task.FromResult(lease);
            }
        }

        /// <summary>
        /// Provides one deterministic port lease.
        /// </summary>
        internal sealed class FakePortLease : IAiRuntimeProcessPoolPortLease
        {
            private readonly Action release;
            private int disposed;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakePortLease"/> class.
            /// </summary>
            public FakePortLease(
                int port,
                Action release)
            {
                this.Port = port;
                this.release = release;
            }

            /// <inheritdoc />
            public int Port { get; }

            /// <inheritdoc />
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                {
                    this.release();
                }

                return ValueTask.CompletedTask;
            }
        }
    }
}
