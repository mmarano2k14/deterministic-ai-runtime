using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Protocol;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Protocol
{
    /// <summary>Locks the external SDK protocol, retry and transport-response invariants.</summary>
    public sealed class AiSdkProtocolFoundationTests
    {
        [Fact]
        public void Protocol_Exposes_Exact_Public_Operations_Once()
        {
            Assert.Equal(1, AiSdkProtocol.Version);
            Assert.Equal(
                new[]
                {
                    AiSdkOperationNames.PublishPipeline,
                    AiSdkOperationNames.SubmitExecution,
                    AiSdkOperationNames.ObserveExecution,
                    AiSdkOperationNames.GetExecutionResult,
                    AiSdkOperationNames.CancelExecution
                },
                AiSdkProtocol.Operations.Select(operation => operation.Name).ToArray());
            Assert.Equal(AiSdkProtocol.Operations.Count, AiSdkProtocol.Operations.Select(operation => operation.Name).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void Automatic_Transport_Retry_Is_Restricted_To_Read_Operations()
        {
            Assert.Equal(AiSdkTransportRetryMode.Never, AiSdkProtocol.GetOperation(AiSdkOperationNames.PublishPipeline).TransportRetryMode);
            Assert.Equal(AiSdkTransportRetryMode.Never, AiSdkProtocol.GetOperation(AiSdkOperationNames.SubmitExecution).TransportRetryMode);
            Assert.Equal(AiSdkTransportRetryMode.SafeRead, AiSdkProtocol.GetOperation(AiSdkOperationNames.ObserveExecution).TransportRetryMode);
            Assert.Equal(AiSdkTransportRetryMode.SafeRead, AiSdkProtocol.GetOperation(AiSdkOperationNames.GetExecutionResult).TransportRetryMode);
            Assert.Equal(AiSdkTransportRetryMode.Never, AiSdkProtocol.GetOperation(AiSdkOperationNames.CancelExecution).TransportRetryMode);
        }

        [Fact]
        public void Transport_Request_Defaults_To_Current_Protocol_Version()
        {
            using var document = JsonDocument.Parse("{\"executionId\":\"execution-1\"}");
            var request = new AiSdkTransportRequest
            {
                Operation = AiSdkOperationNames.ObserveExecution,
                Arguments = document.RootElement.Clone()
            };

            Assert.Equal(AiSdkProtocolVersions.Current, request.ProtocolVersion);
            Assert.Equal("execution-1", request.Arguments.GetProperty("executionId").GetString());
        }

        [Fact]
        public void Transport_Response_Cannot_Report_Result_And_Error_Together()
        {
            using var document = JsonDocument.Parse("{\"status\":\"Running\"}");
            var success = AiSdkTransportResponse.Success(document.RootElement.Clone());
            var failure = AiSdkTransportResponse.Failure(new AiSdkError
            {
                Kind = AiSdkErrorKind.Transport,
                Code = "transport_failure",
                Message = "Connection failed.",
                IsRetryable = true
            });

            Assert.True(success.IsSuccess);
            Assert.NotNull(success.Result);
            Assert.Null(success.Error);
            Assert.False(failure.IsSuccess);
            Assert.Null(failure.Result);
            Assert.NotNull(failure.Error);
        }
    }
}
