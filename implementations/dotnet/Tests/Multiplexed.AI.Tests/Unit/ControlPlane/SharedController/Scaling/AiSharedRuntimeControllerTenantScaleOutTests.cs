using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Admission;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Claiming;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedController;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    /// <summary>
    /// Tests tenant-aware scale-out propagation through the shared runtime controller.
    /// </summary>
    public sealed class AiSharedRuntimeControllerTenantScaleOutTests
    {
        /// <summary>
        /// Verifies that tenant runtime settings resolved by admission are propagated into
        /// the scale-out request published by the shared runtime controller.
        /// </summary>
        /// <returns>A task representing the asynchronous test.</returns>
        [Fact]
        public async Task SubmitRunAsync_Should_Publish_Tenant_Runtime_Settings_When_Admission_Requests_ScaleOut()
        {
            var tenantRuntimeSettings =
                new AiTenantRuntimeSettings
                {
                    TenantId = "tenant-a",
                    TenantGroupId = "tenant-group-a",
                    IsolationMode = AiRuntimeInstanceIsolationMode.Dedicated,
                    PreferDedicatedCapacity = true,
                    AllowSharedFallback = false,
                    MaxRuntimeInstances = 3,
                    RuntimeInstanceIdPrefix = "tenant-a-runtime",
                    WorkerCountPerInstance = 10,
                    MaxConcurrentRunsPerInstance = 5,
                    LocalQueueCapacity = 500
                };

            var admissionController =
                new CapturingAdmissionController(
                    new AiRunAdmissionDecision
                    {
                        DecisionType = AiRunAdmissionDecisionType.RequestScaleOut,
                        Reason = "No runtime instance can currently accept the run and scale-out is allowed.",
                        TenantId = tenantRuntimeSettings.TenantId,
                        TenantGroupId = tenantRuntimeSettings.TenantGroupId,
                        TenantRuntimeSettings = tenantRuntimeSettings,
                        VisibleInstanceCount = 0,
                        AvailableInstanceCount = 0,
                        CurrentInstanceCount = 0,
                        MaxInstanceCount = tenantRuntimeSettings.MaxRuntimeInstances
                    });

            var store =
                new InMemorySharedRunStore();

            var scaleOutPublisher =
                new CapturingScaleOutRequestPublisher();

            var controller =
                new AiSharedRuntimeController(
                    admissionController,
                    store,
                    new NoopSharedQueue(),
                    new NoopSharedRunDispatcher(),
                    scaleOutPublisher,
                    new StaticControlPlaneIdResolver("test-control-plane"),
                    new StaticTenantRuntimeSettingsProvider(tenantRuntimeSettings),
                    Options.Create(
                        new AiSharedRuntimeControllerOptions
                        {
                            EnableSubmitRun = true,
                            EnableGetRun = true,
                            EnableListRuns = true,
                            EnableCancelRun = true,
                            SubmitMode = AiSharedRuntimeSubmitMode.DirectDispatch,
                            ReturnFailureResultInsteadOfThrowing = false,
                            MeasureDuration = true
                        }),
                    new NoopControlPlaneObserver(),
                    new StaticExecutionContextSnapshotProvider(
                        new ExecutionContextSnapshot
                        {
                            TenantId = "tenant-a",
                            TenantGroupId = "tenant-group-a",
                            ContextKey = "ctx-tenant-a",
                            CurrentNamespace = "tenant-a",
                            Namespaces = new List<NamespaceEntry>(),
                            UserId = "test-user",
                            Project = "test-project"
                        }));

            var result =
                await controller
                    .SubmitRunAsync(
                        new AiSharedRuntimeControllerRequest
                        {
                            Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                            RequestedSharedRunId = "shared-run-tenant-a-1",
                            PipelineKey = "tenant-pipeline",
                            CorrelationId = "corr-tenant-a-1",
                            RequestedBy = "unit-test",
                            Source = "unit-test",
                            Reason = "tenant scale-out test",
                            RunRequest = new AiRuntimePipelineRunRequest
                            {
                                PipelineName = "tenant-pipeline",
                                Input = "hello"
                            }
                        })
                    .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("shared-run-tenant-a-1", result.SharedRunId);

            Assert.NotNull(scaleOutPublisher.LastRequest);

            var scaleOutRequest =
                scaleOutPublisher.LastRequest!;

            Assert.Equal("shared-run-tenant-a-1", scaleOutRequest.SharedRunId);
            Assert.Equal("test-control-plane", scaleOutRequest.SharedRun.ControlPlaneId);
            Assert.Equal("tenant-a", scaleOutRequest.TenantId);
            Assert.Equal("tenant-group-a", scaleOutRequest.TenantGroupId);
            Assert.Equal("tenant-pipeline", scaleOutRequest.PipelineKey);

            Assert.Equal(AiRuntimeInstanceIsolationMode.Dedicated, scaleOutRequest.IsolationMode);
            Assert.True(scaleOutRequest.PreferDedicatedCapacity);
            Assert.False(scaleOutRequest.AllowSharedFallback);
            Assert.Equal(3, scaleOutRequest.MaxRuntimeInstances);
            Assert.Equal("tenant-a-runtime", scaleOutRequest.RuntimeInstanceIdPrefix);
            Assert.Equal(10, scaleOutRequest.WorkerCountPerInstance);
            Assert.Equal(5, scaleOutRequest.MaxConcurrentRunsPerInstance);
            Assert.Equal(500, scaleOutRequest.LocalQueueCapacity);

            Assert.Equal(0, scaleOutRequest.VisibleInstanceCount);
            Assert.Equal(0, scaleOutRequest.AvailableInstanceCount);
            Assert.Equal(0, scaleOutRequest.CurrentInstanceCount);
            Assert.Equal(3, scaleOutRequest.MaxInstanceCount);

            Assert.NotNull(admissionController.LastRequest);
            Assert.Equal("tenant-a", admissionController.LastRequest!.TenantId);
            Assert.Equal("tenant-a", admissionController.LastRequest.RunRequest.ExecutionContextSnapshot?.TenantId);
            Assert.Equal("tenant-group-a", admissionController.LastRequest.RunRequest.ExecutionContextSnapshot?.TenantGroupId);
            Assert.Equal("tenant-a", admissionController.LastRequest.RunRequest.ExecutionContextSnapshot?.CurrentNamespace);

            var createdRun =
                Assert.Single(store.Records.Values);

            Assert.Equal(AiSharedRunStatus.ScaleOutRequested, createdRun.Status);
            Assert.NotNull(createdRun.AdmissionDecision);
            Assert.NotNull(createdRun.AdmissionDecision.TenantRuntimeSettings);
            Assert.Equal("tenant-a", createdRun.ExecutionContextSnapshot.TenantId);
            Assert.Equal("tenant-group-a", createdRun.ExecutionContextSnapshot.TenantGroupId);
            Assert.Equal("tenant-a", createdRun.ExecutionContextSnapshot.CurrentNamespace);
        }

        /// <summary>
        /// Admission controller fake that captures the admission request and returns a fixed decision.
        /// </summary>
        private sealed class CapturingAdmissionController : IAiRunAdmissionController
        {
            private readonly AiRunAdmissionDecision decision;

            /// <summary>
            /// Initializes a new instance of the <see cref="CapturingAdmissionController" /> class.
            /// </summary>
            /// <param name="decision">The admission decision to return.</param>
            public CapturingAdmissionController(
                AiRunAdmissionDecision decision)
            {
                this.decision =
                    decision
                    ?? throw new ArgumentNullException(nameof(decision));
            }

            /// <summary>
            /// Gets the last admission request observed by the fake controller.
            /// </summary>
            public AiRunAdmissionRequest? LastRequest { get; private set; }

            /// <inheritdoc />
            public Task<AiRunAdmissionDecision> AdmitAsync(
                AiRunAdmissionRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.LastRequest =
                    request
                    ?? throw new ArgumentNullException(nameof(request));

                return Task.FromResult(this.decision);
            }
        }

        /// <summary>
        /// In-memory shared run store used by the controller test.
        /// </summary>
        private sealed class InMemorySharedRunStore : IAiSharedRunStore
        {
            /// <summary>
            /// Gets stored shared run records keyed by shared run id.
            /// </summary>
            public Dictionary<string, AiSharedRunRecord> Records { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            /// <inheritdoc />
            public Task<AiSharedRunRecord> CreateAsync(
                AiSharedRunRecord record,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(record);

                cancellationToken.ThrowIfCancellationRequested();

                this.Records[record.SharedRunId] =
                    record;

                return Task.FromResult(record);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> GetAsync(
                string sharedRunId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.Records.TryGetValue(
                    sharedRunId,
                    out var record);

                return Task.FromResult(record);
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiSharedRunRecord>> ListAsync(
                bool includeCancelled = false,
                bool includeCompleted = false,
                bool includeFailed = false,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult<IReadOnlyList<AiSharedRunRecord>>(
                    this.Records.Values.ToArray());
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> MarkDispatchedAsync(
                string sharedRunId,
                string runtimeInstanceId,
                string? localRunId,
                string? executionId,
                string? message,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.Records.TryGetValue(
                    sharedRunId,
                    out var record);

                return Task.FromResult(record);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> MarkDispatchFailedAsync(
                string sharedRunId,
                string runtimeInstanceId,
                string? failureReason,
                string? message,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!this.Records.TryGetValue(
                        sharedRunId,
                        out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                var updated =
                    new AiSharedRunRecord
                    {
                        SharedRunId = existing.SharedRunId,
                        ControlPlaneId = existing.ControlPlaneId,
                        Status = existing.Status,
                        RunRequest = existing.RunRequest,
                        LocalRunId = existing.LocalRunId,
                        ExecutionId = existing.ExecutionId,
                        AssignedRuntimeInstanceId = runtimeInstanceId,
                        AdmissionDecision = existing.AdmissionDecision,
                        ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                        PipelineKey = existing.PipelineKey,
                        CorrelationId = existing.CorrelationId,
                        RequestedBy = existing.RequestedBy,
                        Source = existing.Source,
                        Reason = message ?? existing.Reason,
                        FailureReason = failureReason,
                        SubmittedAtUtc = existing.SubmittedAtUtc,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = existing.Metadata
                    };

                this.Records[sharedRunId] =
                    updated;

                return Task.FromResult<AiSharedRunRecord?>(updated);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> MarkRequeuedAfterScaleOutAsync(
                string sharedRunId,
                string? reason = null,
                IReadOnlyDictionary<string, string>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!this.Records.TryGetValue(
                        sharedRunId,
                        out var existing))
                {
                    return Task.FromResult<AiSharedRunRecord?>(null);
                }

                var mergedMetadata =
                    new Dictionary<string, string>(
                        existing.Metadata,
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

                var updated =
                    new AiSharedRunRecord
                    {
                        SharedRunId = existing.SharedRunId,
                        ControlPlaneId = existing.ControlPlaneId,
                        Status = AiSharedRunStatus.QueuedGlobally,
                        RunRequest = existing.RunRequest,
                        LocalRunId = existing.LocalRunId,
                        ExecutionId = existing.ExecutionId,
                        AssignedRuntimeInstanceId = existing.AssignedRuntimeInstanceId,
                        AdmissionDecision = existing.AdmissionDecision,
                        ExecutionContextSnapshot = existing.ExecutionContextSnapshot,
                        PipelineKey = existing.PipelineKey,
                        CorrelationId = existing.CorrelationId,
                        RequestedBy = existing.RequestedBy,
                        Source = existing.Source,
                        Reason = string.IsNullOrWhiteSpace(reason)
                            ? "Scale-out fulfilled; shared run requeued for dispatch."
                            : reason,
                        FailureReason = string.Empty,
                        SubmittedAtUtc = existing.SubmittedAtUtc,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        Metadata = mergedMetadata
                    };

                this.Records[sharedRunId] =
                    updated;

                return Task.FromResult<AiSharedRunRecord?>(updated);
            }

            /// <inheritdoc />
            public Task<AiSharedRunRecord?> CancelAsync(
                string sharedRunId,
                string? reason,
                string? requestedBy,
                string? source,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.Records.TryGetValue(
                    sharedRunId,
                    out var record);

                return Task.FromResult(record);
            }
        }

        /// <summary>
        /// Shared run dispatcher fake. It should not be invoked by this scale-out test.
        /// </summary>
        private sealed class NoopSharedRunDispatcher : IAiSharedRunDispatcher
        {
            /// <inheritdoc />
            public Task<AiSharedRunDispatchResult> DispatchAsync(
                AiSharedRunDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    new AiSharedRunDispatchResult
                    {
                        Success = false,
                        SharedRunId = request.SharedRun.SharedRunId,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        FailureReason = "noop-dispatcher"
                    });
            }
        }

        /// <summary>
        /// Scale-out publisher fake that captures the published scale-out request.
        /// </summary>
        private sealed class CapturingScaleOutRequestPublisher : IAiRuntimeScaleOutRequestPublisher
        {
            /// <summary>
            /// Gets the last scale-out request published by the controller.
            /// </summary>
            public AiRuntimeScaleOutRequest? LastRequest { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeScaleOutRequestResult> PublishAsync(
                AiRuntimeScaleOutRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                this.LastRequest =
                    request
                    ?? throw new ArgumentNullException(nameof(request));

                return Task.FromResult(
                    new AiRuntimeScaleOutRequestResult
                    {
                        Success = true,
                        SharedRunId = request.SharedRunId,
                        ScaleOutRequestId = $"scale-out-{request.SharedRunId}",
                        RequestedTargetInstanceCount = request.CurrentInstanceCount + 1,
                        Message = "captured",
                        PublishedAtUtc = DateTimeOffset.UtcNow
                    });
            }
        }

        /// <summary>
        /// Static tenant runtime settings provider used by the controller test.
        /// </summary>
        private sealed class StaticTenantRuntimeSettingsProvider : IAiTenantRuntimeSettingsProvider
        {
            private readonly AiTenantRuntimeSettings settings;

            /// <summary>
            /// Initializes a new instance of the <see cref="StaticTenantRuntimeSettingsProvider" /> class.
            /// </summary>
            /// <param name="settings">The tenant runtime settings to return.</param>
            public StaticTenantRuntimeSettingsProvider(
                AiTenantRuntimeSettings settings)
            {
                this.settings =
                    settings
                    ?? throw new ArgumentNullException(nameof(settings));
            }

            /// <inheritdoc />
            public AiTenantRuntimeSettings GetSettings(
                string? tenantId,
                string? tenantGroupId)
            {
                return this.settings;
            }
        }

        /// <summary>
        /// No-op observer used by the controller test.
        /// </summary>
        private sealed class NoopControlPlaneObserver : IAiControlPlaneObserver
        {
            /// <inheritdoc />
            public Task RecordAsync(
                AiControlPlaneEvent controlPlaneEvent,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Static execution context snapshot provider used by the controller test.
        /// </summary>
        private sealed class StaticExecutionContextSnapshotProvider : IExecutionContextSnapshotProvider
        {
            private readonly ExecutionContextSnapshot snapshot;

            /// <summary>
            /// Initializes a new instance of the <see cref="StaticExecutionContextSnapshotProvider" /> class.
            /// </summary>
            /// <param name="snapshot">The execution context snapshot to return.</param>
            public StaticExecutionContextSnapshotProvider(
                ExecutionContextSnapshot snapshot)
            {
                this.snapshot =
                    snapshot
                    ?? throw new ArgumentNullException(nameof(snapshot));
            }

            /// <inheritdoc />
            public ExecutionContextSnapshot MapToSnapshot()
            {
                return this.snapshot;
            }
        }
    }
}
