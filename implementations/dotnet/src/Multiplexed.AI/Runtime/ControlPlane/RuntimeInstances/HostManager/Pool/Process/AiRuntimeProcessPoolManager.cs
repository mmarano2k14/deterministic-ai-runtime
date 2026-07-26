using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Manages the deterministic local lifecycle of independently identifiable runtime child
    /// processes in one process-host runtime pool.
    /// </summary>
    /// <remarks>
    /// This manager does not create operating-system processes directly. Child creation and process
    /// completion observation are delegated to <see cref="IAiRuntimeProcessPoolChildFactory"/> and
    /// <see cref="IAiRuntimeProcessPoolChild"/> so lifecycle and replacement behavior can be proven
    /// before the real process adapter is introduced.
    /// </remarks>
    public sealed class AiRuntimeProcessPoolManager : IAiRuntimeProcessPoolManager
    {
        private readonly AiRuntimeProcessPoolOptions options;
        private readonly IAiRuntimeProcessPoolChildFactory childFactory;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly SortedDictionary<int, IAiRuntimeProcessPoolChild> children = new();
        private int nextOrdinal;
        private bool shutdownRequested;
        private AiRuntimeProcessPoolManagerStatus status =
            AiRuntimeProcessPoolManagerStatus.Created;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeProcessPoolManager"/> class.
        /// </summary>
        /// <param name="options">The enabled process pool options.</param>
        /// <param name="childFactory">The child-process factory.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="options"/> or <paramref name="childFactory"/> is
        /// <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the process pool options are disabled.
        /// </exception>
        public AiRuntimeProcessPoolManager(
            AiRuntimeProcessPoolOptions options,
            IAiRuntimeProcessPoolChildFactory childFactory)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(childFactory);

            this.options = CopyOptions(options);
            AiRuntimeProcessPoolOptionsValidator.Validate(this.options);

            if (!this.options.Enabled)
            {
                throw new InvalidOperationException(
                    "The process-host Runtime Pool Manager requires enabled options.");
            }

            this.childFactory = childFactory;
            this.Identity =
                AiRuntimeProcessPoolIdentityFactory.CreatePoolIdentity(this.options);
        }

        /// <inheritdoc />
        public AiRuntimeProcessPoolIdentity Identity { get; }

        /// <inheritdoc />
        public Task<AiRuntimeProcessPoolSnapshot> EnsureInitialCapacityAsync(
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
            ValidateRequiredProcessCount(
                requiredProcessCount,
                this.options.MaximumProcessCount);

            await this.lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                this.ThrowIfCapacityCannotBeEnsured();

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
                if (this.status == AiRuntimeProcessPoolManagerStatus.Stopped)
                {
                    return;
                }

                this.shutdownRequested = true;
                this.status = AiRuntimeProcessPoolManagerStatus.Stopping;

                using var shutdownCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                shutdownCancellation.CancelAfter(
                    TimeSpan.FromSeconds(
                        this.options.ShutdownTimeoutSeconds));

                var failures = new List<Exception>();

                foreach (var ordinal in this.children.Keys.OrderByDescending(value => value).ToArray())
                {
                    var child = this.children[ordinal];

                    try
                    {
                        await child
                            .StopAsync(shutdownCancellation.Token)
                            .ConfigureAwait(false);

                        if (child.Status != AiRuntimeProcessPoolChildStatus.Stopped)
                        {
                            throw new InvalidOperationException(
                                $"Runtime child '{child.RuntimeInstanceId}' did not report Stopped after StopAsync.");
                        }

                        this.children.Remove(ordinal);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(
                            new InvalidOperationException(
                                $"Failed to stop runtime child '{child.RuntimeInstanceId}'.",
                                exception));
                    }
                }

                this.status = this.children.Count == 0
                    ? AiRuntimeProcessPoolManagerStatus.Stopped
                    : AiRuntimeProcessPoolManagerStatus.Faulted;

                if (failures.Count > 0)
                {
                    throw new AggregateException(
                        "One or more runtime pool child processes could not be stopped.",
                        failures);
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Ensures capacity while the lifecycle gate is held.
        /// </summary>
        /// <param name="requiredProcessCount">The required child-process count.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous capacity operation.</returns>
        private async Task EnsureCapacityUnsafeAsync(
            int requiredProcessCount,
            CancellationToken cancellationToken)
        {
            try
            {
                while (this.children.Count < requiredProcessCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var child =
                        await this.StartNextChildAsync(cancellationToken)
                            .ConfigureAwait(false);

                    this.children.Add(child.Ordinal, child);
                    this.ObserveChildCompletion(child);
                }

                this.UpdateActiveStatusUnsafe();
            }
            catch
            {
                this.UpdateActiveStatusUnsafe();
                throw;
            }
        }

        /// <summary>
        /// Starts the next independently identifiable child process.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The validated child-process handle.</returns>
        private async Task<IAiRuntimeProcessPoolChild> StartNextChildAsync(
            CancellationToken cancellationToken)
        {
            var ordinal = checked(this.nextOrdinal + 1);
            var runtimeInstanceId =
                AiRuntimeProcessPoolIdentityFactory.CreateRuntimeInstanceId(
                    this.Identity,
                    ordinal);

            var request = new AiRuntimeProcessPoolChildStartRequest
            {
                PoolId = this.Identity.PoolId,
                HostId = this.Identity.HostId,
                RuntimeInstanceId = runtimeInstanceId,
                Ordinal = ordinal
            };

            var child =
                await this.childFactory
                    .StartAsync(
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (child is null)
            {
                throw new InvalidOperationException(
                    "The runtime pool child factory returned a null child handle.");
            }

            try
            {
                ValidateChild(request, child);
            }
            catch
            {
                await StopInvalidChildBestEffortAsync(child)
                    .ConfigureAwait(false);

                throw;
            }

            this.nextOrdinal = ordinal;
            return child;
        }

        /// <summary>
        /// Starts background observation of one child completion.
        /// </summary>
        /// <param name="child">The tracked child process.</param>
        private void ObserveChildCompletion(
            IAiRuntimeProcessPoolChild child)
        {
            _ = this.ObserveChildCompletionAsync(child);
        }

        /// <summary>
        /// Removes an exited child and restores minimum capacity unless shutdown has started.
        /// </summary>
        /// <param name="child">The tracked child process.</param>
        /// <returns>A task representing asynchronous child completion handling.</returns>
        private async Task ObserveChildCompletionAsync(
            IAiRuntimeProcessPoolChild child)
        {
            try
            {
                await child.Completion.ConfigureAwait(false);
            }
            catch
            {
                // Completion failure still means this child can no longer be treated as safe
                // capacity. The exact adapter failure will be surfaced later through observability.
            }

            await this.lifecycleGate
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                if (!this.children.TryGetValue(child.Ordinal, out var trackedChild) ||
                    !ReferenceEquals(trackedChild, child))
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

                this.UpdateActiveStatusUnsafe();

                try
                {
                    await this.EnsureCapacityUnsafeAsync(
                            this.options.MinimumProcessCount,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    this.status = AiRuntimeProcessPoolManagerStatus.Degraded;
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Creates a stable lifecycle snapshot while the lifecycle gate is held.
        /// </summary>
        /// <returns>The current pool lifecycle snapshot.</returns>
        private AiRuntimeProcessPoolSnapshot CreateSnapshotUnsafe()
        {
            var childSnapshots =
                this.children
                    .Values
                    .OrderBy(child => child.Ordinal)
                    .Select(
                        child =>
                            new AiRuntimeProcessPoolChildSnapshot
                            {
                                PoolId = child.PoolId,
                                HostId = child.HostId,
                                RuntimeInstanceId = child.RuntimeInstanceId,
                                Ordinal = child.Ordinal,
                                Status = child.Status
                            })
                    .ToArray();

            return new AiRuntimeProcessPoolSnapshot
            {
                PoolId = this.Identity.PoolId,
                HostId = this.Identity.HostId,
                Status = this.status,
                MinimumProcessCount = this.options.MinimumProcessCount,
                MaximumProcessCount = this.options.MaximumProcessCount,
                IsBelowMinimumCapacity =
                    childSnapshots.Length < this.options.MinimumProcessCount,
                Children = childSnapshots
            };
        }

        /// <summary>
        /// Updates the active manager status from the current first-class capacity boundary.
        /// </summary>
        private void UpdateActiveStatusUnsafe()
        {
            if (this.shutdownRequested)
            {
                return;
            }

            this.status = this.children.Count >= this.options.MinimumProcessCount
                ? AiRuntimeProcessPoolManagerStatus.Running
                : AiRuntimeProcessPoolManagerStatus.Degraded;
        }

        /// <summary>
        /// Rejects capacity reconciliation after shutdown has started.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when capacity cannot safely be created in the current manager status.
        /// </exception>
        private void ThrowIfCapacityCannotBeEnsured()
        {
            if (!this.shutdownRequested &&
                this.status != AiRuntimeProcessPoolManagerStatus.Stopping &&
                this.status != AiRuntimeProcessPoolManagerStatus.Stopped &&
                this.status != AiRuntimeProcessPoolManagerStatus.Faulted)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Runtime pool capacity cannot be ensured while the manager status is '{this.status}'.");
        }

        /// <summary>
        /// Validates a requested process count.
        /// </summary>
        /// <param name="requiredProcessCount">The required child-process count.</param>
        /// <param name="maximumProcessCount">The configured maximum process count.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the requested count is outside the supported boundary.
        /// </exception>
        private static void ValidateRequiredProcessCount(
            int requiredProcessCount,
            int maximumProcessCount)
        {
            if (requiredProcessCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredProcessCount),
                    requiredProcessCount,
                    "The required process count must be greater than zero.");
            }

            if (requiredProcessCount > maximumProcessCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(requiredProcessCount),
                    requiredProcessCount,
                    "The required process count cannot exceed MaximumProcessCount.");
            }
        }

        /// <summary>
        /// Validates that a child factory preserved every authoritative identity.
        /// </summary>
        /// <param name="request">The authoritative child start request.</param>
        /// <param name="child">The returned child handle.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the child handle changes an authoritative identity or is not running.
        /// </exception>
        private static void ValidateChild(
            AiRuntimeProcessPoolChildStartRequest request,
            IAiRuntimeProcessPoolChild child)
        {
            if (!StringComparer.Ordinal.Equals(request.PoolId, child.PoolId))
            {
                throw new InvalidOperationException(
                    "The runtime child factory returned a different PoolId.");
            }

            if (!StringComparer.Ordinal.Equals(request.HostId, child.HostId))
            {
                throw new InvalidOperationException(
                    "The runtime child factory returned a different HostId.");
            }

            if (!StringComparer.Ordinal.Equals(
                    request.RuntimeInstanceId,
                    child.RuntimeInstanceId))
            {
                throw new InvalidOperationException(
                    "The runtime child factory returned a different RuntimeInstanceId.");
            }

            if (request.Ordinal != child.Ordinal)
            {
                throw new InvalidOperationException(
                    "The runtime child factory returned a different child ordinal.");
            }

            if (child.Status != AiRuntimeProcessPoolChildStatus.Running)
            {
                throw new InvalidOperationException(
                    "The runtime child factory must return a running child handle.");
            }

            if (child.Completion is null)
            {
                throw new InvalidOperationException(
                    "The runtime child factory returned a null completion task.");
            }
        }

        /// <summary>
        /// Attempts to stop an invalid child handle without masking the identity validation failure.
        /// </summary>
        /// <param name="child">The invalid child handle.</param>
        /// <returns>A task representing the best-effort cleanup.</returns>
        private static async Task StopInvalidChildBestEffortAsync(
            IAiRuntimeProcessPoolChild child)
        {
            try
            {
                await child
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The authoritative identity validation exception must remain the surfaced failure.
            }
        }

        /// <summary>
        /// Copies mutable options so later caller mutation cannot change manager correctness.
        /// </summary>
        /// <param name="options">The source options.</param>
        /// <returns>An isolated options copy.</returns>
        private static AiRuntimeProcessPoolOptions CopyOptions(
            AiRuntimeProcessPoolOptions options)
        {
            return new AiRuntimeProcessPoolOptions
            {
                Enabled = options.Enabled,
                PoolId = options.PoolId,
                HostIdPrefix = options.HostIdPrefix,
                RuntimeInstanceIdPrefix = options.RuntimeInstanceIdPrefix,
                InitialProcessCount = options.InitialProcessCount,
                MinimumProcessCount = options.MinimumProcessCount,
                MaximumProcessCount = options.MaximumProcessCount,
                StartupParallelism = options.StartupParallelism,
                ShutdownTimeoutSeconds = options.ShutdownTimeoutSeconds
            };
        }
    }
}
