using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests shared queue pump control-plane observability events.
    /// </summary>
    public sealed class AiSharedQueuePumpObservabilityTests
    {
        /// <summary>
        /// Verifies that a successful pump cycle records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_Started_And_Succeeded_Events_When_Dispatch_Succeeds()
        {
            var observer = new CapturingControlPlaneObserver();

            var pump = CreatePump(
                new SequenceSharedQueueDispatcher(
                    new AiSharedQueueDispatchResult
                    {
                        Success = true
                    }),
                observer);

            var result = await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneArea.SharedQueue, observer.Events[0].Area);
            Assert.Equal("shared-queue-pump-cycle", observer.Events[0].Operation);
            Assert.Null(observer.Events[0].Outcome);

            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.SharedQueue, observer.Events[1].Area);
            Assert.Equal("shared-queue-pump-cycle", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);

            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("worker-1", observer.Events[1].Correlation.WorkerId);
            Assert.Equal("pipeline-1", observer.Events[1].Correlation.PipelineKey);
            Assert.Equal("tenant-a", observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal("control-plane-1", observer.Events[1].Properties["controlPlaneId"]?.ToString());
            Assert.Equal("1", observer.Events[1].Properties["attemptedDispatchCount"]?.ToString());
            Assert.Equal("1", observer.Events[1].Properties["successfulDispatchCount"]?.ToString());
            Assert.Equal("0", observer.Events[1].Properties["failedDispatchCount"]?.ToString());
        }

        /// <summary>
        /// Verifies that a pump cycle with dispatch failures records a completed-with-issues event.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_CompletedWithIssues_Event_When_Dispatch_Fails()
        {
            var observer = new CapturingControlPlaneObserver();

            var pump = CreatePump(
                new SequenceSharedQueueDispatcher(
                    new AiSharedQueueDispatchResult
                    {
                        Success = false,
                        FailureReason = "dispatch failed"
                    }),
                observer);

            var result = await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("shared-queue-dispatch-failures-detected", observer.Events[1].FailureReason);
            Assert.Equal("1", observer.Events[1].Properties["attemptedDispatchCount"]?.ToString());
            Assert.Equal("0", observer.Events[1].Properties["successfulDispatchCount"]?.ToString());
            Assert.Equal("1", observer.Events[1].Properties["failedDispatchCount"]?.ToString());
        }

        /// <summary>
        /// Verifies that an exception during pump dispatch records a failed event.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_Failed_Event_When_Dispatch_Throws()
        {
            var observer = new CapturingControlPlaneObserver();

            var pump = CreatePump(
                new ThrowingSharedQueueDispatcher(),
                observer);

            var result = await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, observer.Events.Count);

            Assert.Equal(AiControlPlaneEventType.OperationStarted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal(typeof(InvalidOperationException).FullName, observer.Events[1].Properties["exception.type"]?.ToString());
            Assert.Equal("dispatcher failed", observer.Events[1].Properties["exception.message"]?.ToString());
        }

        /// <summary>
        /// Verifies that a disabled pump records a denied completed event.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_Denied_Event_When_Pump_Is_Disabled()
        {
            var observer = new CapturingControlPlaneObserver();

            var pump = CreatePump(
                new SequenceSharedQueueDispatcher(
                    new AiSharedQueueDispatchResult
                    {
                        Success = true
                    }),
                observer,
                enabled: false);

            var result = await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Single(observer.Events);

            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[0].EventType);
            Assert.Equal(AiControlPlaneArea.SharedQueue, observer.Events[0].Area);
            Assert.Equal("shared-queue-pump-cycle", observer.Events[0].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[0].Outcome);
            Assert.Equal("shared-queue-pump-disabled", observer.Events[0].FailureReason);
            Assert.Equal("False", observer.Events[0].Properties["enabled"]?.ToString());
        }

        /// <summary>
        /// Verifies that shared queue pump control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_SharedQueue_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var pump = CreatePump(
                new SequenceSharedQueueDispatcher(
                    new AiSharedQueueDispatchResult
                    {
                        Success = true
                    }),
                observer);

            await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.Queue, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedqueue.shared-queue-pump-cycle.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.Queue, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.sharedqueue.shared-queue-pump-cycle.succeeded", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("worker-1", ledger.Entries[1].Context.WorkerId);
            Assert.Equal("correlation-1", ledger.Entries[1].Context.CorrelationId);
            Assert.Equal("tenant-a", ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal("control-plane-1", ledger.Entries[1].Metadata!["control.plane.id"]);
            Assert.Equal("pipeline-1", ledger.Entries[1].Metadata!["pipeline.key"]);
            Assert.Equal("1", ledger.Entries[1].Metadata!["attemptedDispatchCount"]);
            Assert.Equal("1", ledger.Entries[1].Metadata!["successfulDispatchCount"]);
            Assert.Equal("0", ledger.Entries[1].Metadata!["failedDispatchCount"]);
        }

        /// <summary>
        /// Verifies that shared queue pump dispatch failures are recorded to the decision ledger as completed-with-issues.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_CompletedWithIssues_SharedQueue_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var pump = CreatePump(
                new SequenceSharedQueueDispatcher(
                    new AiSharedQueueDispatchResult
                    {
                        Success = false,
                        FailureReason = "dispatch failed"
                    }),
                observer);

            await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.Queue, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedqueue.shared-queue-pump-cycle.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.Queue, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.CompletedWithIssues, ledger.Entries[1].Outcome);
            Assert.Equal("shared-queue-dispatch-failures-detected", ledger.Entries[1].Reason);
            Assert.Equal("control.sharedqueue.shared-queue-pump-cycle.completedwithissues", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("worker-1", ledger.Entries[1].Context.WorkerId);
            Assert.Equal("correlation-1", ledger.Entries[1].Context.CorrelationId);
            Assert.Equal("tenant-a", ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal("control-plane-1", ledger.Entries[1].Metadata!["control.plane.id"]);
            Assert.Equal("pipeline-1", ledger.Entries[1].Metadata!["pipeline.key"]);
            Assert.Equal("1", ledger.Entries[1].Metadata!["attemptedDispatchCount"]);
            Assert.Equal("0", ledger.Entries[1].Metadata!["successfulDispatchCount"]);
            Assert.Equal("1", ledger.Entries[1].Metadata!["failedDispatchCount"]);
        }

        /// <summary>
        /// Verifies that shared queue pump exceptions are recorded to the decision ledger as failed events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PumpOnceAsync_Should_Record_Failed_SharedQueue_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var pump = CreatePump(
                new ThrowingSharedQueueDispatcher(),
                observer);

            var result = await pump
                .PumpOnceAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.Queue, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedqueue.shared-queue-pump-cycle.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.Queue, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Failed, ledger.Entries[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), ledger.Entries[1].Reason);
            Assert.Equal("control.sharedqueue.shared-queue-pump-cycle.failed", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("worker-1", ledger.Entries[1].Context.WorkerId);
            Assert.Equal("correlation-1", ledger.Entries[1].Context.CorrelationId);
            Assert.Equal("tenant-a", ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal("control-plane-1", ledger.Entries[1].Metadata!["control.plane.id"]);
            Assert.Equal("pipeline-1", ledger.Entries[1].Metadata!["pipeline.key"]);
            Assert.Equal(typeof(InvalidOperationException).FullName, ledger.Entries[1].Metadata!["exception.type"]);
            Assert.Equal("dispatcher failed", ledger.Entries[1].Metadata!["exception.message"]);
        }

        /// <summary>
        /// Creates a shared queue pump for tests.
        /// </summary>
        /// <param name="dispatcher">The shared queue dispatcher.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <param name="enabled">A value indicating whether the pump is enabled.</param>
        /// <returns>The shared queue pump.</returns>
        private static AiSharedQueuePump CreatePump(
            IAiSharedQueueDispatcher dispatcher,
            IAiControlPlaneObserver observer,
            bool enabled = true)
        {
            return new AiSharedQueuePump(
                dispatcher,
                Options.Create(
                    new AiSharedQueuePumpOptions
                    {
                        Enabled = enabled,
                        MaxDispatchesPerCycle = 1,
                        DefaultClaimTtl = TimeSpan.FromSeconds(30),
                        StopCycleWhenNoItemAvailable = true,
                        StopCycleOnDispatchFailure = true,
                        WorkerId = "worker-from-options",
                        Source = "unit-test"
                    }),
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                NullLogger<AiSharedQueuePump>.Instance,
                observer);
        }

        /// <summary>
        /// Creates a shared queue pump request.
        /// </summary>
        /// <returns>The shared queue pump request.</returns>
        private static AiSharedQueuePumpRequest CreateRequest()
        {
            return new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1",
                PumpWorkerId = "worker-1",
                TenantId = "tenant-a",
                PipelineKey = "pipeline-1",
                MaxDispatches = 1,
                ClaimTtl = TimeSpan.FromSeconds(30),
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test pump",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = "control-plane-1"
                }
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
        /// Shared queue dispatcher that returns configured dispatch results in sequence.
        /// </summary>
        private sealed class SequenceSharedQueueDispatcher : IAiSharedQueueDispatcher
        {
            private readonly Queue<AiSharedQueueDispatchResult> results;

            /// <summary>
            /// Initializes a new instance of the <see cref="SequenceSharedQueueDispatcher"/> class.
            /// </summary>
            /// <param name="results">The dispatch results.</param>
            public SequenceSharedQueueDispatcher(
                params AiSharedQueueDispatchResult[] results)
            {
                this.results = new Queue<AiSharedQueueDispatchResult>(results);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueDispatchResult> DispatchNextAsync(
                AiSharedQueueDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                if (this.results.Count == 0)
                {
                    return Task.FromResult(
                        new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            NoItemAvailable = true
                        });
                }

                return Task.FromResult(this.results.Dequeue());
            }
        }

        /// <summary>
        /// Shared queue dispatcher that always throws.
        /// </summary>
        private sealed class ThrowingSharedQueueDispatcher : IAiSharedQueueDispatcher
        {
            /// <inheritdoc />
            public Task<AiSharedQueueDispatchResult> DispatchNextAsync(
                AiSharedQueueDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("dispatcher failed");
            }
        }
    }
}