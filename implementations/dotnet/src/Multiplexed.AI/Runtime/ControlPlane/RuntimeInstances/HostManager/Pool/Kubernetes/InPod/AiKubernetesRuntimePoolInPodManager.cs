using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.InPod
{
    /// <summary>
    /// Runs the existing Process Pool child lifecycle inside one Kubernetes Pod while preserving
    /// the exact Pod UID and planned initial runtime identities.
    /// </summary>
    public sealed class AiKubernetesRuntimePoolInPodManager :
        IAiRuntimeProcessPoolManager
    {
        private readonly AiKubernetesRuntimePoolInPodOptions options;
        private readonly IAiRuntimeProcessPoolChildFactory childFactory;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly SortedDictionary<int, IAiRuntimeProcessPoolChild>
            children = new();
        private readonly IReadOnlyDictionary<int, string>
            plannedInitialRuntimeIds;
        private int nextOrdinal;
        private bool shutdownRequested;
        private AiRuntimeProcessPoolManagerStatus status =
            AiRuntimeProcessPoolManagerStatus.Created;

        /// <summary>
        /// Initializes a new in-Pod Runtime Pool Manager.
        /// </summary>
        public AiKubernetesRuntimePoolInPodManager(
            AiKubernetesRuntimePoolInPodOptions options,
            string hostId,
            IAiRuntimeProcessPoolChildFactory childFactory)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            ArgumentNullException.ThrowIfNull(childFactory);

            AiKubernetesRuntimePoolInPodOptionsValidator.Validate(
                options,
                requirePodUidFile: false);

            this.options = options;
            this.childFactory = childFactory;
            this.plannedInitialRuntimeIds =
                options.RuntimeInstances.ToDictionary(
                    item => item.Ordinal,
                    item => item.RuntimeInstanceId);

            this.Identity = new AiRuntimeProcessPoolIdentity
            {
                PoolId = options.PoolId,
                HostId = hostId,
                RuntimeInstanceIdPrefix =
                    options.RuntimeInstanceIdPrefix
            };
        }

        /// <inheritdoc />
        public AiRuntimeProcessPoolIdentity Identity { get; }

        /// <inheritdoc />
        public Task<AiRuntimeProcessPoolSnapshot>
            EnsureInitialCapacityAsync(
                CancellationToken cancellationToken = default)
        {
            return this.EnsureCapacityAsync(
                this.options.InitialProcessCount,
                cancellationToken);
        }

        /// <inheritdoc />
        public async Task<AiRuntimeProcessPoolSnapshot> EnsureCapacityAsync(
            int requiredProcessCount,
            CancellationToken cancellationToken = default)
        {
            if (requiredProcessCount <= 0
                || requiredProcessCount
                    > this.options.MaximumProcessCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredProcessCount));
            }

            await this.lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.shutdownRequested)
                {
                    throw new InvalidOperationException(
                        "The Kubernetes Runtime Pool is stopping.");
                }

                await this.EnsureCapacityUnsafeAsync(
                        requiredProcessCount,
                        cancellationToken)
                    .ConfigureAwait(false);

                return this.CreateSnapshotUnsafe();
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task<AiRuntimeProcessPoolSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            await this.lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                return this.CreateSnapshotUnsafe();
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            await this.lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.status
                    == AiRuntimeProcessPoolManagerStatus.Stopped)
                {
                    return;
                }

                this.shutdownRequested = true;
                this.status =
                    AiRuntimeProcessPoolManagerStatus.Stopping;

                using var stopCancellation =
                    CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);

                stopCancellation.CancelAfter(
                    TimeSpan.FromSeconds(
                        this.options.ShutdownTimeoutSeconds));

                var failures = new List<Exception>();

                foreach (var child in this.children
                    .Values
                    .OrderByDescending(item => item.Ordinal)
                    .ToArray())
                {
                    try
                    {
                        await child
                            .StopAsync(stopCancellation.Token)
                            .ConfigureAwait(false);

                        this.children.Remove(child.Ordinal);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }

                this.status = this.children.Count == 0
                    ? AiRuntimeProcessPoolManagerStatus.Stopped
                    : AiRuntimeProcessPoolManagerStatus.Faulted;

                if (failures.Count > 0)
                {
                    throw new AggregateException(
                        "One or more in-Pod runtime children could not be stopped.",
                        failures);
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Ensures child capacity while the lifecycle gate is held.
        /// </summary>
        private async Task EnsureCapacityUnsafeAsync(
            int requiredProcessCount,
            CancellationToken cancellationToken)
        {
            while (this.children.Count < requiredProcessCount)
            {
                var child =
                    await this.StartNextChildAsync(cancellationToken)
                        .ConfigureAwait(false);

                this.children.Add(child.Ordinal, child);
                _ = this.ObserveCompletionAsync(child);
            }

            this.UpdateStatusUnsafe();
        }

        /// <summary>
        /// Starts the next exact initial child or a fresh replacement child.
        /// </summary>
        private async Task<IAiRuntimeProcessPoolChild>
            StartNextChildAsync(
                CancellationToken cancellationToken)
        {
            var ordinal = checked(this.nextOrdinal + 1);

            var runtimeInstanceId =
                this.plannedInitialRuntimeIds.TryGetValue(
                    ordinal,
                    out var plannedRuntimeId)
                    ? plannedRuntimeId
                    : string.Concat(
                        this.Identity.RuntimeInstanceIdPrefix,
                        "-replacement-",
                        ordinal,
                        "-",
                        Guid.NewGuid().ToString("N"));

            var request = new AiRuntimeProcessPoolChildStartRequest
            {
                PoolId = this.Identity.PoolId,
                HostId = this.Identity.HostId,
                RuntimeInstanceId = runtimeInstanceId,
                Ordinal = ordinal
            };

            var child =
                await this.childFactory
                    .StartAsync(request, cancellationToken)
                    .ConfigureAwait(false);

            ValidateChild(request, child);
            this.nextOrdinal = ordinal;

            return child;
        }

        /// <summary>
        /// Removes an exited child and restores minimum capacity.
        /// </summary>
        private async Task ObserveCompletionAsync(
            IAiRuntimeProcessPoolChild child)
        {
            try
            {
                await child.Completion.ConfigureAwait(false);
            }
            catch
            {
            }

            await this.lifecycleGate
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                if (!this.children.TryGetValue(
                        child.Ordinal,
                        out var tracked)
                    || !ReferenceEquals(tracked, child))
                {
                    return;
                }

                this.children.Remove(child.Ordinal);

                if (this.shutdownRequested)
                {
                    this.status = this.children.Count == 0
                        ? AiRuntimeProcessPoolManagerStatus.Stopped
                        : AiRuntimeProcessPoolManagerStatus.Faulted;
                    return;
                }

                this.UpdateStatusUnsafe();

                try
                {
                    await this.EnsureCapacityUnsafeAsync(
                            this.options.MinimumProcessCount,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    this.status =
                        AiRuntimeProcessPoolManagerStatus.Degraded;
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Creates the current immutable lifecycle snapshot.
        /// </summary>
        private AiRuntimeProcessPoolSnapshot CreateSnapshotUnsafe()
        {
            var childSnapshots =
                this.children
                    .Values
                    .OrderBy(item => item.Ordinal)
                    .Select(
                        item =>
                            new AiRuntimeProcessPoolChildSnapshot
                            {
                                PoolId = item.PoolId,
                                HostId = item.HostId,
                                RuntimeInstanceId =
                                    item.RuntimeInstanceId,
                                Ordinal = item.Ordinal,
                                Status = item.Status
                            })
                    .ToArray();

            return new AiRuntimeProcessPoolSnapshot
            {
                PoolId = this.Identity.PoolId,
                HostId = this.Identity.HostId,
                Status = this.status,
                MinimumProcessCount =
                    this.options.MinimumProcessCount,
                MaximumProcessCount =
                    this.options.MaximumProcessCount,
                IsBelowMinimumCapacity =
                    childSnapshots.Length
                    < this.options.MinimumProcessCount,
                Children = childSnapshots
            };
        }

        /// <summary>
        /// Updates the active status from exact child capacity.
        /// </summary>
        private void UpdateStatusUnsafe()
        {
            if (this.shutdownRequested)
            {
                return;
            }

            this.status =
                this.children.Count
                >= this.options.MinimumProcessCount
                    ? AiRuntimeProcessPoolManagerStatus.Running
                    : AiRuntimeProcessPoolManagerStatus.Degraded;
        }

        /// <summary>
        /// Validates every authoritative child identity.
        /// </summary>
        private static void ValidateChild(
            AiRuntimeProcessPoolChildStartRequest request,
            IAiRuntimeProcessPoolChild child)
        {
            ArgumentNullException.ThrowIfNull(child);

            if (!StringComparer.Ordinal.Equals(
                    request.PoolId,
                    child.PoolId)
                || !StringComparer.Ordinal.Equals(
                    request.HostId,
                    child.HostId)
                || !StringComparer.Ordinal.Equals(
                    request.RuntimeInstanceId,
                    child.RuntimeInstanceId)
                || request.Ordinal != child.Ordinal
                || child.Status
                    != AiRuntimeProcessPoolChildStatus.Running)
            {
                throw new InvalidOperationException(
                    "The in-Pod child factory changed an authoritative runtime identity or status.");
            }
        }
    }
}
