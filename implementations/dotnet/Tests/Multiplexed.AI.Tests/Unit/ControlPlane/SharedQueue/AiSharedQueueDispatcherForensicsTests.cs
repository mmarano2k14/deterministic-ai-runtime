using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Reservations;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using Multiplexed.AI.Tests.Fixtures;
using Multiplexed.Rbac.Core.ExecutionContext;
using Xunit;
using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedQueue
{
    /// <summary>
    /// Tests recovery forensics emitted by the shared queue dispatcher.
    /// </summary>
    public sealed class AiSharedQueueDispatcherForensicsTests
    {
        /// <summary>
        /// Verifies that successful recovery redispatch records replacement runtime selection forensics.
        /// </summary>
        [Fact]
        public async Task DispatchNextAsync_Should_Record_Replacement_Runtime_Selected_Forensics_When_Recovery_Redispatch_Succeeds()
        {
            var snapshot = CreateSnapshot();

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["controlPlaneId"] = "control-plane-1",
                ["recovery.forensicsId"] = "runtime-recovery:execution-1:shared-run-1:local-run-failed-1",
                ["recovery.failedExecutionId"] = "execution-1",
                ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                ["recovery.failedLocalRunId"] = "local-run-failed-1"
            };

            var queueItem = new AiSharedQueueItem
            {
                SharedRunId = "shared-run-1",
                ControlPlaneId = "control-plane-1",
                Status = AiSharedQueueItemStatus.Claimed,
                ExecutionContextSnapshot = snapshot,
                PipelineKey = "pipeline-1",
                Priority = 0,
                ClaimedByRuntimeInstanceId = "pump-runtime-1",
                ClaimedByWorkerId = "pump-worker-1",
                ClaimToken = "claim-token-1",
                EnqueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ClaimedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ClaimExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                Reason = "claimed-for-dispatch",
                Metadata = metadata
            };

            var sharedRun = new AiSharedRunRecord
            {
                SharedRunId = "shared-run-1",
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = snapshot,
                    Input = new Dictionary<string, object?>
                    {
                        ["value"] = 42
                    }
                },
                ExecutionContextSnapshot = snapshot,
                LocalRunId = "local-run-failed-1",
                ExecutionId = "execution-1",
                AssignedRuntimeInstanceId = "runtime-failed-1",
                AdmissionDecision = null,
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-1",
                RequestedBy = "test",
                Source = "unit-test",
                Reason = "recovery-redispatch",
                FailureReason = null,
                SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Metadata = metadata,
                ControlPlaneId = "control-plane-1"
            };

            var sharedQueue = new FakeSharedQueue
            {
                ClaimedItem = queueItem
            };

            var sharedRunStore = new FakeSharedRunStore
            {
                Record = sharedRun
            };

            var sharedRunDispatcher = new FakeSharedRunDispatcher
            {
                Result = new AiSharedRunDispatchResult
                {
                    Success = true,
                    SharedRunId = "shared-run-1",
                    RuntimeInstanceId = "runtime-replacement-1",
                    LocalRunId = "local-run-replacement-1",
                    ExecutionId = "execution-1",
                    ClaimToken = "claim-token-1",
                    Message = "dispatched",
                    FailureReason = null,
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(-10),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 10,
                    Diagnostics = Array.Empty<string>(),
                    Metadata = new Dictionary<string, string>()
                }
            };

            var admissionController = new FakeRunAdmissionController
            {
                Decision = new AiRunAdmissionDecision
                {
                    DecisionType = AiRunAdmissionDecisionType.AssignToInstance,
                    AssignedRuntimeInstanceId = "runtime-replacement-1",
                    TenantId = "tenant-1",
                    TenantGroupId = "tenant-group-1",
                    Reason = "assigned",
                    VisibleInstanceCount = 1,
                    AvailableInstanceCount = 1,
                    CurrentInstanceCount = 1,
                    MaxInstanceCount = 3,
                    Metadata = new Dictionary<string, string>()
                }
            };

            var registry = new FakeRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = "runtime-replacement-1",
                        TenantId = "tenant-1",
                        TenantGroupId = "tenant-group-1",
                        HostName = "localhost",
                        ProcessId = Environment.ProcessId,
                        WorkerCount = 1,
                        MaxConcurrentRuns = 3,
                        QueueCapacity = 10,
                        RuntimeVersion = "test",
                        Metadata = new Dictionary<string, string>(),
                        RegisteredAtUtc = DateTimeOffset.UtcNow,
                        Role = AiRuntimeInstanceRole.Runtime,
                        HostId = "host-1",
                        RuntimeId = "runtime-1",
                        ControlPlaneHostId = "control-plane-host-1",
                        ControlPlaneId = "control-plane-1"
                    })
                .ConfigureAwait(false);

            var reservationStore = new FakeRuntimeAdmissionReservationStore();
            var scaleOutPublisher = new FakeRuntimeScaleOutRequestPublisher();
            var tenantRuntimeSettingsProvider = new FakeTenantRuntimeSettingsProvider();
            var executionContextAccessor = new FakeExecutionContextAccessor();
            var forensicsRecorder = new FakeRuntimeRecoveryForensicsRecorder();

            var dispatcher = new AiSharedQueueDispatcher(
                sharedQueue,
                sharedRunStore,
                sharedRunDispatcher,
                admissionController,
                reservationStore,
                registry,
                scaleOutPublisher,
                tenantRuntimeSettingsProvider,
                new StaticControlPlaneIdResolver("controlPlaneId"),
                executionContextAccessor,
                NullLogger<AiSharedQueueDispatcher>.Instance,
                forensicsRecorder);

            var result = await dispatcher
                .DispatchNextAsync(
                    new AiSharedQueueDispatchRequest
                    {
                        RuntimeInstanceId = "pump-runtime-1",
                        WorkerId = "pump-worker-1",
                        TenantId = "tenant-1",
                        PipelineKey = "pipeline-1",
                        ClaimTtl = TimeSpan.FromSeconds(30),
                        CorrelationId = "correlation-1",
                        RequestedBy = "test",
                        Source = "unit-test",
                        Reason = "test recovery redispatch",
                        Metadata = metadata
                    })
                .ConfigureAwait(false);

            Assert.True(result.Success, result.FailureReason ?? result.Message);
            Assert.Equal("runtime-replacement-1", result.RuntimeInstanceId);
            Assert.Equal(1, sharedQueue.MarkDispatchedCalls);
            Assert.Equal(1, sharedRunStore.MarkDispatchedCalls);
            Assert.Equal(1, sharedRunDispatcher.DispatchCalls);
            Assert.Equal(1, reservationStore.ReserveCalls);
            Assert.Equal(1, reservationStore.ReleaseCalls);

            var forensicEvent = Assert.Single(forensicsRecorder.Events);

            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-failed-1", forensicEvent.ForensicsId);
            Assert.Equal(AiRuntimeRecoveryForensicsEventType.ReplacementRuntimeSelected, forensicEvent.EventType);
            Assert.Equal("selected", forensicEvent.Outcome);
            Assert.Equal("execution-1", forensicEvent.ExecutionId);
            Assert.Equal("shared-run-1", forensicEvent.SharedRunId);
            Assert.True(
                string.IsNullOrWhiteSpace(forensicEvent.LocalRunId) ||
                string.Equals("local-run-replacement-1", forensicEvent.LocalRunId, StringComparison.Ordinal),
                $"Replacement runtime selection forensics should either omit LocalRunId before dispatch or expose the selected replacement local run id. Actual='{forensicEvent.LocalRunId}'.");

            Assert.Equal("runtime-replacement-1", forensicEvent.RuntimeInstanceId);
            Assert.Equal("runtime-replacement-1", forensicEvent.Metadata["replacement.runtimeInstanceId"]);

            if (forensicEvent.Metadata.ContainsKey("replacement.localRunId"))
            {
                Assert.Equal("local-run-replacement-1", forensicEvent.Metadata["replacement.localRunId"]);
            }
            Assert.Equal("runtime-failed-1", forensicEvent.Metadata["failed.runtimeInstanceId"]);
            Assert.Equal("local-run-failed-1", forensicEvent.Metadata["failed.localRunId"]);
            Assert.Equal("claim-token-1", forensicEvent.Metadata["queue.claimToken"]);
        }

        /// <summary>
        /// Creates a deterministic execution context snapshot for the test.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-tenant-1",
                Project = "project-1",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "tenant-1",
                Namespaces = [],
                InFlightCount = 0,
                TtlSeconds = 30,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Fake shared queue.
        /// </summary>
        private sealed class FakeSharedQueue : IAiSharedQueue
        {
            /// <summary>
            /// Gets or sets the claimed queue item.
            /// </summary>
            public AiSharedQueueItem? ClaimedItem { get; set; }

            /// <summary>
            /// Gets the number of mark-dispatched calls.
            /// </summary>
            public int MarkDispatchedCalls { get; private set; }

            /// <inheritdoc />
            public Task<AiSharedQueueItem> EnqueueAsync(
                AiSharedQueueItem item,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(item);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> GetAsync(
                string sharedRunId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiSharedQueueItem>> ListAsync(
                bool includeTerminal = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiSharedQueueItem>>(
                    ClaimedItem is null
                        ? Array.Empty<AiSharedQueueItem>()
                        : new[] { ClaimedItem });
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> ClaimNextAsync(
                AiSharedQueueClaimRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> MarkDispatchedAsync(
                string sharedRunId,
                string claimToken,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                MarkDispatchedCalls++;

                if (ClaimedItem is not null &&
                    string.Equals(ClaimedItem.SharedRunId, sharedRunId, StringComparison.Ordinal) &&
                    string.Equals(ClaimedItem.ClaimToken, claimToken, StringComparison.Ordinal))
                {
                    ClaimedItem = new AiSharedQueueItem
                    {
                        SharedRunId = ClaimedItem.SharedRunId,
                        ControlPlaneId = ClaimedItem.ControlPlaneId,
                        Status = AiSharedQueueItemStatus.Dispatched,
                        ExecutionContextSnapshot = ClaimedItem.ExecutionContextSnapshot,
                        PipelineKey = ClaimedItem.PipelineKey,
                        Priority = ClaimedItem.Priority,
                        ClaimedByRuntimeInstanceId = ClaimedItem.ClaimedByRuntimeInstanceId,
                        ClaimedByWorkerId = ClaimedItem.ClaimedByWorkerId,
                        ClaimToken = ClaimedItem.ClaimToken,
                        EnqueuedAtUtc = ClaimedItem.EnqueuedAtUtc,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        ClaimedAtUtc = ClaimedItem.ClaimedAtUtc,
                        ClaimExpiresAtUtc = ClaimedItem.ClaimExpiresAtUtc,
                        Reason = reason,
                        Metadata = ClaimedItem.Metadata
                    };
                }

                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> RequeueAsync(
                string sharedRunId,
                string claimToken,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }

            public Task<AiSharedQueueItem?> ClaimAsync(
               string sharedRunId,
               AiSharedQueueClaimRequest request,
               CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(sharedRunId);
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<AiSharedQueueItem?>(
                    null);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> CancelAsync(
                string sharedRunId,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
                string sharedRunId,
                string claimToken,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }

            /// <inheritdoc />
            public Task<AiSharedQueueItem?> RequeueDispatchedAsync(
                string sharedRunId,
                string claimToken,
                string? reason,
                IReadOnlyDictionary<string, string>? metadata,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<AiSharedQueueItem?>(ClaimedItem);
            }
        }

        /// <summary>
        /// Fake shared run store.
        /// </summary>
        private sealed class FakeSharedRunStore : IAiSharedRunStore
        {
            /// <summary>
            /// Gets or sets the stored shared run record.
            /// </summary>
            public AiSharedRunRecord? Record { get; set; }

            /// <summary>
            /// Gets the number of mark-dispatched calls.
            /// </summary>
            public int MarkDispatchedCalls { get; private set; }

            /// <inheritdoc />
            public Task<AiSharedRunRecord> CreateAsync(
                AiSharedRunRecord record,
                CancellationToken cancellationToken = default)
            {
                Record = record;

                return Task.FromResult(record);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> GetAsync(
                string sharedRunId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Record);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
                bool includeCancelled = false,
                bool includeCompleted = false,
                bool includeFailed = false,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IReadOnlyList<AiSharedRunRecord>>(
                    Record is null
                        ? Array.Empty<AiSharedRunRecord>()
                        : new[] { Record });
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> CancelAsync(
                string sharedRunId,
                string? reason = null,
                string? requestedBy = null,
                string? source = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Record);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> MarkDispatchedAsync(
                string sharedRunId,
                string runtimeInstanceId,
                string? localRunId = null,
                string? executionId = null,
                string? reason = null,
                CancellationToken cancellationToken = default)
            {
                MarkDispatchedCalls++;

                if (Record is not null &&
                    string.Equals(Record.SharedRunId, sharedRunId, StringComparison.Ordinal))
                {
                    Record = new AiSharedRunRecord
                    {
                        SharedRunId = Record.SharedRunId,
                        Status = AiSharedRunStatus.Dispatched,
                        RunRequest = Record.RunRequest,
                        ExecutionContextSnapshot = Record.ExecutionContextSnapshot,
                        LocalRunId = localRunId ?? Record.LocalRunId,
                        ExecutionId = executionId ?? Record.ExecutionId,
                        AssignedRuntimeInstanceId = runtimeInstanceId,
                        AdmissionDecision = Record.AdmissionDecision,
                        PipelineKey = Record.PipelineKey,
                        CorrelationId = Record.CorrelationId,
                        RequestedBy = Record.RequestedBy,
                        Source = Record.Source,
                        Reason = reason,
                        FailureReason = Record.FailureReason,
                        SubmittedAtUtc = Record.SubmittedAtUtc,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = Record.Metadata,
                        ControlPlaneId = Record.ControlPlaneId
                    };
                }

                return Task.FromResult(Record);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> MarkDispatchFailedAsync(
                string sharedRunId,
                string runtimeInstanceId,
                string? failureReason,
                string? message,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Record);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutAsync(
                string sharedRunId,
                string? reason = null,
                IReadOnlyDictionary<string, string>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Record is not null &&
                    string.Equals(Record.SharedRunId, sharedRunId, StringComparison.Ordinal))
                {
                    var mergedMetadata =
                        new Dictionary<string, string>(
                            Record.Metadata,
                            StringComparer.OrdinalIgnoreCase);

                    if (metadata is not null)
                    {
                        foreach (var item in metadata)
                        {
                            mergedMetadata[item.Key] =
                                item.Value;
                        }
                    }

                    mergedMetadata["scaleOutRequeued"] =
                        "true";

                    mergedMetadata["scaleOutRequeuedAtUtc"] =
                        DateTimeOffset.UtcNow.ToString("O");

                    Record = new AiSharedRunRecord
                    {
                        SharedRunId = Record.SharedRunId,
                        Status = AiSharedRunStatus.QueuedGlobally,
                        RunRequest = Record.RunRequest,
                        ExecutionContextSnapshot = Record.ExecutionContextSnapshot,
                        LocalRunId = Record.LocalRunId,
                        ExecutionId = Record.ExecutionId,
                        AssignedRuntimeInstanceId = Record.AssignedRuntimeInstanceId,
                        AdmissionDecision = Record.AdmissionDecision,
                        PipelineKey = Record.PipelineKey,
                        CorrelationId = Record.CorrelationId,
                        RequestedBy = Record.RequestedBy,
                        Source = Record.Source,
                        Reason = string.IsNullOrWhiteSpace(reason)
                            ? "Scale-out fulfilled; shared run requeued for dispatch."
                            : reason,
                        FailureReason = string.Empty,
                        SubmittedAtUtc = Record.SubmittedAtUtc,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = mergedMetadata,
                        ControlPlaneId = Record.ControlPlaneId
                    };
                }

                return Task.FromResult(Record);
            }

            public Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutIfCurrentAsync(
                string sharedRunId,
                string? expectedAssignedRuntimeInstanceId,
                string? expectedLocalRunId,
                string? reason = null,
                IReadOnlyDictionary<string, string>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Record is null ||
                    !string.Equals(
                        Record.SharedRunId,
                        sharedRunId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        Record.AssignedRuntimeInstanceId,
                        expectedAssignedRuntimeInstanceId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        Record.LocalRunId,
                        expectedLocalRunId,
                        StringComparison.Ordinal))
                {
                    return Task.FromResult<AiSharedRunRecord?>(Record);
                }

                return this.MarkRequeuedAfterScaleOutAsync(
                    sharedRunId,
                    reason,
                    metadata,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Fake shared run dispatcher.
        /// </summary>
        private sealed class FakeSharedRunDispatcher : IAiSharedRunDispatcher
        {
            /// <summary>
            /// Gets or sets the dispatch result.
            /// </summary>
            public required AiSharedRunDispatchResult Result { get; set; }

            /// <summary>
            /// Gets the number of dispatch calls.
            /// </summary>
            public int DispatchCalls { get; private set; }

            /// <inheritdoc />
            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                DispatchCalls++;

                return Task.FromResult(Result);
            }
        }

        /// <summary>
        /// Fake admission controller.
        /// </summary>
        private sealed class FakeRunAdmissionController : IAiRunAdmissionController
        {
            /// <summary>
            /// Gets or sets the admission decision.
            /// </summary>
            public required AiRunAdmissionDecision Decision { get; set; }

            /// <inheritdoc />
            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Decision);
            }
        }

        /// <summary>
        /// Fake admission reservation store.
        /// </summary>
        private sealed class FakeRuntimeAdmissionReservationStore : IAiRuntimeAdmissionReservationStore
        {
            /// <summary>
            /// Gets the number of reserve calls.
            /// </summary>
            public int ReserveCalls { get; private set; }

            /// <summary>
            /// Gets the number of release calls.
            /// </summary>
            public int ReleaseCalls { get; private set; }

            /// <inheritdoc />
            public Task ReserveAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                ReserveCalls++;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task ReleaseAsync(
                string runtimeInstanceId,
                int runCount = 1,
                CancellationToken cancellationToken = default)
            {
                ReleaseCalls++;

                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task<int> GetReservedRunCountAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(0);
            }
        }

        /// <summary>
        /// Fake runtime scale-out publisher.
        /// </summary>
        private sealed class FakeRuntimeScaleOutRequestPublisher : IAiRuntimeScaleOutRequestPublisher
        {
            /// <inheritdoc />
            public Task<AiRuntimeScaleOutRequestResult> PublishAsync(
                AiRuntimeScaleOutRequest request,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(
                    new AiRuntimeScaleOutRequestResult
                    {
                        Success = true,
                        SharedRunId = request.SharedRunId,
                        ScaleOutRequestId = "scale-out-1",
                        RequestedTargetInstanceCount = 1,
                        Message = "accepted",
                        FailureReason = null,
                        PublishedAtUtc = DateTimeOffset.UtcNow,
                        Diagnostics = Array.Empty<string>()
                    });
            }
        }

        /// <summary>
        /// Fake tenant runtime settings provider.
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
                    TenantId = tenantId ?? "tenant-1",
                    TenantGroupId = tenantGroupId,
                    IsolationMode = AiRuntimeInstanceIsolationMode.Shared,
                    PreferDedicatedCapacity = false,
                    AllowSharedFallback = true,
                    MaxRuntimeInstances = 3,
                    WorkerCountPerInstance = 1,
                    MaxConcurrentRunsPerInstance = 3,
                    LocalQueueCapacity = 10,
                    RuntimeInstanceIdPrefix = "runtime",
                    Metadata = new Dictionary<string, string>()
                };
            }
        }

        /// <summary>
        /// Fake RBAC execution context accessor.
        /// </summary>
        private sealed class FakeExecutionContextAccessor : IExecutionContextAccessor
        {
            /// <inheritdoc />
            public RbacExecutionContext? Current { get; private set; }

            /// <inheritdoc />
            public void Set(
                RbacExecutionContext executionContext)
            {
                Current = executionContext;
            }

            /// <inheritdoc />
            public void Clear()
            {
                Current = null;
            }
        }

        /// <summary>
        /// Fake runtime recovery forensics recorder.
        /// </summary>
        private sealed class FakeRuntimeRecoveryForensicsRecorder : IAiRuntimeRecoveryForensicsRecorder
        {
            /// <summary>
            /// Gets recorded recovery forensics events.
            /// </summary>
            public List<AiRuntimeRecoveryForensicsEvent> Events { get; } = [];

            /// <inheritdoc />
            public Task RecordAsync(
                AiRuntimeRecoveryForensicsRecord record,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task RecordEventAsync(
                AiRuntimeRecoveryForensicsEvent recoveryEvent,
                CancellationToken cancellationToken = default)
            {
                Events.Add(recoveryEvent);

                return Task.CompletedTask;
            }
        }
    }
}