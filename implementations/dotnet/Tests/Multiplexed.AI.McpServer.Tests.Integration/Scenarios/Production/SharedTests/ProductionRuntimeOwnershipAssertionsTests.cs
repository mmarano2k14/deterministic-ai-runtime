using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Assertions;
using Xunit;
using Xunit.Sdk;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.SharedTests
{
    public sealed class ProductionRuntimeOwnershipAssertionsTests
    {
        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Accept_No_Recovery_Without_Initial_Assignment_Event()
        {
            var parent = CreateParent();

            var proof =
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        Array.Empty<AiRuntimeLifecycleEvent>(),
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal),
                        "stable ownership proof");

            Assert.Equal(0, proof.ExpectedRecoveredSharedRunCount);
            Assert.Equal(0, proof.ObservedRecoveredSharedRunCount);
            Assert.Equal(0, proof.RecoveryReleaseCount);
            Assert.Equal(0, proof.RecoveryReassignmentCount);
            Assert.Equal(0, proof.RecoveryTransitionCount);
            Assert.Equal(0, proof.ValidRuntimeOwnershipOverlapCount);
        }

        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Accept_Connected_Recovery_Handoff_Without_Initial_Assignment_Event()
        {
            var parent = CreateParent();

            var proof =
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        CreateRecoveryEvents(parent),
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            parent.SharedRunId
                        },
                        "recovery ownership proof");

            Assert.Equal(1, proof.ExpectedRecoveredSharedRunCount);
            Assert.Equal(1, proof.ObservedRecoveredSharedRunCount);
            Assert.Equal(1, proof.RecoveryReleaseCount);
            Assert.Equal(1, proof.RecoveryReassignmentCount);
            Assert.Equal(1, proof.RecoveryTransitionCount);
            Assert.Equal(0, proof.ValidRuntimeOwnershipOverlapCount);
        }

        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Not_Depend_On_Release_And_Reassignment_Timestamp_Order()
        {
            var parent = CreateParent();

            var proof =
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        CreateRecoveryEvents(
                            parent,
                            releaseTimestampUtc:
                                DateTimeOffset.Parse("2026-08-24T00:00:03Z"),
                            reassignmentTimestampUtc:
                                DateTimeOffset.Parse("2026-08-24T00:00:02Z")),
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            parent.SharedRunId
                        },
                        "timestamp-independent ownership proof");

            Assert.Equal(1, proof.RecoveryTransitionCount);
            Assert.Equal(0, proof.ValidRuntimeOwnershipOverlapCount);
        }

        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Accept_A_Linear_Multi_Recovery_Chain()
        {
            var parent = CreateParent();
            var events =
                CreateRecoveryEvents(parent)
                    .Concat(
                        CreateRecoveryEvents(
                            parent,
                            incidentId: "failure-2",
                            releasedRuntimeInstanceId: "runtime-2",
                            releasedLocalRunId: "local-2",
                            replacementRuntimeInstanceId: "runtime-3",
                            replacementLocalRunId: "local-3"))
                    .ToArray();

            var proof =
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        events,
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            parent.SharedRunId
                        },
                        "multi recovery ownership proof");

            Assert.Equal(1, proof.ObservedRecoveredSharedRunCount);
            Assert.Equal(2, proof.RecoveryTransitionCount);
            Assert.Equal(0, proof.ValidRuntimeOwnershipOverlapCount);
        }

        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Reject_Reassignment_Without_Release()
        {
            var parent = CreateParent();
            var events = new[]
            {
                CreateEvent(
                    AiRuntimeLifecycleEvents.WorkReassigned,
                    "runtime-2",
                    "local-2",
                    parent,
                    incidentId: "failure-1",
                    previousStatus:
                        AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery,
                    currentStatus: "assigned",
                    forensicsId: "forensics-failure-1")
            };

            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        events,
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            parent.SharedRunId
                        },
                        "missing release ownership proof"));
        }

        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Reject_Disconnected_Recovery_Chain()
        {
            var parent = CreateParent();
            var events =
                CreateRecoveryEvents(parent)
                    .Concat(
                        CreateRecoveryEvents(
                            parent,
                            incidentId: "failure-2",
                            releasedRuntimeInstanceId: "runtime-unrelated",
                            releasedLocalRunId: "local-unrelated",
                            replacementRuntimeInstanceId: "runtime-3",
                            replacementLocalRunId: "local-3"))
                    .ToArray();

            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        events,
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal)
                        {
                            parent.SharedRunId
                        },
                        "disconnected ownership proof"));
        }

        [Fact]
        public void AssertExactValidOwnershipHandoff_Should_Reject_Unexpected_Recovery_For_Unrecovered_Run()
        {
            var parent = CreateParent();

            Assert.ThrowsAny<XunitException>(() =>
                ProductionRuntimeOwnershipAssertions
                    .AssertExactValidOwnershipHandoff(
                        CreateRecoveryEvents(parent),
                        new[] { parent },
                        new HashSet<string>(StringComparer.Ordinal),
                        "unexpected recovery ownership proof"));
        }

        private static ProductionRuntimeOwnershipProofTarget CreateParent()
        {
            return new ProductionRuntimeOwnershipProofTarget(
                "shared-run-1",
                "tenant-1",
                "execution-1");
        }

        private static IReadOnlyList<AiRuntimeLifecycleEvent> CreateRecoveryEvents(
            ProductionRuntimeOwnershipProofTarget parent,
            DateTimeOffset? releaseTimestampUtc = null,
            DateTimeOffset? reassignmentTimestampUtc = null,
            string incidentId = "failure-1",
            string releasedRuntimeInstanceId = "runtime-1",
            string releasedLocalRunId = "local-1",
            string replacementRuntimeInstanceId = "runtime-2",
            string replacementLocalRunId = "local-2")
        {
            return new[]
            {
                CreateEvent(
                    AiRuntimeLifecycleEvents.WorkReleased,
                    releasedRuntimeInstanceId,
                    releasedLocalRunId,
                    parent,
                    timestampUtc:
                        releaseTimestampUtc ??
                        DateTimeOffset.Parse("2026-08-24T00:00:02Z"),
                    incidentId: incidentId,
                    previousStatus: "assigned",
                    currentStatus:
                        AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery,
                    forensicsId: $"forensics-{incidentId}"),
                CreateEvent(
                    AiRuntimeLifecycleEvents.WorkReassigned,
                    replacementRuntimeInstanceId,
                    replacementLocalRunId,
                    parent,
                    timestampUtc:
                        reassignmentTimestampUtc ??
                        DateTimeOffset.Parse("2026-08-24T00:00:03Z"),
                    incidentId: incidentId,
                    previousStatus:
                        AiRuntimeRecoveryTransitionStatuses.ReleasedForRecovery,
                    currentStatus: "assigned",
                    forensicsId: $"forensics-{incidentId}")
            };
        }

        private static AiRuntimeLifecycleEvent CreateEvent(
            string eventType,
            string runtimeInstanceId,
            string localRunId,
            ProductionRuntimeOwnershipProofTarget parent,
            DateTimeOffset? timestampUtc = null,
            string? incidentId = null,
            string? previousStatus = null,
            string? currentStatus = null,
            string? forensicsId = null)
        {
            return new AiRuntimeLifecycleEvent
            {
                EventId =
                    $"{eventType}:{runtimeInstanceId}:{localRunId}:{incidentId ?? "none"}",
                EventType = eventType,
                TimestampUtc =
                    timestampUtc ??
                    DateTimeOffset.Parse("2026-08-24T00:00:01Z"),
                ControlPlaneId = "control-plane-1",
                PoolId = "pool-1",
                RuntimeInstanceId = runtimeInstanceId,
                TenantId = parent.TenantId,
                SharedRunId = parent.SharedRunId,
                LocalRunId = localRunId,
                ExecutionId = parent.ExecutionId,
                RuntimeFailureIncidentId = incidentId,
                ForensicsId = forensicsId,
                PreviousStatus = previousStatus,
                CurrentStatus = currentStatus
            };
        }
    }
}
