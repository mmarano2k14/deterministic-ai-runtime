using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Starts a RuntimeInstanceOnly process child and returns it only after authoritative readiness.
    /// </summary>
    /// <remarks>
    /// Readiness is delegated to the existing provider-neutral waiter, which validates registry,
    /// capacity, and transport usability. The factory does not dispatch runs or mutate execution
    /// state.
    /// </remarks>
    public sealed class RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory :
        IAiRuntimeProcessPoolChildFactory
    {
        private readonly IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory planFactory;
        private readonly IAiRuntimeProcessPoolChildProcessLauncher processLauncher;
        private readonly IAiRuntimeInstanceReadinessWaiter readinessWaiter;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory"/> class.
        /// </summary>
        /// <param name="planFactory">The RuntimeInstanceOnly launch plan factory.</param>
        /// <param name="processLauncher">The operating-system child-process launcher.</param>
        /// <param name="readinessWaiter">The provider-neutral runtime readiness waiter.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when any dependency is <see langword="null"/>.
        /// </exception>
        public RuntimeInstanceOnlyAiRuntimeProcessPoolChildFactory(
            IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory planFactory,
            IAiRuntimeProcessPoolChildProcessLauncher processLauncher,
            IAiRuntimeInstanceReadinessWaiter readinessWaiter)
        {
            this.planFactory = planFactory ?? throw new ArgumentNullException(nameof(planFactory));
            this.processLauncher = processLauncher ?? throw new ArgumentNullException(nameof(processLauncher));
            this.readinessWaiter = readinessWaiter ?? throw new ArgumentNullException(nameof(readinessWaiter));
        }

        /// <inheritdoc />
        public async Task<IAiRuntimeProcessPoolChild> StartAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var plan =
                await this.planFactory
                    .CreateAsync(request, cancellationToken)
                    .ConfigureAwait(false);

            AiRuntimeProcessPoolPortLeasedChild? child = null;

            try
            {
                var startedChild =
                    await this.processLauncher
                        .StartAsync(
                            request,
                            plan.ProcessOptions,
                            cancellationToken)
                        .ConfigureAwait(false);

                child =
                    new AiRuntimeProcessPoolPortLeasedChild(
                        startedChild,
                        plan.PortLease);

                var readinessTask =
                    this.readinessWaiter.WaitUntilReadyAsync(
                        plan.ReadinessRequest,
                        cancellationToken);

                var completedTask =
                    await Task.WhenAny(
                            readinessTask,
                            child.Completion)
                        .ConfigureAwait(false);

                if (ReferenceEquals(completedTask, child.Completion))
                {
                    var childExit =
                        await child.Completion.ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Runtime pool child '{request.RuntimeInstanceId}' exited before readiness. Kind={childExit.Kind}, ExitCode={childExit.ExitCode}.");
                }

                var readiness =
                    await readinessTask.ConfigureAwait(false);

                if (!readiness.Success)
                {
                    throw new InvalidOperationException(
                        $"Runtime pool child '{request.RuntimeInstanceId}' did not become ready. Reason={readiness.FailureReason ?? "unknown"}, TimedOut={readiness.TimedOut}.");
                }

                if (!StringComparer.Ordinal.Equals(
                        request.RuntimeInstanceId,
                        readiness.RuntimeInstanceId))
                {
                    throw new InvalidOperationException(
                        "The runtime readiness result returned a different RuntimeInstanceId.");
                }

                if (child.Completion.IsCompleted)
                {
                    var childExit =
                        await child.Completion.ConfigureAwait(false);

                    throw new InvalidOperationException(
                        $"Runtime pool child '{request.RuntimeInstanceId}' completed during readiness. Kind={childExit.Kind}, ExitCode={childExit.ExitCode}.");
                }

                return child;
            }
            catch
            {
                if (child is null)
                {
                    await plan.PortLease.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await StopFailedStartBestEffortAsync(child).ConfigureAwait(false);
                }

                throw;
            }
        }

        /// <summary>
        /// Stops a child that failed readiness without masking the readiness failure.
        /// </summary>
        private static async Task StopFailedStartBestEffortAsync(
            AiRuntimeProcessPoolPortLeasedChild child)
        {
            try
            {
                await child.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await child.Completion.ConfigureAwait(false);
            }
            catch
            {
                // The original launch or readiness exception remains authoritative. A failed stop
                // intentionally keeps the port lease reserved until the child actually completes.
            }
        }
    }
}
