using System.Text.Json.Serialization;
using Multiplexed.Abstractions.AI.Invocation;

namespace Multiplexed.Abstractions.AI.Policies
{
    /// <summary>
    /// Defines a configured AI policy entry.
    /// </summary>
    [JsonConverter(typeof(AiConfiguredPolicyDefinitionJsonConverter))]
    public sealed class AiConfiguredPolicyDefinition
    {
        /// <summary>
        /// Gets or sets the registered policy name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional policy kind.
        /// </summary>
        /// <remarks>
        /// Examples include <c>Retry</c>, <c>Retention</c>, <c>Concurrency</c>,
        /// <c>Timeout</c>, <c>Validation</c>, and <c>Routing</c>.
        /// </remarks>
        public string? Kind { get; set; }

        /// <summary>Optional policy language override; independent of the policy family.</summary>
        public string? ExecutionLanguage { get; set; }

        /// <summary>Native/custom implementation descriptor. MCP is never a policy kind.</summary>
        public AiInvocationDefinition? Invocation { get; set; }


        /// <summary>
        /// Gets or sets policy-specific configuration.
        /// </summary>
        public Dictionary<string, object?> Config { get; set; } = new();
    }
}