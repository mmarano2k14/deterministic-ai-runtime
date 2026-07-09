using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Scaling;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests store-backed runtime scale-out request publisher control-plane observability events.
    /// </summary>
    public sealed class StoreBackedAiRuntimeScaleOutRequestPublisherObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxxx";

        /// <summary>
        /// Verifies that successful scale-out request publication records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Started_And_Succeeded_Events_When_Request_Is_Persisted()
        {
            var observer = new CapturingControlPlaneObserver();
            var store = new CapturingScaleOutRequestStore();
            var publisher = CreatePublisher(store, observer);

            var result = await publisher
                .PublishAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("scale-out-shared-run-1", result.ScaleOutRequestId);
            Assert.Single(store.CreatedRecords);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, observer.Events[1].Area);
            Assert.Equal("runtime-scale-out-request-publish", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("pipeline-1", observer.Events[1].Correlation.PipelineKey);
            Assert.Equal("scale-out-shared-run-1", observer.Events[1].Properties["scaleOutRequestId"]?.ToString());
            Assert.Equal("control-plane-1", observer.Events[1].Properties["controlPlaneId"]?.ToString());
            Assert.Equal("http", observer.Events[1].Properties["providerHint"]?.ToString());
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("True", observer.Events[1].Properties["success"]?.ToString());
        }

        /// <summary>
        /// Verifies that scale-out request publication records failed events when the store throws.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Failed_Event_When_Store_Create_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var publisher = CreatePublisher(new ThrowingScaleOutRequestStore(), observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await publisher
                        .PublishAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("scale-out-shared-run-1", observer.Events[1].Properties["scaleOutRequestId"]?.ToString());
            Assert.Equal("store exploded", observer.Events[1].Properties["exception.message"]?.ToString());
        }

        /// <summary>
        /// Verifies that successful scale-out request publication control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Succeeded_ScaleOutPublish_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var publisher = CreatePublisher(new CapturingScaleOutRequestStore(), observer);

            var result = await publisher
                .PublishAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-request-publish.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-request-publish.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("scale-out-shared-run-1", ledger.Entries[1].Metadata!["scaleOutRequestId"]);
            Assert.Equal("http", ledger.Entries[1].Metadata!["providerHint"]);
            Assert.Equal("True", ledger.Entries[1].Metadata!["success"]);
        }

        /// <summary>
        /// Verifies that failed scale-out request publication control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task PublishAsync_Should_Record_Failed_ScaleOutPublish_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var publisher = CreatePublisher(new ThrowingScaleOutRequestStore(), observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await publisher
                        .PublishAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-request-publish.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Failed, ledger.Entries[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), ledger.Entries[1].Reason);
            Assert.Equal("control.scaling.runtime-scale-out-request-publish.failed", ledger.Entries[1].EventType);
            Assert.Equal("scale-out-shared-run-1", ledger.Entries[1].Metadata!["scaleOutRequestId"]);
            Assert.Equal("store exploded", ledger.Entries[1].Metadata!["exception.message"]);
        }

        /// <summary>
        /// Asserts the common scale-out publish started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, controlPlaneEvent.Area);
            Assert.Equal("runtime-scale-out-request-publish", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("shared-run-1", controlPlaneEvent.Correlation.RunId);
            Assert.Equal("pipeline-1", controlPlaneEvent.Correlation.PipelineKey);
        }

        /// <summary>
        /// Creates a scale-out request publisher.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The publisher.</returns>
        private static StoreBackedAiRuntimeScaleOutRequestPublisher CreatePublisher(
            IAiRuntimeScaleOutRequestStore store,
            IAiControlPlaneObserver observer)
        {
            return new StoreBackedAiRuntimeScaleOutRequestPublisher(
                store,
                new StaticAiControlPlaneIdResolver("control-plane-1"),
                Options.Create(
                    new AiRuntimeInstanceRegistrationOptions
                    {
                        ProviderName = "http"
                    }),
                observer);
        }

        /// <summary>
        /// Creates a scale-out request.
        /// </summary>
        /// <returns>The scale-out request.</returns>
        private static AiRuntimeScaleOutRequest CreateRequest()
        {
            var executionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create();

            return new AiRuntimeScaleOutRequest
            {
                SharedRunId = "shared-run-1",
                SharedRun = new AiSharedRunRecord
                {
                    SharedRunId = "shared-run-1",
                    Status = default,
                    RunRequest = new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "pipeline-1",
                        ExecutionContextSnapshot = executionContextSnapshot
                    },
                    ExecutionContextSnapshot = executionContextSnapshot,
                    PipelineKey = "pipeline-1",
                    CorrelationId = "correlation-1",
                    RequestedBy = "unit-test",
                    Source = "unit-test",
                    Reason = "unit-test scale-out",
                    SubmittedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Metadata = new Dictionary<string, string>(),
                    ControlPlaneId = "control-plane-1"
                },
                ExecutionContextSnapshot = executionContextSnapshot,
                TenantId = ExpectedTenantId,
                TenantGroupId = ExpectedTenantGroupId,
                PipelineKey = "pipeline-1",
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                PreferDedicatedCapacity = true,
                AllowSharedFallback = false,
                MaxRuntimeInstances = 3,
                RuntimeInstanceIdPrefix = "runtime",
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 100,
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 3,
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "No runtime instance can currently accept the run and scale-out is allowed.",
                CorrelationId = "correlation-1",
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
        /// Scale-out request store that captures created records.
        /// </summary>
        private class CapturingScaleOutRequestStore : IAiRuntimeScaleOutRequestStore
        {
            /// <summary>
            /// Gets the created scale-out request records.
            /// </summary>
            public List<AiRuntimeScaleOutRequestRecord> CreatedRecords { get; } = new();

            /// <inheritdoc />
            public virtual Task<AiRuntimeScaleOutRequestRecord> CreateAsync(
                AiRuntimeScaleOutRequestRecord request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);

                this.CreatedRecords.Add(request);

                return Task.FromResult(request);
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutRequestRecord?> GetAsync(
                string requestId,
                CancellationToken cancellationToken = default)
            {
                var record = this.CreatedRecords.Find(item =>
                    string.Equals(
                        item.RequestId,
                        requestId,
                        StringComparison.Ordinal));

                return Task.FromResult<AiRuntimeScaleOutRequestRecord?>(record);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListAsync(
                AiRuntimeScaleOutRequestQuery query,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>>(
                    this.CreatedRecords.ToArray());
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListPendingAsync(
                AiRuntimeScaleOutRequestQuery query,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>>(
                    this.CreatedRecords.FindAll(item => item.Status == AiRuntimeScaleOutRequestStatus.Pending).ToArray());
            }

            /// <inheritdoc />
            public Task<bool> MarkObservedAsync(
                string requestId,
                string observedBy,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkFulfilledAsync(
                string requestId,
                string fulfilledBy,
                string? runtimeInstanceId = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkRejectedAsync(
                string requestId,
                string rejectedBy,
                string reason,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkExpiredAsync(
                string requestId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkCancelledAsync(
                string requestId,
                string cancelledBy,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }
        }

        /// <summary>
        /// Scale-out request store that throws when creating a record.
        /// </summary>
        private sealed class ThrowingScaleOutRequestStore : CapturingScaleOutRequestStore
        {
            /// <inheritdoc />
            public override Task<AiRuntimeScaleOutRequestRecord> CreateAsync(
                AiRuntimeScaleOutRequestRecord request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("store exploded");
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
        /// Provides a minimal runtime observability facade for scale-out publisher observability tests.
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
    }
}
