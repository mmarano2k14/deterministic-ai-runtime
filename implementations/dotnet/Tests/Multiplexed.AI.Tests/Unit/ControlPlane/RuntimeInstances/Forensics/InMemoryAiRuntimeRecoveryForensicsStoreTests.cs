using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Tests the in-memory runtime recovery forensics store.
    /// </summary>
    public sealed class InMemoryAiRuntimeRecoveryForensicsStoreTests
    {
        /// <summary>
        /// Verifies that the store can upsert and retrieve a recovery forensics record by forensics id.
        /// </summary>
        [Fact]
        public async Task UpsertAsync_Should_Store_Record_By_ForensicsId()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            var record = CreateRecord("forensics-001", "execution-001", "shared-run-001");

            await store.UpsertAsync(record);

            var loaded = await store.GetByForensicsIdAsync("forensics-001");

            loaded.Should().NotBeNull();
            loaded!.Identity.ForensicsId.Should().Be("forensics-001");
            loaded.Identity.ExecutionId.Should().Be("execution-001");
            loaded.Identity.SharedRunId.Should().Be("shared-run-001");
            loaded.Artifacts.Restored.Should().Contain(AiRuntimeRecoveryArtifactName.DurableExecutionId);
            loaded.Artifacts.Recreated.Should().Contain(AiRuntimeRecoveryArtifactName.ReplacementLocalRunId);
            loaded.Artifacts.LostVolatile.Should().Contain(AiRuntimeRecoveryArtifactName.OldClaimToken);
        }

        /// <summary>
        /// Verifies that appending events preserves the recovery event timeline.
        /// </summary>
        [Fact]
        public async Task AppendEventAsync_Should_Preserve_Event_Timeline()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-002", "execution-002", "shared-run-002"));

            await store.AppendEventAsync("forensics-002", CreateEvent("event-002-b", "forensics-002", AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered, "execution-002", "shared-run-002", "replacement-local-run-001", "runtime-2", DateTimeOffset.UtcNow.AddSeconds(2)));
            await store.AppendEventAsync("forensics-002", CreateEvent("event-002-a", "forensics-002", AiRuntimeRecoveryForensicsEventType.SharedRunRequeuedForResume, "execution-002", "shared-run-002", "failed-local-run-001", "runtime-1", DateTimeOffset.UtcNow.AddSeconds(1)));

            var loaded = await store.GetByForensicsIdAsync("forensics-002");

            loaded.Should().NotBeNull();
            loaded!.Events.Should().HaveCount(2);
            loaded.Events.Select(x => x.EventType).Should().ContainInOrder(AiRuntimeRecoveryForensicsEventType.SharedRunRequeuedForResume, AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered);
        }

        /// <summary>
        /// Verifies that duplicate event identifiers are ignored during append.
        /// </summary>
        [Fact]
        public async Task AppendEventAsync_Should_Deduplicate_Events_By_EventId()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-003", "execution-003", "shared-run-003"));

            var evt = CreateEvent("event-003", "forensics-003", AiRuntimeRecoveryForensicsEventType.ExecutionRecoveryCandidateDetected, "execution-003", "shared-run-003", "local-run-003", "runtime-1", DateTimeOffset.UtcNow);

            await store.AppendEventAsync("forensics-003", evt);
            await store.AppendEventAsync("forensics-003", evt);

            var loaded = await store.GetByForensicsIdAsync("forensics-003");

            loaded.Should().NotBeNull();
            loaded!.Events.Should().HaveCount(1);
            loaded.Events.Single().EventId.Should().Be("event-003");
        }

        /// <summary>
        /// Verifies that records can be queried by execution id.
        /// </summary>
        [Fact]
        public async Task ListByExecutionIdAsync_Should_Return_Matching_Records()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-004-a", "execution-004", "shared-run-004-a"));
            await store.UpsertAsync(CreateRecord("forensics-004-b", "execution-004", "shared-run-004-b"));
            await store.UpsertAsync(CreateRecord("forensics-004-c", "execution-other", "shared-run-other"));

            var records = await store.ListByExecutionIdAsync("execution-004");

            records.Should().HaveCount(2);
            records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("forensics-004-a", "forensics-004-b");
        }

        /// <summary>
        /// Verifies that records can be queried by shared run id.
        /// </summary>
        [Fact]
        public async Task ListBySharedRunIdAsync_Should_Return_Matching_Records()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-005-a", "execution-005-a", "shared-run-005"));
            await store.UpsertAsync(CreateRecord("forensics-005-b", "execution-005-b", "shared-run-005"));
            await store.UpsertAsync(CreateRecord("forensics-005-c", "execution-005-c", "shared-run-other"));

            var records = await store.ListBySharedRunIdAsync("shared-run-005");

            records.Should().HaveCount(2);
            records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("forensics-005-a", "forensics-005-b");
        }

        /// <summary>
        /// Verifies that records can be queried by failed or replacement runtime instance id.
        /// </summary>
        [Fact]
        public async Task ListByRuntimeInstanceIdAsync_Should_Return_Failed_And_Replacement_Runtime_Matches()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-006-a", "execution-006-a", "shared-run-006-a", failedRuntimeInstanceId: "runtime-1", replacementRuntimeInstanceId: "runtime-2"));
            await store.UpsertAsync(CreateRecord("forensics-006-b", "execution-006-b", "shared-run-006-b", failedRuntimeInstanceId: "runtime-3", replacementRuntimeInstanceId: "runtime-1"));
            await store.UpsertAsync(CreateRecord("forensics-006-c", "execution-006-c", "shared-run-006-c", failedRuntimeInstanceId: "runtime-4", replacementRuntimeInstanceId: "runtime-5"));

            var records = await store.ListByRuntimeInstanceIdAsync("runtime-1");

            records.Should().HaveCount(2);
            records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("forensics-006-a", "forensics-006-b");
        }

        /// <summary>
        /// Verifies that records can be queried by runtime failure incident id.
        /// </summary>
        [Fact]
        public async Task ListByRuntimeFailureIncidentIdAsync_Should_Return_All_Records_For_Same_Runtime_Failure()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-007-a", "execution-007-a", "shared-run-007-a", runtimeFailureIncidentId: "incident-runtime-1"));
            await store.UpsertAsync(CreateRecord("forensics-007-b", "execution-007-b", "shared-run-007-b", runtimeFailureIncidentId: "incident-runtime-1"));
            await store.UpsertAsync(CreateRecord("forensics-007-c", "execution-007-c", "shared-run-007-c", runtimeFailureIncidentId: "incident-runtime-2"));

            var records = await store.ListByRuntimeFailureIncidentIdAsync("incident-runtime-1");

            records.Should().HaveCount(2);
            records.Select(x => x.Identity.ForensicsId).Should().BeEquivalentTo("forensics-007-a", "forensics-007-b");
        }

        /// <summary>
        /// Verifies that recent records are returned newest first and limited.
        /// </summary>
        [Fact]
        public async Task ListRecentAsync_Should_Return_Limited_Records_Newest_First()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();

            await store.UpsertAsync(CreateRecord("forensics-008-a", "execution-008-a", "shared-run-008-a", createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-3)));
            await store.UpsertAsync(CreateRecord("forensics-008-b", "execution-008-b", "shared-run-008-b", createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-2)));
            await store.UpsertAsync(CreateRecord("forensics-008-c", "execution-008-c", "shared-run-008-c", createdAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1)));

            var records = await store.ListRecentAsync(2);

            records.Should().HaveCount(2);
            records.Select(x => x.Identity.ForensicsId).Should().ContainInOrder("forensics-008-c", "forensics-008-b");
        }

        /// <summary>
        /// Creates a recovery forensics record for tests.
        /// </summary>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="failedRuntimeInstanceId">The failed runtime instance identifier.</param>
        /// <param name="replacementRuntimeInstanceId">The replacement runtime instance identifier.</param>
        /// <param name="runtimeFailureIncidentId">The runtime failure incident identifier.</param>
        /// <param name="createdAtUtc">The record creation timestamp.</param>
        /// <returns>The recovery forensics record.</returns>
        private static AiRuntimeRecoveryForensicsRecord CreateRecord(
            string forensicsId,
            string executionId,
            string sharedRunId,
            string failedRuntimeInstanceId = "runtime-1",
            string replacementRuntimeInstanceId = "runtime-2",
            string runtimeFailureIncidentId = "incident-runtime-1",
            DateTimeOffset? createdAtUtc = null)
        {
            var now = createdAtUtc ?? DateTimeOffset.UtcNow;

            return new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = forensicsId,
                    ExecutionId = executionId,
                    SharedRunId = sharedRunId,
                    PipelineName = "pipeline-forensics-test",
                    TenantId = "tenant-a",
                    TenantGroupId = "tenant-group-a",
                    ControlPlaneId = "control-plane-test"
                },
                Failure = new AiRuntimeRecoveryFailureInfo
                {
                    RuntimeFailureIncidentId = runtimeFailureIncidentId,
                    FailedRuntimeInstanceId = failedRuntimeInstanceId,
                    FailedLocalRunId = $"failed-local-{forensicsId}",
                    FailureSignal = "runtime-unhealthy",
                    HealthStatusBefore = "ready",
                    HealthStatusAfter = "unhealthy",
                    SuppressCapacityReason = "runtime-unhealthy",
                    FailureDetectedAtUtc = now
                },
                Recovery = new AiRuntimeRecoveryInfo
                {
                    RecoveryMode = "resume-existing-execution",
                    RecoveryKind = "in-flight-execution-resume",
                    Outcome = "completed",
                    Reason = "failed-runtime-instance",
                    RecoveryStartedAtUtc = now,
                    RecoveryCompletedAtUtc = now
                },
                Replacement = new AiRuntimeRecoveryReplacementInfo
                {
                    ReplacementRuntimeInstanceId = replacementRuntimeInstanceId,
                    ReplacementLocalRunId = $"replacement-local-{forensicsId}",
                    DispatchReason = "recovered-shared-run",
                    SelectedAtUtc = now,
                    LocalRunRegisteredAtUtc = now
                },
                Context = new AiRuntimeRecoveryContextInfo
                {
                    SnapshotContextKey = $"snapshot-context-{forensicsId}",
                    RecordContextKey = $"record-context-{forensicsId}",
                    ContextKeyMismatch = true,
                    RehydratedByExecutionId = true,
                    RehydrationReason = "record-context-key-mismatch"
                },
                Dag = new AiRuntimeRecoveryDagInfo
                {
                    StepCount = 100,
                    CompletedStepsBeforeRecovery = 49,
                    RecoveredFromStep = "step-050",
                    FinalCompletedSteps = 100,
                    CompletedStepsReplayed = false,
                    Outcome = "completed"
                },
                Artifacts = new AiRuntimeRecoveryArtifacts
                {
                    Restored =
                    [
                        AiRuntimeRecoveryArtifactName.DurableExecutionId,
                        AiRuntimeRecoveryArtifactName.DagState,
                        AiRuntimeRecoveryArtifactName.CompletedDagSteps,
                        AiRuntimeRecoveryArtifactName.ExecutionContextSnapshot
                    ],
                    Recreated =
                    [
                        AiRuntimeRecoveryArtifactName.ReplacementRuntimeInstance,
                        AiRuntimeRecoveryArtifactName.ReplacementLocalRunId,
                        AiRuntimeRecoveryArtifactName.RuntimeRunExecutionIndexEntry
                    ],
                    LostVolatile =
                    [
                        AiRuntimeRecoveryArtifactName.FailedRuntimeLocalQueueMemory,
                        AiRuntimeRecoveryArtifactName.OldClaimToken,
                        AiRuntimeRecoveryArtifactName.OldLease
                    ]
                },
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        /// <summary>
        /// Creates a recovery forensics event for tests.
        /// </summary>
        /// <param name="eventId">The event identifier.</param>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="eventType">The event type.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="sharedRunId">The shared run identifier.</param>
        /// <param name="localRunId">The local run identifier.</param>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="timestampUtc">The event timestamp.</param>
        /// <returns>The recovery forensics event.</returns>
        private static AiRuntimeRecoveryForensicsEvent CreateEvent(
            string eventId,
            string forensicsId,
            string eventType,
            string executionId,
            string sharedRunId,
            string localRunId,
            string runtimeInstanceId,
            DateTimeOffset timestampUtc)
        {
            return new AiRuntimeRecoveryForensicsEvent
            {
                EventId = eventId,
                ForensicsId = forensicsId,
                TimestampUtc = timestampUtc,
                EventType = eventType,
                Outcome = "ok",
                Reason = "test",
                ExecutionId = executionId,
                SharedRunId = sharedRunId,
                LocalRunId = localRunId,
                RuntimeInstanceId = runtimeInstanceId
            };
        }
    }
}