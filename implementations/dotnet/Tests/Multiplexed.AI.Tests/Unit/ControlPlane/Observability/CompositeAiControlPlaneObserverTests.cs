using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.McpServer.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests the composite control-plane observer.
    /// </summary>
    public sealed class CompositeAiControlPlaneObserverTests
    {
        /// <summary>
        /// Verifies that the composite observer forwards an event to all registered sinks.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Forward_Event_To_All_Registered_Sinks()
        {
            var firstSink = new CapturingControlPlaneEventSink();
            var secondSink = new CapturingControlPlaneEventSink();

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    firstSink,
                    secondSink
                });

            var controlPlaneEvent = new AiControlPlaneEvent
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

            await observer.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Same(controlPlaneEvent, firstSink.Events[0]);
            Assert.Same(controlPlaneEvent, secondSink.Events[0]);
        }

        /// <summary>
        /// Verifies that the composite observer still invokes remaining sinks when one sink fails.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Invoke_Remaining_Sinks_When_One_Sink_Fails()
        {
            var failingSink = new FailingControlPlaneEventSink();
            var capturingSink = new CapturingControlPlaneEventSink();

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            failingSink,
            capturingSink
                });

            var controlPlaneEvent = new AiControlPlaneEvent
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

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => observer.RecordAsync(controlPlaneEvent, CancellationToken.None)).ConfigureAwait(false);

            Assert.Same(controlPlaneEvent, capturingSink.Events[0]);
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
    }
}