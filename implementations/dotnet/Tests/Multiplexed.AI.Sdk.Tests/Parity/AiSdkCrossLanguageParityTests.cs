using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Sdk.Contracts.Publication;
using Multiplexed.AI.Sdk.Contracts.Replay;
using Multiplexed.AI.Sdk.Contracts.Watch;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Parity
{
    /// <summary>Locks the .NET SDK to the same language-neutral fixtures used by TypeScript and Python.</summary>
    public sealed class AiSdkCrossLanguageParityTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNameCaseInsensitive = false,
            WriteIndented = false
        };

        [Fact]
        public async Task Full_Request_Wire_Shapes_Match_Shared_Fixture()
        {
            using var fixture = LoadFixture();
            var root = fixture.RootElement;
            var responses = root.GetProperty("responses");
            var transport = new RecordingTransport(operation => operation switch
            {
                var value when value == AiSdkOperationNames.PublishPipeline => Success(responses.GetProperty("publish")),
                var value when value == AiSdkOperationNames.SubmitExecution => Success(responses.GetProperty("submit")),
                var value when value == AiSdkOperationNames.ObserveExecution => Success(responses.GetProperty("observe")),
                var value when value == AiSdkOperationNames.GetExecutionResult => Success(responses.GetProperty("result")),
                var value when value == AiSdkOperationNames.CancelExecution => Success(responses.GetProperty("cancel")),
                var value when value == AiSdkOperationNames.PauseExecution => Success(responses.GetProperty("pause")),
                var value when value == AiSdkOperationNames.ResumeExecution => Success(responses.GetProperty("resume")),
                var value when value == AiSdkOperationNames.SubmitExecutionInput => Success(responses.GetProperty("submitInput")),
                var value when value == AiSdkOperationNames.ReplayExecution => Success(responses.GetProperty("replay")),
                _ => throw new InvalidOperationException($"Unexpected operation '{operation}'.")
            });
            var client = new AiSdkClient(transport);

            var requests = root.GetProperty("requests");
            var publication = Deserialize<AiSdkPipelinePublicationRequest>(
                requests.GetProperty("publish").GetProperty("arguments").GetProperty("request"));
            var submission = Deserialize<AiSdkExecutionSubmissionRequest>(
                requests.GetProperty("submit").GetProperty("arguments").GetProperty("request"));
            var cancellation = Deserialize<AiSdkExecutionCancellationRequest>(
                requests.GetProperty("cancel").GetProperty("arguments").GetProperty("request"));
            var pause = Deserialize<AiSdkExecutionControlRequest>(
                requests.GetProperty("pause").GetProperty("arguments").GetProperty("request"));
            var resume = Deserialize<AiSdkExecutionControlRequest>(
                requests.GetProperty("resume").GetProperty("arguments").GetProperty("request"));
            var submitInput = Deserialize<AiSdkExecutionInputSubmissionRequest>(
                requests.GetProperty("submitInput").GetProperty("arguments").GetProperty("request"));
            var replay = Deserialize<AiSdkExecutionReplayRequest>(
                requests.GetProperty("replay").GetProperty("arguments").GetProperty("request"));

            var publicationResponse = await client.PublishPipelineAsync(publication);
            var submissionResponse = await client.SubmitExecutionAsync(submission);
            var observationResponse = await client.ObserveExecutionAsync("exec-parity");
            var resultResponse = await client.GetExecutionResultAsync("exec-parity");
            var cancellationResponse = await client.CancelExecutionAsync("exec-parity", cancellation);
            var pauseResponse = await client.PauseExecutionAsync("exec-parity", pause);
            var resumeResponse = await client.ResumeExecutionAsync("exec-parity", resume);
            var inputResponse = await client.SubmitExecutionInputAsync("exec-parity", submitInput);
            var replayResponse = await client.ReplayExecutionAsync("exec-parity", replay);

            Assert.Equal("pub-parity", publicationResponse.PublicationRef);
            Assert.Equal(AiSdkExecutionStatus.Pending, submissionResponse.Status);
            Assert.Equal(AiSdkExecutionStatus.Running, observationResponse.Status);
            Assert.Equal(AiSdkExecutionStatus.Completed, resultResponse.Status);
            Assert.True(cancellationResponse.CancellationRequested);
            Assert.Equal(AiSdkExecutionControlOperation.Pause, pauseResponse.Operation);
            Assert.Equal(AiSdkExecutionControlOperation.Resume, resumeResponse.Operation);
            Assert.Equal(AiSdkExecutionControlOperation.SubmitInput, inputResponse.Operation);
            Assert.True(replayResponse.Succeeded);

            Assert.Equal(9, transport.Requests.Count);
            AssertRequestMatches(requests.GetProperty("publish"), transport.Requests[0]);
            AssertRequestMatches(requests.GetProperty("submit"), transport.Requests[1]);
            AssertRequestMatches(requests.GetProperty("observe"), transport.Requests[2]);
            AssertRequestMatches(requests.GetProperty("result"), transport.Requests[3]);
            AssertRequestMatches(requests.GetProperty("cancel"), transport.Requests[4]);
            AssertRequestMatches(requests.GetProperty("pause"), transport.Requests[5]);
            AssertRequestMatches(requests.GetProperty("resume"), transport.Requests[6]);
            AssertRequestMatches(requests.GetProperty("submitInput"), transport.Requests[7]);
            AssertRequestMatches(requests.GetProperty("replay"), transport.Requests[8]);
        }

        [Fact]
        public async Task Minimal_Defaults_And_Optional_Omission_Match_Shared_Fixture()
        {
            using var fixture = LoadFixture();
            var root = fixture.RootElement;
            var responses = root.GetProperty("responses");
            var transport = new RecordingTransport(operation => operation switch
            {
                var value when value == AiSdkOperationNames.PublishPipeline => Success(responses.GetProperty("publish")),
                var value when value == AiSdkOperationNames.SubmitExecution => Success(responses.GetProperty("submit")),
                var value when value == AiSdkOperationNames.CancelExecution => Success(responses.GetProperty("cancel")),
                var value when value == AiSdkOperationNames.PauseExecution => Success(responses.GetProperty("pause")),
                var value when value == AiSdkOperationNames.ResumeExecution => Success(responses.GetProperty("resume")),
                var value when value == AiSdkOperationNames.ReplayExecution => Success(responses.GetProperty("replay")),
                _ => throw new InvalidOperationException($"Unexpected operation '{operation}'.")
            });
            var client = new AiSdkClient(transport);

            await client.PublishPipelineAsync(new AiSdkPipelinePublicationRequest
            {
                Definition = new AiSdkPipelineDefinition { Name = "minimal" }
            });
            await client.SubmitExecutionAsync(new AiSdkExecutionSubmissionRequest
            {
                PublicationRef = "pub-minimal",
                Input = null
            });
            await client.CancelExecutionAsync(
                "exec-minimal",
                new AiSdkExecutionCancellationRequest());
            await client.PauseExecutionAsync("exec-minimal");
            await client.ResumeExecutionAsync("exec-minimal");
            await client.ReplayExecutionAsync("exec-minimal");

            var expected = root.GetProperty("minimalRequests");
            AssertRequestMatches(expected.GetProperty("publish"), transport.Requests[0]);
            AssertRequestMatches(expected.GetProperty("submit"), transport.Requests[1]);
            AssertRequestMatches(expected.GetProperty("cancel"), transport.Requests[2]);
            AssertRequestMatches(expected.GetProperty("pause"), transport.Requests[3]);
            AssertRequestMatches(expected.GetProperty("resume"), transport.Requests[4]);
            AssertRequestMatches(expected.GetProperty("replay"), transport.Requests[5]);
        }

        [Fact]
        public void Protocol_Schema_And_Enum_Literals_Match_Shared_Fixture()
        {
            using var fixture = LoadFixture();
            var root = fixture.RootElement;

            Assert.Equal(root.GetProperty("protocolVersion").GetInt32(), AiSdkProtocolVersions.Current);

            var operations = root.GetProperty("operations");
            Assert.Equal(operations.GetProperty("publishPipeline").GetString(), AiSdkOperationNames.PublishPipeline);
            Assert.Equal(operations.GetProperty("submitExecution").GetString(), AiSdkOperationNames.SubmitExecution);
            Assert.Equal(operations.GetProperty("observeExecution").GetString(), AiSdkOperationNames.ObserveExecution);
            Assert.Equal(operations.GetProperty("watchExecution").GetString(), AiSdkOperationNames.WatchExecution);
            Assert.Equal(operations.GetProperty("getExecutionResult").GetString(), AiSdkOperationNames.GetExecutionResult);
            Assert.Equal(operations.GetProperty("cancelExecution").GetString(), AiSdkOperationNames.CancelExecution);
            Assert.Equal(operations.GetProperty("pauseExecution").GetString(), AiSdkOperationNames.PauseExecution);
            Assert.Equal(operations.GetProperty("resumeExecution").GetString(), AiSdkOperationNames.ResumeExecution);
            Assert.Equal(operations.GetProperty("submitExecutionInput").GetString(), AiSdkOperationNames.SubmitExecutionInput);
            Assert.Equal(operations.GetProperty("replayExecution").GetString(), AiSdkOperationNames.ReplayExecution);

            var schemas = root.GetProperty("schemaVersions");
            Assert.Equal(schemas.GetProperty("pipelineDefinition").GetInt32(), AiSdkSchemaVersions.PipelineDefinition);
            Assert.Equal(schemas.GetProperty("pipelinePublicationRequest").GetInt32(), AiSdkSchemaVersions.PipelinePublicationRequest);
            Assert.Equal(schemas.GetProperty("pipelinePublicationResponse").GetInt32(), AiSdkSchemaVersions.PipelinePublicationResponse);
            Assert.Equal(schemas.GetProperty("executionSubmissionRequest").GetInt32(), AiSdkSchemaVersions.ExecutionSubmissionRequest);
            Assert.Equal(schemas.GetProperty("executionSubmissionResponse").GetInt32(), AiSdkSchemaVersions.ExecutionSubmissionResponse);
            Assert.Equal(schemas.GetProperty("executionObservation").GetInt32(), AiSdkSchemaVersions.ExecutionObservation);
            Assert.Equal(schemas.GetProperty("executionResult").GetInt32(), AiSdkSchemaVersions.ExecutionResult);
            Assert.Equal(schemas.GetProperty("executionCancellationRequest").GetInt32(), AiSdkSchemaVersions.ExecutionCancellationRequest);
            Assert.Equal(schemas.GetProperty("executionCancellationResponse").GetInt32(), AiSdkSchemaVersions.ExecutionCancellationResponse);
            Assert.Equal(schemas.GetProperty("executionWatchRequest").GetInt32(), AiSdkSchemaVersions.ExecutionWatchRequest);
            Assert.Equal(schemas.GetProperty("executionWatchEvent").GetInt32(), AiSdkSchemaVersions.ExecutionWatchEvent);
            Assert.Equal(schemas.GetProperty("executionWatchResyncRequired").GetInt32(), AiSdkSchemaVersions.ExecutionWatchResyncRequired);
            Assert.Equal(schemas.GetProperty("executionControlRequest").GetInt32(), AiSdkSchemaVersions.ExecutionControlRequest);
            Assert.Equal(schemas.GetProperty("executionControlResponse").GetInt32(), AiSdkSchemaVersions.ExecutionControlResponse);
            Assert.Equal(schemas.GetProperty("executionInputSubmissionRequest").GetInt32(), AiSdkSchemaVersions.ExecutionInputSubmissionRequest);
            Assert.Equal(schemas.GetProperty("executionReplayRequest").GetInt32(), AiSdkSchemaVersions.ExecutionReplayRequest);
            Assert.Equal(schemas.GetProperty("executionReplayResponse").GetInt32(), AiSdkSchemaVersions.ExecutionReplayResponse);

            var enums = root.GetProperty("enumValues");
            Assert.Equal(ReadStrings(enums, "executionMode"), Enum.GetNames<AiSdkExecutionMode>());
            Assert.Equal(ReadStrings(enums, "invocationKind"), Enum.GetNames<AiSdkInvocationKind>());
            Assert.Equal(ReadStrings(enums, "publicationFunctionKind"), Enum.GetNames<AiSdkPublicationFunctionKind>());
            Assert.Equal(ReadStrings(enums, "publicationDependencyPackageKind"), Enum.GetNames<AiSdkPublicationDependencyPackageKind>());
            Assert.Equal(ReadStrings(enums, "executionStatus"), Enum.GetNames<AiSdkExecutionStatus>());
            Assert.Equal(ReadStrings(enums, "executionStepStatus"), Enum.GetNames<AiSdkExecutionStepStatus>());
            Assert.Equal(ReadStrings(enums, "executionWatchChannel"), Enum.GetNames<AiSdkExecutionWatchChannel>());
            Assert.Equal(ReadStrings(enums, "executionWatchEventKind"), Enum.GetNames<AiSdkExecutionWatchEventKind>());
            Assert.Equal(ReadStrings(enums, "executionWatchResyncReason"), Enum.GetNames<AiSdkExecutionWatchResyncReason>());
            Assert.Equal(ReadStrings(enums, "executionControlOperation"), Enum.GetNames<AiSdkExecutionControlOperation>());
            Assert.Equal(ReadStrings(enums, "executionControlStatus"), Enum.GetNames<AiSdkExecutionControlStatus>());
            Assert.Equal(ReadStrings(enums, "executionControlAction"), Enum.GetNames<AiSdkExecutionControlAction>());
        }

        [Fact]
        public void Watch_Contracts_Roundtrip_Shared_Fixture()
        {
            using var fixture = LoadFixture();
            var watch = fixture.RootElement.GetProperty("watchContracts");

            AssertRoundtrip<AiSdkExecutionWatchRequest>(watch.GetProperty("request"));
            AssertRoundtrip<AiSdkExecutionWatchEvent>(watch.GetProperty("snapshot"));
            AssertRoundtrip<AiSdkExecutionWatchEvent>(watch.GetProperty("event"));
            AssertRoundtrip<AiSdkExecutionWatchEvent>(watch.GetProperty("resyncRequired"));
        }

        [Fact]
        public void Response_Models_Roundtrip_Shared_Fixture()
        {
            using var fixture = LoadFixture();
            var responses = fixture.RootElement.GetProperty("responses");

            AssertRoundtrip<AiSdkPipelinePublicationResponse>(responses.GetProperty("publish"));
            AssertRoundtrip<AiSdkExecutionSubmissionResponse>(responses.GetProperty("submit"));
            AssertRoundtrip<AiSdkExecutionObservation>(responses.GetProperty("observe"));
            AssertRoundtrip<AiSdkExecutionResult>(responses.GetProperty("result"));
            AssertRoundtrip<AiSdkExecutionResult>(responses.GetProperty("failedResult"));
            AssertRoundtrip<AiSdkExecutionCancellationResponse>(responses.GetProperty("cancel"));
            AssertRoundtrip<AiSdkExecutionControlResponse>(responses.GetProperty("pause"));
            AssertRoundtrip<AiSdkExecutionControlResponse>(responses.GetProperty("resume"));
            AssertRoundtrip<AiSdkExecutionControlResponse>(responses.GetProperty("submitInput"));
            AssertRoundtrip<AiSdkExecutionReplayResponse>(responses.GetProperty("replay"));
        }

        [Fact]
        public void Normalized_Error_Kind_Set_Matches_Shared_Fixture()
        {
            using var fixture = LoadFixture();
            var expected = ReadStrings(fixture.RootElement, "errorKinds");
            var actual = Enum.GetValues<AiSdkErrorKind>()
                .Select(ToCanonicalErrorKind)
                .ToArray();

            Assert.Equal(expected, actual);
        }

        private static JsonDocument LoadFixture() =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "fixtures", "sdk-parity-v1.json")));

        private static T Deserialize<T>(JsonElement element) =>
            element.Deserialize<T>(JsonOptions)
            ?? throw new JsonException($"Could not deserialize parity fixture as {typeof(T).Name}.");

        private static void AssertRoundtrip<T>(JsonElement expected)
        {
            var model = Deserialize<T>(expected);
            var actual = JsonSerializer.SerializeToElement(model, JsonOptions);
            AssertJsonEqual(expected, actual);
        }

        private static void AssertRequestMatches(JsonElement expected, AiSdkTransportRequest actual)
        {
            Assert.Equal(expected.GetProperty("operation").GetString(), actual.Operation);
            Assert.Equal(AiSdkProtocolVersions.Current, actual.ProtocolVersion);
            AssertJsonEqual(expected.GetProperty("arguments"), actual.Arguments);
        }

        private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
        {
            var expectedNode = JsonNode.Parse(expected.GetRawText());
            var actualNode = JsonNode.Parse(actual.GetRawText());
            Assert.True(
                JsonNode.DeepEquals(expectedNode, actualNode),
                $"JSON differs. Expected: {expected.GetRawText()} Actual: {actual.GetRawText()}");
        }

        private static string[] ReadStrings(JsonElement parent, string propertyName) =>
            parent.GetProperty(propertyName).EnumerateArray().Select(item => item.GetString()!).ToArray();

        private static string ToCanonicalErrorKind(AiSdkErrorKind kind) => kind switch
        {
            AiSdkErrorKind.Transport => "transport",
            AiSdkErrorKind.Authentication => "authentication",
            AiSdkErrorKind.Authorization => "authorization",
            AiSdkErrorKind.InvalidRequest => "invalid_request",
            AiSdkErrorKind.UnsupportedSchema => "unsupported_schema",
            AiSdkErrorKind.NotFound => "not_found",
            AiSdkErrorKind.Conflict => "conflict",
            AiSdkErrorKind.ResultUnavailable => "result_unavailable",
            AiSdkErrorKind.RemoteFailure => "remote_failure",
            AiSdkErrorKind.InvalidResponse => "invalid_response",
            AiSdkErrorKind.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SDK error kind.")
        };

        private static AiSdkTransportResponse Success(JsonElement element) =>
            AiSdkTransportResponse.Success(element.Clone());

        private sealed class RecordingTransport : IAiSdkTransport
        {
            private readonly Func<string, AiSdkTransportResponse> _response;

            public RecordingTransport(Func<string, AiSdkTransportResponse> response)
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
                return ValueTask.FromResult(_response(request.Operation));
            }
        }
    }
}
