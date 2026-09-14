using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Multiplexed.Abstractions.AI.Invocation.Durable;

namespace Multiplexed.AI.Runtime.Invocation.Durable
{
    /// <summary>
    /// Versioned, length-prefixed identity encoding. Values remain ordinal and are never
    /// trimmed. Typed tuple uniqueness, not this hash alone, is enforced by persistence.
    /// </summary>
    public static class AiDurableInvocationKeys
    {
        public static string OperationId(AiDurableInvocationIdentity identity) =>
            "invocation-" + IdentityHash(identity);

        public static string EffectIdempotencyKey(AiDurableInvocationIdentity identity) =>
            "invocation-effect-" + IdentityHash(identity);

        private static string IdentityHash(AiDurableInvocationIdentity identity)
        {
            AiDurableInvocationValidation.ValidateIdentity(identity);
            var encoding = new UTF8Encoding(false, true);
            using var stream = new MemoryStream();
            Span<byte> size = stackalloc byte[4];
            foreach (var value in new[] { "ai-durable-invocation:v1", identity.TenantId, identity.ExecutionId, identity.StepName })
            {
                var bytes = encoding.GetBytes(value);
                BinaryPrimitives.WriteInt32BigEndian(size, bytes.Length);
                stream.Write(size);
                stream.Write(bytes);
            }
            Span<byte> generation = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(generation, identity.Generation);
            stream.Write(generation);
            return Hash(stream.ToArray());
        }

        internal static string HashText(string value) => Hash(new UTF8Encoding(false, true).GetBytes(value));
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
