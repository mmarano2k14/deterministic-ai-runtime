using System.Text.Json;
using Multiplexed.Abstractions.AI.Steps;

namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Maps a normalized internal MCP result to the existing step result. Tool errors
    /// remain business failures; malformed envelopes throw. No remote orchestration
    /// instruction, automatic link fetch or payload-store reference is honored.
    /// </summary>
    internal static class AiMcpToolResponseReader
    {
        public static AiStepResult Read(JsonElement response, string requestId)
        {
            var root = AiMcpToolJson.CopyResponse(response);
            Require(root.ValueKind == JsonValueKind.Object, "MCP response must be an object.");
            foreach (var property in root.EnumerateObject())
            {
                Require(property.Name is "schemaVersion" or "requestId" or "isError" or "content" or "structuredContent",
                    "Unknown MCP response envelope field.");
            }
            Require(root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number &&
                schema.TryGetInt32(out var version) && version == 1, "Unsupported MCP response schema.");
            Require(root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String &&
                id.GetString() == requestId, "MCP response correlation mismatch.");
            Require(root.TryGetProperty("isError", out var error) &&
                error.ValueKind is JsonValueKind.True or JsonValueKind.False, "MCP response requires an explicit isError boolean.");
            Require(root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array,
                "MCP response requires a content array.");

            var text = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                Require(block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out _),
                    "MCP content blocks require a type.");
                var type = block.GetProperty("type");
                Require(type.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(type.GetString()),
                    "MCP content block type must be a non-empty string.");
                if (type.GetString() == "text")
                {
                    Require(block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String,
                        "MCP text content requires text.");
                    text.Add(value.GetString()!);
                }
                // Other block kinds remain opaque JSON. Their protocol-specific schema
                // is a real MCP transport responsibility; no URI/resource is followed.
            }

            JsonElement? structured = null;
            if (root.TryGetProperty("structuredContent", out var structuredValue))
            {
                Require(structuredValue.ValueKind == JsonValueKind.Object, "MCP structuredContent must be an object when present.");
                structured = structuredValue;
            }
            var output = text.Count == 0 ? null : string.Join("\n", text);
            object primary = structured.HasValue ? structured.Value : content;
            var data = new Dictionary<string, object?>(StringComparer.Ordinal) { ["content"] = content };
            if (structured.HasValue) data["structuredContent"] = structured.Value;
            if (!error.GetBoolean()) return AiStepResult.Ok(primary, output, data);

            var result = AiStepResult.Fail(string.IsNullOrWhiteSpace(output) ? "MCP tool reported an error." : output,
                value: primary, data: data);
            result.Output = output;
            return result;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
