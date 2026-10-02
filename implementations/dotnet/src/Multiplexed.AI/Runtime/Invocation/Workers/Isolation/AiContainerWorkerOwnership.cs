namespace Multiplexed.AI.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// Server-owned physical container ownership markers used only for launch attestation and stale
    /// container cleanup. The scope is deployment configuration and must never contain tenant or run identity.
    /// </summary>
    internal static class AiContainerWorkerOwnership
    {
        internal const string ManagedLabel = "multiplexed.ai.hosted-worker";
        internal const string OwnerScopeLabel = "multiplexed.ai.owner-scope";
        internal const string ManagedLabelValue = "1";

        internal static void ValidateOwnerScope(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
                !char.IsAsciiLetterOrDigit(value[0]) ||
                value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
            {
                throw new ArgumentException(
                    "Container owner scope must be a bounded opaque server-owned identifier.",
                    nameof(value));
            }
        }

        internal static string ManagedLabelArgument =>
            $"--label={ManagedLabel}={ManagedLabelValue}";

        internal static string OwnerScopeLabelArgument(string ownerScope)
        {
            ValidateOwnerScope(ownerScope);
            return $"--label={OwnerScopeLabel}={ownerScope}";
        }

        internal static bool IsManagedContainerName(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Length <= 63 &&
            value.StartsWith("multiplexed-ai-", StringComparison.Ordinal) &&
            char.IsAsciiLetterOrDigit(value[0]) &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }
}
