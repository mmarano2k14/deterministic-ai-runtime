using System.Text.Json;
using Multiplexed.Abstractions.AI.Policies;
using Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Models;

namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Policies
{
    public sealed class RuntimeAnalysisScenarioPolicyContext
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new()
            {
                PropertyNameCaseInsensitive = true
            };

        public RuntimeAnalysisSuggestedScenario Scenario { get; init; } =
            new RuntimeAnalysisSuggestedScenario();

        public string PlanKey { get; init; } = string.Empty;

        public IReadOnlyDictionary<string, AiConfiguredPolicyDefinition>
            PolicyDefinitions { get; init; } =
                new Dictionary<string, AiConfiguredPolicyDefinition>(
                    StringComparer.Ordinal);

        public AiConfiguredPolicyDefinition GetRequiredPolicyDefinition(
            string policyKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                policyKey);

            if (!PolicyDefinitions.TryGetValue(
                    policyKey,
                    out var definition))
            {
                throw new InvalidOperationException(
                    $"Configured policy definition '{policyKey}' was not supplied by the pipeline.");
            }

            return definition;
        }

        public T GetRequiredPolicyConfig<T>(
            string policyKey,
            string configKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                configKey);

            var definition = GetRequiredPolicyDefinition(
                policyKey);

            if (!definition.Config.TryGetValue(
                    configKey,
                    out var rawValue)
                || rawValue is null)
            {
                throw new InvalidOperationException(
                    $"Configured policy '{policyKey}' is missing config value '{configKey}'.");
            }

            if (rawValue is T typedValue)
            {
                return typedValue;
            }

            try
            {
                if (rawValue is JsonElement jsonElement)
                {
                    return jsonElement.Deserialize<T>(
                               SerializerOptions)
                           ?? throw new InvalidOperationException(
                               $"Configured policy '{policyKey}' value '{configKey}' deserialized to null.");
                }

                var json = JsonSerializer.Serialize(
                    rawValue,
                    SerializerOptions);

                return JsonSerializer.Deserialize<T>(
                           json,
                           SerializerOptions)
                       ?? throw new InvalidOperationException(
                           $"Configured policy '{policyKey}' value '{configKey}' deserialized to null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Configured policy '{policyKey}' value '{configKey}' cannot be converted to '{typeof(T).Name}'.",
                    exception);
            }
        }
    }
}
