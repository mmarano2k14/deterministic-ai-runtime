using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Publication;
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
    }
}
