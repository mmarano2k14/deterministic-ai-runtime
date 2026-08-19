using Multiplexed.Abstractions.AI.Execution.Payloads.Models;

namespace Multiplexed.Abstractions.AI.Execution.Payloads.Stores
{
    /// <summary>
    /// Defines the optional payload-store capability for idempotent immutable writes under a caller-supplied key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This capability is used when crash-safe execution metadata must persist an external payload before the
    /// durable relation that references it is created. Repeating the same write with the same key and content
    /// must converge on the existing payload instead of creating a second artifact.
    /// </para>
    /// <para>
    /// Implementations must reject a write when the supplied key already exists with different content.
    /// </para>
    /// </remarks>
    public interface IAiImmutablePayloadStore : IAiPayloadStore
    {
        /// <summary>
        /// Persists immutable serialized payload content under an exact caller-supplied key.
        /// </summary>
        /// <param name="key">The stable payload key.</param>
        /// <param name="content">The serialized payload content.</param>
        /// <param name="metadata">Optional semantic payload metadata.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exact persisted payload key.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <paramref name="key"/> already exists with different content.
        /// </exception>
        Task<string> SaveImmutableAsync(
            string key,
            string content,
            AiPayloadMetadata metadata,
            CancellationToken cancellationToken = default);
    }
}
