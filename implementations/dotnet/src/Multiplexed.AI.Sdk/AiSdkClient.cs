using System.Runtime.CompilerServices;
using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Contracts.Replay;
using Multiplexed.AI.Sdk.Contracts.Watch;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Serialization;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk
{
    /// <summary>
    /// Ergonomic .NET client over the public SDK transport. It serializes only portable SDK contracts and never
    /// participates in scheduling, recovery, lease, epoch, queue or result-acceptance decisions.
    /// </summary>
    public sealed class AiSdkClient : IAiSdkClient
    {
        private const int MaxConsecutiveDuplicateWatchItems = 3;

        private readonly IAiSdkTransport _transport;

        public AiSdkClient(IAiSdkTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        public Task<AiSdkPipelinePublicationResponse> PublishPipelineAsync(
            AiSdkPipelinePublicationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateSchema(
                "pipeline publication request",
                request.SchemaVersion,
                AiSdkSchemaVersions.PipelinePublicationRequest);

            return InvokeAsync<AiSdkPipelinePublicationResponse>(
                AiSdkOperationNames.PublishPipeline,
                AiSdkJsonSerializer.ToElement(new RequestArguments<AiSdkPipelinePublicationRequest>(request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.PipelinePublicationResponse,
                cancellationToken);
        }

        public Task<AiSdkExecutionSubmissionResponse> SubmitExecutionAsync(
            AiSdkExecutionSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateSchema(
                "execution submission request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionSubmissionRequest);

            return InvokeAsync<AiSdkExecutionSubmissionResponse>(
                AiSdkOperationNames.SubmitExecution,
                AiSdkJsonSerializer.ToElement(new RequestArguments<AiSdkExecutionSubmissionRequest>(request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionSubmissionResponse,
                cancellationToken);
        }

        public Task<AiSdkExecutionObservation> ObserveExecutionAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);

            return InvokeAsync<AiSdkExecutionObservation>(
                AiSdkOperationNames.ObserveExecution,
                AiSdkJsonSerializer.ToElement(new ExecutionArguments(executionId)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionObservation,
                cancellationToken);
        }

        public IAsyncEnumerable<AiSdkExecutionWatchEvent> WatchExecutionAsync(
            AiSdkExecutionWatchRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateSchema(
                "execution watch request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionWatchRequest);
            ValidateExecutionId(request.ExecutionId);
            ArgumentNullException.ThrowIfNull(request.Channels);
            foreach (var channel in request.Channels)
            {
                if (!Enum.IsDefined(channel))
                {
                    throw new AiSdkException(new AiSdkError
                    {
                        Kind = AiSdkErrorKind.InvalidRequest,
                        Code = "invalid_watch_channel",
                        Message = $"Unknown execution Watch channel '{channel}'."
                    });
                }
            }

            return WatchExecutionCoreAsync(request, cancellationToken);
        }

        public Task<AiSdkExecutionResult> GetExecutionResultAsync(
            string executionId,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);

            return InvokeAsync<AiSdkExecutionResult>(
                AiSdkOperationNames.GetExecutionResult,
                AiSdkJsonSerializer.ToElement(new ExecutionArguments(executionId)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionResult,
                cancellationToken);
        }

        public Task<AiSdkExecutionCancellationResponse> CancelExecutionAsync(
            string executionId,
            AiSdkExecutionCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            ArgumentNullException.ThrowIfNull(request);
            ValidateSchema(
                "execution cancellation request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionCancellationRequest);

            return InvokeAsync<AiSdkExecutionCancellationResponse>(
                AiSdkOperationNames.CancelExecution,
                AiSdkJsonSerializer.ToElement(new CancellationArguments(executionId, request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionCancellationResponse,
                cancellationToken);
        }

        public Task<AiSdkExecutionControlResponse> PauseExecutionAsync(
            string executionId,
            AiSdkExecutionControlRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            request ??= new AiSdkExecutionControlRequest();
            ValidateSchema(
                "execution control request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionControlRequest);

            return InvokeAsync<AiSdkExecutionControlResponse>(
                AiSdkOperationNames.PauseExecution,
                AiSdkJsonSerializer.ToElement(new ExecutionRequestArguments<AiSdkExecutionControlRequest>(executionId, request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionControlResponse,
                cancellationToken);
        }

        public Task<AiSdkExecutionControlResponse> ResumeExecutionAsync(
            string executionId,
            AiSdkExecutionControlRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            request ??= new AiSdkExecutionControlRequest();
            ValidateSchema(
                "execution control request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionControlRequest);

            return InvokeAsync<AiSdkExecutionControlResponse>(
                AiSdkOperationNames.ResumeExecution,
                AiSdkJsonSerializer.ToElement(new ExecutionRequestArguments<AiSdkExecutionControlRequest>(executionId, request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionControlResponse,
                cancellationToken);
        }

        public Task<AiSdkExecutionControlResponse> SubmitExecutionInputAsync(
            string executionId,
            AiSdkExecutionInputSubmissionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            ArgumentNullException.ThrowIfNull(request);
            ValidateSchema(
                "execution input submission request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionInputSubmissionRequest);
            if (string.IsNullOrWhiteSpace(request.WaitingKey))
            {
                throw new AiSdkException(new AiSdkError
                {
                    Kind = AiSdkErrorKind.InvalidRequest,
                    Code = "waiting_key_required",
                    Message = "A non-empty waitingKey is required."
                });
            }
            if (request.Input.ValueKind != JsonValueKind.Object)
            {
                throw new AiSdkException(new AiSdkError
                {
                    Kind = AiSdkErrorKind.InvalidRequest,
                    Code = "input_object_required",
                    Message = "Execution input must be a JSON object."
                });
            }

            return InvokeAsync<AiSdkExecutionControlResponse>(
                AiSdkOperationNames.SubmitExecutionInput,
                AiSdkJsonSerializer.ToElement(new ExecutionRequestArguments<AiSdkExecutionInputSubmissionRequest>(executionId, request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionControlResponse,
                cancellationToken);
        }

        public Task<AiSdkExecutionReplayResponse> ReplayExecutionAsync(
            string executionId,
            AiSdkExecutionReplayRequest? request = null,
            CancellationToken cancellationToken = default)
        {
            ValidateExecutionId(executionId);
            request ??= new AiSdkExecutionReplayRequest();
            ValidateSchema(
                "execution replay request",
                request.SchemaVersion,
                AiSdkSchemaVersions.ExecutionReplayRequest);

            return InvokeAsync<AiSdkExecutionReplayResponse>(
                AiSdkOperationNames.ReplayExecution,
                AiSdkJsonSerializer.ToElement(new ExecutionRequestArguments<AiSdkExecutionReplayRequest>(executionId, request)),
                response => response.SchemaVersion,
                AiSdkSchemaVersions.ExecutionReplayResponse,
                cancellationToken);
        }

        private async IAsyncEnumerable<AiSdkExecutionWatchEvent> WatchExecutionCoreAsync(
            AiSdkExecutionWatchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var afterSequence = request.AfterSequence;
            var includeInitialSnapshot = request.IncludeInitialSnapshot;
            var awaitingResyncSnapshot = false;
            var consecutiveDuplicates = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nextRequest = request with
                {
                    AfterSequence = afterSequence,
                    IncludeInitialSnapshot = includeInitialSnapshot
                };

                var item = await InvokeAsync<AiSdkExecutionWatchEvent>(
                        AiSdkOperationNames.WatchExecution,
                        AiSdkJsonSerializer.ToElement(new RequestArguments<AiSdkExecutionWatchRequest>(nextRequest)),
                        response => response.SchemaVersion,
                        AiSdkSchemaVersions.ExecutionWatchEvent,
                        cancellationToken)
                    .ConfigureAwait(false);

                ValidateWatchEvent(nextRequest, item);

                if (item.Kind == AiSdkExecutionWatchEventKind.ResyncRequired)
                {
                    if (awaitingResyncSnapshot)
                    {
                        throw InvalidWatchResponse(
                            "watch_resync_loop",
                            "The execution Watch server requested another resynchronization before returning the authoritative snapshot.");
                    }

                    yield return item;
                    afterSequence = null;
                    includeInitialSnapshot = true;
                    awaitingResyncSnapshot = true;
                    consecutiveDuplicates = 0;
                    continue;
                }

                if (awaitingResyncSnapshot && item.Kind != AiSdkExecutionWatchEventKind.Snapshot)
                {
                    throw InvalidWatchResponse(
                        "watch_resync_snapshot_required",
                        "The execution Watch server did not return the authoritative snapshot required to complete resynchronization.");
                }

                if (afterSequence is { } cursor && item.Sequence is { } sequence)
                {
                    if (sequence == cursor)
                    {
                        consecutiveDuplicates++;
                        if (consecutiveDuplicates > MaxConsecutiveDuplicateWatchItems)
                        {
                            throw InvalidWatchResponse(
                                "duplicate_watch_sequence_loop",
                                "The execution Watch server repeatedly returned the already-consumed public sequence.");
                        }

                        continue;
                    }

                    if (request.Channels.Count == 0 && sequence > cursor + 1)
                    {
                        var gap = CreateGapDetectedResync(request.ExecutionId, cursor, sequence, item.OccurredAtUtc);
                        yield return gap;
                        afterSequence = null;
                        includeInitialSnapshot = true;
                        awaitingResyncSnapshot = true;
                        consecutiveDuplicates = 0;
                        continue;
                    }
                }

                consecutiveDuplicates = 0;
                yield return item;

                if (awaitingResyncSnapshot)
                {
                    awaitingResyncSnapshot = false;
                }

                if (IsTerminalWatchItem(item))
                {
                    yield break;
                }

                afterSequence = item.Sequence!.Value;
                includeInitialSnapshot = false;
            }
        }

        private static AiSdkExecutionWatchEvent CreateGapDetectedResync(
            string executionId,
            long requestedAfterSequence,
            long observedSequence,
            DateTimeOffset occurredAtUtc)
        {
            return new AiSdkExecutionWatchEvent
            {
                ExecutionId = executionId,
                Kind = AiSdkExecutionWatchEventKind.ResyncRequired,
                OccurredAtUtc = occurredAtUtc,
                ResyncRequired = new AiSdkExecutionWatchResyncRequired
                {
                    Reason = AiSdkExecutionWatchResyncReason.GapDetected,
                    RequestedAfterSequence = requestedAfterSequence,
                    EarliestAvailableSequence = requestedAfterSequence + 1,
                    LatestSequence = observedSequence,
                    Message = "A gap was detected in the unfiltered public execution Watch stream."
                }
            };
        }

        private static void ValidateWatchEvent(
            AiSdkExecutionWatchRequest request,
            AiSdkExecutionWatchEvent item)
        {
            if (!string.Equals(item.ExecutionId, request.ExecutionId, StringComparison.Ordinal))
            {
                throw InvalidWatchResponse(
                    "watch_execution_mismatch",
                    "The execution Watch response does not belong to the requested execution.");
            }

            switch (item.Kind)
            {
                case AiSdkExecutionWatchEventKind.Snapshot:
                    if (item.Snapshot is null || item.Sequence is null)
                    {
                        throw InvalidWatchResponse(
                            "invalid_watch_snapshot",
                            "An execution Watch snapshot requires both snapshot and sequence values.");
                    }
                    ValidateSchema(
                        "execution Watch snapshot",
                        item.Snapshot.SchemaVersion,
                        AiSdkSchemaVersions.ExecutionObservation);
                    break;

                case AiSdkExecutionWatchEventKind.Event:
                    if (item.Sequence is null || item.Channel is null || string.IsNullOrWhiteSpace(item.EventType))
                    {
                        throw InvalidWatchResponse(
                            "invalid_watch_event",
                            "An execution Watch event requires sequence, channel and eventType values.");
                    }
                    break;

                case AiSdkExecutionWatchEventKind.ResyncRequired:
                    if (item.ResyncRequired is null)
                    {
                        throw InvalidWatchResponse(
                            "invalid_watch_resync",
                            "A ResyncRequired Watch item requires a resyncRequired document.");
                    }
                    ValidateSchema(
                        "execution Watch resync document",
                        item.ResyncRequired.SchemaVersion,
                        AiSdkSchemaVersions.ExecutionWatchResyncRequired);
                    return;

                default:
                    throw InvalidWatchResponse(
                        "unknown_watch_kind",
                        $"Unknown execution Watch item kind '{item.Kind}'.");
            }

            if (item.Sequence!.Value < 0)
            {
                throw InvalidWatchResponse(
                    "invalid_watch_sequence",
                    "Execution Watch sequence values cannot be negative.");
            }

            if (request.AfterSequence is { } afterSequence && item.Sequence!.Value < afterSequence)
            {
                throw InvalidWatchResponse(
                    "regressing_watch_sequence",
                    "The execution Watch response regressed behind the requested public sequence.");
            }
        }

        private static bool IsTerminalWatchItem(AiSdkExecutionWatchEvent item)
        {
            if (item.Snapshot is { } snapshot)
            {
                return IsTerminalStatus(snapshot.Status);
            }

            if (item.Kind != AiSdkExecutionWatchEventKind.Event ||
                item.Channel != AiSdkExecutionWatchChannel.Lifecycle ||
                item.Payload is not JsonElement payload ||
                payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("status", out var statusElement) ||
                statusElement.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<AiSdkExecutionStatus>(statusElement.GetString(), ignoreCase: false, out var status))
            {
                return false;
            }

            return IsTerminalStatus(status);
        }

        private static bool IsTerminalStatus(AiSdkExecutionStatus status) =>
            status is AiSdkExecutionStatus.Completed or AiSdkExecutionStatus.Failed or AiSdkExecutionStatus.Cancelled;

        private static AiSdkException InvalidWatchResponse(string code, string message) =>
            new(new AiSdkError
            {
                Kind = AiSdkErrorKind.InvalidResponse,
                Code = code,
                Message = message
            });

        private async Task<TResponse> InvokeAsync<TResponse>(
            string operation,
            JsonElement arguments,
            Func<TResponse, int> responseSchemaVersion,
            int expectedResponseSchemaVersion,
            CancellationToken cancellationToken)
        {
            AiSdkTransportResponse transportResponse;
            try
            {
                transportResponse = await _transport.InvokeAsync(
                    new AiSdkTransportRequest
                    {
                        Operation = operation,
                        Arguments = arguments
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AiSdkException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new AiSdkException(
                    new AiSdkError
                    {
                        Kind = AiSdkErrorKind.Transport,
                        Code = "transport_failure",
                        Message = "The SDK transport failed before a normalized response was returned.",
                        IsRetryable = false
                    },
                    ex);
            }

            if (!transportResponse.IsSuccess)
            {
                throw new AiSdkException(
                    transportResponse.Error
                    ?? new AiSdkError
                    {
                        Kind = AiSdkErrorKind.InvalidResponse,
                        Code = "invalid_transport_response",
                        Message = "The SDK transport reported failure without an error document."
                    });
            }

            if (transportResponse.Result is not { } result)
            {
                throw new AiSdkException(new AiSdkError
                {
                    Kind = AiSdkErrorKind.InvalidResponse,
                    Code = "missing_transport_result",
                    Message = "The SDK transport reported success without a result document."
                });
            }

            TResponse response;
            try
            {
                response = AiSdkJsonSerializer.FromElement<TResponse>(result);
            }
            catch (JsonException ex)
            {
                throw new AiSdkException(
                    new AiSdkError
                    {
                        Kind = AiSdkErrorKind.InvalidResponse,
                        Code = "invalid_response_json",
                        Message = $"The '{operation}' response does not match the public SDK contract."
                    },
                    ex);
            }

            ValidateSchema(
                $"{operation} response",
                responseSchemaVersion(response),
                expectedResponseSchemaVersion);

            return response;
        }

        private static void ValidateExecutionId(string executionId)
        {
            if (!string.IsNullOrWhiteSpace(executionId))
            {
                return;
            }

            throw new AiSdkException(new AiSdkError
            {
                Kind = AiSdkErrorKind.InvalidRequest,
                Code = "execution_id_required",
                Message = "A non-empty executionId is required."
            });
        }

        private static void ValidateSchema(string document, int actual, int expected)
        {
            if (actual == expected)
            {
                return;
            }

            throw new AiSdkException(new AiSdkError
            {
                Kind = AiSdkErrorKind.UnsupportedSchema,
                Code = "unsupported_schema",
                Message = $"Unsupported {document} schemaVersion '{actual}'. Expected '{expected}'."
            });
        }

        private sealed record RequestArguments<TRequest>(TRequest Request);

        private sealed record ExecutionArguments(string ExecutionId);

        private sealed record CancellationArguments(
            string ExecutionId,
            AiSdkExecutionCancellationRequest Request);

        private sealed record ExecutionRequestArguments<TRequest>(
            string ExecutionId,
            TRequest Request);
    }
}
