using System.Collections.Generic;
using System.Linq;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Validates strongly typed parent-container bootstrap arguments.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodCommandLineFactoryTests
    {
        /// <summary>
        /// Verifies every exact planned child identity and port.
        /// </summary>
        [Fact]
        public void Create_Should_Preserve_Exact_Child_Identities_And_Ports()
        {
            var poolOptions =
                new AiKubernetesRuntimePoolOptions
                {
                    Enabled = true,
                    PoolId = "pool-01",
                    Namespace = "ai-runtime",
                    ProviderName = "http",
                    TransportName = "http",
                    InitialRuntimeInstanceCount = 3,
                    MinimumRuntimeInstanceCount = 3,
                    MaximumRuntimeInstanceCount = 3,
                    StartupParallelism = 1,
                    StableTransportPort = 8080,
                    ReadinessPort = 8081,
                    FirstChildTransportPort = 18080
                };

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    poolOptions,
                    "request-0001",
                    "runtime-a1");

            var hostOptions =
                new AiKubernetesRuntimePoolHostOptions
                {
                    RuntimeImage = "runtime:test",
                    RedisConnectionString = "redis:6379",
                    MongoConnectionString =
                        "mongodb://mongo:27017",
                    MongoDatabaseName =
                        "multiplexed_ai_tests",
                    OpenAiApiKey =
                        "step-5d-placeholder"
                };

            var spec =
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    poolOptions,
                    hostOptions)
                    .Build(plan);

            var request =
                new AiRuntimeHostStartRequest
                {
                    RequestId = "scale-001",
                    ControlPlaneId = "cp-01",
                    PoolId = "pool-01",
                    RuntimeInstanceId = "runtime-a1",
                    ProviderName = "http",
                    TransportName = "http",
                    WorkerCountPerInstance = 3,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 0,
                    ExecutionContextSnapshot =
                        new ExecutionContextSnapshot
                        {
                            ContextKey = "ctx-01",
                            Project = "tests",
                            UserId = "user-01",
                            TenantId = "tenant-01",
                            TenantGroupId =
                                "tenant-group-01",
                            CurrentNamespace = "tests",
                            Namespaces =
                                new List<NamespaceEntry>(),
                            TtlSeconds = 3600
                        }
                };

            var arguments =
                new AiKubernetesRuntimePoolInPodCommandLineFactory(
                    hostOptions)
                    .Create(spec, request);

            Assert.Contains(
                "--AiKubernetesRuntimePoolInPod:RuntimeInstances:0:RuntimeInstanceId=runtime-a1",
                arguments);
            Assert.Contains(
                "--AiKubernetesRuntimePoolInPod:RuntimeInstances:0:TransportPort=18080",
                arguments);
            Assert.Contains(
                "--AiKubernetesRuntimePoolInPod:RuntimeInstances:1:TransportPort=18081",
                arguments);
            Assert.Contains(
                "--AiKubernetesRuntimePoolInPod:RuntimeInstances:2:TransportPort=18082",
                arguments);
            Assert.Contains(
                "--Kestrel:Endpoints:RuntimePool:Url=http://0.0.0.0:8080",
                arguments);
            Assert.Contains(
                "--Kestrel:Endpoints:RuntimePool:Protocols=Http1",
                arguments);
            Assert.Contains(
                "--Kestrel:Endpoints:Readiness:Url=http://0.0.0.0:8081",
                arguments);
            Assert.Contains(
                "--Kestrel:Endpoints:Readiness:Protocols=Http1",
                arguments);
            Assert.DoesNotContain(
                arguments,
                argument =>
                    argument.StartsWith(
                        "--ASPNETCORE_URLS=",
                        System.StringComparison.Ordinal));
            Assert.DoesNotContain(
                arguments,
                argument =>
                    argument.StartsWith(
                        "--Kestrel:EndpointDefaults:Protocols=",
                        System.StringComparison.Ordinal));
            Assert.Contains(
                "--ConnectionStrings:Redis=redis:6379",
                arguments);
            Assert.Contains(
                "--ConnectionStrings:Mongo=mongodb://mongo:27017",
                arguments);
            Assert.Contains(
                "--Mongo:DatabaseName=multiplexed_ai_tests",
                arguments);
            Assert.Contains(
                "--OpenAI:ApiKey=step-5d-placeholder",
                arguments);
            Assert.Contains(
                "--AiEngine:ControlPlane:ControlPlaneId=cp-01",
                arguments);
            Assert.Contains(
                "--AiKubernetesRuntimePoolInPod:LocalQueueCapacity=0",
                arguments);
            Assert.Equal(
                arguments.Count,
                arguments.Distinct().Count());
        }

        /// <summary>
        /// Verifies that clear-text gRPC is isolated on an HTTP/2-only stable endpoint while
        /// Kubernetes readiness remains available on a separate HTTP/1 endpoint.
        /// </summary>
        [Fact]
        public void Create_Should_Use_Http2Only_StableEndpoint_For_Grpc()
        {
            var poolOptions =
                new AiKubernetesRuntimePoolOptions
                {
                    Enabled = true,
                    PoolId = "pool-grpc-01",
                    Namespace = "ai-runtime",
                    ProviderName = "grpc",
                    TransportName = "grpc",
                    InitialRuntimeInstanceCount = 3,
                    MinimumRuntimeInstanceCount = 3,
                    MaximumRuntimeInstanceCount = 3,
                    StartupParallelism = 1,
                    StableTransportPort = 8080,
                    ReadinessPort = 8081,
                    FirstChildTransportPort = 19080
                };

            var plan =
                AiKubernetesRuntimePoolPodPlanFactory.Create(
                    poolOptions,
                    "request-grpc-0001",
                    "runtime-grpc-a1");

            var hostOptions =
                new AiKubernetesRuntimePoolHostOptions
                {
                    RuntimeImage = "runtime:test",
                    RedisConnectionString = "redis:6379",
                    MongoConnectionString =
                        "mongodb://mongo:27017",
                    MongoDatabaseName =
                        "multiplexed_ai_tests",
                    OpenAiApiKey =
                        "step-5f-placeholder"
                };

            var spec =
                new AiKubernetesRuntimePoolPodSpecBuilder(
                    poolOptions,
                    hostOptions)
                    .Build(plan);

            var request =
                new AiRuntimeHostStartRequest
                {
                    RequestId = "scale-grpc-001",
                    ControlPlaneId = "cp-grpc-01",
                    PoolId = "pool-grpc-01",
                    RuntimeInstanceId = "runtime-grpc-a1",
                    ProviderName = "grpc",
                    TransportName = "grpc",
                    WorkerCountPerInstance = 3,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 100,
                    ExecutionContextSnapshot =
                        new ExecutionContextSnapshot
                        {
                            ContextKey = "ctx-grpc-01",
                            Project = "tests",
                            UserId = "user-grpc-01",
                            TenantId = "tenant-grpc-01",
                            TenantGroupId =
                                "tenant-group-grpc-01",
                            CurrentNamespace = "tests",
                            Namespaces =
                                new List<NamespaceEntry>(),
                            TtlSeconds = 3600
                        }
                };

            var arguments =
                new AiKubernetesRuntimePoolInPodCommandLineFactory(
                    hostOptions)
                    .Create(spec, request);

            Assert.Contains(
                "--Kestrel:Endpoints:RuntimePool:Protocols=Http2",
                arguments);
            Assert.Contains(
                "--Kestrel:Endpoints:Readiness:Protocols=Http1",
                arguments);
            Assert.Contains(
                "--Kestrel:Endpoints:Readiness:Url=http://0.0.0.0:8081",
                arguments);
        }

    }
}
