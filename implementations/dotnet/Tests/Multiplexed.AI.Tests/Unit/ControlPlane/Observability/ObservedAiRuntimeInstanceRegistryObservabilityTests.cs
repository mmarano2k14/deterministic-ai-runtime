using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Verifies observability behavior for the observed runtime instance registry decorator.
    /// </summary>
    public sealed class ObservedAiRuntimeInstanceRegistryObservabilityTests
    {
        /// <summary>
        /// Verifies that dependency injection exposes the observed decorator while keeping the concrete registry as the inner implementation.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ServiceProvider_Should_Resolve_Observed_Registry_And_Record_Register_Event()
        {
            var services = new ServiceCollection();
            var observer = new CapturingControlPlaneObserver();

            services.AddSingleton<IAiControlPlaneObserver>(observer);
            services.AddSingleton<CapturingRuntimeInstanceRegistry>();
            services.AddSingleton<IAiRuntimeInstanceRegistry>(provider =>
                new ObservedAiRuntimeInstanceRegistry(
                    provider.GetRequiredService<CapturingRuntimeInstanceRegistry>(),
                    provider.GetRequiredService<IAiControlPlaneObserver>()));

            await using var provider = services.BuildServiceProvider();
            var registry = provider.GetRequiredService<IAiRuntimeInstanceRegistry>();

            Assert.IsType<ObservedAiRuntimeInstanceRegistry>(registry);

            var snapshot = await registry
                .RegisterAsync(CreateRegistration(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal("runtime-1", snapshot.RuntimeInstanceId);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-register");
            AssertSucceededEvent(observer.Events[1], "runtime-instance-register");
            Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
        }

        /// <summary>
        /// Verifies that heartbeat records a completed event when the inner registry returns a snapshot.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task HeartbeatAsync_Should_Record_Succeeded_Event_When_RuntimeInstance_Exists()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceRegistry();
            var registry = new ObservedAiRuntimeInstanceRegistry(inner, observer);

            await registry.RegisterAsync(CreateRegistration(), CancellationToken.None).ConfigureAwait(false);
            observer.Events.Clear();

            var snapshot = await registry
                .HeartbeatAsync(
                    "runtime-1",
                    queuedRunCount: 1,
                    runningRunCount: 2,
                    activeRunCount: 3,
                    availableRunSlots: 4,
                    activeWorkerCount: 5,
                    availableWorkerCount: 6,
                    maxLocalWorkersPerExecution: 7,
                    isQueuePaused: false,
                    canAcceptRun: true,
                    status: AiRuntimeInstanceStatus.Ready,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            Assert.NotNull(snapshot);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-heartbeat");
            AssertSucceededEvent(observer.Events[1], "runtime-instance-heartbeat");
            Assert.Equal("Ready", observer.Events[1].Properties["status"]?.ToString());
        }

        /// <summary>
        /// Verifies that get records completed-with-issues when the runtime instance is not found.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task GetAsync_Should_Record_CompletedWithIssues_Event_When_RuntimeInstance_Is_Not_Found()
        {
            var observer = new CapturingControlPlaneObserver();
            var registry = new ObservedAiRuntimeInstanceRegistry(new CapturingRuntimeInstanceRegistry(), observer);

            var snapshot = await registry
                .GetAsync("missing-runtime", CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(snapshot);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-get", "missing-runtime");
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("runtime-instance-not-found-or-not-visible", observer.Events[1].FailureReason);
            Assert.Equal("missing-runtime", observer.Events[1].Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that list records a succeeded event with the visible runtime instance count.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task ListAsync_Should_Record_Succeeded_Event_With_RuntimeInstance_Count()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceRegistry();
            var registry = new ObservedAiRuntimeInstanceRegistry(inner, observer);

            await inner.RegisterAsync(CreateRegistration(), CancellationToken.None).ConfigureAwait(false);
            observer.Events.Clear();

            var snapshots = await registry
                .ListAsync(includeStopped: false, CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Single(snapshots);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-list", null);
            AssertSucceededEvent(observer.Events[1], "runtime-instance-list", null);
            Assert.Equal("1", observer.Events[1].Properties["runtimeInstanceCount"]?.ToString());
        }

        /// <summary>
        /// Verifies that unregister records a succeeded event when the runtime instance exists.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task UnregisterAsync_Should_Record_Succeeded_Event_When_RuntimeInstance_Exists()
        {
            var observer = new CapturingControlPlaneObserver();
            var inner = new CapturingRuntimeInstanceRegistry();
            var registry = new ObservedAiRuntimeInstanceRegistry(inner, observer);

            await inner.RegisterAsync(CreateRegistration(), CancellationToken.None).ConfigureAwait(false);

            var snapshot = await registry
                .UnregisterAsync("runtime-1", CancellationToken.None)
                .ConfigureAwait(false);

            Assert.NotNull(snapshot);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-unregister");
            AssertSucceededEvent(observer.Events[1], "runtime-instance-unregister");
        }

        /// <summary>
        /// Verifies that inner registry exceptions are recorded then rethrown.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RegisterAsync_Should_Record_Failed_Event_And_Rethrow_When_Inner_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var registry = new ObservedAiRuntimeInstanceRegistry(new ThrowingRuntimeInstanceRegistry(), observer);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    registry.RegisterAsync(CreateRegistration(), CancellationToken.None))
                .ConfigureAwait(false);

            Assert.Equal("registry exploded", exception.Message);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0], "runtime-instance-register");
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal("InvalidOperationException", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Creates a runtime instance registration.
        /// </summary>
        /// <returns>The runtime instance registration.</returns>
        private static AiRuntimeInstanceRegistration CreateRegistration()
        {
            return new AiRuntimeInstanceRegistration
            {
                RuntimeInstanceId = "runtime-1",
                ControlPlaneId = "control-plane-1",
                TenantId = "tenant-a",
                TenantGroupId = "group-a",
                WorkerCount = 2,
                MaxConcurrentRuns = 4,
                QueueCapacity = 8,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pipelineKey"] = "pipeline-a"
                },
                RegisteredAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Asserts a started control-plane event.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured event.</param>
        /// <param name="operation">The expected operation.</param>
        /// <param name="runtimeInstanceId">The expected runtime instance id.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent,
            string operation,
            string? runtimeInstanceId = "runtime-1")
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
            Assert.Equal(operation, controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal(runtimeInstanceId, controlPlaneEvent.Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Asserts a succeeded control-plane event.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured event.</param>
        /// <param name="operation">The expected operation.</param>
        /// <param name="runtimeInstanceId">The expected runtime instance id.</param>
        private static void AssertSucceededEvent(
            AiControlPlaneEvent controlPlaneEvent,
            string operation,
            string? runtimeInstanceId = "runtime-1")
        {
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.InstanceRegistry, controlPlaneEvent.Area);
            Assert.Equal(operation, controlPlaneEvent.Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, controlPlaneEvent.Outcome);
            Assert.Equal(runtimeInstanceId, controlPlaneEvent.Correlation.RuntimeInstanceId);
            Assert.NotNull(controlPlaneEvent.DurationMs);
        }

        /// <summary>
        /// Captures control-plane events.
        /// </summary>
        private sealed class CapturingControlPlaneObserver : IAiControlPlaneObserver
        {
            /// <summary>
            /// Gets the captured events.
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
        /// Capturing runtime instance registry used by decorator tests.
        /// </summary>
        private class CapturingRuntimeInstanceRegistry : IAiRuntimeInstanceRegistry
        {
            private readonly Dictionary<string, AiRuntimeInstanceSnapshot> snapshots = new(StringComparer.Ordinal);

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                var snapshot = CreateSnapshot(registration, AiRuntimeInstanceStatus.Ready);
                this.snapshots[registration.RuntimeInstanceId] = snapshot;
                return Task.FromResult(snapshot);
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot?> HeartbeatAsync(
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
                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var updated = CloneSnapshot(existing, status, queuedRunCount, runningRunCount, activeRunCount);
                this.snapshots[runtimeInstanceId] = updated;
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot?> GetAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                this.snapshots.TryGetValue(runtimeInstanceId, out var snapshot);
                return Task.FromResult(snapshot);
            }

            /// <inheritdoc />
            public virtual Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListAsync(
                bool includeStopped = false,
                CancellationToken cancellationToken = default)
            {
                IReadOnlyList<AiRuntimeInstanceSnapshot> result = this.snapshots.Values
                    .Where(snapshot => includeStopped || snapshot.Status != AiRuntimeInstanceStatus.Stopped)
                    .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToArray();

                return Task.FromResult(result);
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot?> MarkDrainingAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.ChangeStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Draining);
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot?> MarkUnhealthyAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return this.ChangeStatusAsync(runtimeInstanceId, AiRuntimeInstanceStatus.Unhealthy);
            }

            /// <inheritdoc />
            public virtual Task<AiRuntimeInstanceSnapshot?> UnregisterAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var stopped = CloneSnapshot(existing, AiRuntimeInstanceStatus.Stopped, existing.QueuedRunCount, existing.RunningRunCount, existing.ActiveRunCount);
                this.snapshots.Remove(runtimeInstanceId);
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(stopped);
            }

            private Task<AiRuntimeInstanceSnapshot?> ChangeStatusAsync(
                string runtimeInstanceId,
                AiRuntimeInstanceStatus status)
            {
                if (!this.snapshots.TryGetValue(runtimeInstanceId, out var existing))
                {
                    return Task.FromResult<AiRuntimeInstanceSnapshot?>(null);
                }

                var updated = CloneSnapshot(existing, status, existing.QueuedRunCount, existing.RunningRunCount, existing.ActiveRunCount);
                this.snapshots[runtimeInstanceId] = updated;
                return Task.FromResult<AiRuntimeInstanceSnapshot?>(updated);
            }

            private static AiRuntimeInstanceSnapshot CreateSnapshot(
                AiRuntimeInstanceRegistration registration,
                AiRuntimeInstanceStatus status)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = registration.RuntimeInstanceId,
                    ControlPlaneId = registration.ControlPlaneId,
                    TenantId = registration.TenantId,
                    TenantGroupId = registration.TenantGroupId,
                    Status = status,
                    WorkerCount = registration.WorkerCount,
                    MaxConcurrentRuns = registration.MaxConcurrentRuns,
                    QueueCapacity = registration.QueueCapacity,
                    QueuedRunCount = 0,
                    RunningRunCount = 0,
                    ActiveRunCount = 0,
                    CanAcceptRun = true,
                    IsQueuePaused = false,
                    RegisteredAtUtc = registration.RegisteredAtUtc,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = registration.Metadata
                };
            }

            private static AiRuntimeInstanceSnapshot CloneSnapshot(
                AiRuntimeInstanceSnapshot snapshot,
                AiRuntimeInstanceStatus status,
                int queuedRunCount,
                int runningRunCount,
                int activeRunCount)
            {
                return new AiRuntimeInstanceSnapshot
                {
                    RuntimeInstanceId = snapshot.RuntimeInstanceId,
                    ControlPlaneId = snapshot.ControlPlaneId,
                    TenantId = snapshot.TenantId,
                    TenantGroupId = snapshot.TenantGroupId,
                    Status = status,
                    WorkerCount = snapshot.WorkerCount,
                    MaxConcurrentRuns = snapshot.MaxConcurrentRuns,
                    QueueCapacity = snapshot.QueueCapacity,
                    QueuedRunCount = queuedRunCount,
                    RunningRunCount = runningRunCount,
                    ActiveRunCount = activeRunCount,
                    CanAcceptRun = snapshot.CanAcceptRun,
                    IsQueuePaused = snapshot.IsQueuePaused,
                    RegisteredAtUtc = snapshot.RegisteredAtUtc,
                    LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                    Metadata = snapshot.Metadata
                };
            }
        }

        /// <summary>
        /// Throwing runtime instance registry used by failure-path tests.
        /// </summary>
        private sealed class ThrowingRuntimeInstanceRegistry : CapturingRuntimeInstanceRegistry
        {
            /// <inheritdoc />
            public override Task<AiRuntimeInstanceSnapshot> RegisterAsync(
                AiRuntimeInstanceRegistration registration,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("registry exploded");
            }
        }
    }
}
