using System.Text.Json;

namespace Multiplexed.AI.Runtime.Invocation
{
    /// <summary>
    /// Reads one configured policy-family declaration using the shared casing and
    /// deserialization rules. Policy-family merge/precedence semantics remain with
    /// each binding resolver.
    /// </summary>
    internal static class AiPolicyDeclarationReader
    {
        internal static bool TryRead<TDefinition>(
            IReadOnlyDictionary<string, object?> config,
            string configKey,
            string ambiguousMessage,
            string invalidMessage,
            out TDefinition? definition)
            where TDefinition : class
        {
            ArgumentNullException.ThrowIfNull(config);
            if (string.IsNullOrWhiteSpace(configKey)) throw new ArgumentException("Policy configuration key is required.", nameof(configKey));

            var matches = config
                .Where(pair => pair.Key.Equals(configKey, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length > 1)
            {
                throw new InvalidOperationException(ambiguousMessage);
            }

            if (matches.Length == 0)
            {
                definition = null;
                return false;
            }

            if (matches[0].Value is null)
            {
                definition = null;
                return true;
            }

            definition = matches[0].Value as TDefinition
                ?? JsonSerializer.Deserialize<TDefinition>(JsonSerializer.Serialize(matches[0].Value))
                ?? throw new InvalidOperationException(invalidMessage);

            return true;
        }
    }
}
