using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Starts the opt-in process-host Runtime Pool Manager with fully ready initial capacity and
    /// stops every managed child during host shutdown.
    /// </summary>
    public sealed class AiRuntimeProcessPoolHostedService : IHostedService
    {
        private readonly IAiRuntimeProcessPoolManager manager;
        private readonly ILogger<AiRuntimeProcessPoolHostedService> logger;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private bool started;
        private bool stopped;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeProcessPoolHostedService"/> class.
        /// </summary>
        /// <param name="manager">The process-host Runtime Pool Manager.</param>
        /// <param name="logger">The hosted-service logger.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="manager"/> or <paramref name="logger"/> is
        /// <see langword="null"/>.
        /// </exception>
        public AiRuntimeProcessPoolHostedService(
            IAiRuntimeProcessPoolManager manager,
            ILogger<AiRuntimeProcessPoolHostedService> logger)
        {
            this.manager = manager ?? throw new ArgumentNullException(nameof(manager));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public async Task StartAsync(
            CancellationToken cancellationToken)
        {
            await this.lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.started)
                {
                    return;
                }

                if (this.stopped)
                {
                    throw new InvalidOperationException(
                        "The runtime process pool hosted service cannot restart after shutdown.");
                }

                try
                {
                    var snapshot =
                        await this.manager
                            .EnsureInitialCapacityAsync(cancellationToken)
                            .ConfigureAwait(false);

                    ValidateStartedSnapshot(snapshot);

                    this.started = true;

                    this.logger.LogInformation(
                        "Runtime process pool started. PoolId={PoolId}, HostId={HostId}, ChildCount={ChildCount}, MinimumProcessCount={MinimumProcessCount}, MaximumProcessCount={MaximumProcessCount}",
                        snapshot.PoolId,
                        snapshot.HostId,
                        snapshot.Children.Count,
                        snapshot.MinimumProcessCount,
                        snapshot.MaximumProcessCount);
                }
                catch
                {
                    await this.StopAfterFailedStartBestEffortAsync()
                        .ConfigureAwait(false);

                    throw;
                }
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken)
        {
            await this.lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.stopped)
                {
                    return;
                }

                await this.manager
                    .StopAsync(cancellationToken)
                    .ConfigureAwait(false);

                this.started = false;
                this.stopped = true;

                this.logger.LogInformation(
                    "Runtime process pool stopped. PoolId={PoolId}, HostId={HostId}",
                    this.manager.Identity.PoolId,
                    this.manager.Identity.HostId);
            }
            finally
            {
                this.lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Validates the initial-capacity snapshot returned before the host is allowed to finish
        /// startup.
        /// </summary>
        /// <param name="snapshot">The initial process-pool snapshot.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="snapshot"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pool did not establish its configured minimum ready capacity.
        /// </exception>
        private static void ValidateStartedSnapshot(
            AiRuntimeProcessPoolSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (snapshot.Status != AiRuntimeProcessPoolManagerStatus.Running ||
                snapshot.IsBelowMinimumCapacity ||
                snapshot.Children.Count < snapshot.MinimumProcessCount)
            {
                throw new InvalidOperationException(
                    $"The runtime process pool did not establish minimum ready capacity. " +
                    $"Status={snapshot.Status}, ChildCount={snapshot.Children.Count}, " +
                    $"MinimumProcessCount={snapshot.MinimumProcessCount}.");
            }
        }

        /// <summary>
        /// Attempts to clean up partial child capacity after failed host startup without masking
        /// the authoritative startup exception.
        /// </summary>
        private async Task StopAfterFailedStartBestEffortAsync()
        {
            try
            {
                await this.manager
                    .StopAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                this.started = false;
                this.stopped = true;
            }
            catch (Exception exception)
            {
                this.logger.LogError(
                    exception,
                    "Failed to clean up runtime process pool after startup failure. PoolId={PoolId}, HostId={HostId}",
                    this.manager.Identity.PoolId,
                    this.manager.Identity.HostId);
            }
        }
    }
}
