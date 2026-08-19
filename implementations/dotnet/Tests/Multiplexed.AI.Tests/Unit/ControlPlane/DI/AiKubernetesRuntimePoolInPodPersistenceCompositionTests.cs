using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.DI
{
    /// <summary>
    /// Validates durable Process Host parity projected into Kubernetes Runtime Pool children.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodPersistenceCompositionTests
    {
        /// <summary>
        /// Verifies that every in-Pod RuntimeInstanceOnly child receives the same durable
        /// snapshot, payload, ledger, replay metadata, and trace settings as Process Host children.
        /// </summary>
        [Fact]
        public async Task Add_Should_Project_Complete_Durable_ProcessHost_Profile_To_Children()
        {
            var podUidFilePath = Path.GetTempFileName();

            try
            {
                await File.WriteAllTextAsync(
                    podUidFilePath,
                    "pod-uid-01");

                var services = new ServiceCollection();

                services.AddSingleton<IAiRuntimeProcessPoolPortAllocator>(
                    new FixedPortAllocator(19080));

                services.AddAiKubernetesRuntimePoolInPod(
                    CreateOptions(podUidFilePath));

                using var serviceProvider =
                    services.BuildServiceProvider();

                var factory =
                    serviceProvider.GetRequiredService<
                        IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory>();

                var plan =
                    await factory.CreateAsync(
                        new AiRuntimeProcessPoolChildStartRequest
                        {
                            PoolId = "pool-01",
                            HostId = "pod-uid-01",
                            RuntimeInstanceId = "runtime-01",
                            Ordinal = 1
                        });

                try
                {
                    var environment =
                        plan.ProcessOptions.EnvironmentVariables;

                    Assert.Equal(
                        "127.0.0.1:6379",
                        environment["ConnectionStrings__Redis"]);
                    Assert.Equal(
                        "mongodb://127.0.0.1:27017",
                        environment["ConnectionStrings__Mongo"]);
                    Assert.Equal(
                        "multiplexed-ai",
                        environment["Mongo__DatabaseName"]);

                    Assert.Equal(
                        "true",
                        environment["AiPayloadStore__Enabled"]);
                    Assert.Equal(
                        "mongo-redis",
                        environment["AiPayloadStore__Provider"]);
                    Assert.Equal(
                        "true",
                        environment[
                            "AiPayloadStore__RequireReplaySafePayloads"]);

                    Assert.Equal(
                        "true",
                        environment["AiEngine__PayloadStore__Enabled"]);
                    Assert.Equal(
                        "mongo-redis",
                        environment["AiEngine__PayloadStore__Provider"]);
                    Assert.Equal(
                        "true",
                        environment[
                            "AiEngine__PayloadStore__RequireReplaySafePayloads"]);

                    Assert.Equal(
                        "true",
                        environment["AiEngine__Payloads__Enabled"]);
                    Assert.Equal(
                        "mongo-redis",
                        environment["AiEngine__Payloads__Provider"]);
                    Assert.Equal(
                        "true",
                        environment[
                            "AiEngine__Payloads__RequireReplaySafePayloads"]);

                    Assert.Equal(
                        "mongo",
                        environment["AiDecisionLedger__Provider"]);
                    Assert.Equal(
                        "mongo",
                        environment[
                            "AiObservability__Ledger__Provider"]);

                    Assert.Equal(
                        "true",
                        environment["AiEngine__Snapshots__Enabled"]);
                    Assert.Equal(
                        "true",
                        environment[
                            "AiEngine__Snapshots__Mongo__Enabled"]);
                    Assert.Equal(
                        "mongodb://127.0.0.1:27017",
                        environment[
                            "AiEngine__Snapshots__Mongo__ConnectionString"]);
                    Assert.Equal(
                        "multiplexed-ai",
                        environment[
                            "AiEngine__Snapshots__Mongo__DatabaseName"]);

                    Assert.Equal(
                        "mongo",
                        environment[
                            "AiExecutionReplay__MetadataStore__Provider"]);
                    Assert.Equal(
                        "ai_execution_replay_metadata",
                        environment[
                            "AiExecutionReplay__MetadataStore__Mongo__CollectionName"]);

                    Assert.Equal(
                        "true",
                        environment[
                            "AiEngine__Observability__EnableTracing"]);
                    Assert.Equal(
                        "true",
                        environment[
                            "AiEngine__Observability__EnableInMemoryRecording"]);
                    Assert.Equal(
                        "Mongo",
                        environment[
                            "AiEngine__Observability__Tracing__Mode"]);
                    Assert.Equal(
                        "ai_runtime_traces",
                        environment[
                            "AiEngine__Observability__Tracing__MongoCollectionName"]);

                    Assert.Equal(
                        "true",
                        environment[
                            "AiChildDagComposition__Enabled"]);

                    Assert.Equal(
                        "ai-runtime",
                        environment[
                            "AiRuntimeInstanceRegistration__ProviderMetadata__kubernetes.namespace"]);
                    Assert.Equal(
                        "runtime-pool-pod-01",
                        environment[
                            "AiRuntimeInstanceRegistration__ProviderMetadata__kubernetes.pod.name"]);
                    Assert.Equal(
                        "minikube",
                        environment[
                            "AiRuntimeInstanceRegistration__ProviderMetadata__kubernetes.node.name"]);

                    Assert.Equal(
                        "kubernetes",
                        environment[
                            "AiRuntimeInstanceRegistration__Metadata__host.provider"]);

                    Assert.False(
                        plan.ProcessOptions.RedirectOutput);
                }
                finally
                {
                    await plan.PortLease.DisposeAsync();
                }
            }
            finally
            {
                File.Delete(podUidFilePath);
            }
        }

        private static AiKubernetesRuntimePoolInPodOptions CreateOptions(
            string podUidFilePath)
        {
            var options =
                new AiKubernetesRuntimePoolInPodOptions
                {
                    Enabled = true,
                    PoolId = "pool-01",
                    PodUidFilePath = podUidFilePath,
                    KubernetesNamespace = "ai-runtime",
                    KubernetesPodName = "runtime-pool-pod-01",
                    KubernetesNodeName = "minikube",
                    RuntimeInstanceIdPrefix = "runtime",
                    ControlPlaneId = "control-plane-01",
                    ProviderName = "grpc",
                    TransportName = "grpc",
                    InitialProcessCount = 1,
                    MinimumProcessCount = 1,
                    MaximumProcessCount = 1,
                    StartupParallelism = 1,
                    DotnetExecutablePath = "dotnet",
                    RuntimeHostAssemblyPath = "runtime-host.dll",
                    WorkingDirectory = ".",
                    EndpointHost = "127.0.0.1",
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 1,
                    LocalQueueCapacity = 2,
                    RedisConnectionString = "127.0.0.1:6379",
                    MongoConnectionString = "mongodb://127.0.0.1:27017",
                    MongoDatabaseName = "multiplexed-ai",
                    ContextKey = "context-01",
                    Project = "runtime-pool-tests",
                    UserId = "system",
                    TenantId = "tenant-01",
                    TenantGroupId = "tenant-group-01",
                    CurrentNamespace = "default"
                };

            options.ChildEnvironmentVariables[
                "AiChildDagComposition__Enabled"] = "true";

            /*
             * Simulate ProcessHost-oriented settings projected by the outer Kubernetes
             * control plane. These inherited values must never replace the in-Pod
             * authoritative durable endpoints or physical Kubernetes identity.
             */
            options.ChildEnvironmentVariables[
                "ConnectionStrings__Redis"] = "localhost:6379";
            options.ChildEnvironmentVariables[
                "ConnectionStrings__Mongo"] = "mongodb://localhost:27017";
            options.ChildEnvironmentVariables[
                "Mongo__DatabaseName"] = "wrong-processhost-database";
            options.ChildEnvironmentVariables[
                "AiEngine__Snapshots__Mongo__ConnectionString"] =
                "mongodb://localhost:27017";
            options.ChildEnvironmentVariables[
                "AiEngine__Snapshots__Mongo__DatabaseName"] =
                "wrong-processhost-database";
            options.ChildEnvironmentVariables[
                "AiRuntimeInstanceRegistration__ProviderMetadata__kubernetes.namespace"] =
                "wrong-namespace";
            options.ChildEnvironmentVariables[
                "AiRuntimeInstanceRegistration__ProviderMetadata__kubernetes.pod.name"] =
                "wrong-pod";
            options.ChildEnvironmentVariables[
                "AiRuntimeInstanceRegistration__Metadata__host.provider"] =
                "process";

            options.RuntimeInstances.Add(
                new AiKubernetesRuntimePoolInPodRuntimeInstanceOptions
                {
                    Ordinal = 1,
                    RuntimeInstanceId = "runtime-01",
                    TransportPort = 19080
                });

            return options;
        }

        private sealed class FixedPortAllocator :
            IAiRuntimeProcessPoolPortAllocator
        {
            private readonly int port;

            public FixedPortAllocator(
                int port)
            {
                this.port = port;
            }

            public Task<IAiRuntimeProcessPoolPortLease> ReserveAsync(
                int basePort,
                int maxPort,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IAiRuntimeProcessPoolPortLease>(
                    new FixedPortLease(this.port));
            }
        }

        private sealed class FixedPortLease :
            IAiRuntimeProcessPoolPortLease
        {
            public FixedPortLease(
                int port)
            {
                this.Port = port;
            }

            public int Port { get; }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
