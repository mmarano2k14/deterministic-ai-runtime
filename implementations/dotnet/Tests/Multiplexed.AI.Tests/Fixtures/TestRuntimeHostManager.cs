using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Tests.Fixtures
{
    public sealed class TestRuntimeHostManager : IAiRuntimeHostManager
    {
        public int CallCount { get; private set; }

        public AiRuntimeHostStartRequest? LastRequest { get; private set; }

        public Task<AiRuntimeHostStartResult> StartRuntimeAsync(AiRuntimeHostStartRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            this.CallCount++;
            this.LastRequest = request;

            return Task.FromResult(new AiRuntimeHostStartResult
            {
                Success = true,
                RuntimeInstanceId = request.RuntimeInstanceId,
                ProviderName = request.ProviderName,
                TransportName = request.TransportName,
                TransportEndpoint = request.TransportEndpoint,
                ExecutionContextSnapshot = request.ExecutionContextSnapshot,
                Metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["hostManager"] = "test"
                }
            });
        }
    }
}