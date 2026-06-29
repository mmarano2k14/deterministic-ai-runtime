using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Admission;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Tests.Fixtures;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests run admission controller control-plane observability events.
    /// </summary>
    public sealed class AiRunAdmissionControllerObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxx";

        /// <summary>
        /// Verifies that an assign-to-instance admission decision records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task AdmitAsync_Should_Record_Started_And_Succeeded_Events_When_Runtime_Instance_Is_Assigned()
        {
            var observer = new CapturingControlPlaneObserver();

            var controller = CreateController(
                registry: new FakeRuntimeInstanceRegistry(
                    new[]
                    {
                        CreateRuntimeInstanceSnapshot("runtime-1", canAcceptRun: true, availableRunSlots: 2)
                    }),
                capacityStore: new FakeRuntimeInstanceCapacityStore(
                    new[]
                    {
                        CreateCapacityDescriptor("runtime-1", canAcceptRun: true, availableRunSlots: 2)
                    }),
                reservationStore: new FakeRuntimeAdmissionReservationStore(),
                options: CreateOptions(),
                observer: observer);

            var decision = await controller
                .AdmitAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.Equal("runtime-1", decision.AssignedRuntimeInstanceId);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Admission, observer.Events[1].Area);
            Assert.Equal("runtime-admission-decision", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("pipeline-1", observer.Events[1].Correlation.PipelineKey);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("AssignToInstance", observer.Events[1].Properties["decisionType"]?.ToString());
            Assert.Equal("runtime-1", observer.Events[1].Properties["assignedRuntimeInstanceId"]?.ToString());
        }

        /// <summary>
        /// Verifies that disabled admission records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task AdmitAsync_Should_Record_Started_And_Denied_Events_When_Admission_Is_Disabled()
        {
            var observer = new CapturingControlPlaneObserver();

            var options = CreateOptions();
            options.Enabled = false;

            var controller = CreateController(
                registry: new FakeRuntimeInstanceRegistry(Array.Empty<AiRuntimeInstanceSnapshot>()),
                capacityStore: new FakeRuntimeInstanceCapacityStore(Array.Empty<AiRuntimeInstanceCapacityDescriptor>()),
                reservationStore: new FakeRuntimeAdmissionReservationStore(),
                options: options,
                observer: observer);

            var decision = await controller
                .AdmitAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(AiRunAdmissionDecisionType.Reject, decision.DecisionType);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.Equal("Run admission is disabled.", observer.Events[1].FailureReason);
            Assert.Equal("Reject", observer.Events[1].Properties["decisionType"]?.ToString());
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
        }

        /// <summary>
        /// Verifies that admission records completed-with-issues when scale-out is requested.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task AdmitAsync_Should_Record_CompletedWithIssues_Event_When_ScaleOut_Is_Requested()
        {
            var observer = new CapturingControlPlaneObserver();

            var options = CreateOptions();
            options.EnableScaleOutRequest = true;
            options.MaxInstanceCount = 3;
            options.EnableGlobalQueueFallback = false;
            options.RejectWhenNoCapacity = false;

            var controller = CreateController(
                registry: new FakeRuntimeInstanceRegistry(Array.Empty<AiRuntimeInstanceSnapshot>()),
                capacityStore: new FakeRuntimeInstanceCapacityStore(Array.Empty<AiRuntimeInstanceCapacityDescriptor>()),
                reservationStore: new FakeRuntimeAdmissionReservationStore(),
                options: options,
                observer: observer);

            var decision = await controller
                .AdmitAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(AiRunAdmissionDecisionType.RequestScaleOut, decision.DecisionType);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("No runtime instance can currently accept the run and scale-out is allowed.", observer.Events[1].FailureReason);
            Assert.Equal("RequestScaleOut", observer.Events[1].Properties["decisionType"]?.ToString());
            Assert.Equal("0", observer.Events[1].Properties["visibleInstanceCount"]?.ToString());
            Assert.Equal("0", observer.Events[1].Properties["availableInstanceCount"]?.ToString());
        }

        /// <summary>
        /// Verifies that admission exceptions record failed events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task AdmitAsync_Should_Record_Failed_Event_When_Registry_Throws()
        {
            var observer = new CapturingControlPlaneObserver();

            var controller = CreateController(
                registry: new ThrowingRuntimeInstanceRegistry(),
                capacityStore: new FakeRuntimeInstanceCapacityStore(Array.Empty<AiRuntimeInstanceCapacityDescriptor>()),
                reservationStore: new FakeRuntimeAdmissionReservationStore(),
                options: CreateOptions(),
                observer: observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await controller
                        .AdmitAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("registry exploded", observer.Events[1].Properties["exception.message"]?.ToString());
        }

        /// <summary>
        /// Verifies that successful admission decisions are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task AdmitAsync_Should_Record_Succeeded_Admission_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var controller = CreateController(
                registry: new FakeRuntimeInstanceRegistry(
                    new[]
                    {
                        CreateRuntimeInstanceSnapshot("runtime-1", canAcceptRun: true, availableRunSlots: 2)
                    }),
                capacityStore: new FakeRuntimeInstanceCapacityStore(
                    new[]
                    {
                        CreateCapacityDescriptor("runtime-1", canAcceptRun: true, availableRunSlots: 2)
                    }),
                reservationStore: new FakeRuntimeAdmissionReservationStore(),
                options: CreateOptions(),
                observer: observer);

            var decision = await controller
                .AdmitAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(AiRunAdmissionDecisionType.AssignToInstance, decision.DecisionType);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Admission, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.admission.runtime-admission-decision.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Admission, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.admission.runtime-admission-decision.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("AssignToInstance", ledger.Entries[1].Metadata!["decisionType"]);
            Assert.Equal("runtime-1", ledger.Entries[1].Metadata!["assignedRuntimeInstanceId"]);
        }

        /// <summary>
        /// Verifies that denied admission decisions are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task AdmitAsync_Should_Record_Denied_Admission_ControlPlane_Event_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });

            var options = CreateOptions();
            options.Enabled = false;

            var controller = CreateController(
                registry: new FakeRuntimeInstanceRegistry(Array.Empty<AiRuntimeInstanceSnapshot>()),
                capacityStore: new FakeRuntimeInstanceCapacityStore(Array.Empty<AiRuntimeInstanceCapacityDescriptor>()),
                reservationStore: new FakeRuntimeAdmissionReservationStore(),
                options: options,
                observer: observer);

            var decision = await controller
                .AdmitAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(AiRunAdmissionDecisionType.Reject, decision.DecisionType);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Admission, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.admission.runtime-admission-decision.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Admission, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, ledger.Entries[1].Outcome);
            Assert.Equal("Run admission is disabled.", ledger.Entries[1].Reason);
            Assert.Equal("control.admission.runtime-admission-decision.denied", ledger.Entries[1].EventType);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("Reject", ledger.Entries[1].Metadata!["decisionType"]);
        }

        /// <summary>
        /// Asserts the common admission started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Admission, controlPlaneEvent.Area);
            Assert.Equal("runtime-admission-decision", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("run-1", controlPlaneEvent.Correlation.RunId);
            Assert.Equal("pipeline-1", controlPlaneEvent.Correlation.PipelineKey);
        }

        /// <summary>
        /// Creates an admission controller.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="capacityStore">The capacity store.</param>
        /// <param name="reservationStore">The reservation store.</param>
        /// <param name="options">The admission options.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The admission controller.</returns>
        private static AiRunAdmissionController CreateController(
            IAiRuntimeInstanceRegistry registry,
            IAiRuntimeInstanceCapacityStore capacityStore,
            IAiRuntimeAdmissionReservationStore reservationStore,
            AiRunAdmissionOptions options,
            IAiControlPlaneObserver observer)
        {
            return new AiRunAdmissionController(
                registry,
                reservationStore,
                capacityStore,
                new FakeTenantRuntimeSettingsProvider(),
                Options.Create(options),
                NullLogger<AiRunAdmissionController>.Instance,
                observer);
        }

        /// <summary>
        /// Creates default admission options.
        /// </summary>
        /// <returns>The admission options.</returns>
        private static AiRunAdmissionOptions CreateOptions()
        {
            return new AiRunAdmissionOptions
            {
                Enabled = true,
                EnableScaleOutRequest = true,
                EnableGlobalQueueFallback = false,
                RejectWhenNoCapacity = true,
                PreferRequestedRuntimeInstance = true,
                AllowPausedInstances = false,
                AllowDrainingInstances = false,
                AllowUnhealthyInstances = false,
                MaxInstanceCount = 3
            };
        }

        /// <summary>
        /// Creates an admission request.
        /// </summary>
        /// <returns>The admission request.</returns>
        private static AiRunAdmissionRequest CreateRequest()
        {
            return new AiRunAdmissionRequest
            {
                RunId = "run-1",
                TenantId = ExpectedTenantId,
                PipelineKey = "pipeline-1",
                PreferredRuntimeInstanceId = null,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create()
                }
            };
        }

        /// <summary>
        /// Creates a runtime instance snapshot.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="canAcceptRun">A value indicating whether the instance can accept a run.</param>
        /// <param name="availableRunSlots">The available run slots.</param>
        /// <returns>The runtime instance snapshot.</returns>
        private static AiRuntimeInstanceSnapshot CreateRuntimeInstanceSnapshot(
            string runtimeInstanceId,
            bool canAcceptRun,
            int availableRunSlots)
        {
            return new AiRuntimeInstanceSnapshot
            {
                RuntimeInstanceId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                CanAcceptRun = canAcceptRun,
                IsQueuePaused = false,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                AvailableRunSlots = availableRunSlots,
                WorkerCount = 2,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 2,
                MaxLocalWorkersPerExecution = 2
            };
        }

        /// <summary>
        /// Creates a runtime instance capacity descriptor.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="canAcceptRun">A value indicating whether the instance can accept a run.</param>
        /// <param name="availableRunSlots">The available run slots.</param>
        /// <returns>The runtime instance capacity descriptor.</returns>
        private static AiRuntimeInstanceCapacityDescriptor CreateCapacityDescriptor(
            string runtimeInstanceId,
            bool canAcceptRun,
            int availableRunSlots)
        {
            return new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = runtimeInstanceId,
                Role = AiRuntimeInstanceRole.Runtime,
                Status = AiRuntimeInstanceStatus.Ready,
                CanAcceptRun = canAcceptRun,
                IsQueuePaused = false,
                AvailableRunSlots = availableRunSlots,
                WorkerCount = 2,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 2,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ControlPlaneId = "control-plane-1"
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
        /// Runtime instance registry that returns configured snapshots.
        /// </summary>
        private sealed class FakeRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly IReadOnlyList<AiRuntimeInstanceSnapshot> instances;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeInstanceRegistry"/> class.
            /// </summary>
            /// <param name="instances">The runtime instance snapshots.</param>
            public FakeRuntimeInstanceRegistry(
                IReadOnlyList<AiRuntimeInstanceSnapshot> instances)
            {
                this.instances = instances;
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                foreach (var instance in this.instances)
                {
                    if (string.Equals(instance.RuntimeInstanceId, runtimeInstanceId, StringComparison.Ordinal))
                    {
                        return Task.FromResult<AiRuntimeInstanceSnapshot?>(instance);
                    }
                }

                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
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
                return Task.FromResult(this.instances);
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
                    CreateRuntimeInstanceSnapshot(
                        registration.RuntimeInstanceId,
                        canAcceptRun: true,
                        availableRunSlots: 1));
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
        /// Runtime instance registry that throws on list.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            /// <inheritdoc />
            public Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
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
                throw new InvalidOperationException("registry exploded");
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
                    CreateRuntimeInstanceSnapshot(
                        registration.RuntimeInstanceId,
                        canAcceptRun: true,
                        availableRunSlots: 1));
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
        /// Runtime instance capacity store that returns configured descriptors.
        /// </summary>
        private sealed class FakeRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
        {
            private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeInstanceCapacityStore"/> class.
            /// </summary>
            /// <param name="descriptors">The capacity descriptors.</param>
            public FakeRuntimeInstanceCapacityStore(
                IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> descriptors)
            {
                this.descriptors = new Dictionary<string, AiRuntimeInstanceCapacityDescriptor>(StringComparer.Ordinal);

                foreach (var descriptor in descriptors)
                {
                    this.descriptors[descriptor.RuntimeInstanceId] = descriptor;
                }
            }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                this.descriptors.TryGetValue(runtimeInstanceId, out var descriptor);

                return Task.FromResult<AiRuntimeInstanceCapacityDescriptor?>(descriptor);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>(
                    this.descriptors.Values.ToArray());
            }

            /// <inheritdoc />
            public Task PublishAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(descriptor);

                this.descriptors[descriptor.RuntimeInstanceId] = descriptor;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<bool> RemoveAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    this.descriptors.Remove(runtimeInstanceId));
            }
        }

        /// <summary>
        /// Admission reservation store that tracks in-memory reserved run counts.
        /// </summary>
        private sealed class FakeRuntimeAdmissionReservationStore : IAiRuntimeAdmissionReservationStore
        {
            private readonly Dictionary<string, int> reservedRunCounts = new(StringComparer.Ordinal);

            /// <inheritdoc />
            public Task ReserveAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                this.reservedRunCounts.TryGetValue(runtimeInstanceId, out var current);

                this.reservedRunCounts[runtimeInstanceId] =
                    current + Math.Max(0, runCount);

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task ReleaseAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                this.reservedRunCounts.TryGetValue(runtimeInstanceId, out var current);

                var next =
                    Math.Max(
                        0,
                        current - Math.Max(0, runCount));

                if (next == 0)
                {
                    this.reservedRunCounts.Remove(runtimeInstanceId);
                }
                else
                {
                    this.reservedRunCounts[runtimeInstanceId] = next;
                }

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> GetReservedRunCountAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

                this.reservedRunCounts.TryGetValue(runtimeInstanceId, out var count);

                return Task.FromResult(count);
            }
        }

        /// <summary>
        /// Tenant runtime settings provider used by admission tests.
        /// </summary>
        private sealed class FakeTenantRuntimeSettingsProvider : IAiTenantRuntimeSettingsProvider
        {
            /// <inheritdoc />
            public AiTenantRuntimeSettings GetSettings(
                string? tenantId,
                string? tenantGroupId)
            {
                return new AiTenantRuntimeSettings
                {
                    TenantId = tenantId ?? ExpectedTenantId,
                    TenantGroupId = tenantGroupId ?? ExpectedTenantGroupId,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                    PreferDedicatedCapacity = false,
                    AllowSharedFallback = true,
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 2,
                    MaxConcurrentRunsPerInstance = 2,
                    RuntimeInstanceIdPrefix = "runtime",
                    LocalQueueCapacity = 100
                };
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
        /// Provides a minimal runtime observability facade for admission observability tests.
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