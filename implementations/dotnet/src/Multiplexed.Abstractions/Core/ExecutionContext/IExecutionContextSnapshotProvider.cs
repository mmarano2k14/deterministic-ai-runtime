namespace Multiplexed.Abstractions.Core.ExecutionContext
{
    /// <summary>
    /// Provides a serializable snapshot of the current execution context.
    /// </summary>
    /// <remarks>
    /// The snapshot is intended for traceability, audit, replay metadata,
    /// tenant filtering, diagnostics, and durable runtime metadata.
    ///
    /// The snapshot context key is volatile and must not be reused as a durable
    /// execution identifier, orchestration key, or tenant partition key.
    /// </remarks>
    public interface IExecutionContextSnapshotProvider
    {
        /// <summary>
        /// Maps the current execution context to a serializable snapshot.
        /// </summary>
        /// <returns>The current execution context snapshot.</returns>
        ExecutionContextSnapshot MapToSnapshot();
    }
}