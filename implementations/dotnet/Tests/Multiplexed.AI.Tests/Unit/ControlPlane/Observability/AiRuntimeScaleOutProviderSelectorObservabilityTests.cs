using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
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
    /// Tests runtime scale-out provider selector control-plane observability events.
    /// </summary>
    public sealed class AiRuntimeScaleOutProviderSelectorObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxxx";

        /// <summary>
        /// Verifies that successful provider selection records started and succeeded events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Record_Started_And_Succeeded_Events_When_Provider_Succeeds()
        {
            var observer = new CapturingControlPlaneObserver();
            var router = new FakeRuntimeInstanceProviderRouter(new SuccessfulScaleOutProvider());
            var selector = CreateSelector(router, observer);

            var result = await selector
                .RequestScaleOutAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationCompleted, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, observer.Events[1].Area);
            Assert.Equal("runtime-scale-out-provider-selection", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Succeeded, observer.Events[1].Outcome);
            Assert.Null(observer.Events[1].FailureReason);
            Assert.Equal("shared-run-1", observer.Events[1].Correlation.RunId);
            Assert.Equal("pipeline-1", observer.Events[1].Correlation.PipelineKey);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal("http", observer.Events[1].Properties["resolvedProviderName"]?.ToString());
            Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
            Assert.Equal("True", observer.Events[1].Properties["success"]?.ToString());
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
        }

        /// <summary>
        /// Verifies that provider-not-found records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Record_Started_And_Denied_Events_When_Provider_Is_Not_Found()
        {
            var observer = new CapturingControlPlaneObserver();
            var selector = CreateSelector(new FakeRuntimeInstanceProviderRouter(null), observer);

            var result = await selector
                .RequestScaleOutAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.True(result.Rejected);
            Assert.Equal("scale-out-provider-not-found", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.Equal("scale-out-provider-not-found", observer.Events[1].FailureReason);
            Assert.Equal("http", observer.Events[1].Properties["resolvedProviderName"]?.ToString());
            Assert.Equal("False", observer.Events[1].Properties["success"]?.ToString());
            Assert.Equal("True", observer.Events[1].Properties["rejected"]?.ToString());
        }

        /// <summary>
        /// Verifies that provider failure without rejection records completed-with-issues.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Record_CompletedWithIssues_Event_When_Provider_Fails_Without_Rejection()
        {
            var observer = new CapturingControlPlaneObserver();
            var selector = CreateSelector(new FakeRuntimeInstanceProviderRouter(new FailingScaleOutProvider()), observer);

            var result = await selector
                .RequestScaleOutAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.False(result.Rejected);
            Assert.Equal("provider capacity unavailable", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.CompletedWithIssues, observer.Events[1].Outcome);
            Assert.Equal("provider capacity unavailable", observer.Events[1].FailureReason);
            Assert.Equal("False", observer.Events[1].Properties["success"]?.ToString());
            Assert.Equal("False", observer.Events[1].Properties["rejected"]?.ToString());
        }

        /// <summary>
        /// Verifies that provider exceptions record failed events and are rethrown.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Record_Failed_Event_When_Provider_Throws()
        {
            var observer = new CapturingControlPlaneObserver();
            var selector = CreateSelector(new FakeRuntimeInstanceProviderRouter(new ThrowingScaleOutProvider()), observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await selector
                        .RequestScaleOutAsync(CreateRequest(), CancellationToken.None)
                        .ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Failed, observer.Events[1].Outcome);
            Assert.Equal(nameof(InvalidOperationException), observer.Events[1].FailureReason);
            Assert.Equal("provider exploded", observer.Events[1].Properties["exception.message"]?.ToString());
        }

        /// <summary>
        /// Verifies that successful provider selection control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task RequestScaleOutAsync_Should_Record_Succeeded_ProviderSelection_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var selector = CreateSelector(new FakeRuntimeInstanceProviderRouter(new SuccessfulScaleOutProvider()), observer);

            var result = await selector
                .RequestScaleOutAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-provider-selection.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Succeeded, ledger.Entries[1].Outcome);
            Assert.Equal("control.scaling.runtime-scale-out-provider-selection.succeeded", ledger.Entries[1].EventType);
            Assert.Equal("shared-run-1", ledger.Entries[1].Context.RunId);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("http", ledger.Entries[1].Metadata!["resolvedProviderName"]);
            Assert.Equal("runtime-1", ledger.Entries[1].Metadata!["runtimeInstanceId"]);
            Assert.Equal("True", ledger.Entries[1].Metadata!["success"]);
        }

        /// <summary>
        /// Asserts the common provider selection started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, controlPlaneEvent.Area);
            Assert.Equal("runtime-scale-out-provider-selection", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("shared-run-1", controlPlaneEvent.Correlation.RunId);
            Assert.Equal("pipeline-1", controlPlaneEvent.Correlation.PipelineKey);
            Assert.Equal("correlation-1", controlPlaneEvent.Correlation.CorrelationId);
        }

        /// <summary>
        /// Creates a scale-out provider selector.
        /// </summary>
        /// <param name="router">The runtime instance provider router.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The scale-out provider selector.</returns>
        private static AiRuntimeScaleOutProviderSelector CreateSelector(
            IAiRuntimeInstanceProviderRouter router,
            IAiControlPlaneObserver observer)
        {
            return new AiRuntimeScaleOutProviderSelector(
                router,
                Options.Create(
                    new AiRuntimeInstanceRegistrationOptions
                    {
                        ProviderName = "local"
                    }),
                observer);
        }

        /// <summary>
        /// Creates a scale-out provider request.
        /// </summary>
        /// <returns>The scale-out provider request.</returns>
        private static AiRuntimeScaleOutProviderRequest CreateRequest()
        {
            return new AiRuntimeScaleOutProviderRequest
            {
                RequestId = "scale-out-shared-run-1",
                ControlPlaneId = "control-plane-1",
                ExecutionContextSnapshot = AiExecutionContextSnapshotTestFactory.Create(),
                SharedRunId = "shared-run-1",
                TenantId = ExpectedTenantId,
                TenantGroupId = ExpectedTenantGroupId,
                PipelineKey = "pipeline-1",
                IsolationMode = Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation.AiRuntimeInstanceIsolationMode.Dedicated,
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
                RequestedTargetInstanceCount = 1,
                ProviderHint = "http",
                CorrelationId = "correlation-1",
                RequestedBy = "unit-test",
                Source = "unit-test",
                Reason = "unit-test scale-out provider selection",
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
        /// Runtime instance provider router used by scale-out provider selector tests.
        /// </summary>
        private sealed class FakeRuntimeInstanceProviderRouter : IAiRuntimeInstanceProviderRouter
        {
            private readonly IAiRuntimeInstanceProvider? provider;

            /// <summary>
            /// Initializes a new instance of the <see cref="FakeRuntimeInstanceProviderRouter"/> class.
            /// </summary>
            /// <param name="provider">The optional provider.</param>
            public FakeRuntimeInstanceProviderRouter(
                IAiRuntimeInstanceProvider? provider)
            {
                this.provider = provider;
            }

            /// <inheritdoc />
            public IReadOnlyCollection<string> ProviderNames =>
                this.provider is null
                    ? Array.Empty<string>()
                    : new[] { "http" };

            /// <inheritdoc />
            public TProvider GetRequiredProvider<TProvider>(
                AiRuntimeInstanceCapacityDescriptor descriptor)
                where TProvider : IAiRuntimeInstanceProvider
            {
                if (this.TryGetProvider<TProvider>(descriptor, out var resolvedProvider))
                {
                    return resolvedProvider;
                }

                throw new InvalidOperationException("Provider not found.");
            }

            /// <inheritdoc />
            public bool TryGetProvider<TProvider>(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                out TProvider provider)
                where TProvider : IAiRuntimeInstanceProvider
            {
                if (this.provider is TProvider typedProvider)
                {
                    provider = typedProvider;
                    return true;
                }

                provider = default!;
                return false;
            }
        }

        /// <summary>
        /// Scale-out provider that succeeds.
        /// </summary>
        private sealed class SuccessfulScaleOutProvider : IAiRuntimeScaleOutProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return descriptor is not null;
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
                        RuntimeInstanceId = "runtime-1",
                        ProviderOperationId = "provider-operation-1",
                        Message = "provider fulfilled scale-out",
                        Metadata = new Dictionary<string, string>
                        {
                            ["provider.result"] = "success"
                        }
                    });
            }
        }

        /// <summary>
        /// Scale-out provider that fails without rejecting.
        /// </summary>
        private sealed class FailingScaleOutProvider : IAiRuntimeScaleOutProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return descriptor is not null;
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeScaleOutProviderResult
                    {
                        Success = false,
                        Rejected = false,
                        ProviderOperationId = "provider-operation-1",
                        FailureReason = "provider capacity unavailable",
                        Message = "provider capacity unavailable",
                        Metadata = new Dictionary<string, string>
                        {
                            ["provider.result"] = "capacity-unavailable"
                        }
                    });
            }
        }

        /// <summary>
        /// Scale-out provider that throws.
        /// </summary>
        private sealed class ThrowingScaleOutProvider : IAiRuntimeScaleOutProvider
        {
            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return descriptor is not null;
            }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
                AiRuntimeScaleOutProviderRequest request,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("provider exploded");
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
        /// Provides a minimal runtime observability facade for provider selector observability tests.
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
