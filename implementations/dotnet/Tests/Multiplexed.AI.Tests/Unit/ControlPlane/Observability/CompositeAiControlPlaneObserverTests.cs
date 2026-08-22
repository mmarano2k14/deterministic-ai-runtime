using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests the composite control-plane observer and centralized canonical-event projection dispatch.
    /// </summary>
    public sealed class CompositeAiControlPlaneObserverTests
    {
        /// <summary>
        /// Verifies that legacy generic events preserve the historical fan-out behavior.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Forward_Legacy_Event_To_All_Registered_Sinks()
        {
            var firstSink = new CapturingControlPlaneEventSink();
            var secondSink = new CapturingControlPlaneEventSink();

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    firstSink,
                    secondSink
                });

            var controlPlaneEvent = CreateLegacyEvent();

            await observer.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Same(controlPlaneEvent, firstSink.Events[0]);
            Assert.Same(controlPlaneEvent, secondSink.Events[0]);
        }

        /// <summary>
        /// Verifies that legacy generic dispatch still invokes remaining sinks when one sink fails.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Invoke_Remaining_Legacy_Sinks_When_One_Sink_Fails()
        {
            var failingSink = new FailingControlPlaneEventSink();
            var capturingSink = new CapturingControlPlaneEventSink();

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    failingSink,
                    capturingSink
                });

            var controlPlaneEvent = CreateLegacyEvent();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => observer.RecordAsync(controlPlaneEvent, CancellationToken.None)).ConfigureAwait(false);

            Assert.Same(controlPlaneEvent, capturingSink.Events[0]);
        }

        /// <summary>
        /// Verifies that canonical events are routed only to projection surfaces selected by the central catalog.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Route_Canonical_Event_Using_Central_Projection_Catalog()
        {
            var ledgerSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Ledger);
            var loggingSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Logging);
            var metricsSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Metrics);
            var legacyExtensionSink = new CapturingControlPlaneEventSink();

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    ledgerSink,
                    loggingSink,
                    metricsSink,
                    legacyExtensionSink
                });

            var controlPlaneEvent = CreateCanonicalEvent(AiEngineEvents.Policy.Allowed);

            await observer.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledgerSink.Events);
            Assert.Single(loggingSink.Events);
            Assert.Single(metricsSink.Events);
            Assert.Empty(legacyExtensionSink.Events);
        }

        /// <summary>
        /// Verifies that a best-effort projection failure does not fail canonical event emission.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Not_Fail_Canonical_Event_When_BestEffort_Projection_Fails()
        {
            var ledgerSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Ledger);
            var loggingSink = new FailingProjectionSink(AiEngineEventProjectionTarget.Logging);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    ledgerSink,
                    loggingSink
                });

            var controlPlaneEvent = CreateCanonicalEvent(AiEngineEvents.Policy.Allowed);

            await observer.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledgerSink.Events);
            Assert.Equal(1, loggingSink.InvocationCount);
        }

        /// <summary>
        /// Verifies that a required durable projection failure fails canonical event emission after other applicable sinks are attempted.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Fail_Canonical_Event_When_RequiredDurable_Projection_Fails()
        {
            var ledgerSink = new FailingProjectionSink(AiEngineEventProjectionTarget.Ledger);
            var loggingSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Logging);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    ledgerSink,
                    loggingSink
                });

            var controlPlaneEvent = CreateCanonicalEvent(AiEngineEvents.Policy.Allowed);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => observer.RecordAsync(controlPlaneEvent, CancellationToken.None)).ConfigureAwait(false);

            Assert.Equal(1, ledgerSink.InvocationCount);
            Assert.Single(loggingSink.Events);
        }

        /// <summary>
        /// Verifies that canonical emission fails before fan-out when a required projection sink is missing.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Fail_Canonical_Event_When_Required_Projection_Sink_Is_Missing()
        {
            var loggingSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Logging);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    loggingSink
                });

            var controlPlaneEvent = CreateCanonicalEvent(AiEngineEvents.Policy.Allowed);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => observer.RecordAsync(controlPlaneEvent, CancellationToken.None)).ConfigureAwait(false);

            Assert.Contains(nameof(AiEngineEventProjectionTarget.Ledger), exception.Message, StringComparison.Ordinal);
            Assert.Empty(loggingSink.Events);
        }

        /// <summary>
        /// Verifies that one centralized projection surface cannot have multiple sink owners.
        /// </summary>
        [Fact]
        public void Constructor_Should_Reject_Duplicate_Projection_Sink_Owners()
        {
            var firstLedgerSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Ledger);
            var secondLedgerSink = new CapturingProjectionSink(AiEngineEventProjectionTarget.Ledger);

            Assert.Throws<InvalidOperationException>(
                () => new CompositeAiControlPlaneObserver(
                    new IAiControlPlaneEventSink[]
                    {
                        firstLedgerSink,
                        secondLedgerSink
                    }));
        }

        private static AiControlPlaneEvent CreateLegacyEvent()
        {
            return new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "test",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation"
                }
            };
        }

        private static AiControlPlaneEvent CreateCanonicalEvent(
            string semanticEventType)
        {
            return new AiControlPlaneEvent
            {
                EventId = "canonical-event-1",
                SemanticEventType = semanticEventType,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Admission,
                Operation = "canonical-test",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "canonical-correlation-1",
                    ExecutionId = "execution-1"
                }
            };
        }

        private sealed class CapturingControlPlaneEventSink : IAiControlPlaneEventSink
        {
            public List<AiControlPlaneEvent> Events { get; } = new();

            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                this.Events.Add(controlPlaneEvent);

                return Task.CompletedTask;
            }
        }

        private sealed class FailingControlPlaneEventSink : IAiControlPlaneEventSink
        {
            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Sink failed.");
            }
        }

        private sealed class CapturingProjectionSink : IAiControlPlaneEventProjectionSink
        {
            public CapturingProjectionSink(
                AiEngineEventProjectionTarget projectionTarget)
            {
                this.ProjectionTarget = projectionTarget;
            }

            public AiEngineEventProjectionTarget ProjectionTarget { get; }

            public List<AiControlPlaneEvent> Events { get; } = new();

            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                this.Events.Add(controlPlaneEvent);
                return Task.CompletedTask;
            }
        }

        private sealed class FailingProjectionSink : IAiControlPlaneEventProjectionSink
        {
            public FailingProjectionSink(
                AiEngineEventProjectionTarget projectionTarget)
            {
                this.ProjectionTarget = projectionTarget;
            }

            public AiEngineEventProjectionTarget ProjectionTarget { get; }

            public int InvocationCount { get; private set; }

            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                this.InvocationCount++;
                throw new InvalidOperationException("Projection failed.");
            }
        }
    }
}
