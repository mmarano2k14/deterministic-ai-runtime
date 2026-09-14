using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Mcp;

namespace Multiplexed.AI.Runtime.Invocation.Mcp
{
    /// <summary>
    /// Pure server-side effect identity and intent hashing. Object keys use ordinal order;
    /// array order and JSON number spellings are preserved. This is a versioned local
    /// canonicalization contract, not RFC 8785 and not durable effect deduplication.
    /// </summary>
    public static class AiMcpEffectIdentities
    {
        public const int RequestSchemaVersion = 2;
        public const int EffectSchemaVersion = 1;
        private static readonly UTF8Encoding Utf8 = new(false, true);

        /// <summary>
        /// Creates metadata from trusted routing and already-resolved arguments. The
        /// attempt id, deadline, worker, claim and previous Effect value are excluded.
        /// One DAG execution/call site denotes one logical effect; an explicit new
        /// business action needs a distinct execution or call site, not a new retry id.
        /// </summary>
        public static AiMcpEffectIdentity Create(AiMcpToolRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Context);
            var context = request.Context;
            RequireIdentifier(context.TenantId);
            RequireIdentifier(context.TenantGroupId);
            RequireIdentifier(context.ExecutionId);
            RequireIdentifier(context.StepName);
            RequireIdentifier(context.PipelineName);
            RequireIdentifier(context.StepKey);
            if (context.PipelineVersion is not null) RequireIdentifier(context.PipelineVersion);
            RequireIdentifier(request.ConnectionRef);
            RequireIdentifier(request.ConnectionRevision);
            RequireIdentifier(request.Tool);
            if (request.Arguments.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("MCP effect arguments must be a JSON object.");

            // Reuse the MCP boundary's JSON size/depth/duplicate-property checks. No
            // runtime state, RBAC snapshot or secret configuration is serialized here.
            var arguments = AiMcpToolJson.CopyResponse(request.Arguments);
            var scope = JsonSerializer.SerializeToElement(new
            {
                schema = "mcp-effect/v1",
                tenantId = context.TenantId,
                tenantGroupId = context.TenantGroupId,
                executionId = context.ExecutionId,
                stepName = context.StepName
            });
            var effectId = "mcp-effect-v1-" + HashCanonical(scope);
            var intent = JsonSerializer.SerializeToElement(new
            {
                schema = "mcp-intent/v1",
                effectId,
                context,
                connectionRef = request.ConnectionRef,
                connectionRevision = request.ConnectionRevision,
                tool = request.Tool,
                arguments
            });
            return new(EffectSchemaVersion, effectId, "sha256:" + HashCanonical(intent));
        }

        /// <summary>
        /// Validates the versioned internal envelope before network use. Historical
        /// version 1 requests remain readable only without effect metadata. Production
        /// step adapters emit version 2; its effect metadata cannot be omitted or changed.
        /// </summary>
        public static void ValidateRequest(AiMcpToolRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Context);
            if (string.IsNullOrWhiteSpace(request.RequestId) ||
                string.IsNullOrWhiteSpace(request.Context.TenantId) ||
                string.IsNullOrWhiteSpace(request.Context.TenantGroupId) ||
                string.IsNullOrWhiteSpace(request.ConnectionRef) ||
                string.IsNullOrWhiteSpace(request.ConnectionRevision) ||
                string.IsNullOrWhiteSpace(request.Tool) ||
                request.Arguments.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Invalid outbound MCP request envelope.");

            if (request.SchemaVersion == 1 && request.Effect is null) return;
            if (request.SchemaVersion != RequestSchemaVersion || request.Effect is null)
                throw new InvalidOperationException("MCP effect metadata does not match the request schema version.");
            if (request.Effect != Create(request))
                throw new InvalidOperationException("MCP effect identity or request digest does not match the outbound intent.");
        }

        private static void RequireIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
                throw new InvalidOperationException("MCP effect identity requires bounded nonempty routing identifiers.");
            // Reject invalid UTF-16 instead of silently replacing bytes in a hash key.
            try { _ = Utf8.GetByteCount(value); }
            catch (EncoderFallbackException exception)
            { throw new InvalidOperationException("Invalid Unicode in MCP effect identity.", exception); }
        }

        private static string HashCanonical(JsonElement value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteCanonical(writer, value);
                writer.Flush();
            }
            return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        WriteCanonical(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    // Decode/re-encode escaped strings; spelling such as \u0061 must
                    // not change a logical string's hash. No Unicode normalization.
                    var text = value.GetString()!;
                    try { _ = Utf8.GetByteCount(text); }
                    catch (EncoderFallbackException exception)
                    { throw new InvalidOperationException("Invalid Unicode in MCP effect intent.", exception); }
                    writer.WriteStringValue(text);
                    break;
                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }
}
