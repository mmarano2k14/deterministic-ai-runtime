namespace Multiplexed.AI.McpServer.Host.Configuration
{
    /// <summary>
    /// Configures the standalone MCP HTTP authentication boundary.
    /// </summary>
    /// <remarks>
    /// JWT validation supports either:
    /// - an external OpenID Connect/JWT authority; or
    /// - an explicitly configured symmetric signing key for standalone/local deployments.
    ///
    /// Authorization capabilities are not supplied by request bodies. The authenticated
    /// token supplies the tenant/project/namespace/TRN claims used to mint an access context.
    /// </remarks>
    public sealed class AiMcpAuthenticationOptions
    {
        public const string SectionName = "AiMcpAuthentication";

        /// <summary>
        /// Enables the standalone JWT bearer boundary.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Optional external JWT/OpenID Connect authority.
        /// </summary>
        public string? Authority { get; set; }

        /// <summary>
        /// Expected token issuer when symmetric-key validation is used.
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// Expected JWT audience.
        /// </summary>
        public string? Audience { get; set; }

        /// <summary>
        /// Symmetric HMAC signing key used only when no external authority is configured.
        /// Supply through a secret/environment variable, never source control.
        /// </summary>
        public string? SymmetricSigningKey { get; set; }

        /// <summary>
        /// Requires HTTPS metadata for an external authority.
        /// </summary>
        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>
        /// Maximum accepted JWT clock skew in seconds.
        /// </summary>
        public int ClockSkewSeconds { get; set; } = 30;

        /// <summary>
        /// Protected endpoint used to create an RBAC access-context handle.
        /// </summary>
        public string AccessContextPath { get; set; } = "/auth/access-context";

        /// <summary>
        /// TTL copied into durable execution-context snapshots.
        /// Store/session expiry remains owned by the existing context store.
        /// </summary>
        public int ExecutionContextTtlSeconds { get; set; } = 3600;

        public string SubjectClaimType { get; set; } = "sub";
        public string TenantIdClaimType { get; set; } = "tenant_id";
        public string TenantGroupIdClaimType { get; set; } = "tenant_group_id";
        public string ProjectClaimType { get; set; } = "project";
        public string NamespaceClaimType { get; set; } = "namespace";
        public string TrnClaimType { get; set; } = "trn";
    }
}
