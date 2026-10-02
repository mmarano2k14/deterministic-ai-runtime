namespace Multiplexed.AI.Sdk.Authentication
{
    /// <summary>
    /// Options for creating the first runtime access-context handle from an already authenticated HTTP identity.
    /// </summary>
    public sealed record AiSdkAccessContextBootstrapOptions
    {
        /// <summary>Protected HTTP endpoint that creates one access-context handle.</summary>
        public required Uri Endpoint { get; init; }

        /// <summary>
        /// Credential provider used only for the bootstrap HTTP request.
        /// The credential remains outside SDK operation payloads.
        /// </summary>
        public IAiSdkCredentialProvider? CredentialProvider { get; init; }

        /// <summary>Response header carrying the created access-context handle.</summary>
        public string AccessContextHeaderName { get; init; } = "X-Access-Context";

        /// <summary>Maximum duration allowed for the one non-retried bootstrap request.</summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    }
}
