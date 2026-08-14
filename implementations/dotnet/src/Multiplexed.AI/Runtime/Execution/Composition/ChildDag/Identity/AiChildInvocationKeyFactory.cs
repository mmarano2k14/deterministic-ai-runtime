using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Multiplexed.Abstractions.AI.Execution.Composition.ChildDag.Identity;

namespace Multiplexed.AI.Runtime.Execution.Composition.ChildDag.Identity
{
    /// <summary>
    /// Creates deterministic child invocation keys from the authoritative typed invocation identity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The encoding is explicitly versioned and length-prefixed so field boundaries cannot collide.
    /// Values are encoded exactly as supplied after validation; the factory does not silently trim or
    /// normalize identity material because such normalization would change logical equality semantics.
    /// </para>
    /// <para>
    /// The resulting key is an integrity and lookup aid. Durable uniqueness remains owned by the complete
    /// typed identity tuple represented by <see cref="AiChildInvocationIdentity"/>.
    /// </para>
    /// </remarks>
    public static class AiChildInvocationKeyFactory
    {
        private const string EncodingVersion = "ai-child-invocation:v1";
        private const string KeyPrefix = "child-invocation-";

        /// <summary>
        /// Creates a deterministic child invocation key for one complete invocation identity.
        /// </summary>
        /// <param name="identity">The authoritative typed child invocation identity.</param>
        /// <returns>A deterministic lower-case SHA-256 based child invocation key.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="identity"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a required identity component is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <see cref="AiChildInvocationIdentity.InvocationGeneration"/> is negative.
        /// </exception>
        public static string Create(AiChildInvocationIdentity identity)
        {
            ArgumentNullException.ThrowIfNull(identity);

            Validate(identity);

            using var stream = new MemoryStream();
            WriteString(stream, EncodingVersion);
            WriteString(stream, identity.TenantId);
            WriteString(stream, identity.ParentExecutionId);
            WriteString(stream, identity.ParentCallSiteId);
            WriteString(stream, identity.ChildDagId);
            WriteString(stream, identity.ChildDagDefinitionVersion);
            WriteString(stream, identity.CanonicalLogicalInvocationKey);
            WriteInt32(stream, identity.InvocationGeneration);

            var hash = SHA256.HashData(stream.ToArray());

            return string.Concat(
                KeyPrefix,
                Convert.ToHexString(hash).ToLowerInvariant());
        }

        /// <summary>
        /// Validates all components that participate in child invocation identity.
        /// </summary>
        /// <param name="identity">The identity to validate.</param>
        /// <exception cref="ArgumentException">Thrown when a required string component is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the invocation generation is negative.</exception>
        private static void Validate(AiChildInvocationIdentity identity)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.TenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.ParentExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.ParentCallSiteId);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.ChildDagId);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.ChildDagDefinitionVersion);
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.CanonicalLogicalInvocationKey);

            if (identity.InvocationGeneration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AiChildInvocationIdentity.InvocationGeneration),
                    identity.InvocationGeneration,
                    "Child invocation generation cannot be negative.");
            }
        }

        /// <summary>
        /// Writes one UTF-8 string using a four-byte big-endian length prefix.
        /// </summary>
        /// <param name="stream">The canonical identity stream.</param>
        /// <param name="value">The value to append.</param>
        private static void WriteString(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> lengthBuffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, bytes.Length);
            stream.Write(lengthBuffer);
            stream.Write(bytes);
        }

        /// <summary>
        /// Writes one signed integer using a four-byte big-endian representation.
        /// </summary>
        /// <param name="stream">The canonical identity stream.</param>
        /// <param name="value">The integer value to append.</param>
        private static void WriteInt32(Stream stream, int value)
        {
            Span<byte> valueBuffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(valueBuffer, value);
            stream.Write(valueBuffer);
        }
    }
}
