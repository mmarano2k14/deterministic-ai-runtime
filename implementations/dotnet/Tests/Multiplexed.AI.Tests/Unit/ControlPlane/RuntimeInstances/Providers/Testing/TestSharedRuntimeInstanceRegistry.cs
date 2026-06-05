using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Providers.Testing
{
    /// <summary>
    /// In-memory shared runtime instance registry used by provider unit tests.
    /// </summary>
    internal sealed class TestSharedRuntimeInstanceRegistry : IAiSharedRuntimeInstanceRegistry
    {
        private readonly Dictionary<string, IAiSharedRuntimeInstance> instances =
            new(StringComparer.Ordinal);

        /// <inheritdoc />
        public Task RegisterAsync(
            IAiSharedRuntimeInstance instance,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(instance);

            instances[instance.RuntimeInstanceId] = instance;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IAiSharedRuntimeInstance?> GetAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            instances.TryGetValue(
                runtimeInstanceId,
                out var instance);

            return Task.FromResult(instance);
        }

        /// <inheritdoc />
        public Task<IReadOnlyCollection<IAiSharedRuntimeInstance>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            IReadOnlyCollection<IAiSharedRuntimeInstance> result =
                instances.Values.ToArray();

            return Task.FromResult(result);
        }

        /// <inheritdoc />
        public Task<bool> UnregisterAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);

            return Task.FromResult(
                instances.Remove(runtimeInstanceId));
        }
    }
}