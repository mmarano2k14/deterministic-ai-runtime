using Multiplexed.AI.Sdk.Contracts.Common;

namespace Multiplexed.AI.Sdk.Protocol
{
    /// <summary>External SDK protocol metadata shared by the .NET client and transport implementations.</summary>
    public static class AiSdkProtocol
    {
        private static readonly IReadOnlyList<AiSdkOperationDescriptor> KnownOperations =
        [
            new(AiSdkOperationNames.PublishPipeline, AiSdkTransportRetryMode.Never),
            new(AiSdkOperationNames.SubmitExecution, AiSdkTransportRetryMode.Never),
            new(AiSdkOperationNames.ObserveExecution, AiSdkTransportRetryMode.SafeRead),
            new(AiSdkOperationNames.WatchExecution, AiSdkTransportRetryMode.SafeRead),
            new(AiSdkOperationNames.GetExecutionResult, AiSdkTransportRetryMode.SafeRead),
            new(AiSdkOperationNames.CancelExecution, AiSdkTransportRetryMode.Never),
            new(AiSdkOperationNames.PauseExecution, AiSdkTransportRetryMode.Never),
            new(AiSdkOperationNames.ResumeExecution, AiSdkTransportRetryMode.Never),
            new(AiSdkOperationNames.SubmitExecutionInput, AiSdkTransportRetryMode.Never),
            new(AiSdkOperationNames.ReplayExecution, AiSdkTransportRetryMode.Never)
        ];

        public const int Version = AiSdkProtocolVersions.Current;

        public static IReadOnlyList<AiSdkOperationDescriptor> Operations => KnownOperations;

        public static AiSdkOperationDescriptor GetOperation(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return KnownOperations.FirstOrDefault(operation => string.Equals(operation.Name, name, StringComparison.Ordinal))
                ?? throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown public SDK operation.");
        }
    }
}
