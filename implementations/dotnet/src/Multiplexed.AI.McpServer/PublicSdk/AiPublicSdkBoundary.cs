using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Replay;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Execution.Control;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Contracts.Replay;
using Multiplexed.AI.Sdk.Contracts.Watch;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>Maps the dependency-free public contract to existing publication, queue, state and control authorities.</summary>
    public sealed class AiPublicSdkBoundary : IAiPublicSdkBoundary
    {
        private readonly AiPipelinePublicationService _publications;
        private readonly AiPublishedDagRunService _runs;
        private readonly IAiSharedRuntimeController _controller;
        private readonly IAiDagExecutionStore _dagExecutions;
        private readonly AiDagExecutionCancellationCoordinator _cancellation;
        private readonly IExecutionContextSnapshotProvider _context;
        private readonly IAiControlPlaneIdResolver _controlPlane;
        private readonly AiPublicSdkExecutionWatchProjector _watch;
        private readonly IAiExecutionControlPlane _executionControl;
        private readonly IAiReplayControlPlane _replayControl;

        public AiPublicSdkBoundary(
            AiPipelinePublicationService publications,
            AiPublishedDagRunService runs,
            IAiSharedRuntimeController controller,
            IAiDagExecutionStore dagExecutions,
            AiDagExecutionCancellationCoordinator cancellation,
            IExecutionContextSnapshotProvider context,
            IAiControlPlaneIdResolver controlPlane,
            IAiDecisionLedger decisionLedger,
            IOptions<AiPublicSdkExecutionWatchOptions> watchOptions,
            IAiExecutionControlPlane executionControl,
            IAiReplayControlPlane replayControl)
        {
            _publications = publications;
            _runs = runs;
            _controller = controller;
            _dagExecutions = dagExecutions;
            _cancellation = cancellation;
            _context = context;
            _controlPlane = controlPlane;
            ArgumentNullException.ThrowIfNull(watchOptions);
            _watch = new AiPublicSdkExecutionWatchProjector(decisionLedger, watchOptions.Value);
            _executionControl = executionControl ?? throw new ArgumentNullException(nameof(executionControl));
            _replayControl = replayControl ?? throw new ArgumentNullException(nameof(replayControl));
        }

        public async Task<AiSdkPipelinePublicationResponse> PublishAsync(
            AiSdkPipelinePublicationRequest request,
            CancellationToken cancellationToken = default)
        {
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
            var publication = await _publications.PublishAsync(
                scope,
                AiPublicSdkContractMapper.ToInternal(request),
                cancellationToken).ConfigureAwait(false);
            return AiPublicSdkContractMapper.ToPublic(publication);
        }

        public async Task<AiSdkExecutionSubmissionResponse> SubmitAsync(
            AiSdkExecutionSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.SchemaVersion != 1) throw new NotSupportedException($"Execution submission schema version '{request.SchemaVersion}' is not supported.");
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicationRef);
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
            var runKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : request.IdempotencyKey;
            var inputJson = request.Input?.GetRawText() ?? "{}";
            var admission = await _runs.CreateWithDefinitionAsync(
                scope, runKey!, request.PublicationRef, inputJson, cancellationToken).ConfigureAwait(false);
            var record = admission.Record;
            var result = await _controller.SubmitRunAsync(new AiSharedRuntimeControllerRequest
            {
                Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                RequestedSharedRunId = "sdk-" + record.ExecutionId,
                TenantId = scope.TenantId,
                PipelineKey = record.PipelineName,
                CorrelationId = request.CorrelationId,
                Source = "public-sdk",
                Metadata = request.Metadata,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = record.PipelineName ?? throw new InvalidOperationException("Published execution has no pipeline name."),
                    RequestedExecutionId = record.ExecutionId,
                    PipelineDefinitionSnapshot = record.PipelineDefinitionSnapshot
                        ?? throw new InvalidOperationException("Published execution has no immutable definition snapshot."),
                    PipelineDefinition = admission.Definition,
                    Input = inputJson,
                    Metadata = request.Metadata
                }
            }, cancellationToken).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException(result.FailureReason ?? result.Message ?? "Run submission failed.");
            return new AiSdkExecutionSubmissionResponse
            {
                ExecutionId = record.ExecutionId,
                PublicationRef = request.PublicationRef,
                Status = AiPublicSdkContractMapper.ToPublic(record.Status),
                AcceptedAtUtc = result.CompletedAtUtc,
                IdempotencyKey = request.IdempotencyKey,
                CorrelationId = result.CorrelationId ?? request.CorrelationId
            };
        }

        public async Task<AiSdkExecutionObservation> ObserveAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
            var pin = await _runs.ReadPinAsync(scope, executionId, cancellationToken).ConfigureAwait(false);
            return await ReadObservationAsync(scope, pin, executionId, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<AiSdkExecutionWatchEvent> WatchAsync(
            AiSdkExecutionWatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.SchemaVersion != AiSdkSchemaVersions.ExecutionWatchRequest)
            {
                throw new NotSupportedException(
                    $"Execution Watch schema version '{request.SchemaVersion}' is not supported.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(request.ExecutionId);
            ArgumentNullException.ThrowIfNull(request.Channels);
            foreach (var channel in request.Channels)
            {
                if (!Enum.IsDefined(typeof(AiSdkExecutionWatchChannel), channel))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        channel,
                        "Execution Watch contains an unknown public channel.");
                }
            }

            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);

            // Authorization and immutable publication ownership are resolved before any Watch history is read.
            // This prevents even transient cross-tenant/project/namespace event disclosure.
            var pin = await _runs
                .ReadPinAsync(scope, request.ExecutionId, cancellationToken)
                .ConfigureAwait(false);

            var record = await _dagExecutions
                .GetRecordAsync(request.ExecutionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{request.ExecutionId}' was not found.");
            RequireOwner(record, scope, pin);

            if (request.IncludeInitialSnapshot && request.AfterSequence is null)
            {
                // Establish the public boundary on the server rather than composing observe()+watch() in the
                // client. A concurrent projected event forces a retry so the returned snapshot is paired with
                // a stable public high-water mark. This is the first race-closing boundary foundation; deeper
                // cross-store adversarial validation remains a later Watch increment.
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var before = await _watch
                        .GetLatestSequenceAsync(request.ExecutionId, cancellationToken)
                        .ConfigureAwait(false);

                    var snapshot = await ReadObservationAsync(
                            scope,
                            pin,
                            request.ExecutionId,
                            cancellationToken)
                        .ConfigureAwait(false);

                    var after = await _watch
                        .GetLatestSequenceAsync(request.ExecutionId, cancellationToken)
                        .ConfigureAwait(false);

                    if (before != after)
                    {
                        await Task.Yield();
                        continue;
                    }

                    return new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = request.ExecutionId,
                        Sequence = after,
                        Kind = AiSdkExecutionWatchEventKind.Snapshot,
                        OccurredAtUtc = snapshot.UpdatedAtUtc,
                        Snapshot = snapshot
                    };
                }
            }

            return await _watch
                .WaitForNextAsync(
                    request.ExecutionId,
                    request.Channels,
                    request.AfterSequence ?? 0,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<AiSdkExecutionObservation> ReadObservationAsync(
            AiDurableInvocationScope scope,
            AiPublicationRunPin pin,
            string executionId,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pin);
            var definition = await _publications.ReadDefinitionAsync(scope, pin.PublicationRef, cancellationToken).ConfigureAwait(false);
            var record = await _dagExecutions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            var state = await _dagExecutions.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false);
            RequireOwner(record, scope, pin);
            var stepKeys = definition.Steps.ToDictionary(step => step.Name, step => step.StepKey, StringComparer.Ordinal);
            return new AiSdkExecutionObservation
            {
                ExecutionId = record.ExecutionId,
                PublicationRef = pin.PublicationRef,
                PipelineName = definition.Name,
                PipelineVersion = definition.Version ?? string.Empty,
                Status = AiPublicSdkContractMapper.ToPublic(record.Status),
                CreatedAtUtc = new DateTimeOffset(record.CreatedAtUtc, TimeSpan.Zero),
                UpdatedAtUtc = new DateTimeOffset(record.UpdatedAtUtc, TimeSpan.Zero),
                CompletedAtUtc = record.IsTerminal && record.CompletedAtUtc != default
                    ? new DateTimeOffset(record.CompletedAtUtc, TimeSpan.Zero)
                    : null,
                Steps = state?.Steps.Values
                    .OrderBy(step => definition.Steps.FirstOrDefault(item => item.Name == step.StepName)?.Order ?? int.MaxValue)
                    .ThenBy(step => step.StepName, StringComparer.Ordinal)
                    .Select(step => new AiSdkExecutionStepObservation
                    {
                        Name = step.StepName,
                        StepKey = stepKeys.GetValueOrDefault(step.StepName) ?? string.Empty,
                        Status = AiPublicSdkContractMapper.ToPublic(step.Status),
                        StartedAtUtc = ToOffset(step.StartedAtUtc),
                        UpdatedAtUtc = ToOffset(step.UpdatedAtUtc),
                        CompletedAtUtc = ToOffset(step.CompletedAtUtc)
                    }).ToArray() ?? Array.Empty<AiSdkExecutionStepObservation>()
            };
        }

        public async Task<AiSdkExecutionResult> GetResultAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
            var pin = await _runs.ReadPinAsync(scope, executionId, cancellationToken).ConfigureAwait(false);
            var record = await _dagExecutions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            var state = await _dagExecutions.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Execution state was not found.");
            RequireOwner(record, scope, pin);
            if (!record.IsTerminal)
                throw new InvalidOperationException("Execution result is not available before the execution becomes terminal.");
            return new AiSdkExecutionResult
            {
                ExecutionId = executionId,
                Status = AiPublicSdkContractMapper.ToPublic(record.Status),
                Output = record.Status == AiExecutionStatus.Completed
                    ? JsonSerializer.SerializeToElement(state.Data)
                    : null,
                Failure = record.Status == AiExecutionStatus.Failed
                    ? new AiSdkExecutionFailure { Code = "execution_failed", Message = "Execution failed." }
                    : null,
                CompletedAtUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(record.CompletedAtUtc != default ? record.CompletedAtUtc : record.UpdatedAtUtc, DateTimeKind.Utc))
            };
        }

        public async Task<AiSdkExecutionCancellationResponse> CancelAsync(
            string executionId,
            AiSdkExecutionCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(request);
            if (request.SchemaVersion != 1) throw new NotSupportedException($"Execution cancellation schema version '{request.SchemaVersion}' is not supported.");
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
            var pin = await _runs.ReadPinAsync(scope, executionId, cancellationToken).ConfigureAwait(false);
            var record = await _dagExecutions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            RequireOwner(record, scope, pin);
            var snapshot = _context.MapToSnapshot();
            var control = await _cancellation.CancelAsync(
                executionId, request.Reason, snapshot.UserId, cancellationToken).ConfigureAwait(false);
            var cancelledRecord = await _dagExecutions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found after cancellation.");
            return new AiSdkExecutionCancellationResponse
            {
                ExecutionId = executionId,
                CancellationRequested = true,
                Status = AiPublicSdkContractMapper.ToPublic(cancelledRecord.Status),
                RequestedAtUtc = ToOffset(control.CancellationRequestedAtUtc),
                CorrelationId = request.CorrelationId
            };
        }

        public Task<AiSdkExecutionControlResponse> PauseAsync(
            string executionId,
            AiSdkExecutionControlRequest request,
            CancellationToken cancellationToken = default) =>
            ExecuteControlAsync(
                executionId,
                request,
                AiExecutionControlPlaneOperation.Pause,
                AiSdkExecutionControlOperation.Pause,
                cancellationToken);

        public Task<AiSdkExecutionControlResponse> ResumeAsync(
            string executionId,
            AiSdkExecutionControlRequest request,
            CancellationToken cancellationToken = default) =>
            ExecuteControlAsync(
                executionId,
                request,
                AiExecutionControlPlaneOperation.Resume,
                AiSdkExecutionControlOperation.Resume,
                cancellationToken);

        public async Task<AiSdkExecutionControlResponse> SubmitInputAsync(
            string executionId,
            AiSdkExecutionInputSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            ArgumentNullException.ThrowIfNull(request);
            if (request.SchemaVersion != AiSdkSchemaVersions.ExecutionInputSubmissionRequest)
            {
                throw new NotSupportedException(
                    $"Execution input submission schema version '{request.SchemaVersion}' is not supported.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(request.WaitingKey);
            if (request.Input.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Execution input must be a JSON object.", nameof(request));
            }

            var (scope, pin, _) = await RequireExecutionAccessAsync(executionId, cancellationToken).ConfigureAwait(false);
            var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(request.Input.GetRawText())
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            var result = await _executionControl.SubmitHumanInputAsync(
                new AiExecutionControlPlaneRequest
                {
                    ExecutionId = executionId,
                    Operation = AiExecutionControlPlaneOperation.SubmitHumanInput,
                    CorrelationId = request.CorrelationId,
                    RequestedBy = pin.UserId,
                    Source = "public-sdk",
                    Reason = request.Reason,
                    WaitingKey = request.WaitingKey,
                    WaitingStepName = request.WaitingStepName,
                    Input = input,
                    IncludeState = true,
                    IncludeDiagnostics = false
                },
                cancellationToken).ConfigureAwait(false);

            await WakeExecutionIfDurablyWaitingAsync(
                    scope,
                    pin,
                    result,
                    AiExecutionControlAction.SubmitInput,
                    request.Reason,
                    cancellationToken)
                .ConfigureAwait(false);

            return MapControlResult(result, AiSdkExecutionControlOperation.SubmitInput);
        }

        public async Task<AiSdkExecutionReplayResponse> ReplayAsync(
            string executionId,
            AiSdkExecutionReplayRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            ArgumentNullException.ThrowIfNull(request);
            if (request.SchemaVersion != AiSdkSchemaVersions.ExecutionReplayRequest)
            {
                throw new NotSupportedException(
                    $"Execution replay schema version '{request.SchemaVersion}' is not supported.");
            }

            var (_, pin, _) = await RequireExecutionAccessAsync(executionId, cancellationToken).ConfigureAwait(false);
            var result = await _replayControl.ReplayAsync(
                new AiReplayControlRequest
                {
                    ExecutionId = executionId,
                    Operation = AiReplayOperation.Replay,
                    CorrelationId = request.CorrelationId,
                    RequestedBy = pin.UserId,
                    Source = "public-sdk",
                    Reason = request.Reason,
                    IncludeDiagnostics = request.IncludeDiagnostics,
                    IncludeReport = false,
                    IncludeLedger = false,
                    IncludeTimeline = false,
                    StrictDeterminism = request.StrictDeterminism
                },
                cancellationToken).ConfigureAwait(false);

            return new AiSdkExecutionReplayResponse
            {
                ExecutionId = executionId,
                Succeeded = result.Success,
                Deterministic = result.Deterministic,
                Message = result.Message,
                Diagnostics = request.IncludeDiagnostics ? result.Diagnostics.ToArray() : Array.Empty<string>(),
                FailureReason = result.FailureReason,
                CorrelationId = result.CorrelationId ?? request.CorrelationId,
                StartedAtUtc = result.StartedAtUtc,
                CompletedAtUtc = result.CompletedAtUtc,
                DurationMs = result.DurationMs
            };
        }

        private async Task<AiSdkExecutionControlResponse> ExecuteControlAsync(
            string executionId,
            AiSdkExecutionControlRequest request,
            AiExecutionControlPlaneOperation internalOperation,
            AiSdkExecutionControlOperation publicOperation,
            CancellationToken cancellationToken)
        {
            ValidateExecutionId(executionId);
            ArgumentNullException.ThrowIfNull(request);
            if (request.SchemaVersion != AiSdkSchemaVersions.ExecutionControlRequest)
            {
                throw new NotSupportedException(
                    $"Execution control schema version '{request.SchemaVersion}' is not supported.");
            }

            var (scope, pin, _) = await RequireExecutionAccessAsync(executionId, cancellationToken).ConfigureAwait(false);
            var internalRequest = new AiExecutionControlPlaneRequest
            {
                ExecutionId = executionId,
                Operation = internalOperation,
                CorrelationId = request.CorrelationId,
                RequestedBy = pin.UserId,
                Source = "public-sdk",
                Reason = request.Reason,
                IncludeState = true,
                IncludeDiagnostics = false
            };

            var result = internalOperation switch
            {
                AiExecutionControlPlaneOperation.Pause =>
                    await _executionControl.PauseAsync(internalRequest, cancellationToken).ConfigureAwait(false),
                AiExecutionControlPlaneOperation.Resume =>
                    await _executionControl.ResumeAsync(internalRequest, cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException($"Execution control operation '{internalOperation}' is not supported by this public mapping.")
            };

            if (internalOperation == AiExecutionControlPlaneOperation.Resume)
            {
                await WakeExecutionIfDurablyWaitingAsync(
                        scope,
                        pin,
                        result,
                        AiExecutionControlAction.Resume,
                        request.Reason,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return MapControlResult(result, publicOperation);
        }

        private async Task WakeExecutionIfDurablyWaitingAsync(
            AiDurableInvocationScope scope,
            AiPublicationRunPin pin,
            AiExecutionControlPlaneResult controlResult,
            AiExecutionControlAction expectedAction,
            string? reason,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pin);
            ArgumentNullException.ThrowIfNull(controlResult);

            var controlState = controlResult.State;
            if (!controlResult.Success ||
                controlState is null ||
                controlState.Status != AiExecutionControlStatus.Resuming)
            {
                return;
            }

            if (controlState.PendingAction != expectedAction)
            {
                throw new InvalidOperationException(
                    $"Execution '{pin.ExecutionId}' entered Resuming with pending action '{controlState.PendingAction}', expected '{expectedAction}'.");
            }

            var record = await _dagExecutions
                .GetRecordAsync(pin.ExecutionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{pin.ExecutionId}' was not found while scheduling its control wake.");

            RequireOwner(record, scope, pin);

            if (record.IsTerminal)
            {
                return;
            }

            if (expectedAction == AiExecutionControlAction.SubmitInput &&
                await TryScheduleInputExternalWaitContinuationAsync(
                        scope,
                        pin,
                        record,
                        controlState,
                        controlResult.CorrelationId,
                        reason,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return;
            }

            if (record.Status != AiExecutionStatus.Waiting)
            {
                return;
            }

            var sourceSharedRunId = "sdk-" + pin.ExecutionId;
            var sourceResult = await _controller
                .GetRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.GetRun,
                        SharedRunId = sourceSharedRunId,
                        TenantId = scope.TenantId,
                        PipelineKey = record.PipelineName,
                        RequestedBy = pin.UserId,
                        Source = "public-sdk-control-wake",
                        IncludeDiagnostics = false
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var sourceRun = sourceResult.Run;
            if (!sourceResult.Success || sourceRun is null)
            {
                throw new InvalidOperationException(
                    $"The original shared run '{sourceSharedRunId}' could not be resolved for execution '{pin.ExecutionId}' control wake.");
            }

            if (!string.Equals(
                    sourceRun.RunRequest.RequestedExecutionId,
                    pin.ExecutionId,
                    StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(sourceRun.ExecutionId) &&
                 !string.Equals(sourceRun.ExecutionId, pin.ExecutionId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"The original shared run '{sourceSharedRunId}' is not bound to execution '{pin.ExecutionId}'.");
            }

            var sourceContext = sourceRun.ExecutionContextSnapshot;
            if (!string.Equals(sourceContext.TenantId, scope.TenantId, StringComparison.Ordinal) ||
                !string.Equals(sourceContext.TenantGroupId, scope.TenantGroupId, StringComparison.Ordinal) ||
                !string.Equals(sourceContext.Project, pin.Partition.Project, StringComparison.Ordinal) ||
                !string.Equals(sourceContext.CurrentNamespace, pin.Partition.Namespace, StringComparison.Ordinal) ||
                !string.Equals(sourceContext.UserId, pin.UserId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"The original shared run '{sourceSharedRunId}' ownership does not match execution '{pin.ExecutionId}'.");
            }

            if (!string.Equals(sourceRun.RunRequest.PipelineName, record.PipelineName, StringComparison.Ordinal) ||
                !string.Equals(sourceRun.PipelineKey ?? sourceRun.RunRequest.PipelineName, record.PipelineName, StringComparison.Ordinal) ||
                sourceRun.RunRequest.PipelineDefinitionSnapshot is null ||
                record.PipelineDefinitionSnapshot is null ||
                !string.Equals(
                    sourceRun.RunRequest.PipelineDefinitionSnapshot.ContentHash,
                    record.PipelineDefinitionSnapshot.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The original shared run '{sourceSharedRunId}' immutable pipeline binding does not match execution '{pin.ExecutionId}'.");
            }

            var wakeMetadata = new Dictionary<string, string>(
                sourceRun.Metadata,
                StringComparer.OrdinalIgnoreCase)
            {
                ["control.wake"] = "true",
                ["control.wake.executionId"] = pin.ExecutionId,
                ["control.wake.action"] = controlState.PendingAction.ToString(),
                ["control.wake.version"] = controlState.Version.ToString(CultureInfo.InvariantCulture)
            };

            var wakeSharedRunId = string.Join(
                "-",
                "sdk-control-wake",
                pin.ExecutionId,
                controlState.PendingAction.ToString().ToLowerInvariant(),
                controlState.Version.ToString(CultureInfo.InvariantCulture));

            var wakeResult = await _controller
                .SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = wakeSharedRunId,
                        TenantId = scope.TenantId,
                        PipelineKey = sourceRun.PipelineKey ?? sourceRun.RunRequest.PipelineName,
                        PreferredRuntimeInstanceId = sourceRun.AssignedRuntimeInstanceId,
                        Placement = sourceRun.Placement,
                        CorrelationId = controlResult.CorrelationId,
                        RequestedBy = pin.UserId,
                        Source = "public-sdk-control-wake",
                        Reason = reason ?? "Execution control transition requires runtime wake.",
                        Metadata = wakeMetadata,
                        RunRequest = sourceRun.RunRequest
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!wakeResult.Success)
            {
                throw new InvalidOperationException(
                    wakeResult.FailureReason ??
                    wakeResult.Message ??
                    $"Execution '{pin.ExecutionId}' control wake was not accepted.");
            }

            if (!string.IsNullOrWhiteSpace(wakeResult.ExecutionId) &&
                !string.Equals(wakeResult.ExecutionId, pin.ExecutionId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Execution control wake returned execution '{wakeResult.ExecutionId}', expected '{pin.ExecutionId}'.");
            }
        }

        private async Task<bool> TryScheduleInputExternalWaitContinuationAsync(
            AiDurableInvocationScope scope,
            AiPublicationRunPin pin,
            AiExecutionRecord record,
            AiExecutionControlState controlState,
            string? correlationId,
            string? reason,
            CancellationToken cancellationToken)
        {
            if (controlState.InputWaitMode != AiExecutionInputWaitMode.ExternalWaitStep ||
                string.IsNullOrWhiteSpace(controlState.WaitingKey) ||
                string.IsNullOrWhiteSpace(controlState.WaitingStepName) ||
                !controlState.InputReceivedAtUtc.HasValue)
            {
                return false;
            }

            var executionState = await _dagExecutions
                .GetStateAsync(pin.ExecutionId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new KeyNotFoundException(
                    $"Execution state '{pin.ExecutionId}' was not found while scheduling its input continuation.");

            if (!executionState.Steps.TryGetValue(controlState.WaitingStepName, out var waitingStep))
            {
                throw new InvalidOperationException(
                    $"Execution '{pin.ExecutionId}' does not contain waiting input step '{controlState.WaitingStepName}'.");
            }

            if (waitingStep.Status is not (AiStepExecutionStatus.Running or AiStepExecutionStatus.WaitingForExternal))
            {
                return false;
            }

            if (record.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Execution '{pin.ExecutionId}' is not a DAG execution and cannot continue waiting step '{controlState.WaitingStepName}'.");
            }

            if (string.IsNullOrWhiteSpace(record.PipelineName))
            {
                throw new InvalidOperationException(
                    $"Execution '{pin.ExecutionId}' has no pipeline name required for input continuation dispatch.");
            }

            var snapshot = record.ExecutionContextSnapshot
                ?? throw new InvalidOperationException(
                    $"Execution '{pin.ExecutionId}' has no durable execution context snapshot required for input continuation dispatch.");

            if (!string.Equals(snapshot.TenantId, scope.TenantId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.TenantGroupId, scope.TenantGroupId, StringComparison.Ordinal) ||
                !string.Equals(snapshot.Project, pin.Partition.Project, StringComparison.Ordinal) ||
                !string.Equals(snapshot.CurrentNamespace, pin.Partition.Namespace, StringComparison.Ordinal) ||
                !string.Equals(snapshot.UserId, pin.UserId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Execution '{pin.ExecutionId}' input-continuation ownership does not match the public SDK scope.");
            }

            var continuationHash = CreateInputContinuationHash(
                pin.ExecutionId,
                controlState.WaitingStepName,
                controlState.WaitingKey);
            var continuationId = "sdk-input-continuation:" + continuationHash;
            var sharedRunId = "sdk-input-continuation-" + continuationHash;
            var continuation = new AiRuntimeExternalWaitContinuation
            {
                ExecutionId = pin.ExecutionId,
                StepName = controlState.WaitingStepName,
                ContinuationId = continuationId
            };

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["control.wake"] = "true",
                ["control.wake.executionId"] = pin.ExecutionId,
                ["control.wake.action"] = AiExecutionControlAction.SubmitInput.ToString(),
                ["control.wake.version"] = controlState.Version.ToString(CultureInfo.InvariantCulture),
                [AiRuntimeExternalWaitMetadataKeys.Continuation] = "true",
                [AiRuntimeExternalWaitMetadataKeys.ContinuationId] = continuationId,
                [AiRuntimeExternalWaitMetadataKeys.ExecutionId] = pin.ExecutionId,
                [AiRuntimeExternalWaitMetadataKeys.Step] = controlState.WaitingStepName,
                ["input.waitingKey"] = controlState.WaitingKey
            };

            var continuationResult = await _controller
                .SubmitRunAsync(
                    new AiSharedRuntimeControllerRequest
                    {
                        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
                        RequestedSharedRunId = sharedRunId,
                        SubmitModeOverride = AiSharedRuntimeSubmitMode.QueueFirst,
                        TenantId = scope.TenantId,
                        PipelineKey = record.PipelineName,
                        CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? continuationId : correlationId,
                        RequestedBy = pin.UserId,
                        Source = "public-sdk-input-continuation",
                        Reason = reason ?? "Submitted input resumes the exact parked execution step.",
                        Metadata = metadata,
                        RunRequest = new AiRuntimePipelineRunRequest
                        {
                            PipelineName = record.PipelineName,
                            ExternalWaitContinuation = continuation,
                            ExecutionContextSnapshot = snapshot,
                            Metadata = metadata
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!continuationResult.Success || continuationResult.Run is null)
            {
                throw new InvalidOperationException(
                    continuationResult.FailureReason ??
                    continuationResult.Message ??
                    $"Execution '{pin.ExecutionId}' input continuation was not accepted.");
            }

            var acceptedContinuation = continuationResult.Run.RunRequest.ExternalWaitContinuation;
            if (!string.Equals(continuationResult.SharedRunId, sharedRunId, StringComparison.Ordinal) ||
                acceptedContinuation is null ||
                !string.Equals(acceptedContinuation.ExecutionId, continuation.ExecutionId, StringComparison.Ordinal) ||
                !string.Equals(acceptedContinuation.StepName, continuation.StepName, StringComparison.Ordinal) ||
                !string.Equals(acceptedContinuation.ContinuationId, continuation.ContinuationId, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(continuationResult.Run.RunRequest.RequestedExecutionId))
            {
                throw new InvalidOperationException(
                    $"Execution '{pin.ExecutionId}' input continuation did not preserve the exact execution and waiting-step identity.");
            }

            return true;
        }

        private static string CreateInputContinuationHash(
            string executionId,
            string stepName,
            string waitingKey)
        {
            var identity = string.Concat(executionId, "\n", stepName, "\n", waitingKey);
            return Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant();
        }

        private async Task<(AiDurableInvocationScope Scope, AiPublicationRunPin Pin, AiExecutionRecord Record)> RequireExecutionAccessAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            var scope = await ResolveScopeAsync(cancellationToken).ConfigureAwait(false);
            var pin = await _runs.ReadPinAsync(scope, executionId, cancellationToken).ConfigureAwait(false);
            var record = await _dagExecutions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            RequireOwner(record, scope, pin);
            return (scope, pin, record);
        }

        private static AiSdkExecutionControlResponse MapControlResult(
            AiExecutionControlPlaneResult result,
            AiSdkExecutionControlOperation operation)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (!result.Success)
            {
                throw new InvalidOperationException(result.FailureReason ?? result.Message ?? "Execution control operation failed.");
            }

            return new AiSdkExecutionControlResponse
            {
                ExecutionId = result.ExecutionId,
                Operation = operation,
                Accepted = true,
                State = result.State is null ? null : new AiSdkExecutionControlState
                {
                    Status = MapControlStatus(result.State.Status),
                    PendingAction = MapControlAction(result.State.PendingAction),
                    Reason = result.State.Reason,
                    WaitingKey = result.State.WaitingKey,
                    WaitingStepName = result.State.WaitingStepName,
                    UpdatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(result.State.UpdatedAtUtc, DateTimeKind.Utc)),
                    PauseRequestedAtUtc = ToOffset(result.State.PauseRequestedAtUtc),
                    PausedAtUtc = ToOffset(result.State.PausedAtUtc),
                    ResumeRequestedAtUtc = ToOffset(result.State.ResumeRequestedAtUtc),
                    InputReceivedAtUtc = ToOffset(result.State.InputReceivedAtUtc)
                },
                AcceptedAtUtc = result.CompletedAtUtc,
                CorrelationId = result.CorrelationId
            };
        }

        private static AiSdkExecutionControlStatus MapControlStatus(
            Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus status) => status switch
            {
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.None => AiSdkExecutionControlStatus.None,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.Running => AiSdkExecutionControlStatus.Running,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.Pausing => AiSdkExecutionControlStatus.Pausing,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.Paused => AiSdkExecutionControlStatus.Paused,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.Resuming => AiSdkExecutionControlStatus.Resuming,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.Cancelling => AiSdkExecutionControlStatus.Cancelling,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.Cancelled => AiSdkExecutionControlStatus.Cancelled,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlStatus.WaitingForInput => AiSdkExecutionControlStatus.WaitingForInput,
                _ => throw new NotSupportedException($"Execution control status '{status}' is not supported by the public SDK contract.")
            };

        private static AiSdkExecutionControlAction MapControlAction(
            Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction action) => action switch
            {
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction.None => AiSdkExecutionControlAction.None,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction.Pause => AiSdkExecutionControlAction.Pause,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction.Resume => AiSdkExecutionControlAction.Resume,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction.Cancel => AiSdkExecutionControlAction.Cancel,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction.WaitForInput => AiSdkExecutionControlAction.WaitForInput,
                Multiplexed.Abstractions.AI.Execution.Control.AiExecutionControlAction.SubmitInput => AiSdkExecutionControlAction.SubmitInput,
                _ => throw new NotSupportedException($"Execution control action '{action}' is not supported by the public SDK contract.")
            };

        private static void ValidateExecutionId(string executionId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        }

        private async Task<AiDurableInvocationScope> ResolveScopeAsync(CancellationToken cancellationToken)
        {
            var snapshot = _context.MapToSnapshot();
            if (string.IsNullOrWhiteSpace(snapshot.TenantId) || string.IsNullOrWhiteSpace(snapshot.TenantGroupId))
                throw new UnauthorizedAccessException("The authenticated context has no tenant scope.");
            var controlPlaneId = await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false);
            return new AiDurableInvocationScope(snapshot.TenantId, snapshot.TenantGroupId, controlPlaneId);
        }

        private static void RequireOwner(
            AiExecutionRecord record,
            AiDurableInvocationScope scope,
            AiPublicationRunPin pin)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(pin);

            var owner = record.ExecutionContextSnapshot;
            if (owner is null ||
                pin.ExecutionId != record.ExecutionId ||
                pin.Partition.Scope != scope ||
                owner.TenantId != pin.Partition.Scope.TenantId ||
                owner.TenantGroupId != pin.Partition.Scope.TenantGroupId ||
                owner.Project != pin.Partition.Project ||
                owner.CurrentNamespace != pin.Partition.Namespace ||
                owner.UserId != pin.UserId)
            {
                throw new UnauthorizedAccessException(
                    "Execution ownership does not match the immutable publication admission.");
            }
        }

        private static DateTimeOffset? ToOffset(DateTime? value) =>
            value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null;
    }
}
