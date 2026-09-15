using System.Text.Json;

namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Closed server-side wire contracts for hosted custom policy families.
    /// These contracts are family-specific and are not a universal boolean policy protocol or public SDK schema.
    /// </summary>
    public static class AiCustomPolicyFamilyContracts
    {
        public const string ConcurrencyV1 = "concurrency/v1";
        public const string RetryV1 = "retry/v1";
        public const string DelegationV1 = "delegation/v1";

        /// <summary>Validates and reads one retry/v1 response.</summary>
        public static AiRetryPolicyTransportResult ReadRetryV1(JsonElement response, string expectedRequestId)
        {
            var fields = ReadClosedObject(
                response,
                "retry",
                expectedRequestId,
                "decision", "reason", "suggestedDelayMs");

            var decision = RequiredString(fields, "decision");
            var parsedDecision = decision switch
            {
                "pass" => AiRetryPolicyTransportDecision.Pass,
                "retry" => AiRetryPolicyTransportDecision.Retry,
                "stop" => AiRetryPolicyTransportDecision.Stop,
                _ => throw Invalid("retry", "Unknown retry decision.")
            };

            var reason = OptionalReason(fields, "retry");
            if (parsedDecision == AiRetryPolicyTransportDecision.Stop && string.IsNullOrWhiteSpace(reason))
            {
                throw Invalid("retry", "A stop decision requires a reason.");
            }

            TimeSpan? suggestedDelay = null;
            if (fields.TryGetValue("suggestedDelayMs", out var delayValue))
            {
                if (parsedDecision != AiRetryPolicyTransportDecision.Retry ||
                    delayValue.ValueKind != JsonValueKind.Number ||
                    !delayValue.TryGetInt32(out var delayMs) || delayMs < 0 || delayMs > 300000)
                {
                    throw Invalid("retry", "suggestedDelayMs is allowed only on retry, as an integer from 0 through 300000.");
                }

                suggestedDelay = TimeSpan.FromMilliseconds(delayMs);
            }

            return new AiRetryPolicyTransportResult(parsedDecision, reason, suggestedDelay);
        }

        /// <summary>Validates and reads one delegation/v1 response.</summary>
        public static AiDelegationPolicyTransportResult ReadDelegationV1(JsonElement response, string expectedRequestId)
        {
            var fields = ReadClosedObject(
                response,
                "delegation",
                expectedRequestId,
                "decision", "reason");

            var decision = RequiredString(fields, "decision");
            var parsedDecision = decision switch
            {
                "approve" => AiDelegationPolicyTransportDecision.Approve,
                "deny" => AiDelegationPolicyTransportDecision.Deny,
                _ => throw Invalid("delegation", "Unknown delegation decision.")
            };

            var reason = OptionalReason(fields, "delegation");
            if (parsedDecision == AiDelegationPolicyTransportDecision.Deny && string.IsNullOrWhiteSpace(reason))
            {
                throw Invalid("delegation", "A denial requires a reason.");
            }

            return new AiDelegationPolicyTransportResult(parsedDecision, reason);
        }

        private static Dictionary<string, JsonElement> ReadClosedObject(
            JsonElement response,
            string family,
            string expectedRequestId,
            params string[] familyFields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedRequestId);
            if (response.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(family, "The response must be an object.");
            }

            var allowed = new HashSet<string>(familyFields, StringComparer.Ordinal)
            {
                "schemaVersion", "requestId", "policyKind"
            };
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in response.EnumerateObject())
            {
                if (!allowed.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value))
                {
                    throw Invalid(family, "Unknown or duplicate response field.");
                }
            }

            if (!fields.TryGetValue("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
                RequiredString(fields, "requestId") != expectedRequestId ||
                RequiredString(fields, "policyKind") != family)
            {
                throw Invalid(family, "Schema, family or request correlation does not match.");
            }

            return fields;
        }

        private static string RequiredString(IReadOnlyDictionary<string, JsonElement> fields, string name)
        {
            if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw new InvalidOperationException($"Missing or invalid {name}.");
            }

            return value.GetString()!;
        }

        private static string? OptionalReason(IReadOnlyDictionary<string, JsonElement> fields, string family)
        {
            if (!fields.TryGetValue("reason", out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw Invalid(family, "reason must be a string or null.");
            }

            var reason = value.GetString();
            if (reason?.Length > 2048)
            {
                throw Invalid(family, "reason exceeds 2048 characters.");
            }

            return reason;
        }

        private static InvalidOperationException Invalid(string family, string detail) =>
            new($"Invalid custom {family} policy response. {detail}");
    }

    public enum AiRetryPolicyTransportDecision
    {
        Pass = 0,
        Retry = 1,
        Stop = 2
    }

    public sealed record AiRetryPolicyTransportResult(
        AiRetryPolicyTransportDecision Decision,
        string? Reason,
        TimeSpan? SuggestedDelay);

    public enum AiDelegationPolicyTransportDecision
    {
        Approve = 0,
        Deny = 1
    }

    public sealed record AiDelegationPolicyTransportResult(
        AiDelegationPolicyTransportDecision Decision,
        string? Reason);
}
