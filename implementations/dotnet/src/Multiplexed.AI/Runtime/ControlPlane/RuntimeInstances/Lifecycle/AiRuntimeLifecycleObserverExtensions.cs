using System;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Lifecycle;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Lifecycle
{
    /// <summary>
    /// Provides the narrow canonical emission adapter used by runtime lifecycle producers.
    /// </summary>
    public static class AiRuntimeLifecycleObserverExtensions
    {
        /// <summary>
        /// Emits one already-materialized runtime lifecycle fact through the existing Event Manager.
        /// </summary>
        public static Task RecordLifecycleAsync(
            this IAiControlPlaneObserver observer,
            AiRuntimeLifecycleEvent lifecycleEvent,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(observer);
            ArgumentNullException.ThrowIfNull(lifecycleEvent);

            return observer.RecordAsync(
                AiRuntimeLifecycleEngineEventFactory.Create(lifecycleEvent),
                cancellationToken);
        }
    }
}
