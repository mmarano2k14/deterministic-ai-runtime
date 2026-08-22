using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Policies;
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
        /// Verifies that ledger failures preserve the historical best-effort behavior for generic control-plane events.
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
        /// Verifies that canonical semantic ledger failures are surfaced to the Event Manager instead of being swallowed by the sink.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Throw_When_Canonical_Semantic_Ledger_Projection_Fails()
        {
            var observability = new FakeRuntimeObservability(new ThrowingDecisionLedgerRecorder());
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventId = "policy-event-1",
                SemanticEventType = AiEngineEvents.Policy.Allowed,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Admission,
                Operation = "policy-evaluation",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "policy-correlation-1",
                    ExecutionId = "execution-1"
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sink.RecordAsync(controlPlaneEvent, CancellationToken.None)).ConfigureAwait(false);
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
            Assert.Equal(AiDecisionLedgerCategory.Control, ledger.Entries[0].Category);
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
                EventId = "event-1",
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
            Assert.Equal("control-plane-event:event-1", ledger.Entries[0].Context.ExecutionId);
            Assert.Equal("test-correlation", ledger.Entries[0].Context.CorrelationId);
        }

        /// <summary>
        /// Verifies that a canonical semantic event type is projected without rebuilding a competing ledger event string.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Preserve_Canonical_Semantic_EventType_When_Provided()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventId = "recovery-event-1",
                SemanticEventType = AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Recovery,
                Operation = "execution-recovery",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                CausationId = "recovery-cause-1",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "recovery-correlation-1",
                    ExecutionId = "execution-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            var entry = Assert.Single(ledger.Entries);
            Assert.Equal(AiEngineEvents.Recovery.ExecutionRecoveryCompleted, entry.EventType);
            Assert.Equal("recovery-event-1", entry.Metadata!["event.id"]);
            Assert.Equal(AiEngineEvents.Recovery.ExecutionRecoveryCompleted, entry.Metadata["event.semanticType"]);
            Assert.Equal("recovery-cause-1", entry.Metadata["event.causationId"]);
        }

        /// <summary>
        /// Verifies that canonical policy events preserve the existing policy Ledger category, outcome, reason, and correlation.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Project_Canonical_Policy_Event_With_Existing_Ledger_Semantics()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventId = "policy-event-1",
                SemanticEventType = AiEngineEvents.Policy.Allowed,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Policy,
                Operation = "policy.execute",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Message = "Policy allowed execution.",
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "execution-1",
                    ExecutionId = "execution-1",
                    PipelineName = "pipeline-a",
                    PipelineKey = "pipeline-a",
                    RuntimeInstanceId = "worker-1",
                    WorkerId = "worker-1"
                },
                Properties = new Dictionary<string, object?>
                {
                    [AiPolicyMetadataKeys.Name] = "RiskPolicy",
                    [AiPolicyMetadataKeys.Kind] = "Risk",
                    [AiStepMetadataKeys.StepName] = "risk-step",
                    [AiStepMetadataKeys.StepId] = "risk-step",
                    [AiStepMetadataKeys.StepKey] = "risk-step"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            var entry = Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerCategory.Policy, entry.Category);
            Assert.Equal(AiEngineEvents.Policy.Allowed, entry.EventType);
            Assert.Equal(AiDecisionLedgerOutcome.Allowed, entry.Outcome);
            Assert.Equal("Policy allowed execution.", entry.Reason);
            Assert.Equal("execution-1", entry.Context.ExecutionId);
            Assert.Equal("pipeline-a", entry.Context.PipelineName);
            Assert.Equal("risk-step", entry.Context.StepId);
            Assert.Equal("risk-step", entry.Context.StepKey);
            Assert.Equal("worker-1", entry.Context.WorkerId);
            Assert.Equal("RiskPolicy", entry.Metadata![AiPolicyMetadataKeys.Name]);
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
        /// Verifies that admission control-plane events are recorded under the policy ledger category.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Map_Admission_Area_To_Policy_Ledger_Category()
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
            Assert.Equal(AiDecisionLedgerCategory.Admission, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, ledger.Entries[0].Outcome);
        }

        /// <summary>
        /// Verifies that instance registry control-plane events are recorded under the runtime instance ledger category.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Map_InstanceRegistry_Area_To_RuntimeInstance_Ledger_Category()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.InstanceRegistry,
                Operation = "heartbeat",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RuntimeInstanceId = "runtime-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerCategory.RuntimeInstance, ledger.Entries[0].Category);
        }

        /// <summary>
        /// Verifies that shared controller control-plane events are recorded under the shared controller ledger category.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Map_SharedController_Area_To_SharedController_Ledger_Category()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.SharedController,
                Operation = "assign",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RunId = "run-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
        }

        /// <summary>
        /// Verifies that scaling control-plane events are recorded under the scaling ledger category.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Map_Scaling_Area_To_Scaling_Ledger_Category()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Scaling,
                Operation = "scale-out-requested",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RunId = "run-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
        }

        /// <summary>
        /// Verifies that recovery control-plane events are recorded under the recovery ledger category.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RecordAsync_Should_Map_Recovery_Area_To_Recovery_Ledger_Category()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);

            var sink = new RuntimeObservabilityAiControlPlaneEventSink(observability);

            var controlPlaneEvent = new AiControlPlaneEvent
            {
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Recovery,
                Operation = "requeue-unfinished-runs",
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "test-correlation",
                    RuntimeInstanceId = "runtime-1"
                }
            };

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(ledger.Entries);
            Assert.Equal(AiDecisionLedgerCategory.Recovery, ledger.Entries[0].Category);
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