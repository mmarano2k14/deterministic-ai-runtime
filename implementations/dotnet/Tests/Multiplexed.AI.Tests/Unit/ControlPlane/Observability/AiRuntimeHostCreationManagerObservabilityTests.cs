using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests runtime host creation manager control-plane observability events.
    /// </summary>
    public sealed class AiRuntimeHostCreationManagerObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxx";

        /// <summary>
        /// Verifies that successful runtime host creation records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartRuntimeAsync_Should_Record_Started_And_Succeeded_Events_When_Strategy_Starts_Runtime()
        {
            var observer = new CapturingControlPlaneObserver();
            var manager = CreateManager(new SuccessfulHostCreationStrategy(), observer);

            var result = await manager
                .StartRuntimeAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);

            var legacyEvents = GetLegacyEvents(observer);

            Assert.Equal(2, legacyEvents.Length);
            AssertStartedEvent(legacyEvents[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, legacyEvents[1].EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, legacyEvents[1].Area);
            Assert.Equal("runtime-host-creation", legacyEvents[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, legacyEvents[1].Outcome);
            Assert.Null(legacyEvents[1].FailureReason);
            Assert.Equal("runtime-1", legacyEvents[1].Correlation.RuntimeInstanceId);
            Assert.Equal("runtime-1", legacyEvents[1].Properties["runtimeInstanceId"]?.ToString());
            Assert.Equal("http", legacyEvents[1].Properties["providerName"]?.ToString());
            Assert.Equal("Fixture", legacyEvents[1].Properties["hostCreationMode"]?.ToString());
            Assert.Equal(ExpectedTenantId, legacyEvents[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, legacyEvents[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("True", legacyEvents[1].Properties["success"]?.ToString());

            AssertCanonicalLifecycleEvents(
                observer,
                AiRuntimeLifecycleEvents.HostCreationRequested,
                AiRuntimeLifecycleEvents.HostCreationStarted,
                AiRuntimeLifecycleEvents.HostCreationSucceeded);
        }

        /// <summary>
        /// Verifies that missing host creation strategy records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartRuntimeAsync_Should_Record_Started_And_Denied_Events_When_Strategy_Is_Not_Registered()
        {
            var observer = new CapturingControlPlaneObserver();
            var manager = CreateManager(Array.Empty<IAiRuntimeHostCreationStrategy>(), observer);

            var result = await manager
                .StartRuntimeAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("runtime-host-creation-mode-not-registered:Fixture", result.FailureReason);

            var legacyEvents = GetLegacyEvents(observer);

            Assert.Equal(2, legacyEvents.Length);
            AssertStartedEvent(legacyEvents[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, legacyEvents[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, legacyEvents[1].Outcome);
            Assert.Equal("runtime-host-creation-mode-not-registered:Fixture", legacyEvents[1].FailureReason);
            Assert.Equal("runtime-1", legacyEvents[1].Correlation.RuntimeInstanceId);
            Assert.Equal("False", legacyEvents[1].Properties["success"]?.ToString());

            AssertCanonicalLifecycleEvents(
                observer,
                AiRuntimeLifecycleEvents.HostCreationRequested,
                AiRuntimeLifecycleEvents.HostCreationFailed);
        }

        /// <summary>
        /// Verifies that strategy rejection records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartRuntimeAsync_Should_Record_Started_And_Denied_Events_When_Strategy_Rejects_Runtime_Start()
        {
            var observer = new CapturingControlPlaneObserver();
            var manager = CreateManager(new RejectingHostCreationStrategy(), observer);

            var result = await manager
                .StartRuntimeAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("strategy rejected", result.FailureReason);

            var legacyEvents = GetLegacyEvents(observer);

            Assert.Equal(2, legacyEvents.Length);
            AssertStartedEvent(legacyEvents[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, legacyEvents[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, legacyEvents[1].Outcome);
            Assert.Equal("strategy rejected", legacyEvents[1].FailureReason);
            Assert.Equal("False", legacyEvents[1].Properties["success"]?.ToString());

            AssertCanonicalLifecycleEvents(
                observer,
                AiRuntimeLifecycleEvents.HostCreationRequested,
                AiRuntimeLifecycleEvents.HostCreationStarted,
                AiRuntimeLifecycleEvents.HostCreationFailed);
        }

        /// <summary>
        /// Verifies that strategy exceptions record started and failed events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartRuntimeAsync_Should_Record_Started_And_Failed_Events_When_Strategy_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var manager = CreateManager(new ThrowingHostCreationStrategy(), observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await manager
                        .StartRuntimeAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            var legacyEvents = GetLegacyEvents(observer);

            Assert.Equal(2, legacyEvents.Length);
            AssertStartedEvent(legacyEvents[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, legacyEvents[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, legacyEvents[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), legacyEvents[1].FailureReason);
            Assert.Equal("strategy exploded", legacyEvents[1].Properties["exception.message"]?.ToString());

            AssertCanonicalLifecycleEvents(
                observer,
                AiRuntimeLifecycleEvents.HostCreationRequested,
                AiRuntimeLifecycleEvents.HostCreationStarted,
                AiRuntimeLifecycleEvents.HostCreationFailed);
        }

        /// <summary>
        /// Verifies that successful runtime host creation control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartRuntimeAsync_Should_Record_Succeeded_HostCreation_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var manager = CreateManager(new SuccessfulHostCreationStrategy(), observer);

            var result = await manager
                .StartRuntimeAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-host-creation.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.scaling.runtime-host-creation.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("Fixture", ledger.Entries[1].Metadata!["hostCreationMode"]);
            Assert.Equal("True", ledger.Entries[1].Metadata!["success"]);
        }

        /// <summary>
        /// Gets the legacy operation-envelope events without canonical lifecycle facts.
        /// </summary>
        /// <param name="observer">The capturing observer.</param>
        /// <returns>The legacy operation-envelope events in emission order.</returns>
        private static AiControlPlaneEvent[] GetLegacyEvents(
            CapturingControlPlaneObserver observer)
        {
            return observer.Events
                .Where(controlPlaneEvent =>
                    string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
                .ToArray();
        }

        /// <summary>
        /// Verifies the canonical host lifecycle facts independently from legacy envelopes.
        /// </summary>
        /// <param name="observer">The capturing observer.</param>
        /// <param name="expectedEventTypes">The expected canonical event sequence.</param>
        private static void AssertCanonicalLifecycleEvents(
            CapturingControlPlaneObserver observer,
            params string[] expectedEventTypes)
        {
            var canonicalEventTypes = observer.Events
                .Where(controlPlaneEvent =>
                    !string.IsNullOrWhiteSpace(controlPlaneEvent.SemanticEventType))
                .Select(controlPlaneEvent => controlPlaneEvent.SemanticEventType!)
                .ToArray();

            Assert.Equal(expectedEventTypes, canonicalEventTypes);
        }

        /// <summary>
        /// Asserts the common host creation started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, controlPlaneEvent.Area);
            Assert.Equal("runtime-host-creation", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            Assert.Equal("Fixture", controlPlaneEvent.Properties["hostCreationMode"]?.ToString());
        }

        /// <summary>
        /// Creates a runtime host creation manager.
        /// </summary>
        /// <param name="strategy">The host creation strategy.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The runtime host creation manager.</returns>
        private static AiRuntimeHostCreationManager CreateManager(
            IAiRuntimeHostCreationStrategy strategy,
            IAiControlPlaneObserver observer)
        {
            return CreateManager(new[] { strategy }, observer);
        }

        /// <summary>
        /// Creates a runtime host creation manager.
        /// </summary>
        /// <param name="strategies">The host creation strategies.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The runtime host creation manager.</returns>
        private static AiRuntimeHostCreationManager CreateManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            IAiControlPlaneObserver observer)
        {
            return new AiRuntimeHostCreationManager(
                strategies,
                NullLogger<AiRuntimeHostCreationManager>.Instance,
                observer);
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
                ProviderName = "http",
                TransportName = "http",
                TransportEndpoint = "http://localhost:5001",
                HostCreationMode = AiRuntimeHostCreationMode.Fixture,
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
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
        /// Host creation strategy that succeeds.
        /// </summary>
        private sealed class SuccessfulHostCreationStrategy : IAiRuntimeHostCreationStrategy
        {
            /// <inheritdoc />
            public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Fixture;

            /// <inheritdoc />
            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeHostStartResult
                    {
                        Success = true,
                        ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = request.ProviderName,
                        TransportName = request.TransportName,
                        TransportEndpoint = request.TransportEndpoint
                    });
            }
        }

        /// <summary>
        /// Host creation strategy that rejects.
        /// </summary>
        private sealed class RejectingHostCreationStrategy : IAiRuntimeHostCreationStrategy
        {
            /// <inheritdoc />
            public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Fixture;

            /// <inheritdoc />
            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    AiRuntimeHostStartResult.Rejected(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        request.TransportEndpoint,
                        "strategy rejected"));
            }
        }

        /// <summary>
        /// Host creation strategy that throws.
        /// </summary>
        private sealed class ThrowingHostCreationStrategy : IAiRuntimeHostCreationStrategy
        {
            /// <inheritdoc />
            public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Fixture;

            /// <inheritdoc />
            public Task<AiRuntimeHostStartResult> StartAsync(
                AiRuntimeHostStartRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("strategy exploded");
            }
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
        /// Provides a minimal runtime observability facade for host creation observability tests.
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
