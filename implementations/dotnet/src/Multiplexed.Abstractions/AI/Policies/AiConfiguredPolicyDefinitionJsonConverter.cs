using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation;
using System.Text.Json.Serialization;

namespace Multiplexed.Abstractions.AI.Policies
{
    /// <summary>
    /// Supports backward-compatible policy definition serialization and deserialization.
    /// </summary>
    /// <remarks>
    /// Supported JSON formats:
    ///
    /// Legacy string format:
    ///
    /// <code>
    /// "policies": [
    ///   "retry.transient.default"
    /// ]
    /// </code>
    ///
    /// Preferred structured object format:
    ///
    /// <code>
    /// "policies": [
    ///   {
    ///     "name": "concurrency.scope.default",
    ///     "kind": "scope",
    ///     "config": {
    ///       "scope": "provider",
    ///       "value": "openai",
    ///       "limit": 10
    ///     }
    ///   }
    /// ]
    /// </code>
    ///
    /// Backward-compatible aliases are also supported during deserialization:
    ///
    /// <list type="bullet">
    /// <item>
    /// <description><c>key</c> is accepted as an alias for <c>name</c>.</description>
    /// </item>
    /// <item>
    /// <description><c>type</c> is accepted as a legacy alias for <c>kind</c>.</description>
    /// </item>
    /// </list>
    ///
    /// Serialization always writes the preferred fields: <c>name</c>, <c>kind</c>, and <c>config</c>.
    /// </remarks>
    public sealed class AiConfiguredPolicyDefinitionJsonConverter
        : JsonConverter<AiConfiguredPolicyDefinition>
    {
        /// <inheritdoc />
        public override AiConfiguredPolicyDefinition Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var policyName = reader.GetString();

                if (string.IsNullOrWhiteSpace(policyName))
                {
                    throw new JsonException(
                        "Policy name cannot be null or empty.");
                }

                return new AiConfiguredPolicyDefinition
                {
                    Name = policyName
                };
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException(
                    $"Unexpected token '{reader.TokenType}' while reading AI policy definition.");
            }

            using var document = JsonDocument.ParseValue(ref reader);

            var root = document.RootElement;

            var result = new AiConfiguredPolicyDefinition();

            if (root.TryGetProperty("name", out var nameElement))
            {
                result.Name = nameElement.GetString() ?? string.Empty;
            }
            else if (root.TryGetProperty("key", out var keyElement))
            {
                result.Name = keyElement.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("kind", out var kindElement))
            {
                result.Kind = kindElement.GetString();
            }
            else if (root.TryGetProperty("type", out var legacyTypeElement))
            {
                result.Kind = legacyTypeElement.GetString();
            }

            if (root.TryGetProperty("executionLanguage", out var languageElement))
            {
                if (languageElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new JsonException("Policy executionLanguage must be a string or null.");
                }
                result.ExecutionLanguage = languageElement.GetString();
            }

            if (root.TryGetProperty("invocation", out var invocationElement))
            {
                result.Invocation = JsonSerializer.Deserialize<AiInvocationDefinition>(
                    invocationElement.GetRawText(), options);
            }

            if (root.TryGetProperty("config", out var configElement))
            {
                result.Config =
                    JsonSerializer.Deserialize<Dictionary<string, object?>>(
                        configElement.GetRawText(),
                        options)
                    ?? new Dictionary<string, object?>();
            }

            if (string.IsNullOrWhiteSpace(result.Name))
            {
                throw new JsonException(
                    "AI configured policy definition requires a policy name.");
            }

            return result;
        }

        /// <inheritdoc />
        public override void Write(
            Utf8JsonWriter writer,
            AiConfiguredPolicyDefinition value,
            JsonSerializerOptions options)
        {
            ArgumentNullException.ThrowIfNull(writer);
            ArgumentNullException.ThrowIfNull(value);

            writer.WriteStartObject();

            writer.WriteString(
                "name",
                value.Name);

            if (!string.IsNullOrWhiteSpace(value.Kind))
            {
                writer.WriteString(
                    "kind",
                    value.Kind);
            }

            if (value.ExecutionLanguage is not null)
            {
                writer.WriteString("executionLanguage", value.ExecutionLanguage);
            }

            if (value.Invocation is not null)
            {
                writer.WritePropertyName("invocation");
                JsonSerializer.Serialize(writer, value.Invocation, options);
            }

            writer.WritePropertyName("config");

            JsonSerializer.Serialize(
                writer,
                value.Config,
                options);

            writer.WriteEndObject();
        }
    }
}