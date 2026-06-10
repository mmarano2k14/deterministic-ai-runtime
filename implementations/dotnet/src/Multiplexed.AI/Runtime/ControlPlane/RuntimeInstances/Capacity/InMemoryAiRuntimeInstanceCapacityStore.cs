using System.Collections.Concurrent;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Capacity
{
    /// <summary>
    /// Provides an in-memory implementation of
    /// <see cref="IAiRuntimeInstanceCapacityStore"/>.
    /// </summary>
    /// <remarks>
    /// PURPOSE:
    /// - Stores the latest runtime instance capacity descriptors in memory.
    /// - Supports local tests, demos, and single-process execution.
    /// - Allows admission logic to evaluate runtime capacity without Redis.
    ///
    /// IMPORTANT:
    /// - This implementation is process-local only.
    /// - It does not provide distributed coordination.
    /// - Production multi-host or Kubernetes deployments should use the Redis-backed
    ///   implementation.
    /// </remarks>
    public sealed class InMemoryAiRuntimeInstanceCapacityStore :
        IAiRuntimeInstanceCapacityStore
    {
        private readonly ConcurrentDictionary<string, AiRuntimeInstanceCapacityDescriptor> descriptors =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task PublishAsync(
            AiRuntimeInstanceCapacityDescriptor descriptor,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RuntimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            descriptors.AddOrUpdate(
                descriptor.RuntimeInstanceId,
                descriptor,
                (_, _) => descriptor);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<AiRuntimeInstanceCapacityDescriptor?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            descriptors.TryGetValue(
                runtimeInstanceId,
                out var descriptor);

            return Task.FromResult(descriptor);
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<AiRuntimeInstanceCapacityDescriptor>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<AiRuntimeInstanceCapacityDescriptor> result =
                descriptors
                    .Values
                    .OrderBy(
                        descriptor => descriptor.RuntimeInstanceId,
                        StringComparer.Ordinal)
                    .ToArray();

            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public Task<bool> RemoveAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            cancellationToken.ThrowIfCancellationRequested();

            var removed =
                descriptors.TryRemove(
                    runtimeInstanceId,
                    out _);

            return Task.FromResult(removed);
        }
    }
}