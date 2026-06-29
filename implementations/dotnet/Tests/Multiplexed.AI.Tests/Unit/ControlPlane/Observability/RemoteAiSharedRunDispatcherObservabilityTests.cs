using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests remote shared run dispatcher control-plane observability events.
    /// </summary>
    public sealed class RemoteAiSharedRunDispatcherObservabilityTests
    {
        /// <summary>
        /// Verifies that remote shared run dispatch records started and denied events when the runtime instance is not routable.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Started_And_Denied_Events_When_Runtime_Instance_Is_Not_Routable()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(),
                new FakeRuntimeInstanceRegistry(canAcceptRun: false),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("Runtime instance 'runtime-1' is not routable.", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.SharedController, observer.Events[1].Area);
            Assert.Equal("remote-shared-run-dispatch", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.Equal("runtime-instance-not-routable", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("tenant-id-xxxx", observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal("tenant-group-id-xxx", observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("shared-run-1", observer.Events[1].Properties["sharedRunId"]?.ToString());
        }

        /// <summary>
        /// Verifies that remote shared run dispatch records started and failed events when the run request is missing.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Started_And_Failed_Events_When_RunRequest_Is_Missing()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(runRequest: null, forceMissingRunRequest: true), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("Shared run does not contain a runtime pipeline run request.", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal("missing-run-request", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
        }

        /// <summary>
        /// Verifies that remote shared run dispatch records started and failed events when the dispatch provider cannot be resolved.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Started_And_Failed_Events_When_Dispatch_Provider_Is_Not_Found()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new FailingRuntimeInstanceProviderCapabilityResolver(),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal("runtime-instance-dispatch-provider-not-found", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
        }

        /// <summary>
        /// Verifies that remote shared run dispatch records started and succeeded events when provider dispatch succeeds.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Started_And_Succeeded_Events_When_Provider_Dispatch_Succeeds()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("local-run-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("execution-1", observer.Events[1].Correlation.ExecutionId);
            Assert.Equal("local-run-1", observer.Events[1].Properties["localRunId"]?.ToString());
            Assert.Equal("execution-1", observer.Events[1].Properties["executionId"]?.ToString());
            Assert.Equal("True", observer.Events[1].Properties["success"]?.ToString());
        }

        /// <summary>
        /// Verifies that remote shared run dispatch records completed-with-issues when provider dispatch returns failure.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_CompletedWithIssues_Event_When_Provider_Dispatch_Returns_Failure()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(
                    new FailingRuntimeInstanceDispatchProvider()),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("provider dispatch failed", observer.Events[1].FailureReason);
            Assert.Equal("False", observer.Events[1].Properties["success"]?.ToString());
        }

        /// <summary>
        /// Verifies that remote shared run dispatch records failed events when provider dispatch throws.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Failed_Event_When_Provider_Dispatch_Throws()
        {
            var observer = new CapturingControlPlaneObserver();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(
                    new ThrowingRuntimeInstanceDispatchProvider()),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("provider exploded", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
        }

        /// <summary>
        /// Verifies that successful remote shared run dispatch control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Succeeded_RemoteDispatch_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedcontroller.remote-shared-run-dispatch.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.sharedcontroller.remote-shared-run-dispatch.succeeded", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal("execution-1", ledger.Entries[1].Context.ExecutionId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Metadata!["sharedRunId"]);
            Assert.Equal("local-run-1", ledger.Entries[1].Metadata!["localRunId"]);
            Assert.Equal("execution-1", ledger.Entries[1].Metadata!["executionId"]);
        }

        /// <summary>
        /// Verifies that non-routable remote shared run dispatch control-plane events are recorded to the decision ledger as denied.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Denied_RemoteDispatch_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(),
                new FakeRuntimeInstanceRegistry(canAcceptRun: false),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedcontroller.remote-shared-run-dispatch.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, ledger.Entries[1].Outcome);
            Assert.Equal("runtime-instance-not-routable", ledger.Entries[1].Reason);
            Assert.Equal("control.sharedcontroller.remote-shared-run-dispatch.denied", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Metadata!["sharedRunId"]);
        }

        /// <summary>
        /// Verifies that remote shared run dispatch provider exceptions are recorded to the decision ledger as failed.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task DispatchAsync_Should_Record_Failed_RemoteDispatch_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();

            var observability = new FakeRuntimeObservability(ledger);

            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
            new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var dispatcher = new RemoteAiSharedRunDispatcher(
                new SuccessfulRuntimeInstanceProviderCapabilityResolver(
                    new ThrowingRuntimeInstanceDispatchProvider()),
                new FakeRuntimeInstanceRegistry(canAcceptRun: true),
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                new NoopAiRuntimeRecoveryForensicsRecorder(),
                observer);

            var result = await dispatcher
                .DispatchAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, ledger.Entries.Count);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.sharedcontroller.remote-shared-run-dispatch.operationstarted", ledger.Entries[0].EventType);

            Assert.Equal(AiDecisionLedgerCategory.SharedController, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Failed, ledger.Entries[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), ledger.Entries[1].Reason);
            Assert.Equal("control.sharedcontroller.remote-shared-run-dispatch.failed", ledger.Entries[1].EventType);

            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal("shared-run-1", ledger.Entries[1].Metadata!["sharedRunId"]);
            Assert.Equal("False", ledger.Entries[1].Metadata!["success"]);
            Assert.Equal("provider exploded", ledger.Entries[1].Metadata!["failureReason"]);
        }

        /// <summary>
        /// Asserts the common remote dispatch started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.SharedController, controlPlaneEvent.Area);
            Assert.Equal("remote-shared-run-dispatch", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            Assert.Equal("shared-run-1", controlPlaneEvent.Correlation.RunId);
        }

        /// <summary>
        /// Creates a shared run dispatch request.
        /// </summary>
        /// <param name="runRequest">The optional runtime pipeline run request.</param>
        /// <param name="forceMissingRunRequest">A value indicating whether the request should intentionally contain a null run request.</param>
        /// <returns>The shared run dispatch request.</returns>
        private static AiSharedRunDispatchRequest CreateRequest(
            AiRuntimePipelineRunRequest? runRequest = null,
            bool forceMissingRunRequest = false)
        {
            return new AiSharedRunDispatchRequest
            {
                RuntimeInstanceId = "runtime-1",
                ClaimToken = "claim-1",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test remote dispatch",
                Metadata = new Dictionary<string, string>
                {
                    ["controlPlaneId"] = "control-plane-1"
                },
                SharedRun = new AiSharedRunRecord
                {
                    SharedRunId = "shared-run-1",
                    Status = default,
                    RunRequest = forceMissingRunRequest
                        ? null!
                        : runRequest ?? new AiRuntimePipelineRunRequest
                        {
                            PipelineName = "pipeline-1",
                        },
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(),
                    PipelineKey = "pipeline-1",
                    CorrelationId = "correlation-1",
                    RequestedBy = "unit-test",
                    Source = "unit-test",
                    Reason = "unit-test remote dispatch",
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
        /// Runtime instance registry that returns a configured routability state.
        /// </summary>
        private sealed class FakeRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly bool canAcceptRun;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeInstanceRegistry"/> class.
            /// </summary>
            /// <param name="canAcceptRun">A value indicating whether the runtime can accept runs.</param>
            public FakeRuntimeInstanceRegistry(
                bool canAcceptRun)
            {
                this.canAcceptRun = canAcceptRun;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(
                    new AiRuntimeInstanceSnapshot
                    {
                        RuntimeInstanceId = runtimeInstanceId,
                        Status = this.canAcceptRun
                            ? AiRuntimeInstanceStatus.Ready
                            : AiRuntimeInstanceStatus.Unhealthy,
                        CanAcceptRun = this.canAcceptRun,
                        TenantId = "tenant-a",
                        TenantGroupId = "tenant-group-a"
                    });
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
                string runtimeInstanceId,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount,
                int? availableRunSlots,
                int? activeWorkerCount,
                int? availableWorkerCount,
                int? maxLocalWorkersPerExecution,
                bool isQueuePaused,
                bool canAcceptRun,
                AiRuntimeInstanceStatus status,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceSnapshot>>(
                    Array.Empty<AiRuntimeInstanceSnapshot>());
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(registration);

                return Task.FromResult(
                    new AiRuntimeInstanceSnapshot
                    {
                        RuntimeInstanceId = registration.RuntimeInstanceId,
                        Status = this.canAcceptRun
                            ? AiRuntimeInstanceStatus.Ready
                            : AiRuntimeInstanceStatus.Unhealthy,
                        CanAcceptRun = this.canAcceptRun
                    });
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
            }
        }

        /// <summary>
        /// Runtime instance provider capability resolver that returns a dispatch provider.
        /// </summary>
        private sealed class SuccessfulRuntimeInstanceProviderCapabilityResolver : IAiRuntimeInstanceProviderCapabilityResolver
        {
            private readonly IAiRuntimeInstanceDispatchProvider provider;

            /// <summary>
            /// Initializes a new instance of the <see cref="SuccessfulRuntimeInstanceProviderCapabilityResolver"/> class.
            /// </summary>
            /// <param name="provider">The dispatch provider.</param>
            public SuccessfulRuntimeInstanceProviderCapabilityResolver(
                IAiRuntimeInstanceDispatchProvider? provider = null)
            {
                this.provider = provider ?? new SuccessfulRuntimeInstanceDispatchProvider();
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceProviderCapabilityResolution<TProvider>> ResolveAsync<TProvider>(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
                where TProvider : IAiRuntimeInstanceProvider
            {
                if (!typeof(TProvider).IsAssignableFrom(this.provider.GetType()))
                {
                    return Task.FromResult(
                        AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Failed(
                            runtimeInstanceId,
                            "provider capability type mismatch"));
                }

                var resolvedProvider = (TProvider)(object)this.provider;

                return Task.FromResult(
                    AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Succeeded(
                        runtimeInstanceId,
                        new AiRuntimeInstanceCapacityDescriptor
                        {
                            RuntimeInstanceId = runtimeInstanceId,
                            TenantId = "tenant-a",
                            TenantGroupId = "tenant-group-a",
                            Status = AiRuntimeInstanceStatus.Ready,
                            CanAcceptRun = true,
                            AvailableRunSlots = 1,
                            ControlPlaneId = "control-plane-1"
                        },
                        resolvedProvider));
            }
        }

        /// <summary>
        /// Runtime instance provider capability resolver that fails resolution.
        /// </summary>
        private sealed class FailingRuntimeInstanceProviderCapabilityResolver : IAiRuntimeInstanceProviderCapabilityResolver
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceProviderCapabilityResolution<TProvider>> ResolveAsync<TProvider>(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
                where TProvider : IAiRuntimeInstanceProvider
            {
                return Task.FromResult(
                    AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Failed(
                        runtimeInstanceId,
                        "provider not found"));
            }
        }

        /// <summary>
        /// Dispatch provider that succeeds.
        /// </summary>
        private sealed class SuccessfulRuntimeInstanceDispatchProvider : IAiRuntimeInstanceDispatchProvider
        {
            public bool CanHandle(AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return descriptor is not null;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = true,
                        SharedRunId = request.SharedRun.SharedRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        LocalRunId = "local-run-1",
                        ExecutionId = "execution-1",
                        ClaimToken = request.ClaimToken,
                        Message = "dispatched",
                        Metadata = new Dictionary<string, string>
                        {
                            ["provider.result"] = "success"
                        }
                    });
            }
        }

        /// <summary>
        /// Dispatch provider that returns failure.
        /// </summary>
        private sealed class FailingRuntimeInstanceDispatchProvider : IAiRuntimeInstanceDispatchProvider
        {
            public bool CanHandle(AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return descriptor is not null;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiSharedRuntimeInstanceDispatchResult
                    {
                        Success = false,
                        SharedRunId = request.SharedRun.SharedRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        FailureReason = "provider dispatch failed",
                        Message = "provider dispatch failed"
                    });
            }
        }

        /// <summary>
        /// Dispatch provider that throws.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceDispatchProvider : IAiRuntimeInstanceDispatchProvider
        {
            public bool CanHandle(AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return descriptor is not null;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("provider exploded");
            }
        }
    }
}
