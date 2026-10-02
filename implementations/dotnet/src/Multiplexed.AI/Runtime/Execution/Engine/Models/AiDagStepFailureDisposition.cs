namespace Multiplexed.AI.Runtime.Execution.Engine.Models
{
    /// <summary>
    /// Identifies the durable failure transition selected by the retry policy authority.
    /// </summary>
    public enum AiDagStepFailureDisposition
    {
        Retry = 0,
        Fail = 1
    }
}
