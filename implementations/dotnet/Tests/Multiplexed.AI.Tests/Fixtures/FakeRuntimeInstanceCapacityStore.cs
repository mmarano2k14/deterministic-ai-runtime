using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.AI.Tests.Fixtures
{
    /// <summary>
    /// In-memory runtime instance capacity store used by integration tests.
    /// </summary>
    public sealed class FakeRuntimeInstanceCapacityStore : IAiRuntimeInstanceCapacityStore
    {
        private readonly object syncRoot = new();

        private readonly Dictionary<string, AiRuntimeInstanceCapacityDescriptor> capacities =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the capacity descriptors published through the fake store.
        /// </summary>
        public IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> PublishedDescriptors
        {
            get
            {
                lock (syncRoot)
                {
                    return capacities.Values.ToArray();
                }
            }
        }

        /// <inheritdoc />
        public Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(descriptor.RuntimeInstanceId))
            {
                throw new ArgumentException(
                    "Runtime instance capacity descriptor must define a runtime instance identifier.",
                    nameof(descriptor));
            }

            lock (syncRoot)
            {
                capacities[descriptor.RuntimeInstanceId] = descriptor;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                var descriptor =
                    capacities.TryGetValue(runtimeInstanceId, out var value)
                        ? value
                        : null;

                return Task.FromResult(descriptor);
            }
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                return Task.FromResult<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>>(
                    capacities.Values.ToArray());
            }
        }

        /// <inheritdoc />
        public Task<bool> RemoveAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                return Task.FromResult(
                    capacities.Remove(runtimeInstanceId));
            }
        }
    }
}