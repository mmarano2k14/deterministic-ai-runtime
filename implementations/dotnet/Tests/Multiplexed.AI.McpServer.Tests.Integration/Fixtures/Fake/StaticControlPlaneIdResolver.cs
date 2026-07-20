using Multiplexed.Abstractions.AI.ControlPlane.Discovery;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Fake
{
    public sealed class StaticControlPlaneIdResolver : IAiControlPlaneIdResolver
    {
        private readonly string controlPlaneId;

        /// <summary>
        /// Initializes a new instance of the <see cref="StaticControlPlaneIdResolver" /> class.
        /// </summary>
        /// <param name="controlPlaneId">The control-plane id to return.</param>
        public StaticControlPlaneIdResolver(
            string controlPlaneId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);

            this.controlPlaneId = controlPlaneId;
        }

        /// <inheritdoc />
        public Task<string> ResolveAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(this.controlPlaneId);
        }

        /// <inheritdoc />
        public Task<string> ResolveAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                string.IsNullOrWhiteSpace(request.RequestedControlPlaneId)
                    ? this.controlPlaneId
                    : request.RequestedControlPlaneId);
        }

        /// <inheritdoc />
        public Task<IReadOnlyDictionary<string, string>> ResolveMetadataAsync(
            AiControlPlaneIdResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            cancellationToken.ThrowIfCancellationRequested();

            var resolvedControlPlaneId =
                string.IsNullOrWhiteSpace(request.RequestedControlPlaneId)
                    ? this.controlPlaneId
                    : request.RequestedControlPlaneId;

            IReadOnlyDictionary<string, string> metadata =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["controlPlaneId"] = resolvedControlPlaneId,
                    ["logicalControlPlaneId"] = resolvedControlPlaneId,
                    ["runtime.controlPlaneId"] = resolvedControlPlaneId,
                    ["mcp.controlPlaneId"] = resolvedControlPlaneId,
                    ["recovery.controlPlaneId"] = resolvedControlPlaneId,
                    ["scaleout.controlPlaneId"] = resolvedControlPlaneId,
                    ["scenario.controlPlaneId"] = resolvedControlPlaneId,
                    ["control-plane.id"] = resolvedControlPlaneId,
                    ["controlplane.id"] = resolvedControlPlaneId,
                    ["control_plane_id"] = resolvedControlPlaneId
                };

            return Task.FromResult(metadata);
        }
    }
}
