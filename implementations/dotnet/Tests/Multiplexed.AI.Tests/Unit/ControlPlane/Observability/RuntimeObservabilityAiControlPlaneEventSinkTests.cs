using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests the runtime observability control-plane event sink.
    /// </summary>
    public sealed class RuntimeObservabilityAiControlPlaneEventSinkTests
    {
        /// <summary>
        /// Verifies that ledger failures do not break control-plane event recording.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Not_Throw_When_Ledger_Fails()
        {
            var observability = new FakeRuntimeObservability(
                new ThrowingDecisionLedgerRecorder());

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

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

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "pause",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RunId = "run-1",
                    ExecutionId = "execution-1",
                    PipelineKey = "pipeline-1",
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal("execution-1", ledger.Entries[0].Context.ExecutionId);
            Assert.Equal("pause", ledger.Entries[0].Context.Operation);
            Assert.Equal("test-correlation", ledger.Entries[0].Context.CorrelationId);
            Assert.Equal("runtime-1", ledger.Entries[0].Context.RuntimeInstanceId);
            Assert.Equal("worker-1", ledger.Entries[0].Context.WorkerId);
            Assert.Equal(AiDecisionLedgerCategory.Execution, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[0].Outcome);
            Assert.Equal("control.executioncontrol.pause.succeeded", ledger.Entries[0].EventType);
        }

        /// <summary>
        /// Verifies that useful control-plane metadata is sent to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_ControlPlane_Metadata_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "resume",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RunId = "run-1",
                    ExecutionId = "execution-1",
                    PipelineName = "pipeline-name",
                    PipelineVersion = "v1",
                    PipelineKey = "pipeline-1",
                    RuntimeInstanceId = "runtime-1",
                    WorkerId = "worker-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            var metadata = ledger.Entries[0].Metadata!;

            Assert.Equal("OperationCompleted", metadata["eventType"]);
            Assert.Equal("ExecutionControl", metadata["area"]);
            Assert.Equal("resume", metadata["operation"]);
            Assert.Equal("Succeeded", metadata["outcome"]);
            Assert.Equal("test-correlation", metadata["correlationId"]);
            Assert.Equal("run-1", metadata["runId"]);
            Assert.Equal("execution-1", metadata["executionId"]);
            Assert.Equal("pipeline-name", metadata["pipelineName"]);
            Assert.Equal("v1", metadata["pipelineVersion"]);
            Assert.Equal("pipeline-1", metadata["pipelineKey"]);
            Assert.Equal("runtime-1", metadata["runtimeInstanceId"]);
            Assert.Equal("worker-1", metadata["workerId"]);
        }

        /// <summary>
        /// Verifies that custom control-plane event properties are recorded as ledger metadata.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_ControlPlane_Properties_To_Ledger_Metadata()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "cancel",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    ExecutionId = "execution-1"
                },
                Properties = new Dictionary<string, object?>
                {
                    ["requestedBy"] = "operator-1",
                    ["reason"] = "manual-cancel",
                    ["source"] = "mcp-tool"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            var metadata = ledger.Entries[0].Metadata!;

            Assert.Equal("operator-1", metadata["requestedBy"]);
            Assert.Equal("manual-cancel", metadata["reason"]);
            Assert.Equal("mcp-tool", metadata["source"]);
        }

        /// <summary>
        /// Verifies that control-plane events without an execution identifier use a stable synthetic ledger execution identifier from the run identifier.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Use_RunId_Based_Synthetic_ExecutionId_When_ExecutionId_Is_Missing()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "enqueue",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RunId = "run-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal("control-plane-run:run-1", ledger.Entries[0].Context.ExecutionId);
            Assert.Equal("run-1", ledger.Entries[0].Context.RunId);
        }

        /// <summary>
        /// Verifies that control-plane events without execution or run identifiers use a runtime-instance based synthetic ledger execution identifier.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Use_RuntimeInstanceId_Based_Synthetic_ExecutionId_When_ExecutionId_And_RunId_Are_Missing()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.InstanceRegistry,
                Operation = "health-reconcile",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RuntimeInstanceId = "runtime-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal("control-plane-runtime-instance:runtime-1", ledger.Entries[0].Context.ExecutionId);
            Assert.Equal("runtime-1", ledger.Entries[0].Context.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that control-plane events without execution, run, or runtime instance identifiers still receive a synthetic ledger execution identifier.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Use_Event_Based_Synthetic_ExecutionId_When_No_Stronger_Identifier_Exists()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Replay,
                Operation = "timeline-read",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.StartsWith("control-plane-event:", ledger.Entries[0].Context.ExecutionId);
            Assert.Equal("test-correlation", ledger.Entries[0].Context.CorrelationId);
        }

        /// <summary>
        /// Verifies that failed control-plane events are recorded with failure outcome and reason.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_Failed_ControlPlane_Event_With_Reason()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationFailed,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "resume",
                Outcome = AiControlPlaneOperationOutcome.Failed,
                FailureReason = "execution-not-found",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    ExecutionId = "execution-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerOutcome.Failed, ledger.Entries[0].Outcome);
            Assert.Equal("execution-not-found", ledger.Entries[0].Reason);
            Assert.Equal("execution-not-found", ledger.Entries[0].Metadata!["failureReason"]);
            Assert.Equal("control.executioncontrol.resume.failed", ledger.Entries[0].EventType);
        }

        /// <summary>
        /// Verifies that denied control-plane events are recorded with denied outcome and reason.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_Denied_ControlPlane_Event_With_Reason()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Admission,
                Operation = "select-runtime-instance",
                Outcome = AiControlPlaneOperationOutcome.Denied,
                FailureReason = "no-capacity",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RunId = "run-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, ledger.Entries[0].Outcome);
            Assert.Equal("no-capacity", ledger.Entries[0].Reason);
            Assert.Equal("no-capacity", ledger.Entries[0].Metadata!["failureReason"]);
            Assert.Equal("control.admission.select-runtime-instance.denied", ledger.Entries[0].EventType);
            Assert.Equal("control-plane-run:run-1", ledger.Entries[0].Context.ExecutionId);
        }

        /// <summary>
        /// Verifies that control-plane events completed with issues are recorded with the matching ledger outcome.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_CompletedWithIssues_ControlPlane_Event()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "status",
                Outcome = AiControlPlaneOperationOutcome.CompletedWithIssues,
                FailureReason = "partial-runtime-state",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    ExecutionId = "execution-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerOutcome.CompletedWithIssues, ledger.Entries[0].Outcome);
            Assert.Equal("partial-runtime-state", ledger.Entries[0].Reason);
            Assert.Equal("CompletedWithIssues", ledger.Entries[0].Metadata!["outcome"]);
            Assert.Equal("partial-runtime-state", ledger.Entries[0].Metadata!["failureReason"]);
            Assert.Equal("control.executioncontrol.status.completedwithissues", ledger.Entries[0].EventType);
        }

        /// <summary>
        /// Verifies that operation started control-plane events are recorded with the started ledger outcome.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Record_OperationStarted_ControlPlane_Event()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationStarted,
                Area = AiControlPlaneArea.ExecutionControl,
                Operation = "pause",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    ExecutionId = "execution-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.executioncontrol.pause.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal("OperationStarted", ledger.Entries[0].Metadata!["eventType"]);
            Assert.Null(ledger.Entries[0].Metadata!["outcome"]);
        }

        /// <summary>
        /// Captures decision ledger records written by the sink under test.
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
        /// Provides a minimal runtime observability facade for sink tests.
        /// </summary>
        private sealed class FakeRuntimeObservability : IAiRuntimeObservability
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeObservability"/> class.
            /// </summary>
            /// <param name="ledger">The decision ledger recorder.</param>
            public FakeRuntimeObservability(IAiDecisionLedgerRecorder ledger)
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

        /// <summary>
        /// Decision ledger recorder that always throws.
        /// </summary>
        private sealed class ThrowingDecisionLedgerRecorder : IAiDecisionLedgerRecorder
        {
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
                throw new InvalidOperationException("Ledger failed.");
            }
        }
    }
}