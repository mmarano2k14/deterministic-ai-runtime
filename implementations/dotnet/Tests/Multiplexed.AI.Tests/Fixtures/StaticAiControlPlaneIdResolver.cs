using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    public sealed class StaticAiControlPlaneIdResolver : IAiControlPlaneIdResolver
    {
        private readonly string controlPlaneId;

        public StaticAiControlPlaneIdResolver(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            this.controlPlaneId = controlPlaneId;
        }

        public Task<string> ResolveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(controlPlaneId);
        }
    }
}
