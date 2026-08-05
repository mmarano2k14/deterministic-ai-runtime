using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Environment;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests runtime instance registration hosted service observability through observed registry and capacity decorators.
    /// </summary>
    public sealed class AiRuntimeInstanceRegistrationHostedServiceObservabilityTests
    {
        /// <summary>
        /// Verifies that starting the hosted service records capacity publish and registry register ledger events through decorators.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_CapacityPublish_And_RegistryRegister_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var service = CreateService(ledger);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Contains(
                ledger.Records,
                record =>
                    record.EventType == "control.instanceregistry.runtime-instance-capacity-publish.succeeded" &&
                    record.Outcome == AiDecisionLedgerOutcome.Succeeded &&
                    record.Metadata["runtime.instance.id"] == "runtime-1");

            Assert.Contains(
                ledger.Records,
                record =>
                    record.EventType == "control.instanceregistry.runtime-instance-register.succeeded" &&
                    record.Outcome == AiDecisionLedgerOutcome.Succeeded &&
                    record.Metadata["runtime.instance.id"] == "runtime-1");
        }

        /// <summary>
        /// Verifies that stopping the hosted service records registry unregister and capacity remove ledger events through decorators.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StopAsync_Should_Record_RegistryUnregister_And_CapacityRemove_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var service = CreateService(ledger);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Contains(
                ledger.Records,
                record =>
                    record.EventType == "control.instanceregistry.runtime-instance-unregister.succeeded" &&
                    record.Outcome == AiDecisionLedgerOutcome.Succeeded &&
                    record.Metadata["runtime.instance.id"] == "runtime-1");

            Assert.Contains(
                ledger.Records,
                record =>
                    record.EventType == "control.instanceregistry.runtime-instance-capacity-remove.succeeded" &&
                    record.Outcome == AiDecisionLedgerOutcome.Succeeded &&
                    record.Metadata["runtime.instance.id"] == "runtime-1");
        }

        /// <summary>
        /// Verifies that a heartbeat iteration records capacity publish and registry heartbeat ledger events through decorators.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ExecuteAsync_Should_Record_CapacityPublish_And_RegistryHeartbeat_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var service = CreateService(
                ledger,
                options =>
                {
                    options.HeartbeatInterval = TimeSpan.FromMilliseconds(25);
                });

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(
                    () => ledger.Records.Any(record => record.EventType == "control.instanceregistry.runtime-instance-heartbeat.succeeded"),
                    TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Contains(
                ledger.Records,
                record =>
                    record.EventType == "control.instanceregistry.runtime-instance-heartbeat.succeeded" &&
                    record.Outcome == AiDecisionLedgerOutcome.Succeeded &&
                    record.Metadata["runtime.instance.id"] == "runtime-1");
        }

        /// <summary>
        /// Verifies that an expired registry lease is restored by the next heartbeat without restarting the runtime process.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ExecuteAsync_Should_Reregister_When_Heartbeat_Finds_Missing_Registry_Lease()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var registry = new CapturingRuntimeInstanceRegistry();
            var service = CreateService(
                ledger,
                options =>
                {
                    options.HeartbeatInterval = TimeSpan.FromMilliseconds(25);
                },
                registry);

            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            await WaitUntilAsync(
                    () => registry.HeartbeatCount > 0,
                    TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            Assert.True(registry.Expire("runtime-1"));

            await WaitUntilAsync(
                    () => registry.RegisterCount >= 2 &&
                        registry.GetSnapshot("runtime-1") is not null,
                    TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);

            Assert.NotNull(registry.GetSnapshot("runtime-1"));

            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.True(registry.RegisterCount >= 2);
        }

        /// <summary>
        /// Creates the hosted service with observed decorators around fake registry and capacity stores.
        /// </summary>
        /// <param name="ledger">The ledger recorder.</param>
        /// <param name="configureOptions">Optional registration options configuration.</param>
        /// <returns>The hosted service.</returns>
        private static AiRuntimeInstanceRegistrationHostedService CreateService(
            IAiDecisionLedgerRecorder ledger,
            Action<AiRuntimeInstanceRegistrationOptions>? configureOptions = null,
            CapturingRuntimeInstanceRegistry? registry = null)
        {
            var runtimeObservability = new FakeRuntimeObservability(ledger);
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(runtimeObservability);
            var observer = new CompositeAiControlPlaneObserver(new IAiControlPlaneEventSink[] { sink });
            var observedRegistry = new ObservedAiRuntimeInstanceRegistry(
                registry ?? new CapturingRuntimeInstanceRegistry(),
                observer);
            var capacityStore = new ObservedAiRuntimeInstanceCapacityStore(new CapturingRuntimeInstanceCapacityStore(), observer);
            var options = CreateOptions();
            configureOptions?.Invoke(options);

            return new AiRuntimeInstanceRegistrationHostedService(
                observedRegistry,
                new FakeRuntimeEnvironmentProvider(),
                new FakeRuntimePipelineBackgroundController(),
                new StaticControlPlaneIdResolver("control-plane-1"),
                new IAiRuntimeInstanceCapacityStore[] { capacityStore },
                Options.Create(options),
                NullLogger<AiRuntimeInstanceRegistrationHostedService>.Instance);
        }

        /// <summary>
        /// Creates runtime instance registration options.
        /// </summary>
        /// <returns>The registration options.</returns>
        private static AiRuntimeInstanceRegistrationOptions CreateOptions()
        {
            return new AiRuntimeInstanceRegistrationOptions
            {
                Enabled = true,
                RuntimeInstanceId = "runtime-1",
                ProviderName = "local",
                WorkerCount = 4,
                QueueCapacity = 100,
                MaxConcurrentRuns = 8,
                RuntimeVersion = "test-runtime",
                Role = AiRuntimeInstanceRole.Runtime,
                HeartbeatInterval = TimeSpan.FromHours(1),
                RegistryTtl = TimeSpan.FromMinutes(5),
                CapacityTtl = TimeSpan.FromMinutes(5),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = "tenant-a",
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = "group-a",
                    [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = "Dedicated",
                    [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = "False",
                    [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = "True"
                },
                ProviderMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                }
            };
        }

        /// <summary>
        /// Waits until a condition becomes true.
        /// </summary>
        /// <param name="condition">The condition.</param>
        /// <param name="timeout">The timeout.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;

            while (!condition())
            {
                if (DateTimeOffset.UtcNow - startedAtUtc > timeout)
                {
                    throw new TimeoutException("The expected hosted service observability event was not recorded before timeout.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Fake runtime environment provider.
        /// </summary>
        private sealed class FakeRuntimeEnvironmentProvider : IAiRuntimeEnvironmentProvider
        {
            /// <inheritdoc />
            public Task<AiRuntimeEnvironmentSnapshot> GetSnapshotAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AiRuntimeEnvironmentSnapshot
                {
                    RuntimeInstanceId = "runtime-1",
                    ProviderName = "local",
                    HostName = "host-1",
                    ProcessId = 1234,
                    HostId = "host-id-1",
                    RuntimeId = "runtime-id-1",
                    ControlPlaneHostId = "control-plane-host-1",
                    ProviderMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [AiRuntimeInstanceProviderMetadataKeys.ProviderName] = "local"
                    }
                });
            }
        }

        /// <summary>
        /// Captures runtime instance registry operations.
        /// </summary>
        private sealed class CapturingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly ConcurrentDictionary<string, AiRuntimeInstanceSnapshot> snapshots = new(StringComparer.Ordinal);
            private int registerCount;
            private int heartbeatCount;

            public int RegisterCount => Volatile.Read(ref this.registerCount);

            public int HeartbeatCount => Volatile.Read(ref this.heartbeatCount);

            public bool Expire(string runtimeInstanceId)
            {
                return this.snapshots.TryRemove(runtimeInstanceId, out _);
            }

            public AiRuntimeInstanceSnapshot? GetSnapshot(string runtimeInstanceId)
            {
                this.snapshots.TryGetValue(runtimeInstanceId, out var snapshot);
                return snapshot;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref this.registerCount);
                var snapshot = CreateSnapshot(registration.RuntimeInstanceId, registration.ControlPlaneId, AiRuntimeInstanceStatus.Ready, registration.Metadata);
                this.snapshots[registration.RuntimeInstanceId] = snapshot;
                return Task.FromResult(snapshot);
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
                Interlocked.Increment(ref this.heartbeatCount);

                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var snapshot))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var updated = CreateSnapshot(runtimeInstanceId, snapshot.ControlPlaneId, status, snapshot.Metadata);
                this.snapshots[runtimeInstanceId] = updated;
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                this.snapshots.TryGetValue(runtimeInstanceId, out var snapshot);
                return Task.FromResult(snapshot);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(this.snapshots.Values.ToArray());
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.GetAsync(runtimeInstanceId, cancellationToken);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.GetAsync(runtimeInstanceId, cancellationToken);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                if (!this.snapshots.TryRemove(runtimeInstanceId, out var snapshot))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(CreateSnapshot(runtimeInstanceId, snapshot.ControlPlaneId, AiRuntimeInstanceStatus.Stopped, snapshot.Metadata));
            }

            private static AiRuntimeInstanceSnapshot CreateSnapshot(
                string runtimeInstanceId,
                string? controlPlaneId,
                AiRuntimeInstanceStatus status,
                IReadOnlyDictionary<string, string> metadata)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = runtimeInstanceId,
                    ControlPlaneId = controlPlaneId,
                    ControlPlaneHostId = "control-plane-host-1",
                    HostId = "host-id-1",
                    RuntimeId = "runtime-id-1",
                    Role = AiRuntimeInstanceRole.Runtime,
                    Status = status,
                    WorkerCount = 4,
                    QueueCapacity = 100,
                    MaxConcurrentRuns = 8,
                    QueuedRunCount = 0,
                    RunningRunCount = 1,
                    ActiveRunCount = 1,
                    AvailableRunSlots = 7,
                    CanAcceptRun = status == AiRuntimeInstanceStatus.Ready,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = metadata
                };
            }
        }

        /// <summary>
        /// Captures runtime instance capacity store operations.
        /// </summary>
        private sealed class CapturingRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors = new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                this.descriptors[descriptor.RuntimeInstanceId] = descriptor;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                this.descriptors.TryGetValue(runtimeInstanceId, out var descriptor);
                return Task.FromResult(descriptor);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>(this.descriptors.Values.ToArray());
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.descriptors.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Fake runtime observability facade.
        /// </summary>
        private sealed class FakeRuntimeObservability : IAiRuntimeObservability
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeObservability"/> class.
            /// </summary>
            /// <param name="ledger">The ledger recorder.</param>
            public FakeRuntimeObservability(
                IAiDecisionLedgerRecorder ledger)
            {
                this.Ledger = ledger;
            }

            /// <inheritdoc />
            public IAiRuntimeMetrics Metrics => throw new NotSupportedException();

            /// <inheritdoc />
            public IAiRuntimeTracer Tracer => throw new NotSupportedException();

            /// <inheritdoc />
            public IAiDecisionLedgerRecorder Ledger { get; }

            /// <inheritdoc />
            public IAiRuntimeCorrelationAccessor Correlation => throw new NotSupportedException();
        }

        /// <summary>
        /// Captures decision ledger records.
        /// </summary>
        private sealed class CapturingDecisionLedgerRecorder : IAiDecisionLedgerRecorder
        {
            /// <summary>
            /// Gets captured records.
            /// </summary>
            public List<LedgerRecord> Records { get; } = new();

            /// <inheritdoc />
            public Task RecordAsync(
                AiRuntimeLedgerEventCorrelationContext context,
                AiDecisionLedgerCategory category,
                string eventType,
                AiDecisionLedgerOutcome outcome,
                string? reason = null,
                IReadOnlyDictionary<string, string?>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                this.Records.Add(new LedgerRecord(context, category, eventType, outcome, reason, metadata ?? new Dictionary<string, string?>()));
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Captured ledger record.
        /// </summary>
        /// <param name="Context">The ledger context.</param>
        /// <param name="Category">The ledger category.</param>
        /// <param name="EventType">The event type.</param>
        /// <param name="Outcome">The outcome.</param>
        /// <param name="Reason">The optional reason.</param>
        /// <param name="Metadata">The metadata.</param>
        private sealed record LedgerRecord(
            AiRuntimeLedgerEventCorrelationContext Context,
            AiDecisionLedgerCategory Category,
            string EventType,
            AiDecisionLedgerOutcome Outcome,
            string? Reason,
            IReadOnlyDictionary<string, string?> Metadata);
    }
}
