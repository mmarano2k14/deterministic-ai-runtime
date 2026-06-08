using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;

namespace Multiplexed.AI.McpServer.Tests.Integration.Health
{
    /// <summary>
    /// Contains MCP host health endpoint integration tests.
    /// </summary>
    [Collection(McpCollection.Name)]
    public sealed class McpHealthEndpointTests
    {
        private readonly HttpClient client;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpHealthEndpointTests"/> class.
        /// </summary>
        /// <param name="fixture">The shared MCP server fixture.</param>
        public McpHealthEndpointTests(
            McpServerFixture fixture)
        {
            client = fixture.Client;
        }

        /// <summary>
        /// Verifies that the health endpoint returns Healthy.
        /// </summary>
        [Fact]
        public async Task Health_Should_Return_Healthy()
        {
            var response = await client.GetAsync("/health");

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();

            Assert.Equal("Healthy", content);
        }

       
    }
}