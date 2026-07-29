using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Readiness;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Readiness
{
    /// <summary>
    /// Provides tenant-safety tests for <see cref="AiRuntimeInstanceReadinessWaiter"/>.
    /// </summary>
    public sealed class AiRuntimeInstanceReadinessWaiterTenantTests
    {
        /// <summary>
        /// Verifies that a compatible tenant runtime can satisfy readiness without global fallback.
        /// </summary>
        [Fact]
        public async Task WaitUntilReadyAsync_Should_Resolve_Compatible_Tenant_Runtime()
        {
            var controlPlaneId = "control-plane-a";
            var runtimeInstanceId = "host-a:mcp-runtime-1";
            var requestedRuntimeInstanceId = "control-plane-a:tenant-a-runtime-1";
            var executionContextSnapshot =
                AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");

            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();

            await registry
                .RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"))
                .ConfigureAwait(false);

            await capacityStore
                .PublishAsync(
                    CreateCapacityDescriptor(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"))
                .ConfigureAwait(false);

            var waiter =
                new AiRuntimeInstanceReadinessWaiter(
                    registry,
                    capacityStore);

            var result =
                await waiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            RuntimeInstanceId = requestedRuntimeInstanceId,
                            ControlPlaneId = controlPlaneId,
                            ProviderName = "grpc",
                            TransportName = "grpc",
                            ExecutionContextSnapshot = executionContextSnapshot,
                            RequireTransportEndpoint = false,
                            Timeout = TimeSpan.FromSeconds(1),
                            PollInterval = TimeSpan.FromMilliseconds(10)
                        })
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(runtimeInstanceId, result.RuntimeInstanceId);
            Assert.Equal("grpc", result.ProviderName);
            Assert.Equal("grpc", result.TransportName);
        }

        /// <summary>
        /// Verifies that an already-ready compatible sibling cannot satisfy readiness when the
        /// caller requires one exact runtime instance identity.
        /// </summary>
        [Fact]
        public async Task WaitUntilReadyAsync_Should_Reject_Compatible_Runtime_When_Exact_Id_Is_Required()
        {
            var controlPlaneId = "control-plane-a";
            var compatibleRuntimeInstanceId = "host-a:mcp-runtime-1";
            var requestedRuntimeInstanceId = "host-a:mcp-runtime-2";
            var executionContextSnapshot =
                AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");
            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();

            await registry
                .RegisterAsync(
                    CreateRegistration(
                        compatibleRuntimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"))
                .ConfigureAwait(false);
            await capacityStore
                .PublishAsync(
                    CreateCapacityDescriptor(
                        compatibleRuntimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"))
                .ConfigureAwait(false);

            var waiter =
                new AiRuntimeInstanceReadinessWaiter(
                    registry,
                    capacityStore);
            var result =
                await waiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            RuntimeInstanceId = requestedRuntimeInstanceId,
                            RequireExactRuntimeInstanceId = true,
                            ControlPlaneId = controlPlaneId,
                            ProviderName = "grpc",
                            TransportName = "grpc",
                            ExecutionContextSnapshot = executionContextSnapshot,
                            RequireTransportEndpoint = false,
                            Timeout = TimeSpan.FromMilliseconds(50),
                            PollInterval = TimeSpan.FromMilliseconds(10)
                        })
                    .ConfigureAwait(false);
            Assert.False(result.Success);
            Assert.Equal(
                requestedRuntimeInstanceId,
                result.RuntimeInstanceId);
            Assert.Equal(
                "runtime-readiness-exact-registry-missing",
                result.FailureReason);
        }

        /// <summary>
        /// Verifies that a runtime published for another tenant cannot satisfy readiness.
        /// </summary>
        [Fact]
        public async Task WaitUntilReadyAsync_Should_Reject_Runtime_When_Tenant_Does_Not_Match()
        {
            var controlPlaneId = "control-plane-a";
            var runtimeInstanceId = "host-a:mcp-runtime-1";
            var requestedRuntimeInstanceId = "control-plane-a:tenant-a-runtime-1";
            var executionContextSnapshot =
                AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");

            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();

            await registry
                .RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-b",
                        tenantGroupId: "tenant-group-b"))
                .ConfigureAwait(false);

            await capacityStore
                .PublishAsync(
                    CreateCapacityDescriptor(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: "tenant-b",
                        tenantGroupId: "tenant-group-b"))
                .ConfigureAwait(false);

            var waiter =
                new AiRuntimeInstanceReadinessWaiter(
                    registry,
                    capacityStore);

            var result =
                await waiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            RuntimeInstanceId = requestedRuntimeInstanceId,
                            ControlPlaneId = controlPlaneId,
                            ProviderName = "grpc",
                            TransportName = "grpc",
                            ExecutionContextSnapshot = executionContextSnapshot,
                            RequireTransportEndpoint = false,
                            Timeout = TimeSpan.FromMilliseconds(50),
                            PollInterval = TimeSpan.FromMilliseconds(10)
                        })
                    .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("runtime-readiness-compatible-registry-missing", result.FailureReason);
        }

        /// <summary>
        /// Verifies that a runtime without a tenant cannot satisfy tenant-scoped readiness.
        /// </summary>
        [Fact]
        public async Task WaitUntilReadyAsync_Should_Reject_Runtime_When_Tenant_Is_Missing()
        {
            var controlPlaneId = "control-plane-a";
            var runtimeInstanceId = "host-a:mcp-runtime-1";
            var requestedRuntimeInstanceId = "control-plane-a:tenant-a-runtime-1";
            var executionContextSnapshot =
                AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");

            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();

            await registry
                .RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: null,
                        tenantGroupId: null))
                .ConfigureAwait(false);

            await capacityStore
                .PublishAsync(
                    CreateCapacityDescriptor(
                        runtimeInstanceId,
                        controlPlaneId,
                        tenantId: null,
                        tenantGroupId: null))
                .ConfigureAwait(false);

            var waiter =
                new AiRuntimeInstanceReadinessWaiter(
                    registry,
                    capacityStore);

            var result =
                await waiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            RuntimeInstanceId = requestedRuntimeInstanceId,
                            ControlPlaneId = controlPlaneId,
                            ProviderName = "grpc",
                            TransportName = "grpc",
                            ExecutionContextSnapshot = executionContextSnapshot,
                            RequireTransportEndpoint = false,
                            Timeout = TimeSpan.FromMilliseconds(50),
                            PollInterval = TimeSpan.FromMilliseconds(10)
                        })
                    .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("runtime-readiness-compatible-registry-missing", result.FailureReason);
        }

        /// <summary>
        /// Verifies that a compatible runtime from another control-plane cannot satisfy readiness.
        /// </summary>
        [Fact]
        public async Task WaitUntilReadyAsync_Should_Reject_Runtime_When_ControlPlane_Does_Not_Match()
        {
            var requestedControlPlaneId = "control-plane-a";
            var publishedControlPlaneId = "control-plane-b";
            var runtimeInstanceId = "host-a:mcp-runtime-1";
            var requestedRuntimeInstanceId = "control-plane-a:tenant-a-runtime-1";
            var executionContextSnapshot =
                AiExecutionContextSnapshotTestFactory.Create(
                    tenantId: "tenant-a",
                    tenantGroupId: "tenant-group-a");

            var registry = new FakeRuntimeInstanceRegistry();
            var capacityStore = new FakeRuntimeInstanceCapacityStore();

            await registry
                .RegisterAsync(
                    CreateRegistration(
                        runtimeInstanceId,
                        publishedControlPlaneId,
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"))
                .ConfigureAwait(false);

            await capacityStore
                .PublishAsync(
                    CreateCapacityDescriptor(
                        runtimeInstanceId,
                        publishedControlPlaneId,
                        tenantId: "tenant-a",
                        tenantGroupId: "tenant-group-a"))
                .ConfigureAwait(false);

            var waiter =
                new AiRuntimeInstanceReadinessWaiter(
                    registry,
                    capacityStore);

            var result =
                await waiter
                    .WaitUntilReadyAsync(
                        new AiRuntimeInstanceReadinessRequest
                        {
                            RuntimeInstanceId = requestedRuntimeInstanceId,
                            ControlPlaneId = requestedControlPlaneId,
                            ProviderName = "grpc",
                            TransportName = "grpc",
                            ExecutionContextSnapshot = executionContextSnapshot,
                            RequireTransportEndpoint = false,
                            Timeout = TimeSpan.FromMilliseconds(50),
                            PollInterval = TimeSpan.FromMilliseconds(10)
                        })
                    .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("runtime-readiness-compatible-registry-missing", result.FailureReason);
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
            string? tenantId,
            string? tenantGroupId)
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = runtimeInstanceId,
                ControlPlaneId = controlPlaneId,
                ControlPlaneHostId = "host-a",
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
            string? tenantId,
            string? tenantGroupId)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                ControlPlaneId = controlPlaneId,
                ControlPlaneHostId = "host-a",
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
                EffectiveAvailableRunSlots = 5,
                ReservedRunSlots = 0,
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
        /// Creates metadata used by readiness provider, transport, tenant, and control-plane matching.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id.</param>
        /// <param name="tenantId">The tenant id.</param>
        /// <param name="tenantGroupId">The tenant group id.</param>
        /// <returns>The metadata dictionary.</returns>
        private static IReadOnlyDictionary<string, string> CreateMetadata(
            string controlPlaneId,
            string? tenantId,
            string? tenantGroupId)
        {
            var metadata =
                new Dictionary<string, string>
                {
                    ["provider.name"] = "grpc",
                    ["provider"] = "grpc",
                    ["transport.name"] = "grpc",
                    ["controlPlaneId"] = controlPlaneId,
                    ["control-plane.id"] = controlPlaneId,
                    ["controlplane.id"] = controlPlaneId,
                    ["runtime.controlPlaneId"] = controlPlaneId
                };

            AddIfNotEmpty(metadata, "tenant.id", tenantId);
            AddIfNotEmpty(metadata, "tenantId", tenantId);
            AddIfNotEmpty(metadata, "tenant.group.id", tenantGroupId);
            AddIfNotEmpty(metadata, "tenant.groupId", tenantGroupId);
            AddIfNotEmpty(metadata, "tenantGroupId", tenantGroupId);

            return metadata;
        }

        /// <summary>
        /// Adds a metadata value when the value is not empty.
        /// </summary>
        /// <param name="metadata">The metadata dictionary.</param>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        private static void AddIfNotEmpty(
            IDictionary<string, string> metadata,
            string key,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                metadata[key] = value;
            }
        }
    }
}