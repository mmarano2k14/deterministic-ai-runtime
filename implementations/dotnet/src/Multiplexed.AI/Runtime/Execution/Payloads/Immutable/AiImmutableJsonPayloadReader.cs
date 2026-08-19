using System.Text.Json;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;

namespace Multiplexed.AI.Runtime.Execution.Payloads.Immutable
{
    /// <summary>
    /// Loads immutable JSON payload descriptors through the configured execution payload store and verifies
    /// their canonical content hash before exposing the serialized content to execution logic.
    /// </summary>
    /// <remarks>
    /// This reader is storage-provider agnostic. Inline and artifact-backed payloads follow the same canonical
    /// JSON and SHA-256 verification path so callers cannot accidentally trust an unverified persisted snapshot.
    /// </remarks>
    public sealed class AiImmutableJsonPayloadReader
    {
        private readonly IAiPayloadStoreResolver payloadStoreResolver;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiImmutableJsonPayloadReader"/> class.
        /// </summary>
        /// <param name="payloadStoreResolver">The configured execution payload store resolver.</param>
        public AiImmutableJsonPayloadReader(IAiPayloadStoreResolver payloadStoreResolver)
        {
            this.payloadStoreResolver = payloadStoreResolver ?? throw new ArgumentNullException(nameof(payloadStoreResolver));
        }

        /// <summary>
        /// Loads one immutable JSON payload and verifies its stable content hash.
        /// </summary>
        /// <param name="snapshot">The immutable stored-payload descriptor.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The verified canonical JSON content.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the descriptor is incomplete, the external artifact cannot be resolved, the inline value is
        /// not serialized JSON text, or the canonical content does not match the durable content hash.
        /// </exception>
        public async Task<string> LoadAndVerifyAsync(
            AiStoredPayload snapshot,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            string content;
            if (snapshot.IsInline)
            {
                content = snapshot.InlineValue switch
                {
                    string inlineContent => inlineContent,
                    JsonElement { ValueKind: JsonValueKind.String } element =>
                        element.GetString() ?? string.Empty,
                    _ => throw new InvalidOperationException(
                        "Immutable inline JSON payloads must contain serialized JSON text.")
                };
            }
            else
            {
                if (string.IsNullOrWhiteSpace(snapshot.ArtifactId))
                {
                    throw new InvalidOperationException(
                        "Immutable artifact-backed JSON payload is missing its artifact id.");
                }

                content = await this.payloadStoreResolver
                    .Resolve()
                    .LoadAsync(snapshot.ArtifactId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Immutable JSON payload artifact '{snapshot.ArtifactId}' could not be resolved.");
            }

            var canonicalContent = AiCanonicalJson.Canonicalize(content);
            var digest = AiCanonicalJson.ComputeSha256(canonicalContent);

            if (string.IsNullOrWhiteSpace(snapshot.ContentHash) ||
                !string.Equals(snapshot.ContentHash, digest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Immutable JSON payload content does not match its durable content hash.");
            }

            return canonicalContent;
        }
    }
}
