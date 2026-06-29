using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.Observability
{
    /// <summary>
    /// Tests process runtime host creation strategy control-plane observability events.
    /// </summary>
    public sealed class ProcessAiRuntimeHostCreationStrategyObservabilityTests
    {
        private const string ExpectedTenantId = "tenant-id-xxxx";
        private const string ExpectedTenantGroupId = "tenant-group-id-xxx";

        /// <summary>
        /// Verifies that disabled process host creation records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Started_And_Denied_Events_When_Process_Host_Creation_Is_Disabled()
        {
            var observer = new CapturingControlPlaneObserver();
            var strategy = CreateStrategy(
                new AiRuntimeProcessHostCreationOptions
                {
                    Enabled = false,
                    RuntimeHostAssemblyPath = "missing-host.dll",
                    BasePort = 51000,
                    MaxPort = 51010
                },
                observer);

            var result = await strategy
                .StartAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("process-host-creation-disabled", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, observer.Events[1].Area);
            Assert.Equal("runtime-process-host-creation", observer.Events[1].Operation);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.Equal("process-host-creation-disabled", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, observer.Events[1].Properties["tenantId"]?.ToString());
            Assert.Equal(ExpectedTenantGroupId, observer.Events[1].Properties["tenantGroupId"]?.ToString());
            Assert.Equal("Process", observer.Events[1].Properties["hostCreationMode"]?.ToString());
        }

        /// <summary>
        /// Verifies that a missing runtime host assembly path records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Denied_Event_When_RuntimeHostAssemblyPath_Is_Missing()
        {
            var observer = new CapturingControlPlaneObserver();
            var strategy = CreateStrategy(
                new AiRuntimeProcessHostCreationOptions
                {
                    Enabled = true,
                    RuntimeHostAssemblyPath = string.Empty,
                    BasePort = 51020,
                    MaxPort = 51030
                },
                observer);

            var result = await strategy
                .StartAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal("process-runtime-host-assembly-path-missing", result.FailureReason);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.Equal("process-runtime-host-assembly-path-missing", observer.Events[1].FailureReason);
            Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
        }

        /// <summary>
        /// Verifies that a missing runtime host assembly file records started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Denied_Event_When_RuntimeHostAssemblyFile_Is_Not_Found()
        {
            var observer = new CapturingControlPlaneObserver();
            var missingAssemblyPath = Path.Combine(Path.GetTempPath(), $"missing-runtime-host-{Guid.NewGuid():N}.dll");
            var strategy = CreateStrategy(
                new AiRuntimeProcessHostCreationOptions
                {
                    Enabled = true,
                    RuntimeHostAssemblyPath = missingAssemblyPath,
                    BasePort = 51040,
                    MaxPort = 51050
                },
                observer);

            var result = await strategy
                .StartAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.StartsWith("process-runtime-host-assembly-not-found:", result.FailureReason, StringComparison.Ordinal);
            Assert.Equal(2, observer.Events.Count);
            AssertStartedEvent(observer.Events[0]);
            Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
            Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
            Assert.True(
                observer.Events[1].FailureReason?.StartsWith("process-runtime-host-assembly-not-found:", StringComparison.Ordinal) == true);
            Assert.Contains(
                missingAssemblyPath,
                observer.Events[1].FailureReason,
                StringComparison.Ordinal);
            Assert.Equal("runtime-1", observer.Events[1].Correlation.RuntimeInstanceId);
        }

        /// <summary>
        /// Verifies that process start failures record started and denied events.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Denied_Event_When_Process_Start_Fails()
        {
            var observer = new CapturingControlPlaneObserver();
            var temporaryAssemblyPath = Path.Combine(Path.GetTempPath(), $"runtime-host-{Guid.NewGuid():N}.dll");
            await File.WriteAllTextAsync(temporaryAssemblyPath, "not a real assembly", CancellationToken.None).ConfigureAwait(false);

            try
            {
                var strategy = CreateStrategy(
                    new AiRuntimeProcessHostCreationOptions
                    {
                        Enabled = true,
                        RuntimeHostAssemblyPath = temporaryAssemblyPath,
                        DotnetExecutablePath = Path.Combine(Path.GetTempPath(), $"missing-dotnet-{Guid.NewGuid():N}"),
                        BasePort = 51060,
                        MaxPort = 51070
                    },
                    observer);

                var result = await strategy
                    .StartAsync(CreateRequest(), CancellationToken.None)
                    .ConfigureAwait(false);

                Assert.False(result.Success);
                Assert.StartsWith("process-start-failed:", result.FailureReason, StringComparison.Ordinal);
                Assert.Equal(2, observer.Events.Count);
                AssertStartedEvent(observer.Events[0]);
                Assert.Equal(AiControlPlaneEventType.OperationFailed, observer.Events[1].EventType);
                Assert.Equal(AiControlPlaneOperationOutcome.Denied, observer.Events[1].Outcome);
                Assert.StartsWith("process-start-failed:", observer.Events[1].FailureReason, StringComparison.Ordinal);
                Assert.Equal("runtime-1", observer.Events[1].Properties["runtimeInstanceId"]?.ToString());
            }
            finally
            {
                File.Delete(temporaryAssemblyPath);
            }
        }

        /// <summary>
        /// Verifies that denied process host creation control-plane events are recorded to the decision ledger.
        /// </summary>
        /// <returns>A task representing the asynchronous test operation.</returns>
        [Fact]
        public async Task StartAsync_Should_Record_Denied_ProcessHostCreation_ControlPlane_Events_To_Ledger()
        {
            var ledger = new CapturingDecisionLedgerRecorder();
            var observability = new FakeRuntimeObservability(ledger);
            var observer = new CompositeAiControlPlaneObserver(
                new IAiControlPlaneEventSink[]
                {
                    new RuntimeObservabilityAiControlPlaneEventSink(observability)
                });
            var strategy = CreateStrategy(
                new AiRuntimeProcessHostCreationOptions
                {
                    Enabled = false,
                    RuntimeHostAssemblyPath = "missing-host.dll",
                    BasePort = 51080,
                    MaxPort = 51090
                },
                observer);

            var result = await strategy
                .StartAsync(CreateRequest(), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(result.Success);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[0].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Started, ledger.Entries[0].Outcome);
            Assert.Equal("control.scaling.runtime-process-host-creation.operationstarted", ledger.Entries[0].EventType);
            Assert.Equal(AiDecisionLedgerCategory.Scaling, ledger.Entries[1].Category);
            Assert.Equal(AiDecisionLedgerOutcome.Denied, ledger.Entries[1].Outcome);
            Assert.Equal("process-host-creation-disabled", ledger.Entries[1].Reason);
            Assert.Equal("control.scaling.runtime-process-host-creation.denied", ledger.Entries[1].EventType);
            Assert.Equal("runtime-1", ledger.Entries[1].Context.RuntimeInstanceId);
            Assert.Equal(ExpectedTenantId, ledger.Entries[1].Metadata!["tenant.id"]);
            Assert.Equal(ExpectedTenantGroupId, ledger.Entries[1].Metadata!["tenantGroupId"]);
            Assert.Equal("Process", ledger.Entries[1].Metadata!["hostCreationMode"]);
        }

        /// <summary>
        /// Asserts the common process host creation started event shape.
        /// </summary>
        /// <param name="controlPlaneEvent">The captured control-plane event.</param>
        private static void AssertStartedEvent(
            AiControlPlaneEvent controlPlaneEvent)
        {
            Assert.Equal(AiControlPlaneEventType.OperationStarted, controlPlaneEvent.EventType);
            Assert.Equal(AiControlPlaneArea.Scaling, controlPlaneEvent.Area);
            Assert.Equal("runtime-process-host-creation", controlPlaneEvent.Operation);
            Assert.Null(controlPlaneEvent.Outcome);
            Assert.Equal("runtime-1", controlPlaneEvent.Correlation.RuntimeInstanceId);
            Assert.False(string.IsNullOrWhiteSpace(controlPlaneEvent.Correlation.PipelineKey));
        }

        /// <summary>
        /// Creates a process host creation strategy.
        /// </summary>
        /// <param name="options">The process host creation options.</param>
        /// <param name="observer">The control-plane observer.</param>
        /// <returns>The process host creation strategy.</returns>
        private static ProcessAiRuntimeHostCreationStrategy CreateStrategy(
            AiRuntimeProcessHostCreationOptions options,
            IAiControlPlaneObserver observer)
        {
            return new ProcessAiRuntimeHostCreationStrategy(
                Options.Create(options),
                NullLogger<ProcessAiRuntimeHostCreationStrategy>.Instance,
                observer);
        }

        /// <summary>
        /// Creates a runtime host start request.
        /// </summary>
        /// <returns>The runtime host start request.</returns>
        private static AiRuntimeHostStartRequest CreateRequest()
        {
            var snapshot = AiExecutionContextSnapshotTestFactory.Create();

            return new AiRuntimeHostStartRequest
            {
                RuntimeInstanceId = "runtime-1",
                ControlPlaneId = "control-plane-1",
                ProviderName = "http",
                TransportName = AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                TransportEndpoint = "http://localhost:0",
                HostCreationMode = AiRuntimeHostCreationMode.Process,
                ExecutionContextSnapshot = snapshot,
                TenantId = ExpectedTenantId,
                TenantGroupId = ExpectedTenantGroupId,
                IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated.ToString(),
                PreferDedicatedCapacity = true,
                AllowSharedFallback = false,
                MaxRuntimeInstances = 3,
                RuntimeInstanceIdPrefix = "runtime",
                WorkerCountPerInstance = 2,
                MaxConcurrentRunsPerInstance = 2,
                LocalQueueCapacity = 100,
                Metadata = new Dictionary<string, string>
                {
                    ["source"] = "unit-test"
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
        /// Provides a minimal runtime observability facade for process host creation observability tests.
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
