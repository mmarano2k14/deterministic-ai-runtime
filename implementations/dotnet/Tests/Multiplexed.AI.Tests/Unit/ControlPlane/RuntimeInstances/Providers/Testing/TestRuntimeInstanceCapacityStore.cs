using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Testing
{
    /// <summary>
    /// In-memory runtime instance capacity store used by provider unit tests.
    /// </summary>
    internal sealed class TestRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
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