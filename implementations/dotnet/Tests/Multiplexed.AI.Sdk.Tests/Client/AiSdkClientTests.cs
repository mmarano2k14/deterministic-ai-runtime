using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Client
{
    /// <summary>Validates the typed .NET client without introducing runtime or server dependencies.</summary>
    public sealed class AiSdkClientTests
    {
        [Fact]
        public async Task Client_Uses_Exact_Portable_Operation_Arguments_And_Response_Types()
        {
            var transport = new RecordingTransport(request => request.Operation switch
            {
                AiSdkOperationNames.PublishPipeline => Success(new AiSdkPipelinePublicationResponse
                {
                    PublicationRef = "pub-1",
                    PublicationSha256 = "sha",
                    PipelineName = "demo",
                    PipelineVersion = "1"
                }),
                AiSdkOperationNames.SubmitExecution => Success(new AiSdkExecutionSubmissionResponse
                {
                    ExecutionId = "exec-1",
                    PublicationRef = "pub-1",
                    Status = AiSdkExecutionStatus.Pending,
                    AcceptedAtUtc = DateTimeOffset.UnixEpoch
                }),
                AiSdkOperationNames.ObserveExecution => Success(new AiSdkExecutionObservation
                {
                    ExecutionId = "exec-1",
                    PublicationRef = "pub-1",
                    PipelineName = "demo",
                    PipelineVersion = "1",
                    Status = AiSdkExecutionStatus.Running,
                    CreatedAtUtc = DateTimeOffset.UnixEpoch,
                    UpdatedAtUtc = DateTimeOffset.UnixEpoch
                }),
                AiSdkOperationNames.GetExecutionResult => Success(new AiSdkExecutionResult
                {
                    ExecutionId = "exec-1",
                    Status = AiSdkExecutionStatus.Completed,
                    CompletedAtUtc = DateTimeOffset.UnixEpoch
                }),
                AiSdkOperationNames.CancelExecution => Success(new AiSdkExecutionCancellationResponse
                {
                    ExecutionId = "exec-1",
                    CancellationRequested = true,
                    Status = AiSdkExecutionStatus.Running
                }),
                _ => throw new InvalidOperationException("Unexpected operation.")
            });
            IAiSdkClient client = new AiSdkClient(transport);

            var publication = await client.PublishPipelineAsync(new AiSdkPipelinePublicationRequest
            {
                Definition = new AiSdkPipelineDefinition
                {
                    Name = "demo",
                    Version = "1"
                }
            });
            var submission = await client.SubmitExecutionAsync(new AiSdkExecutionSubmissionRequest
            {
                PublicationRef = publication.PublicationRef,
                IdempotencyKey = "run-1"
            });
            var observation = await client.ObserveExecutionAsync(submission.ExecutionId);
            var result = await client.GetExecutionResultAsync(submission.ExecutionId);
            var cancellation = await client.CancelExecutionAsync(
                submission.ExecutionId,
                new AiSdkExecutionCancellationRequest { Reason = "operator" });

            Assert.Equal("pub-1", publication.PublicationRef);
            Assert.Equal("exec-1", submission.ExecutionId);
            Assert.Equal(AiSdkExecutionStatus.Running, observation.Status);
            Assert.Equal(AiSdkExecutionStatus.Completed, result.Status);
            Assert.True(cancellation.CancellationRequested);
            Assert.Equal(5, transport.Requests.Count);

            Assert.Equal(AiSdkOperationNames.PublishPipeline, transport.Requests[0].Operation);
            Assert.Equal("demo", transport.Requests[0].Arguments.GetProperty("request").GetProperty("definition").GetProperty("name").GetString());
            Assert.Equal(AiSdkOperationNames.SubmitExecution, transport.Requests[1].Operation);
            Assert.Equal("run-1", transport.Requests[1].Arguments.GetProperty("request").GetProperty("idempotencyKey").GetString());
            Assert.Equal("exec-1", transport.Requests[2].Arguments.GetProperty("executionId").GetString());
            Assert.Equal("exec-1", transport.Requests[3].Arguments.GetProperty("executionId").GetString());
            Assert.Equal("exec-1", transport.Requests[4].Arguments.GetProperty("executionId").GetString());
            Assert.Equal("operator", transport.Requests[4].Arguments.GetProperty("request").GetProperty("reason").GetString());
        }

        [Fact]
        public async Task Client_Propagates_Normalized_Transport_Error()
        {
            var transport = new RecordingTransport(_ => AiSdkTransportResponse.Failure(new AiSdkError
            {
                Kind = AiSdkErrorKind.Authorization,
                Code = "authorization_failed",
                Message = "Denied."
            }));
            var client = new AiSdkClient(transport);

            var exception = await Assert.ThrowsAsync<AiSdkException>(
                () => client.ObserveExecutionAsync("exec-1"));

            Assert.Equal(AiSdkErrorKind.Authorization, exception.Error.Kind);
            Assert.Equal("authorization_failed", exception.Error.Code);
        }

        [Fact]
        public async Task Client_Fails_Closed_On_Unexpected_Response_Schema()
        {
            var transport = new RecordingTransport(_ => Success(new AiSdkExecutionObservation
            {
                SchemaVersion = AiSdkSchemaVersions.ExecutionObservation + 1,
                ExecutionId = "exec-1"
            }));
            var client = new AiSdkClient(transport);

            var exception = await Assert.ThrowsAsync<AiSdkException>(
                () => client.ObserveExecutionAsync("exec-1"));

            Assert.Equal(AiSdkErrorKind.UnsupportedSchema, exception.Error.Kind);
            Assert.Equal("unsupported_schema", exception.Error.Code);
        }

        [Fact]
        public async Task Client_Does_Not_Invoke_Transport_For_Unsupported_Request_Schema()
        {
            var transport = new RecordingTransport(_ => throw new InvalidOperationException("Must not be invoked."));
            var client = new AiSdkClient(transport);

            var exception = await Assert.ThrowsAsync<AiSdkException>(
                () => client.SubmitExecutionAsync(new AiSdkExecutionSubmissionRequest
                {
                    SchemaVersion = AiSdkSchemaVersions.ExecutionSubmissionRequest + 1,
                    PublicationRef = "pub-1"
                }));

            Assert.Equal(AiSdkErrorKind.UnsupportedSchema, exception.Error.Kind);
            Assert.Empty(transport.Requests);
        }

        private static AiSdkTransportResponse Success<T>(T value) =>
            AiSdkTransportResponse.Success(JsonSerializer.SerializeToElement(value));

        private sealed class RecordingTransport : IAiSdkTransport
        {
            private readonly Func<AiSdkTransportRequest, AiSdkTransportResponse> _response;

            public RecordingTransport(Func<AiSdkTransportRequest, AiSdkTransportResponse> response)
            {
                _response = response;
            }

            public List<AiSdkTransportRequest> Requests { get; } = new();

            public ValueTask<AiSdkTransportResponse> InvokeAsync(
                AiSdkTransportRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                return ValueTask.FromResult(_response(request));
            }
        }
    }
}
