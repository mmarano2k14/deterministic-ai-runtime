using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using System;
using System.Collections.Generic;
using System.Text;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// Test runtime instance capacity store.
    /// </summary>
    public sealed class TestRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
    {
        private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            descriptors[descriptor.RuntimeInstanceId] = descriptor;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            descriptors.TryGetValue(
                runtimeInstanceId,
                out var descriptor);

            return Task.FromResult(descriptor);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> result =
                descriptors.Values.ToArray();

            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public Task<bool> RemoveAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return Task.FromResult(
                descriptors.Remove(runtimeInstanceId));
        }
    }
}
