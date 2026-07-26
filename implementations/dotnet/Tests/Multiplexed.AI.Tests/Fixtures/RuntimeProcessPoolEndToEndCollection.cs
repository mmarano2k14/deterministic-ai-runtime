namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Serializes real process-pool proofs that share the fixture-owned local child port range.
    /// </summary>
    [CollectionDefinition(
        Name,
        DisableParallelization = true)]
    public sealed class RuntimeProcessPoolEndToEndCollection
    {
        /// <summary>
        /// Gets the xUnit collection name used by real process-pool proofs.
        /// </summary>
        public const string Name =
            "RuntimeProcessPoolEndToEnd";
    }
}
