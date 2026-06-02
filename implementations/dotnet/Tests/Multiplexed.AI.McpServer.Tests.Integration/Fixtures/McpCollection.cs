using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration
{
    /// <summary>
    /// Defines the shared MCP test collection.
    /// </summary>
    [CollectionDefinition(Name)]
    public sealed class McpCollection : ICollectionFixture<McpServerFixture>
    {
        /// <summary>
        /// The shared MCP test collection name.
        /// </summary>
        public const string Name = "MCP";
    }
}