using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Validates exact initial identities inside one Pod.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodManagerTests
    {
        /// <summary>
        /// Verifies exact Pod UID and planned initial runtime identities.
        /// </summary>
        [Fact]
        public async Task EnsureInitialCapacityAsync_Should_Use_PodUid_And_Exact_Planned_RuntimeIds()
        {
            var manager =
                new AiKubernetesRuntimePoolInPodManager(
                    CreateOptions(),
                    "pod-uid-123",
                    new RecordingChildFactory());

            var snapshot =
                await manager.EnsureInitialCapacityAsync();

            Assert.Equal("pod-uid-123", snapshot.HostId);
            Assert.Collection(
                snapshot.Children,
                first =>
                    Assert.Equal(
                        "runtime-a1",
                        first.RuntimeInstanceId),
                second =>
                    Assert.Equal(
                        "runtime-a2",
                        second.RuntimeInstanceId),
                third =>
                    Assert.Equal(
                        "runtime-a3",
                        third.RuntimeInstanceId));
            Assert.Equal(
                AiRuntimeProcessPoolManagerStatus.Running,
                snapshot.Status);

            await manager.StopAsync();
        }

        /// <summary>
        /// Verifies that zero local queue capacity remains a valid direct-execution policy.
        /// </summary>
        [Fact]
        public void Validate_Should_Accept_Zero_LocalQueueCapacity()
        {
            var options =
                CreateOptions();

            options.LocalQueueCapacity = 0;

            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(
                options,
                requirePodUidFile: false);
        }

        /// <summary>
        /// Verifies that the shared process-pool child validator also accepts
        /// zero local queue capacity for direct execution.
        /// </summary>
        [Fact]
        public void RuntimeInstanceOptionsValidator_Should_Accept_Zero_LocalQueueCapacity()
        {
            var options =
                new AiRuntimeProcessPoolRuntimeInstanceOptions
                {
                    RuntimeHostAssemblyPath =
                        "Multiplexed.AI.McpServer.Host.dll",
                    ControlPlaneId = "cp-01",
                    LocalQueueCapacity = 0,
                    ExecutionContextSnapshot =
                        new ExecutionContextSnapshot
                        {
                            ContextKey = "ctx-01",
                            TenantGroupId = "tg-01",
                            Project = "tests",
                            UserId = "user-01",
                            TenantId = "tenant-01",
                            CurrentNamespace = "tests",
                            Namespaces =
                                new List<NamespaceEntry>(),
                            TtlSeconds = 3600
                        }
                };

            AiRuntimeProcessPoolRuntimeInstanceOptionsValidator.Validate(
                options);
        }

        /// <summary>
        /// Creates valid in-Pod options.
        /// </summary>
        private static AiKubernetesRuntimePoolInPodOptions CreateOptions()
        {
            var options =
                new AiKubernetesRuntimePoolInPodOptions
                {
                    Enabled = true,
                    PoolId = "pool-01",
                    ControlPlaneId = "cp-01",
                    ProviderName = "http",
                    TransportName = "http",
                    InitialProcessCount = 3,
                    MinimumProcessCount = 3,
                    MaximumProcessCount = 3,
                    StartupParallelism = 1,
                    ContextKey = "ctx-01",
                    Project = "tests",
                    UserId = "user-01",
                    TenantId = "tenant-01",
                    CurrentNamespace = "tests"
                };

            options.RuntimeInstances.Add(
                new()
                {
                    Ordinal = 1,
                    RuntimeInstanceId = "runtime-a1",
                    TransportPort = 18080
                });
            options.RuntimeInstances.Add(
                new()
                {
                    Ordinal = 2,
                    RuntimeInstanceId = "runtime-a2",
                    TransportPort = 18081
                });
            options.RuntimeInstances.Add(
                new()
                {
                    Ordinal = 3,
                    RuntimeInstanceId = "runtime-a3",
                    TransportPort = 18082
                });

            return options;
        }

        /// <summary>
        /// Starts deterministic in-memory child handles.
        /// </summary>
        private sealed class RecordingChildFactory :
            IAiRuntimeProcessPoolChildFactory
        {
            /// <inheritdoc />
            public Task<IAiRuntimeProcessPoolChild> StartAsync(
                AiRuntimeProcessPoolChildStartRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<
                    IAiRuntimeProcessPoolChild>(
                        new RecordingChild(request));
            }
        }

        /// <summary>
        /// Represents one deterministic running child.
        /// </summary>
        private sealed class RecordingChild :
            IAiRuntimeProcessPoolChild
        {
            private readonly TaskCompletionSource<
                AiRuntimeProcessPoolChildExit> completion =
                    new(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously);

            /// <summary>
            /// Initializes the child.
            /// </summary>
            public RecordingChild(
                AiRuntimeProcessPoolChildStartRequest request)
            {
                this.PoolId = request.PoolId;
                this.HostId = request.HostId;
                this.RuntimeInstanceId =
                    request.RuntimeInstanceId;
                this.Ordinal = request.Ordinal;
            }

            /// <inheritdoc />
            public string PoolId { get; }

            /// <inheritdoc />
            public string HostId { get; }

            /// <inheritdoc />
            public string RuntimeInstanceId { get; }

            /// <inheritdoc />
            public int Ordinal { get; }

            /// <inheritdoc />
            public AiRuntimeProcessPoolChildStatus Status
            {
                get;
                private set;
            } = AiRuntimeProcessPoolChildStatus.Running;

            /// <inheritdoc />
            public Task<AiRuntimeProcessPoolChildExit> Completion =>
                this.completion.Task;

            /// <inheritdoc />
            public Task StopAsync(
                CancellationToken cancellationToken = default)
            {
                this.Status =
                    AiRuntimeProcessPoolChildStatus.Stopped;

                this.completion.TrySetResult(
                    new AiRuntimeProcessPoolChildExit
                    {
                        Kind =
                            AiRuntimeProcessPoolChildExitKind
                                .Requested
                    });

                return Task.CompletedTask;
            }
        }
    }
}
