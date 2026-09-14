namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>
    /// Immutable metadata produced by pure invocation resolution. It neither creates
    /// an executable adapter nor proves reference authorization or worker availability.
    /// </summary>
    public sealed record AiInvocationBinding(
        AiInvocationKind Kind,
        string? ExecutionLanguage,
        AiExecutionLanguageSource LanguageSource,
        string? ImplementationRef = null,
        string? ConnectionRef = null,
        string? Tool = null)
    {
        public static AiInvocationBinding Native { get; } = new(
            AiInvocationKind.Native, null, AiExecutionLanguageSource.None);
    }
}
