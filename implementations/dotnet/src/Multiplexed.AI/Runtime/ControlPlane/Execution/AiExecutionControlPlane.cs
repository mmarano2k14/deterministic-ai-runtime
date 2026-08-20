using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Execution;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.ControlPlane;

namespace Multiplexed.AI.Runtime.ControlPlane.Execution
{
    /// <summary>
    /// Runtime implementation of the execution control-plane facade.
    ///
    /// This class wraps the existing execution control service and exposes
    /// adapter-neutral pause, resume, cancel, human input, and status operations.
    ///
    /// Important:
    /// This class must not execute DAG steps, claim work, modify local queues,
    /// or replace worker/runtime execution logic.
    /// </summary>
    public sealed class AiExecutionControlPlane : IAiExecutionControlPlane
    {
        private readonly IAiExecutionControlService _controlService;
        private readonly AiExecutionControlPlaneOptions _options;
        private readonly IAiControlPlaneObserver _observer;

        public AiExecutionControlPlane(
            IAiExecutionControlService controlService,
            IOptions<AiExecutionControlPlaneOptions> options,
            IAiControlPlaneObserver observer)
        {
            _controlService = controlService ?? throw new ArgumentNullException(nameof(controlService));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        public Task<AiExecutionControlPlaneResult> ExecuteAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return request.Operation switch
            {
                AiExecutionControlPlaneOperation.Pause => PauseAsync(request, cancellationToken),
                AiExecutionControlPlaneOperation.Resume => ResumeAsync(request, cancellationToken),
                AiExecutionControlPlaneOperation.Cancel => CancelAsync(request, cancellationToken),
                AiExecutionControlPlaneOperation.SubmitHumanInput => SubmitHumanInputAsync(request, cancellationToken),
                AiExecutionControlPlaneOperation.GetStatus => GetStatusAsync(request, cancellationToken),

                _ => throw new NotSupportedException(
                    $"Execution control-plane operation '{request.Operation}' is not supported.")
            };
        }

        public Task<AiExecutionControlPlaneResult> PauseAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControlOperationAsync(
                request,
                AiExecutionControlPlaneOperation.Pause,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> ResumeAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControlOperationAsync(
                request,
                AiExecutionControlPlaneOperation.Resume,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> CancelAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControlOperationAsync(
                request,
                AiExecutionControlPlaneOperation.Cancel,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> SubmitHumanInputAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControlOperationAsync(
                request,
                AiExecutionControlPlaneOperation.SubmitHumanInput,
                cancellationToken);
        }

        public Task<AiExecutionControlPlaneResult> GetStatusAsync(
            AiExecutionControlPlaneRequest request,
            CancellationToken cancellationToken = default)
        {
            return ExecuteControlOperationAsync(
                request,
                AiExecutionControlPlaneOperation.GetStatus,
                cancellationToken);
        }

        private async Task<AiExecutionControlPlaneResult> ExecuteControlOperationAsync(
            AiExecutionControlPlaneRequest request,
            AiExecutionControlPlaneOperation operation,
            CancellationToken cancellationToken)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            var correlation = CreateCorrelation(request);

            try
            {
                ValidateRequest(request);
                ValidateOperationRequest(request, operation);
                EnsureEnabled(operation);

                await RecordStartedAsync(
                    request,
                    operation,
                    correlation,
                    cancellationToken).ConfigureAwait(false);

                var state = await ExecuteInnerAsync(
                    request,
                    operation,
                    cancellationToken).ConfigureAwait(false);

                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordCompletedAsync(
                    request,
                    operation,
                    correlation,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);

                return new AiExecutionControlPlaneResult
                {
                    ExecutionId = request.ExecutionId,
                    Operation = operation,
                    Success = true,
                    Message = $"Execution control-plane operation '{operation}' completed successfully.",
                    State = request.IncludeState ? state : null,
                    CorrelationId = correlation.CorrelationId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    RequestedBy = request.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs
                };
            }
            catch (Exception exception) when (_options.ReturnFailureResultInsteadOfThrowing)
            {
                var completedAtUtc = DateTimeOffset.UtcNow;
                var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);

                await RecordFailedAsync(
                    request,
                    operation,
                    correlation,
                    exception,
                    durationMs,
                    cancellationToken).ConfigureAwait(false);

                return new AiExecutionControlPlaneResult
                {
                    ExecutionId = request?.ExecutionId ?? string.Empty,
                    Operation = operation,
                    Success = false,
                    Message = $"Execution control-plane operation '{operation}' failed.",
                    Diagnostics = request?.IncludeDiagnostics == true
                        ? new[] { exception.Message }
                        : Array.Empty<string>(),
                    CorrelationId = correlation.CorrelationId,
                    RuntimeInstanceId = request?.RuntimeInstanceId,
                    RequestedBy = request?.RequestedBy,
                    StartedAtUtc = startedAtUtc,
                    CompletedAtUtc = completedAtUtc,
                    DurationMs = durationMs,
                    FailureReason = exception.Message
                };
            }
        }

        private async Task<AiExecutionControlState?> ExecuteInnerAsync(
            AiExecutionControlPlaneRequest request,
            AiExecutionControlPlaneOperation operation,
            CancellationToken cancellationToken)
        {
            return operation switch
            {
                AiExecutionControlPlaneOperation.Pause =>
                    await _controlService.PauseExecutionAsync(
                        request.ExecutionId,
                        request.Reason,
                        request.RequestedBy,
                        cancellationToken).ConfigureAwait(false),

                AiExecutionControlPlaneOperation.Resume =>
                    await _controlService.ResumeExecutionAsync(
                        request.ExecutionId,
                        request.RequestedBy,
                        cancellationToken).ConfigureAwait(false),

                AiExecutionControlPlaneOperation.Cancel =>
                    await _controlService.CancelExecutionAsync(
                        request.ExecutionId,
                        request.Reason,
                        request.RequestedBy,
                        cancellationToken).ConfigureAwait(false),

                AiExecutionControlPlaneOperation.SubmitHumanInput =>
                    await _controlService.SubmitHumanInputAsync(
                        request.ExecutionId,
                        request.WaitingKey!,
                        ToInputDictionary(request.Input),
                        request.RequestedBy,
                        cancellationToken).ConfigureAwait(false),

                AiExecutionControlPlaneOperation.GetStatus =>
                    await _controlService.GetStateAsync(
                        request.ExecutionId,
                        cancellationToken).ConfigureAwait(false),

                _ => throw new NotSupportedException(
                    $"Execution control-plane operation '{operation}' is not supported.")
            };
        }

        private static Dictionary<string, object?> ToInputDictionary(
            object? input)
        {
            if (input is null)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            if (input is Dictionary<string, object?> dictionary)
            {
                return dictionary;
            }

            if (input is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return new Dictionary<string, object?>(
                    readOnlyDictionary,
                    StringComparer.Ordinal);
            }

            throw new ArgumentException(
                "SubmitHumanInput requires Input to be a dictionary of string keys.",
                nameof(input));
        }

        private static AiRuntimeExecutionCorrelationContext CreateCorrelation(
            AiExecutionControlPlaneRequest request)
        {
            return new AiRuntimeExecutionCorrelationContext
            {
                CorrelationId = string.IsNullOrWhiteSpace(request.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : request.CorrelationId,

                ExecutionId = request.ExecutionId,
                RuntimeInstanceId = request.RuntimeInstanceId
            };
        }

        private async Task RecordStartedAsync(
            AiExecutionControlPlaneRequest request,
            AiExecutionControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationStarted,
                    Area = AiControlPlaneArea.ExecutionControl,
                    Operation = operation.ToString(),
                    Correlation = correlation,
                    Message = $"Execution control-plane operation '{operation}' started.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy,
                        ["reason"] = request.Reason,
                        ["waitingKey"] = request.WaitingKey,
                        ["waitingStepName"] = request.WaitingStepName,
                        ["includeState"] = request.IncludeState,
                        ["includeDiagnostics"] = request.IncludeDiagnostics
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RecordCompletedAsync(
            AiExecutionControlPlaneRequest request,
            AiExecutionControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationCompleted,
                    Area = AiControlPlaneArea.ExecutionControl,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Succeeded,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Execution control-plane operation '{operation}' completed successfully.",
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request.Source,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request.RequestedBy
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RecordFailedAsync(
            AiExecutionControlPlaneRequest? request,
            AiExecutionControlPlaneOperation operation,
            AiRuntimeExecutionCorrelationContext correlation,
            Exception exception,
            long durationMs,
            CancellationToken cancellationToken)
        {
            await _observer.RecordAsync(
                new AiControlPlaneEvent
                {
                    EventType = AiControlPlaneEventType.OperationFailed,
                    Area = AiControlPlaneArea.ExecutionControl,
                    Operation = operation.ToString(),
                    Outcome = AiControlPlaneOperationOutcome.Failed,
                    Correlation = correlation,
                    DurationMs = durationMs,
                    Message = $"Execution control-plane operation '{operation}' failed.",
                    FailureReason = exception.Message,
                    Properties = new Dictionary<string, object?>
                    {
                        ["source"] = request?.Source,
                        [AiControlPlaneRequestMetadataKeys.RequestedBy] = request?.RequestedBy,
                        ["exceptionType"] = exception.GetType().Name
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        private static void ValidateRequest(
            AiExecutionControlPlaneRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(request.ExecutionId))
            {
                throw new ArgumentException(
                    "ExecutionId is required for execution control-plane operations.",
                    nameof(request));
            }
        }

        private static void ValidateOperationRequest(
            AiExecutionControlPlaneRequest request,
            AiExecutionControlPlaneOperation operation)
        {
            if (operation != AiExecutionControlPlaneOperation.SubmitHumanInput)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(request.WaitingKey))
            {
                throw new ArgumentException(
                    "WaitingKey is required for SubmitHumanInput operations.",
                    nameof(request));
            }
        }

        private void EnsureEnabled(
            AiExecutionControlPlaneOperation operation)
        {
            var enabled = operation switch
            {
                AiExecutionControlPlaneOperation.Pause => _options.EnablePause,
                AiExecutionControlPlaneOperation.Resume => _options.EnableResume,
                AiExecutionControlPlaneOperation.Cancel => _options.EnableCancel,
                AiExecutionControlPlaneOperation.SubmitHumanInput => _options.EnableSubmitHumanInput,
                AiExecutionControlPlaneOperation.GetStatus => _options.EnableGetStatus,
                _ => false
            };

            if (!enabled)
            {
                throw new InvalidOperationException(
                    $"Execution control-plane operation '{operation}' is disabled.");
            }
        }

        private long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            if (!_options.MeasureDuration)
            {
                return 0;
            }

            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }
    }
}