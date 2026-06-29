using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests local shared run dispatcher control-plane observability events.
    /// </summary>
    public sealed class LocalAiSharedRunDispatcherObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxx";

        /// <summary>
        /// Verifies that local shared run dispatch records started and succeeded events when queue enqueue succeeds.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Started_And_Succeeded_Events_When_Queue_Enqueue_Succeeds()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new LocalAiSharedRunDispatcher(
                new SuccessfulRuntimeQueueControlPlane(),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.SharedController, observer.Events[1].Area);
            Assert.Equal("local-shared-run-dispatch", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("shared-run-1", observer.Events[1].Properties["sharedRunId"]?.ToString());
            Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("True", observer.Events[1].Properties["success"]?.ToString());
        }

        /// <summary>
        /// Verifies that local shared run dispatch records completed-with-issues when queue enqueue returns failure.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_CompletedWithIssues_Event_When_Queue_Enqueue_Returns_Failure()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new LocalAiSharedRunDispatcher(
                new FailingRuntimeQueueControlPlane(),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("queue enqueue failed", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("queue enqueue failed", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("False", observer.Events[1].Properties["success"]?.ToString());
            Assert.Equal("queue enqueue failed", observer.Events[1].Properties["failureReason"]?.ToString());
        }

        /// <summary>
        /// Verifies that local shared run dispatch records failed events when queue enqueue throws.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Failed_Event_When_Queue_Enqueue_Throws()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new LocalAiSharedRunDispatcher(
                new ThrowingRuntimeQueueControlPlane(),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("queue exploded", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("False", observer.Events[1].Properties["success"]?.ToString());
            Assert.Equal("queue exploded", observer.Events[1].Properties["failureReason"]?.ToString());
        }

        /// <summary>
        /// Verifies that successful local shared run dispatch control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Succeeded_LocalDispatch_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var dispatcher = new LocalAiSharedRunDispatcher(
                new SuccessfulRuntimeQueueControlPlane(),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedcontroller.local-shared-run-dispatch.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.sharedcontroller.local-shared-run-dispatch.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("shared-run-1", ledger.Entries[1].Metadata!["sharedRunId"]);
            Assert.Equal("True", ledger.Entries[1].Metadata!["success"]);
        }

        /// <summary>
        /// Verifies that failed local shared run dispatch control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Failed_LocalDispatch_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var dispatcher = new LocalAiSharedRunDispatcher(
                new ThrowingRuntimeQueueControlPlane(),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedcontroller.local-shared-run-dispatch.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Failed, ledger.Entries[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), ledger.Entries[1].Reason);
            Assert.Equal("control.sharedcontroller.local-shared-run-dispatch.failed", ledger.Entries[1].EventType);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("shared-run-1", ledger.Entries[1].Metadata!["sharedRunId"]);
            Assert.Equal("False", ledger.Entries[1].Metadata!["success"]);
            Assert.Equal("queue exploded", ledger.Entries[1].Metadata!["failureReason"]);
        }

        /// <summary>
        /// Verifies that local shared run dispatch enqueue failures are recorded to the decision ledger as completed with issues.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_CompletedWithIssues_LocalDispatch_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var dispatcher = new LocalAiSharedRunDispatcher(
                new FailingRuntimeQueueControlPlane(),
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedcontroller.local-shared-run-dispatch.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.CompletedWithIssues, ledger.Entries[1].Outcome);
            Assert.Equal("queue enqueue failed", ledger.Entries[1].Reason);
            Assert.Equal("control.sharedcontroller.local-shared-run-dispatch.completedwithissues", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("shared-run-1", ledger.Entries[1].Metadata!["sharedRunId"]);
            Assert.Equal("False", ledger.Entries[1].Metadata!["success"]);
            Assert.Equal("queue enqueue failed", ledger.Entries[1].Metadata!["failureReason"]);
        }

        /// <summary>
        /// Asserts the common local dispatch started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.SharedController, controlPlaneEvent.Area);
            Assert.Equal("local-shared-run-dispatch", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", controlPlaneEvent.Correlation.RunId);
        }

        /// <summary>
        /// Creates a shared run dispatch request.
        /// </summary>
        /// <returns>The shared run dispatch request.</returns>
        private static AiSharedRunDispatchRequest CreateRequest()
        {
            return new AiSharedRunDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                ClaimToken = "claim-1",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test local dispatch",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = "control-plane-1"
                },
                SharedRun = new AiSharedRunRecord
                {
                    SharedRunId = "shared-run-1",
                    Status = default,
                    RunRequest = new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "pipeline-1"
                    },
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(),
                    PipelineKey = "pipeline-1",
                    CorrelationId = "correlation-1",
                    RequestedBy = "unit-test",
                    Source = "unit-test",
                    Reason = "unit-test local dispatch",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>
                    {
                        ["shared.run.source"] = "unit-test"
                    },
                    ControlPlaneId = "control-plane-1"
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
        /// Base runtime queue control-plane fake for local dispatch tests.
        /// </summary>
        private abstract class TestRuntimeQueueControlPlane : IAiRuntimeQueueControlPlane
        {
            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> CancelQueuedRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(CancelQueuedRunAsync));
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> CancelRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(CancelRunAsync));
            }

            /// <inheritdoc />
            public abstract Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default);

            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> ExecuteAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(ExecuteAsync));
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> GetQueueStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(GetQueueStatusAsync));
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> GetRunStatusAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(GetRunStatusAsync));
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> PauseQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(PauseQueueAsync));
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeQueueControlPlaneResult> ResumeQueueAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return CreateUnsupportedResultAsync(request, nameof(ResumeQueueAsync));
            }

            /// <summary>
            /// Creates an unsupported operation result.
            /// </summary>
            /// <param name="request">The runtime queue control-plane request.</param>
            /// <param name="operationName">The unsupported operation name.</param>
            /// <returns>The unsupported operation result.</returns>
            protected static Task<AiRuntimeQueueControlPlaneResult> CreateUnsupportedResultAsync(
                AiRuntimeQueueControlPlaneRequest request,
                string operationName)
            {
                return Task.FromResult(
                    new AiRuntimeQueueControlPlaneResult
                    {
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Success = false,
                        FailureReason = "unsupported-test-operation",
                        Message = "Unsupported test operation.",
                        Diagnostics = new[] { operationName }
                    });
            }
        }

        /// <summary>
        /// Runtime queue control-plane that succeeds enqueue.
        /// </summary>
        private sealed class SuccessfulRuntimeQueueControlPlane : TestRuntimeQueueControlPlane
        {
            /// <inheritdoc />
            public override Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeQueueControlPlaneResult
                    {
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Success = true,
                        Message = "queued",
                        Diagnostics = Array.Empty<string>()
                    });
            }
        }

        /// <summary>
        /// Runtime queue control-plane that returns failure.
        /// </summary>
        private sealed class FailingRuntimeQueueControlPlane : TestRuntimeQueueControlPlane
        {
            /// <inheritdoc />
            public override Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeQueueControlPlaneResult
                    {
                        Operation = request.Operation,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Success = false,
                        FailureReason = "queue enqueue failed",
                        Message = "queue enqueue failed",
                        Diagnostics = new[] { "queue enqueue failed" }
                    });
            }
        }

        /// <summary>
        /// Runtime queue control-plane that throws.
        /// </summary>
        private sealed class ThrowingRuntimeQueueControlPlane : TestRuntimeQueueControlPlane
        {
            /// <inheritdoc />
            public override Task<AiRuntimeQueueControlPlaneResult> EnqueueRunAsync(
                AiRuntimeQueueControlPlaneRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("queue exploded");
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
        /// Provides a minimal runtime observability facade for local dispatch observability tests.
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