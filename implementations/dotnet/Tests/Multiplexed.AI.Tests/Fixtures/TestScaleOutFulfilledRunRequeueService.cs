using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Scaling;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    public sealed class TestScaleOutFulfilledRunRequeueService :
        IAiScaleOutFulfilledRunRequeueService
    {
        public int CallCount { get; private set; }

        public AiRuntimeScaleOutRequestRecord? LastRequest { get; private set; }

        public string? LastRuntimeInstanceId { get; private set; }

        public Task RequeueAsync(
            AiRuntimeScaleOutRequestRecord request,
            string? runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            this.CallCount++;
            this.LastRequest = request;
            this.LastRuntimeInstanceId = runtimeInstanceId;

            return Task.CompletedTask;
        }
    }
}
