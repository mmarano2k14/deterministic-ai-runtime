using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests observability decoration for runtime instance capacity stores.
    /// </summary>
    public sealed class ObservedAiRuntimeInstanceCapacityStoreObservabilityTests
    {
        /// <summary>
        /// Verifies that dependency injection can expose the observed decorator while preserving the inner capacity store.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DependencyInjection_Should_Resolve_Observed_CapacityStore_And_Invoke_Inner_Store()
        {
            var services = new ServiceCollection();
            var observer = new CapturingControlPlaneObserver();

            services.AddSingleton<IAiControlPlaneObserver>(observer);
            services.AddSingleton<CapturingRuntimeInstanceCapacityStore>();
            services.AddSingleton<IAiRuntimeInstanceCapacityStore>(provider =>
                new ObservedAiRuntimeInstanceCapacityStore(
                    provider.GetRequiredService<CapturingRuntimeInstanceCapacityStore>(),
                    provider.GetRequiredService<IAiControlPlaneObserver>()));

            using var provider = services.BuildServiceProvider();
            var store = provider.GetRequiredService<IAiRuntimeInstanceCapacityStore>();
            var inner = provider.GetRequiredService<CapturingRuntimeInstanceCapacityStore>();
            var descriptor = CreateDescriptor();

            await store.PublishAsync(descriptor, CancellationToken.None).ConfigureAwait(false);

            Assert.IsType<ObservedAiRuntimeInstanceCapacityStore>(store);
            Assert.Equal(new[] { "runtime-1" }, inner.PublishedRuntimeInstanceIds);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-capacity-publish");
            AssertSucceededEvent(observer.Events[1], "runtime-instance-capacity-publish");
        }

        /// <summary>
        /// Verifies that publish records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Started_And_Succeeded_Events()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceCapacityStore();
            var store = new ObservedAiRuntimeInstanceCapacityStore(inner, observer);

            await store.PublishAsync(CreateDescriptor(), CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(new[] { "runtime-1" }, inner.PublishedRuntimeInstanceIds);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-capacity-publish");
            AssertSucceededEvent(observer.Events[1], "runtime-instance-capacity-publish");
            Assert.Equal("Ready", observer.Events[1].Properties["status"]?.ToString());
        }

        /// <summary>
        /// Verifies that get records completed-with-issues when no descriptor exists.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task GetAsync_Should_Record_CompletedWithIssues_Event_When_Descriptor_Is_Not_Found()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceCapacityStore();
            var store = new ObservedAiRuntimeInstanceCapacityStore(inner, observer);

            var descriptor = await store.GetAsync("runtime-1", CancellationToken.None).ConfigureAwait(false);

            Assert.Null(descriptor);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-capacity-get");
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("runtime-instance-capacity-not-found", observer.Events[1].FailureReason);
        }

        /// <summary>
        /// Verifies that list records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ListAsync_Should_Record_Started_And_Succeeded_Events()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceCapacityStore(new[] { CreateDescriptor() });
            var store = new ObservedAiRuntimeInstanceCapacityStore(inner, observer);

            var descriptors = await store.ListAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.Single(descriptors);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedListEvent(observer.Events[0], "runtime-instance-capacity-list");
            AssertSucceededListEvent(observer.Events[1], "runtime-instance-capacity-list");
            Assert.Equal(1, observer.Events[1].Properties["descriptorCount"]);
        }

        /// <summary>
        /// Verifies that remove records completed-with-issues when the descriptor was not removed.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RemoveAsync_Should_Record_CompletedWithIssues_Event_When_Descriptor_Was_Not_Removed()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceCapacityStore();
            var store = new ObservedAiRuntimeInstanceCapacityStore(inner, observer);

            var removed = await store.RemoveAsync("runtime-1", CancellationToken.None).ConfigureAwait(false);

            Assert.False(removed);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-capacity-remove");
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("runtime-instance-capacity-not-removed", observer.Events[1].FailureReason);
        }

        /// <summary>
        /// Verifies that inner exceptions record failed events and are rethrown.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Failed_Event_When_Inner_Store_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new ThrowingRuntimeInstanceCapacityStore();
            var store = new ObservedAiRuntimeInstanceCapacityStore(inner, observer);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.PublishAsync(CreateDescriptor(), CancellationToken.None)).ConfigureAwait(false);

            Assert.Equal("capacity store exploded", exception.Message);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-capacity-publish");
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal(typeof(InvalidOperationException).FullName, observer.Events[1].Properties["exception.type"]?.ToString());
        }

        /// <summary>
        /// Verifies that capacity events are recorded to the runtime ledger through the sink.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Capacity_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var runtimeObservability = new FakeRuntimeObservability(ledger);
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(runtimeObservability);
            var observer = new CompositeAiControlPlaneObserver(new IAiControlPlaneEventSink[] { sink });
            var inner = new CapturingRuntimeInstanceCapacityStore();
            var store = new ObservedAiRuntimeInstanceCapacityStore(inner, observer);

            await store.PublishAsync(CreateDescriptor(), CancellationToken.None).ConfigureAwait(false);

            Assert.Equal(2, ledger.Records.Count);
            Assert.Equal(AiDecisionLedgerCategory.RuntimeInstance, ledger.Records[0].Category);
            Assert.Equal("control.instanceregistry.runtime-instance-capacity-publish.operationstarted", ledger.Records[0].EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Records[0].Outcome);
            Assert.Equal(AiDecisionLedgerCategory.RuntimeInstance, ledger.Records[1].Category);
            Assert.Equal("control.instanceregistry.runtime-instance-capacity-publish.succeeded", ledger.Records[1].EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Records[1].Outcome);
            Assert.Equal("runtime-1", ledger.Records[1].Metadata["runtime.instance.id"]);
        }

        /// <summary>
        /// Creates a test capacity descriptor.
        /// </summary>
        /// <returns>The capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateDescriptor()
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = "runtime-1",
                ControlPlaneId = "control-plane-1",
                TenantId = "tenant-a",
                TenantGroupId = "group-a",
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 4,
                ActiveWorkerCount = 1,
                AvailableWorkerCount = 3,
                MaxConcurrentRuns = 8,
                MaxRunSlots = 8,
                AvailableRunSlots = 7,
                ReservedRunSlots = 0,
                EffectiveAvailableRunSlots = 7,
                QueuedRunCount = 0,
                RunningRunCount = 1,
                ActiveRunCount = 1,
                IsQueuePaused = false,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pipeline.key"] = "pipeline-1"
                }
            };
        }

        /// <summary>
        /// Asserts the common started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <param name="operation">The expected operation.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent,
            string operation)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
            Assert.Equal(operation, controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Asserts the common succeeded event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The control-plane event.</param>
        /// <param name="operation">The expected operation.</param>
        private static void AssertSucceededEvent(
            AiControlPlaneEvent controlPlaneEvent,
            string operation)
        {
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
            Assert.Equal(operation, controlPlaneEvent.Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
        }


        /// <summary>
        /// Asserts the common capacity list succeeded event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        /// <param name="operation">The expected operation.</param>
        private static void AssertSucceededListEvent(
            AiControlPlaneEvent controlPlaneEvent,
            string operation)
        {
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
            Assert.Equal(operation, controlPlaneEvent.Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, controlPlaneEvent.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.CorrelationId));
            Assert.Null(controlPlaneEvent.Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Captures runtime instance capacity store calls.
        /// </summary>
        private class CapturingRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors = new(StringComparer.Ordinal);

            /// <summary>
            /// Initializes a new instance of the <see cref="CapturingRuntimeInstanceCapacityStore"/> class.
            /// </summary>
            public CapturingRuntimeInstanceCapacityStore()
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="CapturingRuntimeInstanceCapacityStore"/> class.
            /// </summary>
            /// <param name="descriptors">The initial descriptors.</param>
            public CapturingRuntimeInstanceCapacityStore(
                IEnumerable<AiRuntimeInstanceCapacityDescriptor> descriptors)
            {
                foreach (var descriptor in descriptors)
                {
                    this.descriptors[descriptor.RuntimeInstanceId] = descriptor;
                }
            }

            /// <summary>
            /// Gets the published runtime instance identifiers.
            /// </summary>
            public List<string> PublishedRuntimeInstanceIds { get; } = new();

            /// <inheritdoc />
            public virtual Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                this.PublishedRuntimeInstanceIds.Add(descriptor.RuntimeInstanceId);
                this.descriptors[descriptor.RuntimeInstanceId] = descriptor;
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                this.descriptors.TryGetValue(runtimeInstanceId, out var descriptor);
                return Task.FromResult(descriptor);
            }

            /// <inheritdoc />
            public virtual Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>(this.descriptors.Values.OrderBy(descriptor => descriptor.RuntimeInstanceId, StringComparer.Ordinal).ToArray());
            }

            /// <inheritdoc />
            public virtual Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.descriptors.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Capacity store that throws during publish.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceCapacityStore : CapturingRuntimeInstanceCapacityStore
        {
            /// <inheritdoc />
            public override Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("capacity store exploded");
            }
        }

        /// <summary>
        /// Captures control-plane events.
        /// </summary>
        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            /// <summary>
            /// Gets captured events.
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
        /// Captures ledger records.
        /// </summary>
        private sealed class CapturingDecisionLedgerRecorder : IAiDecisionLedgerRecorder
        {
            /// <summary>
            /// Gets captured ledger records.
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
        /// Asserts the common capacity list started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        /// <param name="operation">The expected operation.</param>
        private static void AssertStartedListEvent(
            AiControlPlaneEvent controlPlaneEvent,
            string operation)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
            Assert.Equal(operation, controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.CorrelationId));
            Assert.Null(controlPlaneEvent.Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Captured ledger record.
        /// </summary>
        /// <param name="Context">The ledger context.</param>
        /// <param name="Category">The ledger category.</param>
        /// <param name="EventType">The ledger event type.</param>
        /// <param name="Outcome">The ledger outcome.</param>
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
