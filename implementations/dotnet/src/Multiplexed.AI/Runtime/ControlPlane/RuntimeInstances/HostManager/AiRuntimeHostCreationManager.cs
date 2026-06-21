using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager
{
    /// <summary>
    /// Selects the host creation strategy matching the requested host creation mode.
    /// </summary>
    public sealed class AiRuntimeHostCreationManager : IAiRuntimeHostManager
    {
        /// <summary>
        /// The registered host creation strategies indexed by host creation mode.
        /// </summary>
        private readonly IReadOnlyDictionary<AiRuntimeHostCreationMode, IAiRuntimeHostCreationStrategy> strategies;

        /// <summary>
        /// The logger used to report host creation selection failures.
        /// </summary>
        private readonly ILogger<AiRuntimeHostCreationManager> logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostCreationManager"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        /// <param name="logger">The logger used to report host creation selection failures.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="strategies"/> or <paramref name="logger"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown when multiple strategies are registered for the same host creation mode.</exception>
        public AiRuntimeHostCreationManager(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies,
            ILogger<AiRuntimeHostCreationManager> logger)
        {
            ArgumentNullException.ThrowIfNull(strategies);

            var strategyList = strategies.ToList();
            var duplicatedMode = strategyList
                .GroupBy(strategy => strategy.Mode)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicatedMode is not null)
            {
                throw new InvalidOperationException(
                    $"Multiple runtime host creation strategies are registered for mode '{duplicatedMode.Key}'.");
            }

            this.strategies = strategyList.ToDictionary(strategy => strategy.Mode);
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Task<AiRuntimeHostStartResult> StartRuntimeAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!this.strategies.TryGetValue(request.HostCreationMode, out var strategy))
            {
                this.logger.LogWarning(
                    "No runtime host creation strategy is registered for mode {HostCreationMode}. RuntimeInstanceId={RuntimeInstanceId}, ProviderName={ProviderName}.",
                    request.HostCreationMode,
                    request.RuntimeInstanceId,
                    request.ProviderName);

                return Task.FromResult(AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    $"runtime-host-creation-mode-not-registered:{request.HostCreationMode}"));
            }

            return strategy.StartAsync(request, cancellationToken);
        }
    }
}