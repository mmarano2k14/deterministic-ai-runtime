namespace Multiplexed.AI.Sdk.Errors
{
    /// <summary>Language-neutral categories used by SDK clients to normalize transport and remote failures.</summary>
    public enum AiSdkErrorKind
    {
        Transport,
        Authentication,
        Authorization,
        InvalidRequest,
        UnsupportedSchema,
        NotFound,
        Conflict,
        ResultUnavailable,
        RemoteFailure,
        InvalidResponse,
        Cancelled
    }
}
