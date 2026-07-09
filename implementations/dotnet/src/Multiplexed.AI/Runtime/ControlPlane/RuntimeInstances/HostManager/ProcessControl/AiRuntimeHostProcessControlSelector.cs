using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.ProcessControl
{
    /// <summary>
    /// Selects the runtime host process control matching a host creation mode.
    /// </summary>
    public sealed class AiRuntimeHostProcessControlSelector
    {
        private readonly IEnumerable<IAiRuntimeHostCreationStrategy> strategies;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiRuntimeHostProcessControlSelector"/> class.
        /// </summary>
        /// <param name="strategies">The registered runtime host creation strategies.</param>
        public AiRuntimeHostProcessControlSelector(
            IEnumerable<IAiRuntimeHostCreationStrategy> strategies)
        {
            this.strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        }

        /// <summary>
        /// Gets the process control for the selected host creation mode.
        /// </summary>
        /// <param name="hostCreationMode">The host creation mode.</param>
        /// <returns>The matching runtime host process control.</returns>
        public IAiRuntimeHostProcessControl GetRequired(
            AiRuntimeHostCreationMode hostCreationMode)
        {
            var strategy =
                this.strategies
                    .Where(item => item.Mode == hostCreationMode)
                    .OfType<IAiRuntimeHostProcessControl>()
                    .SingleOrDefault();

            if (strategy is not null)
            {
                return strategy;
            }

            throw new NotSupportedException(
                $"Runtime host process control is not registered for host creation mode '{hostCreationMode}'.");
        }
    }
}