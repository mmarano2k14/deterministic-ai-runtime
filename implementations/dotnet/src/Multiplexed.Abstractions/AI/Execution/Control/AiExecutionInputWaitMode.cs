namespace Multiplexed.Abstractions.AI.Execution.Control
{
    /// <summary>
    /// Defines how submitted input reactivates a durable execution wait.
    /// </summary>
    public enum AiExecutionInputWaitMode
    {
        /// <summary>
        /// Input releases the execution-level control gate and the existing physical run is woken normally.
        /// </summary>
        ExecutionGate = 0,

        /// <summary>
        /// Input satisfies one exact DAG step parked in WaitingForExternal and requires external-wait continuation.
        /// </summary>
        ExternalWaitStep = 1
    }
}
