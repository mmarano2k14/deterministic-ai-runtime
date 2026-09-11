using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>Closed wire envelope. A result cannot introduce lifecycle, retry, Park or authorization commands.</summary>
    public static class AiWorkerInvocationProtocol
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public static byte[] EncodeRequest(AiWorkerInvocationRequest request, int maxBytes)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (maxBytes is < 1024 or > 67108864) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (request.ProtocolVersion != 1 || request.Type != "invoke" || request.Epoch < 1 ||
                request.Generation < 0 || request.DeadlineUtc.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("Invalid worker request identity or protocol.");
            foreach (var value in new[] { request.RequestId, request.OperationId, request.EffectIdempotencyKey,
                request.WorkerId, request.TenantId, request.ExecutionId, request.StepName }) AiPublicationJson.Text(value, "WorkerIdentity");
            ArgumentNullException.ThrowIfNull(request.Code);
            if (request.Code.Target.ExecutionLanguage != request.Code.Runtime.ExecutionLanguage)
                throw new InvalidOperationException("The worker runtime does not match its pinned invocation language.");
            _ = AiDurableInvocationJson.Normalize(request.Inputs.GetRawText(), true);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
            if (bytes.Length > maxBytes) throw new InvalidOperationException("Worker request exceeds its inline byte limit.");
            return bytes;
        }

        public static AiWorkerInvocationFrame ReadFrame(string json, AiWorkerInvocationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(json);
            if (Utf8.GetByteCount(json) > 1048576) throw new InvalidOperationException("Worker frame is too large.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 36 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Worker frame must be an object.");
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!fields.TryAdd(property.Name, property.Value)) throw new InvalidOperationException("Duplicate worker frame field.");
            string Text(string name) => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new InvalidOperationException("A required worker frame string is missing.");
            var type = Text("type");
            var allowed = new HashSet<string>(new[] { "protocolVersion", "type", "requestId", "operationId", "workerId", "epoch" }, StringComparer.Ordinal);
            if (type == "result") { allowed.Add("success"); allowed.Add("payload"); }
            else if (type is not ("ready" or "heartbeat")) throw new InvalidOperationException("Unknown worker frame type.");
            if (fields.Count != allowed.Count || fields.Keys.Any(k => !allowed.Contains(k)) ||
                !fields.TryGetValue("protocolVersion", out var version) || !version.TryGetInt32(out var v) || v != 1 ||
                !fields.TryGetValue("epoch", out var epoch) || !epoch.TryGetInt64(out var e) || e != request.Epoch ||
                Text("requestId") != request.RequestId || Text("operationId") != request.OperationId || Text("workerId") != request.WorkerId)
                throw new InvalidOperationException("Worker frame correlation, version or field set is invalid.");
            if (type != "result") return new(type);
            var success = fields["success"];
            if (success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("Worker success must be an explicit boolean.");
            var payload = AiDurableInvocationJson.Normalize(fields["payload"].GetRawText(), false);
            return new(type, new AiDurableInvocationResult(success.GetBoolean(), payload));
        }
    }
}
