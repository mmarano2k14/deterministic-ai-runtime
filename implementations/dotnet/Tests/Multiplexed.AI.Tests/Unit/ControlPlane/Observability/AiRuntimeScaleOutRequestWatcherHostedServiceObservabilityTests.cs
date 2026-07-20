using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
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
    /// Tests runtime scale-out request watcher control-plane observability events.
    /// </summary>
    public sealed class AiRuntimeScaleOutRequestWatcherHostedServiceObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxxx";

        /// <summary>
        /// Verifies that a fulfilled scale-out request records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ProcessCycleAsync_Should_Record_Started_And_Succeeded_Events_When_Request_Is_Fulfilled_And_Requeued()
        {
            var observer = new CapturingControlPlaneObserver();
            var store = new CapturingScaleOutRequestStore(new[] { CreateRecord() });
            var requeueService = new CapturingScaleOutFulfilledRunRequeueService();
            var service = CreateService(
                store,
                new SuccessfulScaleOutProviderSelector("runtime-1"),
                requeueService,
                observer);

            await service
                .ProcessCycleAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.ObservedRequestIds);
            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.FulfilledRequestIds);
            Assert.Equal(new[] { "runtime-1" }, store.FulfilledRuntimeInstanceIds);
            Assert.Equal(new[] { "scale-out-shared-run-1" }, requeueService.RequeuedRequestIds);
            Assert.Equal(new[] { "runtime-1" }, requeueService.RequeuedRuntimeInstanceIds);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, observer.Events[1].Area);
            Assert.Equal("runtime-scale-out-request-watch", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("pipeline-1", observer.Events[1].Correlation.PipelineKey);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("scale-out-shared-run-1", observer.Events[1].Properties["requestId"]?.ToString());
            Assert.Equal("control-plane-1", observer.Events[1].Properties["controlPlaneId"]?.ToString());
            Assert.Equal("http", observer.Events[1].Properties["providerHint"]?.ToString());
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
        }

        /// <summary>
        /// Verifies that a provider rejection records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ProcessCycleAsync_Should_Record_Denied_Event_When_Provider_Rejects_Request()
        {
            var observer = new CapturingControlPlaneObserver();
            var store = new CapturingScaleOutRequestStore(new[] { CreateRecord() });
            var service = CreateService(
                store,
                new RejectedScaleOutProviderSelector(),
                new CapturingScaleOutFulfilledRunRequeueService(),
                observer);

            await service
                .ProcessCycleAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.ObservedRequestIds);
            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.RejectedRequestIds);
            Assert.Empty(store.FulfilledRequestIds);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.Equal("provider rejected", observer.Events[1].FailureReason);
            Assert.Equal("scale-out-shared-run-1", observer.Events[1].Properties["requestId"]?.ToString());
            Assert.Equal("provider rejected", observer.Events[1].Properties["providerFailureReason"]?.ToString());
        }

        /// <summary>
        /// Verifies that a provider success without runtime instance id records completed-with-issues.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ProcessCycleAsync_Should_Record_CompletedWithIssues_Event_When_Provider_Succeeds_Without_RuntimeInstanceId()
        {
            var observer = new CapturingControlPlaneObserver();
            var store = new CapturingScaleOutRequestStore(new[] { CreateRecord() });
            var service = CreateService(
                store,
                new SuccessfulScaleOutProviderSelector(null),
                new CapturingScaleOutFulfilledRunRequeueService(),
                observer);

            await service
                .ProcessCycleAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.ObservedRequestIds);
            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.RejectedRequestIds);
            Assert.Empty(store.FulfilledRequestIds);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal(
                "Scale-out provider returned success without runtime instance id.",
                observer.Events[1].FailureReason);
            Assert.Equal("scale-out-shared-run-1", observer.Events[1].Properties["requestId"]?.ToString());
        }

        /// <summary>
        /// Verifies that provider exceptions record failed events when provider failure rejection is enabled.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ProcessCycleAsync_Should_Record_Failed_Event_When_Provider_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var store = new CapturingScaleOutRequestStore(new[] { CreateRecord() });
            var service = CreateService(
                store,
                new ThrowingScaleOutProviderSelector(),
                new CapturingScaleOutFulfilledRunRequeueService(),
                observer);

            await service
                .ProcessCycleAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.ObservedRequestIds);
            Assert.Equal(new[] { "scale-out-shared-run-1" }, store.RejectedRequestIds);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("provider exploded", observer.Events[1].Properties["exception.message"]?.ToString());
        }

        /// <summary>
        /// Verifies that fulfilled scale-out watcher events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ProcessCycleAsync_Should_Record_Succeeded_ScaleOutWatch_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var service = CreateService(
                new CapturingScaleOutRequestStore(new[] { CreateRecord() }),
                new SuccessfulScaleOutProviderSelector("runtime-1"),
                new CapturingScaleOutFulfilledRunRequeueService(),
                observer);

            await service
                .ProcessCycleAsync(CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-request-watch.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-request-watch.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("scale-out-shared-run-1", ledger.Entries[1].Metadata!["requestId"]);
            Assert.Equal("runtime-1", ledger.Entries[1].Metadata!["runtimeInstanceId"]);
        }

        /// <summary>
        /// Asserts the common scale-out watcher started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, controlPlaneEvent.Area);
            Assert.Equal("runtime-scale-out-request-watch", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("shared-run-1", controlPlaneEvent.Correlation.RunId);
            Assert.Equal("pipeline-1", controlPlaneEvent.Correlation.PipelineKey);
            Assert.Equal("scale-out-shared-run-1", controlPlaneEvent.Properties["requestId"]?.ToString());
        }

        /// <summary>
        /// Creates the watcher service.
        /// </summary>
        /// <param name="store">The scale-out request store.</param>
        /// <param name="providerSelector">The provider selector.</param>
        /// <param name="requeueService">The fulfilled run requeue service.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The watcher service.</returns>
        private static AiRuntimeScaleOutRequestWatcherHostedService CreateService(
            IAiRuntimeScaleOutRequestStore store,
            IAiRuntimeScaleOutProviderSelector providerSelector,
            IAiScaleOutFulfilledRunRequeueService requeueService,
            IAiControlPlaneObserver observer)
        {
            return new AiRuntimeScaleOutRequestWatcherHostedService(
                store,
                providerSelector,
                requeueService,
                new FixedControlPlaneIdResolver("control-plane-1"),
                Options.Create(
                    new AiRuntimeScaleOutRequestWatcherOptions
                    {
                        Enabled = true,
                        ControlPlaneId = "control-plane-1",
                        WatcherId = "watcher-1",
                        Interval = TimeSpan.FromMilliseconds(10),
                        MaxRequestsPerCycle = 10,
                        RejectOnProviderFailure = true,
                        IgnoreWhenControlPlaneIdMissing = false
                    }),
                observer);
        }

        /// <summary>
        /// Creates a scale-out request record.
        /// </summary>
        /// <returns>The scale-out request record.</returns>
        private static AiRuntimeScaleOutRequestRecord CreateRecord()
        {
            var executionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create();

            return new AiRuntimeScaleOutRequestRecord
            {
                RequestId = "scale-out-shared-run-1",
                ControlPlaneId = "control-plane-1",
                SharedRunId = "shared-run-1",
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
                Status = AiRuntimeScaleOutRequestStatus.Pending,
                Reason = "No runtime instance can currently accept the run and scale-out is allowed.",
                VisibleInstanceCount = 0,
                AvailableInstanceCount = 0,
                CurrentInstanceCount = 0,
                MaxInstanceCount = 3,
                RequestedTargetInstanceCount = 1,
                ProviderHint = "http",
                RequestedBy = "unit-test",
                Source = "unit-test",
                CorrelationId = "correlation-1",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = "control-plane-1",
                    ["tenantId"] = ExpectedTenantId,
                    ["tenantGroupId"] = ExpectedTenantGroupId,
                    ["pipelineKey"] = "pipeline-1",
                    ["providerHint"] = "http"
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
        /// Control-plane id resolver that returns a fixed id.
        /// </summary>
        private sealed class FixedControlPlaneIdResolver : IAiControlPlaneIdResolver
        {
            private readonly string controlPlaneId;

            /// <summary>
            /// Initializes a new instance of the <see cref="FixedControlPlaneIdResolver"/> class.
            /// </summary>
            /// <param name="controlPlaneId">The control-plane identifier.</param>
            public FixedControlPlaneIdResolver(
                string controlPlaneId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
                this.controlPlaneId = controlPlaneId.Trim();
            }

            /// <inheritdoc />
            public Task<string> ResolveAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.controlPlaneId);
            }

            /// <inheritdoc />
            public Task<string> ResolveAsync(
                AiControlPlaneIdResolutionRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(this.controlPlaneId);
            }

            /// <inheritdoc />
            public Task<IReadOnlyDictionary<string, string>> ResolveMetadataAsync(
                AiControlPlaneIdResolutionRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                IReadOnlyDictionary<string, string> metadata =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["controlPlaneId"] = this.controlPlaneId,
                        ["logicalControlPlaneId"] = this.controlPlaneId,
                        ["runtime.controlPlaneId"] = this.controlPlaneId,
                        ["mcp.controlPlaneId"] = this.controlPlaneId,
                        ["recovery.controlPlaneId"] = this.controlPlaneId,
                        ["scaleout.controlPlaneId"] = this.controlPlaneId,
                        ["scenario.controlPlaneId"] = this.controlPlaneId,
                        ["control-plane.id"] = this.controlPlaneId,
                        ["controlplane.id"] = this.controlPlaneId,
                        ["runtime.control-plane.id"] = this.controlPlaneId,
                        ["runtime.controlplane.id"] = this.controlPlaneId,
                        ["mcp.control-plane.id"] = this.controlPlaneId,
                        ["mcp.controlplane.id"] = this.controlPlaneId,
                        ["recovery.control-plane.id"] = this.controlPlaneId,
                        ["recovery.controlplane.id"] = this.controlPlaneId,
                        ["scaleout.control-plane.id"] = this.controlPlaneId,
                        ["scaleout.controlplane.id"] = this.controlPlaneId,
                        ["scenario.control-plane.id"] = this.controlPlaneId,
                        ["scenario.controlplane.id"] = this.controlPlaneId
                    };

                return Task.FromResult(metadata);
            }
        }

        /// <summary>
        /// Scale-out request store that captures lifecycle transitions.
        /// </summary>
        private sealed class CapturingScaleOutRequestStore : IAiRuntimeScaleOutRequestStore
        {
            private readonly IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> pendingRequests;

            /// <summary>
            /// Initializes a new instance of the <see cref="CapturingScaleOutRequestStore"/> class.
            /// </summary>
            /// <param name="pendingRequests">The pending requests.</param>
            public CapturingScaleOutRequestStore(
                IReadOnlyCollection<AiRuntimeScaleOutRequestRecord> pendingRequests)
            {
                this.pendingRequests = pendingRequests;
            }

            /// <summary>
            /// Gets the observed request identifiers.
            /// </summary>
            public List<string> ObservedRequestIds { get; } = new();

            /// <summary>
            /// Gets the fulfilled request identifiers.
            /// </summary>
            public List<string> FulfilledRequestIds { get; } = new();

            /// <summary>
            /// Gets the fulfilled runtime instance identifiers.
            /// </summary>
            public List<string?> FulfilledRuntimeInstanceIds { get; } = new();

            /// <summary>
            /// Gets the rejected request identifiers.
            /// </summary>
            public List<string> RejectedRequestIds { get; } = new();

            /// <summary>
            /// Gets the rejection reasons.
            /// </summary>
            public List<string> RejectionReasons { get; } = new();

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutRequestRecord> CreateAsync(
                AiRuntimeScaleOutRequestRecord request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(request);
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutRequestRecord?> GetAsync(
                string requestId,
                CancellationToken cancellationToken = default)
            {
                foreach (var request in this.pendingRequests)
                {
                    if (string.Equals(request.RequestId, requestId, StringComparison.Ordinal))
                    {
                        return Task.FromResult<AiRuntimeScaleOutRequestRecord?>(request);
                    }
                }

                return Task.FromResult<AiRuntimeScaleOutRequestRecord?>(null);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListAsync(
                AiRuntimeScaleOutRequestQuery query,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.pendingRequests);
            }

            /// <inheritdoc />
            public Task<IReadOnlyCollection<AiRuntimeScaleOutRequestRecord>> ListPendingAsync(
                AiRuntimeScaleOutRequestQuery query,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(this.pendingRequests);
            }

            /// <inheritdoc />
            public Task<bool> MarkObservedAsync(
                string requestId,
                string observedBy,
                CancellationToken cancellationToken = default)
            {
                this.ObservedRequestIds.Add(requestId);
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkFulfilledAsync(
                string requestId,
                string fulfilledBy,
                string? runtimeInstanceId = null,
                CancellationToken cancellationToken = default)
            {
                this.FulfilledRequestIds.Add(requestId);
                this.FulfilledRuntimeInstanceIds.Add(runtimeInstanceId);
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkRejectedAsync(
                string requestId,
                string rejectedBy,
                string reason,
                CancellationToken cancellationToken = default)
            {
                this.RejectedRequestIds.Add(requestId);
                this.RejectionReasons.Add(reason);
                return Task.FromResult(true);
            }

            /// <inheritdoc />
            public Task<bool> MarkExpiredAsync(
                string requestId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }

            /// <inheritdoc />
            public Task<bool> MarkCancelledAsync(
                string requestId,
                string cancelledBy,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Scale-out provider selector that returns success.
        /// </summary>
        private sealed class SuccessfulScaleOutProviderSelector : IAiRuntimeScaleOutProviderSelector
        {
            private readonly string? runtimeInstanceId;

            /// <summary>
            /// Initializes a new instance of the <see cref="SuccessfulScaleOutProviderSelector"/> class.
            /// </summary>
            /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
            public SuccessfulScaleOutProviderSelector(string? runtimeInstanceId)
            {
                this.runtimeInstanceId = runtimeInstanceId;
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = true,
                        RuntimeInstanceId = this.runtimeInstanceId,
                        Message = this.runtimeInstanceId is null
                            ? "Scale-out provider returned success without runtime instance id."
                            : "Scale-out provider fulfilled request."
                    });
            }
        }

        /// <summary>
        /// Scale-out provider selector that rejects requests.
        /// </summary>
        private sealed class RejectedScaleOutProviderSelector : IAiRuntimeScaleOutProviderSelector
        {
            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = false,
                        Rejected = true,
                        FailureReason = "provider rejected",
                        Message = "provider rejected"
                    });
            }
        }

        /// <summary>
        /// Scale-out provider selector that throws.
        /// </summary>
        private sealed class ThrowingScaleOutProviderSelector : IAiRuntimeScaleOutProviderSelector
        {
            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("provider exploded");
            }
        }

        /// <summary>
        /// Captures fulfilled run requeue calls.
        /// </summary>
        private sealed class CapturingScaleOutFulfilledRunRequeueService : IAiScaleOutFulfilledRunRequeueService
        {
            /// <summary>
            /// Gets the requeued request identifiers.
            /// </summary>
            public List<string> RequeuedRequestIds { get; } = new();

            /// <summary>
            /// Gets the requeued runtime instance identifiers.
            /// </summary>
            public List<string> RequeuedRuntimeInstanceIds { get; } = new();

            /// <inheritdoc />
            public Task<AiScaleOutFulfilledRunRequeueResult> RequeueAsync(
                AiRuntimeScaleOutRequestRecord request,
                string? runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                this.RequeuedRequestIds.Add(request.RequestId);

                if (!string.IsNullOrWhiteSpace(runtimeInstanceId))
                {
                    this.RequeuedRuntimeInstanceIds.Add(runtimeInstanceId);
                }

                return Task.FromResult(
                    AiScaleOutFulfilledRunRequeueResult.Succeeded(
                        request.SharedRunId,
                        1,
                        "Scale-out fulfillment requeue captured by the unit-test fixture."));
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
        /// Provides a minimal runtime observability facade for scale-out watcher observability tests.
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