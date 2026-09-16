using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Control
{
    /// <summary>Cancellation is cooperative and request acceptance is not confused with terminal completion.</summary>
    public sealed class AiSdkExecutionControlWireContractTests
    {
        [Fact]
        public void Cancellation_Response_Keeps_Request_And_Execution_State_Separate()
        {
            var response = new AiSdkExecutionCancellationResponse
            {
                ExecutionId = "execution-42",
                CancellationRequested = true,
                Status = AiSdkExecutionStatus.Running,
                RequestedAtUtc = DateTimeOffset.Parse("2026-09-16T08:01:00Z"),
                CorrelationId = "cancel-17"
            };

            var json = JsonSerializer.Serialize(response);
            var restored = JsonSerializer.Deserialize<AiSdkExecutionCancellationResponse>(json)!;

            Assert.True(restored.CancellationRequested);
            Assert.Equal(AiSdkExecutionStatus.Running, restored.Status);
            Assert.Contains("\"status\":\"Running\"", json, StringComparison.Ordinal);
        }

        [Fact]
        public void Cancellation_Request_Does_Not_Carry_Server_Ownership_Fields()
        {
            var request = new AiSdkExecutionCancellationRequest
            {
                Reason = "operator-request",
                CorrelationId = "cancel-17"
            };

            var json = JsonSerializer.Serialize(request);

            Assert.DoesNotContain("lease", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("epoch", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("worker", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runtimeInstance", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
