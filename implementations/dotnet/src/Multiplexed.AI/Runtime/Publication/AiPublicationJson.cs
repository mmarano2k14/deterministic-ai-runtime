using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;
using Multiplexed.AI.Runtime.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Publication
{
    /// <summary>Uses the existing canonical definition format, with bounded and unambiguous publication inputs.</summary>
    internal static class AiPublicationJson
    {
        internal const int MaxDocumentBytes = 4 * 1024 * 1024;
        internal const int MaxManifestBytes = 1024 * 1024;
        internal static readonly UTF8Encoding Utf8 = new(false, true);
        internal static string Serialize(object value)
        {
            var json = AiCanonicalJson.Serialize(value);
            ValidateJson(json, MaxDocumentBytes);
            return json;
        }
        internal static T Read<T>(string json)
        {
            ValidateJson(json, MaxDocumentBytes);
            return AiCanonicalJson.Deserialize<T>(json);
        }
        internal static string Hash(string json) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(json))).ToLowerInvariant();
        internal static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        internal static string EncodeBytes(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        internal static byte[] DecodeBytes(string encoded)
        {
            if (encoded.Length % 4 == 1 || encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidOperationException("Invalid base64url publication bytes.");
            var bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4));
            if (EncodeBytes(bytes) != encoded) throw new InvalidOperationException("Noncanonical publication byte encoding.");
            return bytes;
        }
        internal static string PartitionId(AiPublicationPartition partition) => Hash(Serialize(partition));
        internal static string Key(AiPublicationPartition partition, string kind, string hash) =>
            $"publication/{PartitionId(partition)}/{kind}/{hash}";
        internal static string ExecutionId(AiPublicationPartition partition, string runKey) =>
            "published-" + Hash(Serialize(new { Partition = partition, RunKey = runKey }));
        internal static string PinKey(AiPublicationPartition partition, string executionId) =>
            Key(partition, "run", Hash(executionId));
        internal static string ChildBindingKey(AiPublicationPartition partition, string executionId) =>
            Key(partition, "child-run", Hash(executionId));
        internal static string ReferenceHash(string reference, string prefix)
        {
            if (reference is null || !reference.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("An exact immutable publication reference is required.");
            var hash = reference[prefix.Length..]; ValidateHash(hash); return hash;
        }
        internal static void ValidateHash(string value)
        {
            if (value is null || value.Length != 64 || value.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)))
                throw new InvalidOperationException("A lower-case SHA-256 digest is required.");
        }
        internal static void Text(string? value, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
            if (value.Length > 512 || value.Any(char.IsControl)) throw new ArgumentException("Invalid publication identifier.", name);
            _ = Utf8.GetByteCount(value);
        }
        internal static void Path(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Split('/').Any(p => p is "" or "." or ".." ||
                p.EndsWith('.') || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))))
                throw new InvalidOperationException("Publication file paths must be portable relative paths without traversal.");
            foreach (var segment in path.Split('/'))
            {
                var name = segment.Split('.')[0].ToUpperInvariant();
                if (name is "CON" or "PRN" or "AUX" or "NUL" ||
                    name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                    name[3] is >= '1' and <= '9')
                    throw new InvalidOperationException("Device names are not portable publication paths.");
            }
        }
        internal static void Version(string version)
        {
            if (string.IsNullOrEmpty(version) || version.Length > 128 || !char.IsAsciiDigit(version[0]) ||
                version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '+')))
                throw new InvalidOperationException("An exact version label is required; ranges, URLs and latest are not supported.");
        }
        internal static void ValidateEnvironment(AiPublicationEnvironment environment)
        {
            ArgumentNullException.ThrowIfNull(environment);
            Text(environment.Reference, nameof(environment.Reference));
            if (environment.Reference.Contains("://", StringComparison.Ordinal) || environment.Reference.Equals("latest", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Environment references must identify exact host catalog entries.");
            AiDurableInvocationValidation.ValidateLanguage(environment.ExecutionLanguage);
            Version(environment.RuntimeVersion); ValidateHash(environment.RuntimeSha256);
        }
        internal static void ValidateJson(string json, int limit)
        {
            if (Utf8.GetByteCount(json) > limit) throw new InvalidOperationException("Publication JSON exceeds its size limit.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
            Check(document.RootElement);
        }
        private static void Check(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in element.EnumerateObject())
                {
                    if (!names.Add(item.Name)) throw new InvalidOperationException("Duplicate JSON property names are ambiguous.");
                    Check(item.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Check(item);
        }
    }
}
