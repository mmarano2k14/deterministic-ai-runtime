namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Runtime
{
    public sealed class RuntimeAnalysisRuntimeExecutionException : Exception
    {
        public RuntimeAnalysisRuntimeExecutionException(
            string message)
            : base(
                message)
        {
        }

        public RuntimeAnalysisRuntimeExecutionException(
            string message,
            Exception innerException)
            : base(
                message,
                innerException)
        {
        }
    }
}
