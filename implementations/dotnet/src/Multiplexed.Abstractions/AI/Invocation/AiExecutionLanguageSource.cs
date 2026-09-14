namespace Multiplexed.Abstractions.AI.Invocation
{
    /// <summary>Records the declaration that supplied an effective custom language.</summary>
    public enum AiExecutionLanguageSource
    {
        None = 0,
        Pipeline = 1,
        Step = 2,
        Policy = 3
    }
}
