using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Publication;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Stores;

namespace Multiplexed.AI.McpServer.PublicSdk
{
    /// <summary>Maps the dependency-free public contract to existing publication, queue, state and control authorities.</summary>
    public sealed class AiPublicSdkBoundary : IAiPublicSdkBoundary
    {
        private readonly AiPipelinePublicationService _publications;
        private readonly AiPublishedDagRunService _runs;
        private readonly IAiSharedRuntimeController _controller;
        private readonly IAiExecutionStore _executions;
        private readonly IAiExecutionControlService _control;
        private readonly IExecutionContextSnapshotProvider _context;
        private readonly IAiControlPlaneIdResolver _controlPlane;

        public AiPublicSdkBoundary(
            AiPipelinePublicationService publications,
            AiPublishedDagRunService runs,
            IAiSharedRuntimeController controller,
            IAiExecutionStore executions,
            IAiExecutionControlService control,
            IExecutionContextSnapshotProvider context,
            IAiControlPlaneIdResolver controlPlane)
        {
            _publications = publications;
            _runs = runs;
            _controller = controller;
            _executions = executions;
            _control = control;
            _context = context;
            _controlPlane = controlPlane;
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
            var record = await _runs.CreateAsync(scope, runKey!, request.PublicationRef, inputJson, cancellationToken).ConfigureAwait(false);
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
            var definition = await _publications.ReadDefinitionAsync(scope, pin.PublicationRef, cancellationToken).ConfigureAwait(false);
            var record = await _executions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            var state = await _executions.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false);
            RequireOwner(record, scope);
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
            _ = await _runs.ReadPinAsync(scope, executionId, cancellationToken).ConfigureAwait(false);
            var record = await _executions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            var state = await _executions.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Execution state was not found.");
            RequireOwner(record, scope);
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
            _ = await _runs.ReadPinAsync(scope, executionId, cancellationToken).ConfigureAwait(false);
            var record = await _executions.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Execution '{executionId}' was not found.");
            RequireOwner(record, scope);
            var snapshot = _context.MapToSnapshot();
            var control = await _control.CancelExecutionAsync(
                executionId, request.Reason, snapshot.UserId, cancellationToken).ConfigureAwait(false);
            return new AiSdkExecutionCancellationResponse
            {
                ExecutionId = executionId,
                CancellationRequested = true,
                Status = AiPublicSdkContractMapper.ToPublic(record.Status),
                RequestedAtUtc = ToOffset(control.CancellationRequestedAtUtc),
                CorrelationId = request.CorrelationId
            };
        }

        private async Task<AiDurableInvocationScope> ResolveScopeAsync(CancellationToken cancellationToken)
        {
            var snapshot = _context.MapToSnapshot();
            if (string.IsNullOrWhiteSpace(snapshot.TenantId) || string.IsNullOrWhiteSpace(snapshot.TenantGroupId))
                throw new UnauthorizedAccessException("The authenticated context has no tenant scope.");
            var controlPlaneId = await _controlPlane.ResolveAsync(cancellationToken).ConfigureAwait(false);
            return new AiDurableInvocationScope(snapshot.TenantId, snapshot.TenantGroupId, controlPlaneId);
        }

        private static void RequireOwner(AiExecutionRecord record, AiDurableInvocationScope scope)
        {
            var owner = record.ExecutionContextSnapshot;
            if (owner is null || owner.TenantId != scope.TenantId || owner.TenantGroupId != scope.TenantGroupId)
                throw new UnauthorizedAccessException("Execution ownership does not match the authenticated tenant scope.");
        }

        private static DateTimeOffset? ToOffset(DateTime? value) =>
            value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null;
    }
}
