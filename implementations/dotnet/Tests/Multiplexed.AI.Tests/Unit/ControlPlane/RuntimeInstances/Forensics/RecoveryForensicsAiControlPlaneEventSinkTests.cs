using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Tests the centralized Recovery Forensics projection sink.
    /// </summary>
    public sealed class RecoveryForensicsAiControlPlaneEventSinkTests
    {
        /// <summary>
        /// Verifies that one canonical recovery fact is translated to the existing forensics event model.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Project_Canonical_Recovery_Event_To_Existing_Recorder()
        {
            var recorder = new CapturingRecorder();
            var sink = new RecoveryForensicsAiControlPlaneEventSink(recorder);

            var controlPlaneEvent = CreateEvent(
                AiEngineEvents.Recovery.ReplacementRuntimeSelected,
                "runtime-recovery:execution-1:shared-run-1:local-run-1:replacement.runtime.selected:runtime-2",
                "selected",
                "replacement-runtime-selected-for-recovery-redispatch",
                metadata: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = "tenant-1",
                    [AiRuntimeRecoveryMetadataKeys.ReplacementRuntimeInstanceId] = "runtime-2"
                },
                runtimeInstanceId: "runtime-2");

            await sink.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Empty(recorder.Records);
            var recoveryEvent = Assert.Single(recorder.Events);
            Assert.Equal(controlPlaneEvent.EventId, recoveryEvent.EventId);
            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-1", recoveryEvent.ForensicsId);
            Assert.Equal(AiEngineEvents.Recovery.ReplacementRuntimeSelected, recoveryEvent.EventType);
            Assert.Equal("selected", recoveryEvent.Outcome);
            Assert.Equal("replacement-runtime-selected-for-recovery-redispatch", recoveryEvent.Reason);
            Assert.Equal("execution-1", recoveryEvent.ExecutionId);
            Assert.Equal("shared-run-1", recoveryEvent.SharedRunId);
            Assert.Equal("local-run-1", recoveryEvent.LocalRunId);
            Assert.Equal("runtime-2", recoveryEvent.RuntimeInstanceId);
            Assert.Equal("tenant-1", recoveryEvent.Metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId]);
            Assert.Equal("runtime-2", recoveryEvent.Metadata[AiRuntimeRecoveryMetadataKeys.ReplacementRuntimeInstanceId]);
            Assert.DoesNotContain(AiRuntimeRecoveryMetadataKeys.ProjectionForensicsId, recoveryEvent.Metadata.Keys);
            Assert.DoesNotContain(AiRuntimeRecoveryMetadataKeys.ProjectionOutcome, recoveryEvent.Metadata.Keys);
            Assert.DoesNotContain(AiRuntimeRecoveryMetadataKeys.ProjectionReason, recoveryEvent.Metadata.Keys);
        }

        /// <summary>
        /// Verifies that the shared-run requeue fact preserves the existing rich recovery record contract,
        /// while the local-run mark remains a distinct canonical event appended through the same sink.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Preserve_Rich_Recovery_Transition_Record_Contract()
        {
            var recorder = new CapturingRecorder();
            var sink = new RecoveryForensicsAiControlPlaneEventSink(recorder);
            var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [AiRuntimeRecoveryMetadataKeys.ForensicsId] = "runtime-recovery:execution-1:shared-run-1:local-run-1",
                [AiRuntimeRecoveryMetadataKeys.FailureIncidentId] = "runtime-failure:runtime-1",
                [AiRuntimeRecoveryMetadataKeys.Mode] = AiRuntimeRecoveryModes.ResumeExistingExecution,
                [AiRuntimeRecoveryMetadataKeys.FailedExecutionId] = "execution-1",
                [AiRuntimeRecoveryMetadataKeys.FailedRuntimeInstanceId] = "runtime-1",
                [AiRuntimeRecoveryMetadataKeys.FailedLocalRunId] = "local-run-1",
                [AiRuntimeRecoveryMetadataKeys.Reason] = "runtime-unhealthy",
                [AiRuntimeInstanceIsolationMetadataKeys.TenantId] = "tenant-1",
                [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = "tenant-group-1",
                [AiControlPlaneMetadataKeys.ControlPlaneId] = "control-plane-1",
                [AiPipelineMetadataKeys.Name] = "pipeline-1"
            };

            var sharedRunRequeued = CreateEvent(
                AiEngineEvents.Recovery.SharedRunRequeuedForResume,
                "runtime-recovery:execution-1:shared-run-1:local-run-1:shared.run.requeued.for.resume",
                "requeued",
                "runtime-unhealthy",
                metadata,
                runtimeInstanceId: "runtime-1");

            var localRunMarked = CreateEvent(
                AiEngineEvents.Recovery.FailedLocalRunMarkedRequeuedForRecovery,
                "runtime-recovery:execution-1:shared-run-1:local-run-1:failed.local.run.marked.requeued.for.recovery",
                "requeued",
                "runtime-unhealthy",
                metadata,
                runtimeInstanceId: "runtime-1");

            await sink.RecordAsync(sharedRunRequeued, CancellationToken.None).ConfigureAwait(false);
            await sink.RecordAsync(localRunMarked, CancellationToken.None).ConfigureAwait(false);

            var record = Assert.Single(recorder.Records);
            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-1", record.Identity.ForensicsId);
            Assert.Equal("execution-1", record.Identity.ExecutionId);
            Assert.Equal("shared-run-1", record.Identity.SharedRunId);
            Assert.Equal("tenant-1", record.Identity.TenantId);
            Assert.Equal("tenant-group-1", record.Identity.TenantGroupId);
            Assert.Equal("control-plane-1", record.Identity.ControlPlaneId);
            Assert.Equal("pipeline-1", record.Identity.PipelineName);
            Assert.NotNull(record.Failure);
            Assert.Equal("runtime-failure:runtime-1", record.Failure!.RuntimeFailureIncidentId);
            Assert.Equal("runtime-1", record.Failure.FailedRuntimeInstanceId);
            Assert.Equal("local-run-1", record.Failure.FailedLocalRunId);
            Assert.Equal("runtime-execution-recovery", record.Failure.FailureSignal);
            Assert.Equal("runtime-unhealthy", record.Failure.SuppressCapacityReason);
            Assert.NotNull(record.Recovery);
            Assert.Equal(AiRuntimeRecoveryModes.ResumeExistingExecution, record.Recovery!.RecoveryMode);
            Assert.Equal("in-flight-execution-resume", record.Recovery.RecoveryKind);
            Assert.Equal("requeued", record.Recovery.Outcome);
            Assert.Equal("runtime-unhealthy", record.Recovery.Reason);
            Assert.Contains(AiRuntimeRecoveryArtifactName.DurableExecutionId, record.Artifacts.Restored);
            Assert.Contains(AiRuntimeRecoveryArtifactName.DispatchAssignment, record.Artifacts.Recreated);
            Assert.Contains(AiRuntimeRecoveryArtifactName.FailedRuntimeLocalQueueMemory, record.Artifacts.LostVolatile);
            Assert.Single(record.Events);
            Assert.Equal(AiEngineEvents.Recovery.SharedRunRequeuedForResume, record.Events[0].EventType);

            var appendedEvent = Assert.Single(recorder.Events);
            Assert.Equal(AiEngineEvents.Recovery.FailedLocalRunMarkedRequeuedForRecovery, appendedEvent.EventType);
        }

        /// <summary>
        /// Verifies that recovery-forensics composition registers exactly one centralized projection owner.
        /// </summary>
        [Fact]
        public void AddInMemoryRecoveryForensics_Should_Register_Exactly_One_RecoveryProjectionSink()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddInMemoryAiRuntimeRecoveryForensics();

            using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            var recoveryProjectionSinks = provider
                .GetServices<IAiControlPlaneEventSink>()
                .OfType<IAiControlPlaneEventProjectionSink>()
                .Where(sink =>
                    sink.ProjectionTarget ==
                    AiEngineEventProjectionTarget.RecoveryForensics)
                .ToArray();

            Assert.Single(recoveryProjectionSinks);
            Assert.IsType<RecoveryForensicsAiControlPlaneEventSink>(
                recoveryProjectionSinks[0]);
        }

        /// <summary>
        /// Verifies that strict recorder failures remain visible to the Event Manager instead of being swallowed by the sink.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Surface_Recorder_Failure()
        {
            var sink = new RecoveryForensicsAiControlPlaneEventSink(new ThrowingRecorder());
            var controlPlaneEvent = CreateEvent(
                AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                "event-1",
                "completed",
                "execution-recovery-completed",
                metadata: new Dictionary<string, object?>());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => sink.RecordAsync(controlPlaneEvent, CancellationToken.None)).ConfigureAwait(false);
        }

        /// <summary>
        /// Verifies that the central Event Manager routes a canonical recovery event only to the Recovery Forensics surface.
        /// </summary>
        [Fact]
        public async Task CompositeObserver_Should_Project_Canonical_Recovery_Event_Through_Recovery_Forensics_Sink()
        {
            var recorder = new CapturingRecorder();
            var observer = new CompositeAiControlPlaneObserver(
                [new RecoveryForensicsAiControlPlaneEventSink(recorder)]);
            var controlPlaneEvent = CreateEvent(
                AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                "event-1",
                "completed",
                "execution-recovery-completed",
                metadata: new Dictionary<string, object?>());

            await observer.RecordAsync(controlPlaneEvent, CancellationToken.None).ConfigureAwait(false);

            Assert.Single(recorder.Events);
        }

        private static AiControlPlaneEvent CreateEvent(
            string semanticEventType,
            string eventId,
            string outcome,
            string reason,
            IReadOnlyDictionary<string, object?> metadata,
            string runtimeInstanceId = "runtime-1")
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in metadata)
            {
                properties[pair.Key] = pair.Value;
            }

            properties[AiRuntimeRecoveryMetadataKeys.ProjectionForensicsId] =
                "runtime-recovery:execution-1:shared-run-1:local-run-1";
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionOutcome] = outcome;
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionReason] = reason;
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionSharedRunId] = "shared-run-1";
            properties[AiRuntimeRecoveryMetadataKeys.ProjectionLocalRunId] = "local-run-1";

            return new AiControlPlaneEvent
            {
                EventId = eventId,
                SemanticEventType = semanticEventType,
                EventType = AiControlPlaneEventType.OperationCompleted,
                Area = AiControlPlaneArea.Recovery,
                Operation = semanticEventType,
                Outcome = AiControlPlaneOperationOutcome.Succeeded,
                Correlation = new AiRuntimeExecutionCorrelationContext
                {
                    CorrelationId = "runtime-recovery:execution-1:shared-run-1:local-run-1",
                    RunId = "shared-run-1",
                    ExecutionId = "execution-1",
                    RuntimeInstanceId = runtimeInstanceId
                },
                TimestampUtc = DateTimeOffset.Parse("2026-08-22T00:00:00Z"),
                Properties = properties
            };
        }

        private sealed class CapturingRecorder : IAiRuntimeRecoveryForensicsRecorder
        {
            public List<AiRuntimeRecoveryForensicsRecord> Records { get; } = [];

            public List<AiRuntimeRecoveryForensicsEvent> Events { get; } = [];

            public Task RecordAsync(
                AiRuntimeRecoveryForensicsRecord record,
                CancellationToken cancellationToken = default)
            {
                this.Records.Add(record);
                return Task.CompletedTask;
            }

            public Task RecordEventAsync(
                AiRuntimeRecoveryForensicsEvent evt,
                CancellationToken cancellationToken = default)
            {
                this.Events.Add(evt);
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingRecorder : IAiRuntimeRecoveryForensicsRecorder
        {
            public Task RecordAsync(
                AiRuntimeRecoveryForensicsRecord record,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("forensics failed");
            }

            public Task RecordEventAsync(
                AiRuntimeRecoveryForensicsEvent evt,
                CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("forensics failed");
            }
        }
    }
}
