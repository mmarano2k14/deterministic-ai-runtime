using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using StackExchange.Redis;

namespace Multiplexed.AI.Runtime.ControlPlane.Signals
{
    /// <summary>
    /// Resolves Redis Pub/Sub channels for internal runtime state-change signals.
    /// </summary>
    internal static class RedisAiRuntimeSignalChannel
    {
        /// <summary>
        /// Resolves the exact Redis channel for a runtime subject.
        /// </summary>
        public static RedisChannel Resolve(
            AiRuntimeSignalType signalType,
            string controlPlaneId,
            string subjectId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

            var signalSegment = signalType switch
            {
                AiRuntimeSignalType.DagProgressChanged => "dag-progress",
                AiRuntimeSignalType.SharedRunDispatched => "shared-run-dispatched",

                _ => throw new ArgumentOutOfRangeException(
                    nameof(signalType),
                    signalType,
                    "The runtime signal type is not supported.")
            };

            return RedisChannel.Literal(
                $"multiplexed:ai:control-plane:{controlPlaneId}:signals:{signalSegment}:{subjectId}");
        }
    }
}