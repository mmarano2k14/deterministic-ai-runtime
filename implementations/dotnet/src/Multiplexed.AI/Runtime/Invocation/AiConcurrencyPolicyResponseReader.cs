using System.Text.Json;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.AI.Abstractions.AI.Policies;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Closed concurrency/v1 mapping. Missing, duplicate, unknown or incorrectly typed
    /// fields fail before the common policy engine can observe an allowed result.
    /// This is not a universal boolean policy contract.
    /// </summary>
    internal static class AiConcurrencyPolicyResponseReader
    {
        public static AiPolicyResult Read(JsonElement response, string expectedRequestId)
        {
            if (response.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The response must be an object.");
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in response.EnumerateObject())
            {
                if (property.Name is not ("schemaVersion" or "requestId" or "policyKind" or "decision" or "reason" or "retryAfterMs") ||
                    !fields.TryAdd(property.Name, property.Value))
                {
                    throw Invalid("Unknown or duplicate response field.");
                }
            }

            if (!fields.TryGetValue("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var versionNumber) || versionNumber != 1 ||
                RequiredString(fields, "requestId") != expectedRequestId ||
                RequiredString(fields, "policyKind") != "concurrency")
            {
                throw Invalid("Schema, family or request correlation does not match.");
            }

            var decision = RequiredString(fields, "decision");
            if (decision is not ("allow" or "deny")) throw Invalid("Unknown concurrency decision.");
            string? reason = null;
            if (fields.TryGetValue("reason", out var reasonValue) && reasonValue.ValueKind != JsonValueKind.Null)
            {
                if (reasonValue.ValueKind != JsonValueKind.String) throw Invalid("reason must be a string or null.");
                reason = reasonValue.GetString();
                if (reason?.Length > 2048) throw Invalid("reason exceeds 2048 characters.");
            }
            if (decision == "deny" && string.IsNullOrWhiteSpace(reason)) throw Invalid("A denial requires a reason.");

            TimeSpan? retryAfter = null;
            if (fields.TryGetValue("retryAfterMs", out var retryValue))
            {
                if (decision != "deny" || retryValue.ValueKind != JsonValueKind.Number ||
                    !retryValue.TryGetInt32(out var retryMs) || retryMs < 0 || retryMs > 300000)
                {
                    throw Invalid("retryAfterMs is allowed only on deny, as an integer from 0 through 300000.");
                }
                retryAfter = TimeSpan.FromMilliseconds(retryMs);
            }

            var outcome = new AiConcurrencyPolicyOutcome
            {
                IsAllowed = decision == "allow", Reason = reason, RetryAfter = retryAfter
            };
            return outcome.IsAllowed
                ? AiPolicyResult.Success(outcome, reason)
                : AiPolicyResult.Block(outcome, reason);
        }

        private static string RequiredString(IReadOnlyDictionary<string, JsonElement> fields, string name)
        {
            if (!fields.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                throw Invalid($"Missing or invalid {name}.");
            }
            return value.GetString()!;
        }

        private static InvalidOperationException Invalid(string detail) =>
            new($"Invalid custom concurrency policy response. {detail}");
    }
}
