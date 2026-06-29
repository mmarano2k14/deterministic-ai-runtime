using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests fixture runtime host creation strategy control-plane observability events.
    /// </summary>
    public sealed class FixtureAiRuntimeHostCreationStrategyObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxx";

        /// <summary>
        /// Verifies that successful fixture host creation records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Started_And_Succeeded_Events_When_Fixture_Host_Is_Created()
        {
            var observer = new CapturingControlPlaneObserver();
            var registry = new CapturingRuntimeInstanceRegistry();
            var capacityStore = new CapturingRuntimeInstanceCapacityStore();
            var strategy = new FixtureAiRuntimeHostCreationStrategy(registry, capacityStore, observer);

            var result = await strategy
                .StartAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Single(registry.Registrations);
            Assert.Single(capacityStore.PublishedDescriptors);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, observer.Events[1].Area);
            Assert.Equal("runtime-fixture-host-creation", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(observer.Events[1].Correlation.PipelineKey));
            Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
            Assert.Equal("control-plane-1", observer.Events[1].Properties["controlPlaneId"]?.ToString());
            Assert.Equal("http", observer.Events[1].Properties["providerName"]?.ToString());
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("True", observer.Events[1].Properties["success"]?.ToString());
        }

        /// <summary>
        /// Verifies that registry failures record failed events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Failed_Event_When_Registry_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var strategy = new FixtureAiRuntimeHostCreationStrategy(
                new ThrowingRuntimeInstanceRegistry("registry exploded"),
                new CapturingRuntimeInstanceCapacityStore(),
                observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await strategy
                        .StartAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("registry exploded", observer.Events[1].Properties["exception.message"]?.ToString());
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that capacity publish failures record failed events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Failed_Event_When_Capacity_Publish_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var registry = new CapturingRuntimeInstanceRegistry();
            var strategy = new FixtureAiRuntimeHostCreationStrategy(
                registry,
                new ThrowingRuntimeInstanceCapacityStore("capacity exploded"),
                observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await strategy
                        .StartAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Single(registry.Registrations);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("capacity exploded", observer.Events[1].Properties["exception.message"]?.ToString());
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that successful fixture host creation events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Succeeded_FixtureHostCreation_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var strategy = new FixtureAiRuntimeHostCreationStrategy(
                new CapturingRuntimeInstanceRegistry(),
                new CapturingRuntimeInstanceCapacityStore(),
                observer);

            var result = await strategy
                .StartAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-fixture-host-creation.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.scaling.runtime-fixture-host-creation.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("runtime-1", ledger.Entries[1].Metadata!["runtimeInstanceId"]);
            Assert.Equal("http", ledger.Entries[1].Metadata!["providerName"]);
            Assert.Equal("True", ledger.Entries[1].Metadata!["success"]);
        }

        /// <summary>
        /// Verifies that failed fixture host creation events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Failed_FixtureHostCreation_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var strategy = new FixtureAiRuntimeHostCreationStrategy(
                new ThrowingRuntimeInstanceRegistry("registry exploded"),
                new CapturingRuntimeInstanceCapacityStore(),
                observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await strategy
                        .StartAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-fixture-host-creation.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Failed, ledger.Entries[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), ledger.Entries[1].Reason);
            Assert.Equal("control.scaling.runtime-fixture-host-creation.failed", ledger.Entries[1].EventType);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("registry exploded", ledger.Entries[1].Metadata!["exception.message"]);
        }

        /// <summary>
        /// Asserts the common fixture host creation started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, controlPlaneEvent.Area);
            Assert.Equal("runtime-fixture-host-creation", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.PipelineKey));
        }

        /// <summary>
        /// Creates a runtime host start request.
        /// </summary>
        /// <returns>The runtime host start request.</returns>
        private static AiRuntimeHostStartRequest CreateRequest()
        {
            return new AiRuntimeHostStartRequest
            {
                RuntimeInstanceId = "runtime-1",
                ControlPlaneId = "control-plane-1",
                ProviderName = "http",
                TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                TransportEndpoint = "http://localhost:5001",
                HostCreationMode = AiRuntimeHostCreationMode.Fixture,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(),
                TenantId = ExpectedTenantId,
                TenantGroupId = ExpectedTenantGroupId,
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated.ToString(),
                PreferDedicatedCapacity = true,
                AllowSharedFallback = false,
                MaxRuntimeInstances = 3,
                RuntimeInstanceIdPrefix = "runtime",
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 100,
                Metadata = new Dictionary<string, string>()
            };
        }

        /// <summary>
        /// Captures control-plane events.
        /// </summary>
        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            /// <summary>
            /// Gets the captured control-plane events.
            /// </summary>
            public List<AiControlPlaneEvent> Events { get; } = new();

            /// <inheritdoc />
            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                this.Events.Add(controlPlaneEvent);
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Runtime instance registry that captures registrations.
        /// </summary>
        private class CapturingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            /// <summary>
            /// Gets the captured runtime instance registrations.
            /// </summary>
            public List<AiRuntimeInstanceRegistration> Registrations { get; } = new();

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(registration);
                this.Registrations.Add(registration);
                return Task.FromResult(CreateRuntimeInstanceSnapshot(registration.RuntimeInstanceId));
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    this.Registrations.Any(registration => string.Equals(registration.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal))
                        ? CreateRuntimeInstanceSnapshot(runtimeInstanceId)
                        : null);
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
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(CreateRuntimeInstanceSnapshot(runtimeInstanceId));
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    this.Registrations.Select(registration => CreateRuntimeInstanceSnapshot(registration.RuntimeInstanceId)).ToArray());
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(CreateRuntimeInstanceSnapshot(runtimeInstanceId));
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(CreateRuntimeInstanceSnapshot(runtimeInstanceId));
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(CreateRuntimeInstanceSnapshot(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Runtime instance registry that throws while registering.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceRegistry : CapturingRuntimeInstanceRegistry
        {
            private readonly string message;

            /// <summary>
            /// Initializes a new instance of the <see cref="ThrowingRuntimeInstanceRegistry"/> class.
            /// </summary>
            /// <param name="message">The exception message.</param>
            public ThrowingRuntimeInstanceRegistry(string message)
            {
                this.message = message;
            }

            /// <inheritdoc />
            public override Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(this.message);
            }
        }

        /// <summary>
        /// Runtime instance capacity store that captures published descriptors.
        /// </summary>
        private class CapturingRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            /// <summary>
            /// Gets the published capacity descriptors.
            /// </summary>
            public List<AiRuntimeInstanceCapacityDescriptor> PublishedDescriptors { get; } = new();

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(
                    this.PublishedDescriptors.FirstOrDefault(descriptor => string.Equals(descriptor.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal)));
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>(this.PublishedDescriptors.ToArray());
            }

            /// <inheritdoc />
            public virtual Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(descriptor);
                this.PublishedDescriptors.Add(descriptor);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var descriptor = this.PublishedDescriptors.FirstOrDefault(item => string.Equals(item.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal));

                if (descriptor is null)
                {
                    return Task.FromResult(false);
                }

                this.PublishedDescriptors.Remove(descriptor);
                return Task.FromResult(true);
            }
        }

        /// <summary>
        /// Runtime instance capacity store that throws while publishing.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceCapacityStore : CapturingRuntimeInstanceCapacityStore
        {
            private readonly string message;

            /// <summary>
            /// Initializes a new instance of the <see cref="ThrowingRuntimeInstanceCapacityStore"/> class.
            /// </summary>
            /// <param name="message">The exception message.</param>
            public ThrowingRuntimeInstanceCapacityStore(string message)
            {
                this.message = message;
            }

            /// <inheritdoc />
            public override Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException(this.message);
            }
        }

        /// <summary>
        /// Creates a runtime instance snapshot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <returns>The runtime instance snapshot.</returns>
        private static AiRuntimeInstanceSnapshot CreateRuntimeInstanceSnapshot(
            string runtimeInstanceId)
        {
            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                CanAcceptRun = true,
                IsQueuePaused = false,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                AvailableRunSlots = 2,
                WorkerCount = 2,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 2,
                MaxLocalWorkersPerExecution = 2
            };
        }

        /// <summary>
        /// Captures decision ledger records written by the runtime observability sink.
        /// </summary>
        private sealed class CapturingDecisionLedgerRecorder : IAiDecisionLedgerRecorder
        {
            /// <summary>
            /// Gets the captured ledger entries.
            /// </summary>
            public List<CapturedLedgerEntry> Entries { get; } = new();

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
                this.Entries.Add(
                    new CapturedLedgerEntry(
                        context,
                        category,
                        eventType,
                        outcome,
                        reason,
                        metadata));

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Represents a captured decision ledger entry.
        /// </summary>
        /// <param name="Context">The captured ledger correlation context.</param>
        /// <param name="Category">The captured ledger category.</param>
        /// <param name="EventType">The captured ledger event type.</param>
        /// <param name="Outcome">The captured ledger outcome.</param>
        /// <param name="Reason">The captured ledger reason.</param>
        /// <param name="Metadata">The captured ledger metadata.</param>
        private sealed record CapturedLedgerEntry(
            AiRuntimeLedgerEventCorrelationContext Context,
            AiDecisionLedgerCategory Category,
            string EventType,
            AiDecisionLedgerOutcome Outcome,
            string? Reason,
            IReadOnlyDictionary<string, string?>? Metadata);

        /// <summary>
        /// Provides a minimal runtime observability facade for fixture host creation observability tests.
        /// </summary>
        private sealed class FakeRuntimeObservability : IAiRuntimeObservability
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeObservability"/> class.
            /// </summary>
            /// <param name="ledger">The decision ledger recorder.</param>
            public FakeRuntimeObservability(
                IAiDecisionLedgerRecorder ledger)
            {
                this.Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
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
    }
}
