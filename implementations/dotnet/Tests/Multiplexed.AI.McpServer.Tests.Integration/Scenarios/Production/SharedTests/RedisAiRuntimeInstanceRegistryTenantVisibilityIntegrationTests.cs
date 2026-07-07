using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Auth;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Isolation;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    /// <summary>
    /// Provides Redis tenant-visibility tests for runtime instance registry and capacity stores.
    /// </summary>
    public sealed class RedisAiRuntimeInstanceStoreTenantVisibilitySharedTests :
        IAsyncLifetime
    {
        private readonly IConnectionMultiplexer redis;
        private readonly IDatabase database;

        /// <summary>
        /// Initializes a new instance of the <see cref="RedisAiRuntimeInstanceStoreTenantVisibilitySharedTests"/> class.
        /// </summary>
        public RedisAiRuntimeInstanceStoreTenantVisibilitySharedTests()
        {
            this.redis =
                ConnectionMultiplexer.Connect(
                    Environment.GetEnvironmentVariable("MULTIPLEXED_TEST_REDIS") ??
                    Environment.GetEnvironmentVariable("REDIS_CONNECTION_STRING") ??
                    "localhost:6379,abortConnect=false");

            this.database =
                this.redis.GetDatabase();
        }

        /// <inheritdoc />
        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task DisposeAsync()
        {
            await this.redis.CloseAsync().ConfigureAwait(false);
            await this.redis.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that a tenant-scoped Redis registry reader can list a tenant runtime written to the same control-plane scope.
        /// </summary>
        [Fact]
        public async Task Redis_RuntimeInstanceRegistry_Should_List_Tenant_Runtime_From_Request_Scoped_Context()
        {
            var controlPlaneId =
                "redis-registry-tenant-visibility-" + Guid.NewGuid().ToString("N");

            var runtimeInstanceId =
                "host-a:mcp-runtime-1";

            var tenantId =
                "tenant-dedicated-single";

            var tenantGroupId =
                "tenant-mode-group-dedicated-single";

            await this.DeleteRuntimeRegistryKeysAsync(
                    controlPlaneId,
                    runtimeInstanceId)
                .ConfigureAwait(false);

            var writer =
                CreateRegistry(
                    this.redis,
                    controlPlaneId,
                    executionContextSnapshot: null);

            await writer
                .RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId,
                        tenantGroupId))
                .ConfigureAwait(false);

            await writer
                .HeartbeatAsync(
                    runtimeInstanceId,
                    queuedRunCount: 0,
                    runningRunCount: 0,
                    activeRunCount: 0,
                    availableRunSlots: 5,
                    activeWorkerCount: 0,
                    availableWorkerCount: 10,
                    maxLocalWorkersPerExecution: null,
                    isQueuePaused: false,
                    canAcceptRun: true,
                    status: AiRuntimeInstanceStatus.Ready)
                .ConfigureAwait(false);

            var requestSnapshot =
                McpRbacTestContextFactory.CreateDefaultSnapshot(
                    tenantId: tenantId,
                    tenantGroupId: tenantGroupId);

            var reader =
                CreateRegistry(
                    this.redis,
                    controlPlaneId,
                    requestSnapshot);

            var snapshots =
                await reader
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            var runtime =
                Assert.Single(
                    snapshots,
                    snapshot => snapshot.RuntimeInstanceId == runtimeInstanceId);

            Assert.Equal(controlPlaneId, runtime.ControlPlaneId);
            Assert.Equal(tenantId, runtime.TenantId);
            Assert.Equal(tenantGroupId, runtime.TenantGroupId);
            Assert.Equal(AiRuntimeInstanceRole.Runtime, runtime.Role);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, runtime.Status);
            Assert.True(runtime.CanAcceptRun);
            Assert.Equal(5, runtime.AvailableRunSlots);
        }

        /// <summary>
        /// Verifies that a tenant-scoped Redis capacity reader can list tenant capacity written to the same control-plane scope.
        /// </summary>
        [Fact]
        public async Task Redis_RuntimeInstanceCapacityStore_Should_List_Tenant_Capacity_From_Request_Scoped_Context()
        {
            var controlPlaneId =
                "redis-capacity-tenant-visibility-" + Guid.NewGuid().ToString("N");

            var runtimeInstanceId =
                "host-a:mcp-runtime-1";

            var tenantId =
                "tenant-dedicated-single";

            var tenantGroupId =
                "tenant-mode-group-dedicated-single";

            await this.DeleteRuntimeCapacityKeysAsync(
                    controlPlaneId,
                    runtimeInstanceId)
                .ConfigureAwait(false);

            var writer =
                CreateCapacityStore(
                    this.redis,
                    controlPlaneId,
                    executionContextSnapshot: null);

            await writer
                .PublishAsync(
                    CreateCapacityDescriptor(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId,
                        tenantGroupId))
                .ConfigureAwait(false);

            var requestSnapshot =
                McpRbacTestContextFactory.CreateDefaultContext(
                    tenantId: tenantId,
                    tenantGroupId: tenantGroupId);


            var reader =
                CreateCapacityStore(
                    this.redis,
                    controlPlaneId,
                    McpRbacTestContextFactory.MapToSnapshot(requestSnapshot));

            var descriptors =
                await reader
                    .ListAsync()
                    .ConfigureAwait(false);

            var descriptor =
                Assert.Single(
                    descriptors,
                    item => item.RuntimeInstanceId == runtimeInstanceId);

            Assert.Equal(controlPlaneId, descriptor.ControlPlaneId);
            Assert.Equal(tenantId, descriptor.TenantId);
            Assert.Equal(tenantGroupId, descriptor.TenantGroupId);
            Assert.Equal(AiRuntimeInstanceRole.Runtime, descriptor.Role);
            Assert.Equal(AiRuntimeInstanceStatus.Ready, descriptor.Status);
            Assert.True(descriptor.CanAcceptRun);
            Assert.Equal(5, descriptor.AvailableRunSlots);
            Assert.Equal(10, descriptor.AvailableWorkerCount);
        }

        /// <summary>
        /// Verifies that a runtime registered for another tenant is not visible to a tenant-scoped Redis registry reader.
        /// </summary>
        [Fact]
        public async Task Redis_RuntimeInstanceRegistry_Should_Not_List_Runtime_From_Another_Tenant()
        {
            var controlPlaneId =
                "redis-registry-cross-tenant-visibility-" + Guid.NewGuid().ToString("N");

            var runtimeInstanceId =
                "host-a:mcp-runtime-1";

            await this.DeleteRuntimeRegistryKeysAsync(
                    controlPlaneId,
                    runtimeInstanceId)
                .ConfigureAwait(false);

            var writer =
                CreateRegistry(
                    this.redis,
                    controlPlaneId,
                    executionContextSnapshot: null);

            await writer
                .RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-b",
                        tenantGroupId: "tenant-group-b"))
                .ConfigureAwait(false);

            await writer
                .HeartbeatAsync(
                    runtimeInstanceId,
                    queuedRunCount: 0,
                    runningRunCount: 0,
                    activeRunCount: 0,
                    availableRunSlots: 5,
                    activeWorkerCount: 0,
                    availableWorkerCount: 10,
                    maxLocalWorkersPerExecution: null,
                    isQueuePaused: false,
                    canAcceptRun: true,
                    status: AiRuntimeInstanceStatus.Ready)
                .ConfigureAwait(false);

            var requestSnapshot =
                McpRbacTestContextFactory.CreateDefaultContext(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");

            var reader =
                CreateRegistry(
                    this.redis,
                    controlPlaneId,
                    McpRbacTestContextFactory.MapToSnapshot(requestSnapshot));

            var snapshots =
                await reader
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            Assert.DoesNotContain(
                snapshots,
                snapshot => snapshot.RuntimeInstanceId == runtimeInstanceId);
        }

        /// <summary>
        /// Verifies that capacity published for another tenant is not visible to a tenant-scoped Redis capacity reader.
        /// </summary>
        [Fact]
        public async Task Redis_RuntimeInstanceCapacityStore_Should_Not_List_Capacity_From_Another_Tenant()
        {
            var controlPlaneId =
                "redis-capacity-cross-tenant-visibility-" + Guid.NewGuid().ToString("N");

            var runtimeInstanceId =
                "host-a:mcp-runtime-1";

            await this.DeleteRuntimeCapacityKeysAsync(
                    controlPlaneId,
                    runtimeInstanceId)
                .ConfigureAwait(false);

            var writer =
                CreateCapacityStore(
                    this.redis,
                    controlPlaneId,
                    executionContextSnapshot: null);

            await writer
                .PublishAsync(
                    CreateCapacityDescriptor(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-b",
                        tenantGroupId: "tenant-group-b"))
                .ConfigureAwait(false);

            var requestSnapshot =
                McpRbacTestContextFactory.CreateDefaultContext(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");

            var reader =
                CreateCapacityStore(
                    this.redis,
                    controlPlaneId,
                    McpRbacTestContextFactory.MapToSnapshot(requestSnapshot));

            var descriptors =
                await reader
                    .ListAsync()
                    .ConfigureAwait(false);

            Assert.DoesNotContain(
                descriptors,
                descriptor => descriptor.RuntimeInstanceId == runtimeInstanceId);
        }

        /// <summary>
        /// Creates a Redis runtime instance registry.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="executionContextSnapshot">The optional tenant execution context snapshot.</param>
        /// <returns>The Redis runtime instance registry.</returns>
        private static RedisAiRuntimeInstanceRegistry CreateRegistry(
            IConnectionMultiplexer redis,
            string controlPlaneId,
            ExecutionContextSnapshot? executionContextSnapshot)
        {
            return new RedisAiRuntimeInstanceRegistry(
                redis,
                CreateRegistrationOptions(),
                new FixedAiControlPlaneIdResolver(controlPlaneId),
                CreateVisibilityEvaluator(),
                executionContextSnapshot is null
                    ? null
                    : new FixedExecutionContextSnapshotProvider(executionContextSnapshot));
        }

        /// <summary>
        /// Creates a Redis runtime instance capacity store.
        /// </summary>
        /// <param name="redis">The Redis connection multiplexer.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="executionContextSnapshot">The optional tenant execution context snapshot.</param>
        /// <returns>The Redis runtime instance capacity store.</returns>
        private static RedisAiRuntimeInstanceCapacityStore CreateCapacityStore(
            IConnectionMultiplexer redis,
            string controlPlaneId,
            ExecutionContextSnapshot? executionContextSnapshot)
        {
            return new RedisAiRuntimeInstanceCapacityStore(
                redis,
                CreateRegistrationOptions(),
                new FixedAiControlPlaneIdResolver(controlPlaneId),
                CreateVisibilityEvaluator(),
                executionContextSnapshot is null
                    ? null
                    : new FixedExecutionContextSnapshotProvider(executionContextSnapshot));
        }

        /// <summary>
        /// Creates runtime registration options for Redis-backed runtime instance stores.
        /// </summary>
        /// <returns>The runtime instance registration options.</returns>
        private static IOptions<AiRuntimeInstanceRegistrationOptions> CreateRegistrationOptions()
        {
            return Options.Create(
                new AiRuntimeInstanceRegistrationOptions
                {
                    RegistryTtl = TimeSpan.FromMinutes(5),
                    CapacityTtl = TimeSpan.FromMinutes(5)
                });
        }

        /// <summary>
        /// Creates the tenant visibility evaluator.
        /// </summary>
        /// <returns>The visibility evaluator.</returns>
        private static IAiRuntimeInstanceVisibilityEvaluator CreateVisibilityEvaluator()
        {
            return new AiRuntimeInstanceVisibilityEvaluator(
                new HardcodedAiTenantRuntimeSettingsProvider());
        }

        /// <summary>
        /// Creates a runtime instance registration.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="tenantGroupId">The tenant group id.</param>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration(
            string runtimeInstanceId,
            string controlPlaneId,
            string tenantId,
            string tenantGroupId)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                ControlPlaneId = controlPlaneId,
                ControlPlaneHostId = "control-plane-host-a",
                HostId = "host-a",
                RuntimeId = "mcp-runtime-1",
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                Role = AiRuntimeInstanceRole.Runtime,
                WorkerCount = 10,
                MaxConcurrentRuns = 5,
                QueueCapacity = 1000,
                Metadata = CreateMetadata(
                    controlPlaneId,
                    tenantId,
                    tenantGroupId),
                RegisteredAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Creates a runtime capacity descriptor.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="tenantGroupId">The tenant group id.</param>
        /// <returns>The runtime capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateCapacityDescriptor(
            string runtimeInstanceId,
            string controlPlaneId,
            string tenantId,
            string tenantGroupId)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                ControlPlaneId = controlPlaneId,
                ControlPlaneHostId = "control-plane-host-a",
                TenantId = tenantId,
                TenantGroupId = tenantGroupId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 10,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 10,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                MaxConcurrentRuns = 5,
                MaxRunSlots = 5,
                AvailableRunSlots = 5,
                ReservedRunSlots = 0,
                EffectiveAvailableRunSlots = 5,
                IsQueuePaused = false,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = CreateMetadata(
                    controlPlaneId,
                    tenantId,
                    tenantGroupId)
            };
        }

        /// <summary>
        /// Creates metadata used for tenant, control-plane, provider, and transport matching.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="tenantGroupId">The tenant group id.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            string controlPlaneId,
            string tenantId,
            string tenantGroupId)
        {
            return new Dictionary<string, string>
            {
                ["provider.name"] = "grpc",
                ["provider"] = "grpc",
                ["transport.name"] = "grpc",
                ["controlPlaneId"] = controlPlaneId,
                ["control-plane.id"] = controlPlaneId,
                ["controlplane.id"] = controlPlaneId,
                ["runtime.controlPlaneId"] = controlPlaneId,
                ["tenant.id"] = tenantId,
                ["tenantId"] = tenantId,
                ["tenant.group.id"] = tenantGroupId,
                ["tenant.groupId"] = tenantGroupId,
                ["tenantGroupId"] = tenantGroupId
            };
        }

        /// <summary>
        /// Deletes Redis registry keys for one test runtime.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        private async Task DeleteRuntimeRegistryKeysAsync(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            await this.database
                .SetRemoveAsync(
                    GetRuntimeInstanceSetKey(controlPlaneId),
                    runtimeInstanceId)
                .ConfigureAwait(false);

            await this.database
                .KeyDeleteAsync(
                    GetRuntimeInstanceKey(
                        controlPlaneId,
                        runtimeInstanceId))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes Redis capacity keys for one test runtime.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        private async Task DeleteRuntimeCapacityKeysAsync(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            await this.database
                .SetRemoveAsync(
                    GetRuntimeCapacitySetKey(controlPlaneId),
                    runtimeInstanceId)
                .ConfigureAwait(false);

            await this.database
                .KeyDeleteAsync(
                    GetRuntimeCapacityKey(
                        controlPlaneId,
                        runtimeInstanceId))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the Redis runtime instance set key.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <returns>The Redis set key.</returns>
        private static string GetRuntimeInstanceSetKey(
            string controlPlaneId)
        {
            return $"ai:control-plane:{NormalizeKeySegment(controlPlaneId)}:runtime-instances";
        }

        /// <summary>
        /// Gets the Redis runtime instance entry key.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns>The Redis entry key.</returns>
        private static string GetRuntimeInstanceKey(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            return $"ai:control-plane:{NormalizeKeySegment(controlPlaneId)}:runtime-instance:{NormalizeKeySegment(runtimeInstanceId)}";
        }

        /// <summary>
        /// Gets the Redis runtime capacity set key.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <returns>The Redis set key.</returns>
        private static string GetRuntimeCapacitySetKey(
            string controlPlaneId)
        {
            return $"ai:control-plane:{NormalizeKeySegment(controlPlaneId)}:runtime-instance-capacity";
        }

        /// <summary>
        /// Gets the Redis runtime capacity entry key.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="runtimeInstanceId">The runtime instance id.</param>
        /// <returns>The Redis entry key.</returns>
        private static string GetRuntimeCapacityKey(
            string controlPlaneId,
            string runtimeInstanceId)
        {
            return $"ai:control-plane:{NormalizeKeySegment(controlPlaneId)}:runtime-instance-capacity:{NormalizeKeySegment(runtimeInstanceId)}";
        }

        /// <summary>
        /// Normalizes a Redis key segment like the Redis runtime instance stores.
        /// </summary>
        /// <param name="value">The value to normalize.</param>
        /// <returns>The normalized value.</returns>
        private static string NormalizeKeySegment(
            string value)
        {
            return value
                .Trim()
                .Replace(" ", "-", StringComparison.Ordinal)
                .Replace("\\", "/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Provides a fixed control-plane id resolver.
        /// </summary>
        private sealed class FixedAiControlPlaneIdResolver :
            IAiControlPlaneIdResolver
        {
            private readonly string controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="FixedAiControlPlaneIdResolver"/> class.
            /// </summary>
            /// <param name="controlPlaneId">The control-plane id.</param>
            public FixedAiControlPlaneIdResolver(
                string controlPlaneId)
            {
                this.controlPlaneId =
                    controlPlaneId ?? throw new ArgumentNullException(nameof(controlPlaneId));
            }

            /// <inheritdoc />
            public Task<string> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.controlPlaneId);
            }
        }

        /// <summary>
        /// Provides a fixed execution context snapshot.
        /// </summary>
        private sealed class FixedExecutionContextSnapshotProvider :
            IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="FixedExecutionContextSnapshotProvider"/> class.
            /// </summary>
            /// <param name="snapshot">The execution context snapshot.</param>
            public FixedExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot =
                    snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return this.snapshot;
            }
        }
    }
}