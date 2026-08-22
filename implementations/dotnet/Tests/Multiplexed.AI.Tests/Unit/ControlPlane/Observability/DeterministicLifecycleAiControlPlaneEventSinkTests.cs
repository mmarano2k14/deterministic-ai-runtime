using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests deterministic lifecycle waiting over the centralized realtime projection.
    /// </summary>
    public sealed class DeterministicLifecycleAiControlPlaneEventSinkTests
    {
        /// <summary>
        /// Verifies that a wait resolves from bounded history when subscription happens after emission.
        /// </summary>
        [Fact]
        public async Task WaitForAsync_Should_Close_Subscribe_After_Emission_Race()
        {
            var sink = new DeterministicLifecycleAiControlPlaneEventSink();
            var expected = CreateEvent("event-1", "execution-1");

            await sink.RecordAsync(expected, CancellationToken.None).ConfigureAwait(false);

            var observed = await sink.WaitForAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.ChildDag.ContinuationDelivered,
                    ExecutionId = "execution-1"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.Same(expected, observed);
        }

        /// <summary>
        /// Verifies that a pending wait resolves when the matching canonical event arrives later.
        /// </summary>
        [Fact]
        public async Task WaitForAsync_Should_Resolve_When_Matching_Event_Arrives()
        {
            var sink = new DeterministicLifecycleAiControlPlaneEventSink();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var waitTask = sink.WaitForAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.ChildDag.ContinuationDelivered,
                    ExecutionId = "execution-2"
                },
                watchdog.Token);

            await sink.RecordAsync(
                CreateEvent("event-wrong", "execution-other"),
                CancellationToken.None).ConfigureAwait(false);

            Assert.False(waitTask.IsCompleted);

            var expected = CreateEvent("event-2", "execution-2");
            await sink.RecordAsync(expected, CancellationToken.None).ConfigureAwait(false);

            var observed = await waitTask.ConfigureAwait(false);
            Assert.Same(expected, observed);
        }

        /// <summary>
        /// Verifies that hard watchdog cancellation remains the liveness boundary for an unmet wait.
        /// </summary>
        [Fact]
        public async Task WaitForAsync_Should_Honor_Hard_Watchdog_Cancellation()
        {
            var sink = new DeterministicLifecycleAiControlPlaneEventSink();
            using var watchdog = new CancellationTokenSource();

            var waitTask = sink.WaitForAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.ChildDag.ContinuationDelivered,
                    ExecutionId = "never-arrives"
                },
                watchdog.Token);

            watchdog.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => waitTask).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that waits cannot be registered for events excluded from the realtime projection contract.
        /// </summary>
        [Fact]
        public async Task WaitForAsync_Should_Reject_Event_Without_Realtime_Projection()
        {
            var sink = new DeterministicLifecycleAiControlPlaneEventSink();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sink.WaitForAsync(
                    new AiDeterministicLifecycleEventCriteria
                    {
                        SemanticEventType = AiEngineEvents.Policy.Evaluated
                    },
                    CancellationToken.None)).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that the deterministic observer is the realtime projection selected by the existing Event Manager.
        /// </summary>
        [Fact]
        public async Task CompositeObserver_Should_Deliver_Realtime_Canonical_Event_To_Deterministic_Observer()
        {
            var sink = new DeterministicLifecycleAiControlPlaneEventSink();
            var eventManager = new CompositeAiControlPlaneObserver(new IAiControlPlaneEventSink[] { sink });
            var expected = CreateEvent("event-3", "execution-3");

            await eventManager.RecordAsync(expected, CancellationToken.None).ConfigureAwait(false);

            var observed = await sink.WaitForAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiEngineEvents.ChildDag.ContinuationDelivered,
                    EventId = "event-3"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.Same(expected, observed);
        }

        /// <summary>
        /// Verifies that a durable canonical fact can be resolved without waiting for a realtime event.
        /// </summary>
        [Fact]
        public async Task WaitForAsync_Should_Return_Existing_Durable_Evidence_Before_Subscription()
        {
            var expected = CreateRuntimeLifecycleEvent("durable-event-1", "execution-durable-1");
            var evidenceReader = new SequenceEvidenceReader(expected);
            var sink = new DeterministicLifecycleAiControlPlaneEventSink(
                new IAiDeterministicLifecycleEvidenceReader[] { evidenceReader });

            var observed = await sink.WaitForAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiRuntimeLifecycleEvents.RuntimeRegistered,
                    ExecutionId = "execution-durable-1"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.Same(expected, observed);
            Assert.Equal(1, evidenceReader.ReadCount);
        }

        /// <summary>
        /// Verifies the durable-evidence → subscribe → durable-evidence race-closing sequence.
        /// </summary>
        [Fact]
        public async Task WaitForAsync_Should_Recheck_Durable_Evidence_After_Subscription()
        {
            var expected = CreateRuntimeLifecycleEvent("durable-event-2", "execution-durable-2");
            var evidenceReader = new SequenceEvidenceReader(null, expected);
            var sink = new DeterministicLifecycleAiControlPlaneEventSink(
                new IAiDeterministicLifecycleEvidenceReader[] { evidenceReader });

            var observed = await sink.WaitForAsync(
                new AiDeterministicLifecycleEventCriteria
                {
                    SemanticEventType = AiRuntimeLifecycleEvents.RuntimeRegistered,
                    ExecutionId = "execution-durable-2"
                },
                CancellationToken.None).ConfigureAwait(false);

            Assert.Same(expected, observed);
            Assert.Equal(2, evidenceReader.ReadCount);
            Assert.Contains(expected, sink.GetRecentEvents());
        }

        /// <summary>
        /// Verifies that DI exposes one shared deterministic observer/sink instance.
        /// </summary>
        [Fact]
        public void AddAiControlPlaneDeterministicLifecycleObservation_Should_Register_Shared_Realtime_Projection()
        {
            var services = new ServiceCollection();
            services.AddAiControlPlaneDeterministicLifecycleObservation();

            using var provider = services.BuildServiceProvider(validateScopes: true);

            var observer = provider.GetRequiredService<IAiDeterministicLifecycleObserver>();
            var sink = provider.GetRequiredService<DeterministicLifecycleAiControlPlaneEventSink>();
            var eventSink = Assert.Single(provider.GetServices<IAiControlPlaneEventSink>());

            Assert.Same(sink, observer);
            Assert.Same(sink, eventSink);
            Assert.Equal(AiEngineEventProjectionTarget.Realtime, sink.ProjectionTarget);
        }

        private static AiControlPlaneEvent CreateRuntimeLifecycleEvent(
            string eventId,
            string executionId)
        {
            return new AiControlPlaneEvent
            {
                EventId = eventId,
                SemanticEventType = AiRuntimeLifecycleEvents.RuntimeRegistered,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.InstanceRegistry,
                Operation = AiRuntimeLifecycleEvents.RuntimeRegistered,
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = $"correlation:{executionId}",
                    ExecutionId = executionId,
                    RuntimeInstanceId = "runtime-1"
                }
            };
        }

        private sealed class SequenceEvidenceReader : IAiDeterministicLifecycleEvidenceReader
        {
            private readonly Queue<AiControlPlaneEvent?> sequence;

            public SequenceEvidenceReader(params AiControlPlaneEvent?[] sequence)
            {
                this.sequence = new Queue<AiControlPlaneEvent?>(sequence);
            }

            public int ReadCount { get; private set; }

            public Task<AiControlPlaneEvent?> FindAsync(
                AiDeterministicLifecycleEventCriteria criteria,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.ReadCount++;

                return Task.FromResult(
                    this.sequence.Count == 0
                        ? null
                        : this.sequence.Dequeue());
            }
        }

        private static AiControlPlaneEvent CreateEvent(string eventId, string executionId)
        {
            return new AiControlPlaneEvent
            {
                EventId = eventId,
                SemanticEventType = AiEngineEvents.ChildDag.ContinuationDelivered,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ChildDag,
                Operation = "continuation-delivered",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = $"correlation:{executionId}",
                    ExecutionId = executionId,
                    RuntimeInstanceId = "runtime-1"
                }
            };
        }
    }
}
