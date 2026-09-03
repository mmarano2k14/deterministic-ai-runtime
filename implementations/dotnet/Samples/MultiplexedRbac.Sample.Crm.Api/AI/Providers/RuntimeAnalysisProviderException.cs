namespace MultiplexedRbac.Sample.Crm.Api.AI.Providers
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
