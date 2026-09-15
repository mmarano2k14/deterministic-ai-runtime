using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using Multiplexed.AI.Abstractions.AI.Policies;

namespace Multiplexed.AI.Tests.Runtime.Invocation
{
    /// <summary>Freezes the finite server-side family matrix and closed retry/delegation wire contracts.</summary>
    public sealed class AiCustomPolicyFamilyContractTests
    {
        [Fact]
        public void Capability_Matrix_Covers_Every_Declared_Policy_Kind_Exactly_Once()
        {
            var declared = Enum.GetValues<AiPolicyKind>();
            Assert.Equal(declared.Length, AiCustomPolicyFamilyCapabilities.All.Count);
            Assert.Equal(declared.OrderBy(value => value), AiCustomPolicyFamilyCapabilities.All.Select(value => value.Kind).OrderBy(value => value));
            Assert.Equal(declared.Length, AiCustomPolicyFamilyCapabilities.All.Select(value => value.Kind).Distinct().Count());
        }

        [Theory]
        [InlineData(AiPolicyKind.Concurrency, AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyContracts.ConcurrencyV1)]
        [InlineData(AiPolicyKind.Retry, AiCustomPolicyFamilyAvailability.Hosted, AiCustomPolicyFamilyContracts.RetryV1)]
        [InlineData(AiPolicyKind.Delegation, AiCustomPolicyFamilyAvailability.ContractDefined, AiCustomPolicyFamilyContracts.DelegationV1)]
        [InlineData(AiPolicyKind.Retention, AiCustomPolicyFamilyAvailability.NativeOnly, null)]
        [InlineData(AiPolicyKind.Timeout, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null)]
        [InlineData(AiPolicyKind.CircuitBreaker, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null)]
        [InlineData(AiPolicyKind.RateLimit, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null)]
        [InlineData(AiPolicyKind.Validation, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null)]
        [InlineData(AiPolicyKind.Routing, AiCustomPolicyFamilyAvailability.NoRuntimeCheckpoint, null)]
        public void Capability_Matrix_Is_Explicit(
            AiPolicyKind kind,
            AiCustomPolicyFamilyAvailability expectedAvailability,
            string? expectedContract)
        {
            var capability = AiCustomPolicyFamilyCapabilities.Get(kind);
            Assert.Equal(expectedAvailability, capability.Availability);
            Assert.Equal(expectedContract, capability.ContractId);
            Assert.Equal(expectedAvailability == AiCustomPolicyFamilyAvailability.Hosted, capability.SupportsHostedExecution);
            Assert.Equal(
                expectedAvailability is AiCustomPolicyFamilyAvailability.Hosted or AiCustomPolicyFamilyAvailability.ContractDefined,
                capability.SupportsCustomPublication);
        }

        [Fact]
        public void Unknown_Policy_Kind_Has_No_Implicit_Capability()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AiCustomPolicyFamilyCapabilities.Get((AiPolicyKind)999));
        }

        [Fact]
        public void Retry_Request_Is_Family_Specific_And_Does_Not_Export_Runtime_Authority()
        {
            using var config = JsonDocument.Parse("{\"mode\":\"transient\"}");
            var request = new AiRetryPolicyRequest(
                "request-a", "retry.custom", "Step", "charge", "python", "impl-a",
                DateTimeOffset.Parse("2026-09-14T10:00:00Z"),
                new AiRetryPolicyInput(
                    "tenant-a", "group-a", "execution-a", "pipeline-a", "charge", "charge-card",
                    1, 3, "provider timeout", "TimeoutException", DateTimeOffset.Parse("2026-09-14T09:59:59Z")),
                config.RootElement.Clone());

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
            var root = json.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("retry", root.GetProperty("policyKind").GetString());
            Assert.Equal("impl-a", root.GetProperty("implementationRef").GetString());
            Assert.False(root.TryGetProperty("retryAfter", out _));
            Assert.False(root.TryGetProperty("state", out _));
            Assert.False(root.TryGetProperty("services", out _));
            Assert.False(root.TryGetProperty("rbac", out _));
        }

        [Fact]
        public void Delegation_Request_Is_Preallocation_And_Carries_No_Child_Execution_Id()
        {
            using var config = JsonDocument.Parse("{\"region\":\"eu\"}");
            var request = new AiDelegationPolicyRequest(
                "request-a", "delegation.custom", "Step", "spawn", "typescript", "impl-a",
                DateTimeOffset.Parse("2026-09-14T10:00:00Z"),
                new AiDelegationPolicyInput(
                    "tenant-a", "group-a", "parent-a", "spawn", "child-a", "1", "child-key", 0),
                config.RootElement.Clone());

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
            var root = json.RootElement;
            Assert.Equal("delegation", root.GetProperty("policyKind").GetString());
            Assert.Equal("child-key", root.GetProperty("context").GetProperty("childInvocationKey").GetString());
            Assert.False(root.GetProperty("context").TryGetProperty("childExecutionId", out _));
            Assert.False(root.GetProperty("context").TryGetProperty("continuation", out _));
        }

        [Theory]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"retry\",\"decision\":\"pass\"}", AiRetryPolicyTransportDecision.Pass, null)]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"retry\",\"decision\":\"retry\",\"reason\":\"transient\",\"suggestedDelayMs\":1500}", AiRetryPolicyTransportDecision.Retry, 1500)]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"retry\",\"decision\":\"stop\",\"reason\":\"permanent\"}", AiRetryPolicyTransportDecision.Stop, null)]
        public void Retry_V1_Uses_Closed_Family_Specific_Decisions(
            string json,
            AiRetryPolicyTransportDecision expected,
            int? expectedDelayMs)
        {
            using var document = JsonDocument.Parse(json);
            var result = AiCustomPolicyFamilyContracts.ReadRetryV1(document.RootElement, "r");
            Assert.Equal(expected, result.Decision);
            Assert.Equal(expectedDelayMs, result.SuggestedDelay.HasValue ? (int)result.SuggestedDelay.Value.TotalMilliseconds : null);
        }

        [Theory]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"retry\",\"decision\":\"stop\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"retry\",\"decision\":\"pass\",\"suggestedDelayMs\":1}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"other\",\"policyKind\":\"retry\",\"decision\":\"retry\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"concurrency\",\"decision\":\"retry\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"retry\",\"decision\":\"retry\",\"allow\":true}")]
        public void Retry_V1_Fails_Closed(string json)
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<InvalidOperationException>(() => AiCustomPolicyFamilyContracts.ReadRetryV1(document.RootElement, "r"));
        }

        [Theory]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"delegation\",\"decision\":\"approve\"}", AiDelegationPolicyTransportDecision.Approve)]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"delegation\",\"decision\":\"deny\",\"reason\":\"not permitted\"}", AiDelegationPolicyTransportDecision.Deny)]
        public void Delegation_V1_Uses_Approve_Or_Deny(string json, AiDelegationPolicyTransportDecision expected)
        {
            using var document = JsonDocument.Parse(json);
            Assert.Equal(expected, AiCustomPolicyFamilyContracts.ReadDelegationV1(document.RootElement, "r").Decision);
        }

        [Theory]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"delegation\",\"decision\":\"deny\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"delegation\",\"decision\":\"allow\"}")]
        [InlineData("{\"schemaVersion\":1,\"requestId\":\"r\",\"policyKind\":\"delegation\",\"decision\":\"approve\",\"retryAfterMs\":1}")]
        public void Delegation_V1_Fails_Closed(string json)
        {
            using var document = JsonDocument.Parse(json);
            Assert.Throws<InvalidOperationException>(() => AiCustomPolicyFamilyContracts.ReadDelegationV1(document.RootElement, "r"));
        }
    }
}
