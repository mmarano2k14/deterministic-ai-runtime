using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Registry
{
    /// <summary>
    /// Projects first-class runtime pool membership from a runtime instance registry.
    /// </summary>
    /// <remarks>
    /// This adapter preserves compatibility with custom or legacy
    /// <see cref="IAiRuntimeInstanceRegistry"/> implementations that do not directly implement
    /// <see cref="IAiRuntimePoolMembershipReader"/>. Membership is resolved exclusively from the
    /// typed <see cref="AiRuntimeInstanceSnapshot.PoolId"/> and
    /// <see cref="AiRuntimeInstanceSnapshot.HostId"/> properties. Optional metadata is never an
    /// authoritative source for routing, lifecycle, draining, capacity selection, or recovery.
    /// </remarks>
    public sealed class AiRuntimePoolMembershipReader : IAiRuntimePoolMembershipReader
    {
        private readonly IAiRuntimeInstanceRegistry registry;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimePoolMembershipReader"/> class.
        /// </summary>
        /// <param name="registry">The authoritative runtime instance registry.</param>
        public AiRuntimePoolMembershipReader(
            IAiRuntimeInstanceRegistry registry)
        {
            this.registry =
                registry ?? throw new ArgumentNullException(nameof(registry));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            var snapshots =
                await this.registry.ListAsync(
                        includeStopped: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return snapshots
                .Where(snapshot =>
                    string.Equals(
                        snapshot.PoolId,
                        poolId,
                        StringComparison.Ordinal))
                .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<AiRuntimeInstanceSnapshot>> ListByHostIdAsync(
            string hostId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            var snapshots =
                await this.registry.ListAsync(
                        includeStopped: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return snapshots
                .Where(snapshot =>
                    string.Equals(
                        snapshot.HostId,
                        hostId,
                        StringComparison.Ordinal))
                .OrderBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> ListHostIdsByPoolIdAsync(
            string poolId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            var members =
                await this.ListByPoolIdAsync(
                        poolId,
                        cancellationToken)
                    .ConfigureAwait(false);

            return members
                .Select(member => member.HostId)
                .Where(hostId => !string.IsNullOrWhiteSpace(hostId))
                .Select(hostId => hostId!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(hostId => hostId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
