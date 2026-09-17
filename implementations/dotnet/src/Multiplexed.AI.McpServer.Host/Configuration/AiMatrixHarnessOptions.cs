namespace Multiplexed.AI.McpServer.Host.Configuration
{
    /// <summary>Explicit E2E harness settings. Disabled by default and never required by normal hosts.</summary>
    public sealed class AiMatrixHarnessOptions
    {
        public bool Enabled { get; set; }
        public string BearerToken { get; set; } = string.Empty;
        public string UserId { get; set; } = "matrix-user";
        public string TenantId { get; set; } = "matrix-tenant";
        public string TenantGroupId { get; set; } = "matrix-group";
        public string Project { get; set; } = "matrix";
        public string Namespace { get; set; } = "default";
        public string PublicEndpoint { get; set; } = "http://localhost:8081/mcp";
        public string Topology { get; set; } = "local";
        public string Provider { get; set; } = "ProcessHostPool";
        public string ManifestPath { get; set; } = string.Empty;
    }
}
