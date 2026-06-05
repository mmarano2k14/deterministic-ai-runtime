namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http
{
    /// <summary>
    /// Defines the shared HTTP runtime provider integration test collection.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class McpHttpRuntimeCollection :
        ICollectionFixture<McpHttpRuntimeFixture>
    {
        /// <summary>
        /// Gets the collection name.
        /// </summary>
        public const string Name = "MCP HTTP Runtime";
    }
}