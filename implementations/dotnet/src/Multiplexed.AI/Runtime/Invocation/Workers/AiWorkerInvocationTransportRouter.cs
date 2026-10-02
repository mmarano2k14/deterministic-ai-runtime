using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.Abstractions.AI.Publication;
using Multiplexed.AI.Runtime.Invocation.Workers.Isolation;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    public enum AiWorkerInvocationProviderKind
    {
        TrustedProcess = 0,
        IsolatedContainer = 1
    }

    /// <summary>
    /// Routes one prepared immutable invocation to the already configured physical provider.
    /// Selection is derived only from the pinned execution descriptor. The router does not retry,
    /// downgrade isolation, alter journal authority, or select a different runtime profile.
    /// </summary>
    public sealed class AiWorkerInvocationTransportRouter : IAiWorkerInvocationTransport
    {
        private readonly AiWorkerProcessTransport trustedProcess;
        private readonly AiContainerWorkerTransport isolatedContainer;

        public AiWorkerInvocationTransportRouter(
            AiWorkerProcessTransport trustedProcess,
            AiContainerWorkerTransport isolatedContainer)
        {
            this.trustedProcess = trustedProcess ?? throw new ArgumentNullException(nameof(trustedProcess));
            this.isolatedContainer = isolatedContainer ?? throw new ArgumentNullException(nameof(isolatedContainer));
        }

        public Task<AiDurableInvocationResult> InvokeAsync(
            AiWorkerInvocationRequest request,
            Func<CancellationToken, Task> heartbeat,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(heartbeat);

            return SelectProvider(request.Code) switch
            {
                AiWorkerInvocationProviderKind.TrustedProcess =>
                    trustedProcess.InvokeAsync(request, heartbeat, cancellationToken),
                AiWorkerInvocationProviderKind.IsolatedContainer =>
                    isolatedContainer.InvokeAsync(request, heartbeat, cancellationToken),
                _ => throw new NotSupportedException("The prepared worker execution provider is not supported.")
            };
        }

        public static AiWorkerInvocationProviderKind SelectProvider(AiWorkerCodeBundle code)
        {
            ArgumentNullException.ThrowIfNull(code);
            var descriptor = code.ExecutionDescriptor;

            // Historical descriptor-free publications remain an explicit trusted-process compatibility path.
            if (descriptor is null)
                return AiWorkerInvocationProviderKind.TrustedProcess;

            AiPublicationExecutionDescriptors.Validate(descriptor, code.Runtime);
            return descriptor.Artifact.Kind switch
            {
                AiPublicationEnvironmentArtifactKind.HostRuntime => AiWorkerInvocationProviderKind.TrustedProcess,
                AiPublicationEnvironmentArtifactKind.OciImage => AiWorkerInvocationProviderKind.IsolatedContainer,
                _ => throw new NotSupportedException("The prepared worker artifact kind has no configured execution provider.")
            };
        }
    }
}
