using System.Reflection;
using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;

namespace Multiplexed.AI.Sdk.Contracts.Tests.Executions
{
    /// <summary>Public execution contracts remain JSON-portable and do not expose runtime/control-plane identity.</summary>
    public sealed class AiSdkExecutionWireContractTests
    {
        [Fact]
        public void Submission_Roundtrip_Preserves_Public_Identity_Without_Placement_Fields()
        {
            using var input = JsonDocument.Parse("{\"customerId\":\"c-17\",\"amount\":42}");
            var request = new AiSdkExecutionSubmissionRequest
            {
                PublicationRef = "publication/orders/v7@sha256:abc",
                IdempotencyKey = "client-operation-17",
                Input = input.RootElement.Clone(),
                Metadata = new Dictionary<string, string> { ["source"] = "sdk-test" },
                CorrelationId = "corr-17"
            };

            var json = JsonSerializer.Serialize(request);
            var restored = JsonSerializer.Deserialize<AiSdkExecutionSubmissionRequest>(json)!;

            Assert.Equal("publication/orders/v7@sha256:abc", restored.PublicationRef);
            Assert.Equal("client-operation-17", restored.IdempotencyKey);
            Assert.Equal("c-17", restored.Input!.Value.GetProperty("customerId").GetString());
            Assert.Equal(42, restored.Input.Value.GetProperty("amount").GetInt32());
            Assert.Equal("sdk-test", restored.Metadata["source"]);
            Assert.DoesNotContain("tenantId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runtimeInstance", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sharedRun", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localRun", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Submission_Response_Exposes_Durable_Execution_Id_Only()
        {
            var response = new AiSdkExecutionSubmissionResponse
            {
                ExecutionId = "execution-42",
                PublicationRef = "publication/orders/v7@sha256:abc",
                Status = AiSdkExecutionStatus.Pending,
                AcceptedAtUtc = DateTimeOffset.Parse("2026-09-16T07:50:00Z"),
                IdempotencyKey = "client-operation-17"
            };

            var json = JsonSerializer.Serialize(response);

            Assert.Contains("\"executionId\":\"execution-42\"", json, StringComparison.Ordinal);
            Assert.Contains("\"status\":\"Pending\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("sharedRunId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("localRunId", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runtimeInstanceId", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Terminal_Result_Is_Json_Portable_And_Sanitized()
        {
            using var output = JsonDocument.Parse("{\"approved\":true}");
            using var detail = JsonDocument.Parse("{\"field\":\"amount\"}");
            var result = new AiSdkExecutionResult
            {
                ExecutionId = "execution-42",
                Status = AiSdkExecutionStatus.Failed,
                Output = output.RootElement.Clone(),
                Failure = new AiSdkExecutionFailure
                {
                    Code = "validation.failed",
                    Message = "Execution failed validation.",
                    Details = new Dictionary<string, JsonElement>
                    {
                        ["context"] = detail.RootElement.Clone()
                    }
                },
                CompletedAtUtc = DateTimeOffset.Parse("2026-09-16T08:00:00Z")
            };

            var json = JsonSerializer.Serialize(result);
            var restored = JsonSerializer.Deserialize<AiSdkExecutionResult>(json)!;

            Assert.Equal(AiSdkExecutionStatus.Failed, restored.Status);
            Assert.True(restored.Output!.Value.GetProperty("approved").GetBoolean());
            Assert.Equal("validation.failed", restored.Failure!.Code);
            Assert.DoesNotContain("stackTrace", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workerId", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void New_Public_Areas_Do_Not_Expose_Object_ByteArrays_Or_Internal_Identity_Names()
        {
            var assembly = typeof(AiSdkExecutionSubmissionRequest).Assembly;
            var types = assembly.GetExportedTypes()
                .Where(type =>
                    type.Namespace is "Multiplexed.AI.Sdk.Contracts.Executions" or
                    "Multiplexed.AI.Sdk.Contracts.Observation" or
                    "Multiplexed.AI.Sdk.Contracts.Control")
                .ToArray();

            var forbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "SharedRunId",
                "LocalRunId",
                "RuntimeInstanceId",
                "WorkerId",
                "ClaimToken",
                "LeaseEpoch",
                "AssignmentEpoch",
                "PreferredRuntimeInstanceId"
            };

            foreach (var property in types.SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)))
            {
                Assert.NotEqual(typeof(object), property.PropertyType);
                Assert.NotEqual(typeof(byte[]), property.PropertyType);
                Assert.DoesNotContain(property.Name, forbiddenNames);
            }
        }
    }
}
