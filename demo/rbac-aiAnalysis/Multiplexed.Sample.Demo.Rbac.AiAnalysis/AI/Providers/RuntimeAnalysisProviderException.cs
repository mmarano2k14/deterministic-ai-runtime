namespace Multiplexed.Sample.Demo.Rbac.AiAnalysis.AI.Providers
{
    public sealed class RuntimeAnalysisProviderException : Exception
    {
        public RuntimeAnalysisProviderException(
            string message)
            : base(message)
        {
        }

        public RuntimeAnalysisProviderException(
            string message,
            Exception innerException)
            : base(
                message,
                innerException)
        {
        }
    }
}
