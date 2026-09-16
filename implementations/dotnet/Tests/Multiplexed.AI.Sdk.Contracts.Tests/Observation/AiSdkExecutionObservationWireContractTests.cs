using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Observation
{
    /// <summary>Observation contracts expose logical progress without distributed claim or recovery ownership.</summary>
    public sealed class AiSdkExecutionObservationWireContractTests
    {
        [Fact]
        public void Observation_Roundtrip_Uses_String_Statuses_And_Stable_Step_Identity()
        {
            var observation = new AiSdkExecutionObservation
            {
                ExecutionId = "execution-42",
                PublicationRef = "publication/orders/v7@sha256:abc",
                PipelineName = "orders",
                PipelineVersion = "v7",
                Status = AiSdkExecutionStatus.Running,
                CreatedAtUtc = DateTimeOffset.Parse("2026-09-16T07:50:00Z"),
                UpdatedAtUtc = DateTimeOffset.Parse("2026-09-16T07:51:00Z"),
                Steps =
                [
                    new AiSdkExecutionStepObservation
                    {
                        Name = "authorize",
                        StepKey = "step-authorize",
                        Status = AiSdkExecutionStepStatus.Completed,
                        StartedAtUtc = DateTimeOffset.Parse("2026-09-16T07:50:10Z"),
                        CompletedAtUtc = DateTimeOffset.Parse("2026-09-16T07:50:20Z")
                    },
                    new AiSdkExecutionStepObservation
                    {
                        Name = "capture",
                        StepKey = "step-capture",
                        Status = AiSdkExecutionStepStatus.WaitingForExternal,
                        StartedAtUtc = DateTimeOffset.Parse("2026-09-16T07:50:21Z")
                    }
                ]
            };

            var json = JsonSerializer.Serialize(observation);
            var restored = JsonSerializer.Deserialize<AiSdkExecutionObservation>(json)!;

            Assert.Contains("\"status\":\"Running\"", json, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"WaitingForExternal\"", json, StringComparison.Ordinal);
            Assert.Equal("step-capture", restored.Steps[1].StepKey);
            Assert.DoesNotContain("claimedBy", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("claimToken", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("leaseExpires", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("recoveryCount", json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
