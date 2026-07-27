using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances
{
    /// <summary>
    /// Validates runtime pool membership across registry decorators and dependency injection.
    /// </summary>
    public sealed class RuntimePoolMembershipCompositionTests
    {
        /// <summary>
        /// Verifies that a legacy registry can expose typed membership without metadata routing.
        /// </summary>
        [Fact]
        public async Task MembershipAdapter_Should_Project_FirstClassIdentity_For_LegacyRegistry()
        {
            var registry =
                new LegacyRuntimeInstanceRegistry(
                    CreateSnapshots());

            var reader =
                new AiRuntimePoolMembershipReader(registry);

            var poolMembers =
                await reader.ListByPoolIdAsync("pool-shared-01");

            var hostMembers =
                await reader.ListByHostIdAsync("host-a");

            var hostIds =
                await reader.ListHostIdsByPoolIdAsync("pool-shared-01");

            Assert.Equal(
                new[]
                {
                    "runtime-a1",
                    "runtime-a2",
                    "runtime-b1"
                },
                poolMembers.Select(member => member.RuntimeInstanceId));

            Assert.Equal(
                new[]
                {
                    "runtime-a1",
                    "runtime-a2"
                },
                hostMembers.Select(member => member.RuntimeInstanceId));

            Assert.Equal(
                new[]
                {
                    "host-a",
                    "host-b"
                },
                hostIds);
        }

        /// <summary>
        /// Verifies that the observability decorator preserves the membership contract.
        /// </summary>
        [Fact]
        public async Task ObservedRegistry_Should_Preserve_RuntimePoolMembershipContract()
        {
            IAiRuntimePoolMembershipReader reader =
                new ObservedAiRuntimeInstanceRegistry(
                    new LegacyRuntimeInstanceRegistry(CreateSnapshots()),
                    new NoopAiControlPlaneObserver());

            var members =
                await reader.ListByHostIdAsync("host-a");

            Assert.Equal(
                new[]
                {
                    "runtime-a1",
                    "runtime-a2"
                },
                members.Select(member => member.RuntimeInstanceId));
        }

        /// <summary>
        /// Verifies that control-plane dependency injection exposes membership from the configured registry.
        /// </summary>
        [Fact]
        public async Task ControlPlaneDI_Should_Expose_Membership_For_Custom_LegacyRegistry()
        {
            var services = new ServiceCollection();
            var registry =
                new LegacyRuntimeInstanceRegistry(
                    CreateSnapshots());

            services.AddSingleton<IAiRuntimeInstanceRegistry>(registry);
            services.AddAiControlPlane();

            using var serviceProvider =
                services.BuildServiceProvider();

            var resolvedRegistry =
                serviceProvider.GetRequiredService<IAiRuntimeInstanceRegistry>();

            var reader =
                serviceProvider.GetRequiredService<IAiRuntimePoolMembershipReader>();

            var members =
                await reader.ListByPoolIdAsync("pool-shared-01");

            Assert.Same(registry, resolvedRegistry);
            Assert.Equal(3, members.Count);
        }

        /// <summary>
        /// Creates snapshots with typed identities and conflicting diagnostic metadata.
        /// </summary>
        /// <returns>The runtime instance snapshots.</returns>
        private static IReadOnlyList<AiRuntimeInstanceSnapshot> CreateSnapshots()
        {
            return new[]
            {
                CreateSnapshot("runtime-a1", "pool-shared-01", "host-a"),
                CreateSnapshot("runtime-a2", "pool-shared-01", "host-a"),
                CreateSnapshot("runtime-b1", "pool-shared-01", "host-b"),
                CreateSnapshot("runtime-c1", "pool-dedicated-01", "host-c"),
                CreateSnapshot(
                    "runtime-metadata-only",
                    poolId: null,
                    hostId: null,
                    metadataPoolId: "pool-shared-01",
                    metadataHostId: "host-a")
            };
        }

        /// <summary>
        /// Creates one runtime instance snapshot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="poolId">The typed pool identifier.</param>
        /// <param name="hostId">The typed host-incarnation identifier.</param>
        /// <param name="metadataPoolId">The diagnostic pool metadata value.</param>
        /// <param name="metadataHostId">The diagnostic host metadata value.</param>
        /// <returns>The runtime instance snapshot.</returns>
        private static AiRuntimeInstanceSnapshot CreateSnapshot(
            string runtimeInstanceId,
            string? poolId,
            string? hostId,
            string metadataPoolId = "diagnostic-pool",
            string metadataHostId = "diagnostic-host")
        {
            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                PoolId = poolId,
                HostId = hostId,
                RuntimeId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                RegisteredAtUtc = DateTimeOffset.UtcNow,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                SnapshotAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["pool.id"] = metadataPoolId,
                    ["host.id"] = metadataHostId
                }
            };
        }

        /// <summary>
        /// Minimal legacy registry used to validate additive compatibility.
        /// </summary>
        private sealed class LegacyRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly IReadOnlyList<AiRuntimeInstanceSnapshot> snapshots;

            /// <summary>
            /// Initializes a new instance of the <see cref="LegacyRuntimeInstanceRegistry"/> class.
            /// </summary>
            /// <param name="snapshots">The visible runtime instance snapshots.</param>
            public LegacyRuntimeInstanceRegistry(
                IReadOnlyList<AiRuntimeInstanceSnapshot> snapshots)
            {
                this.snapshots =
                    snapshots ?? throw new ArgumentNullException(nameof(snapshots));
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
                string runtimeInstanceId,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    this.snapshots.FirstOrDefault(candidate =>
                        string.Equals(
                            candidate.RuntimeInstanceId,
                            runtimeInstanceId,
                            StringComparison.Ordinal));

                return Task.FromResult(snapshot);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyList<AiRuntimeInstanceSnapshot> result =
                    includeStopped
                        ? this.snapshots
                        : this.snapshots
                            .Where(snapshot =>
                                snapshot.Status != AiRuntimeInstanceStatus.Stopped)
                            .ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }
    }
}
