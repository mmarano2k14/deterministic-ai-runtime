using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using StackExchange.Redis;
using System.Text.Json;

namespace Multiplexed.AI.Runtime.ControlPlane.Signals
{
    /// <summary>
    /// Publishes lightweight internal runtime signals through Redis Pub/Sub.
    /// </summary>
    /// <remarks>
    /// Signals are best-effort notifications only. A Redis publication failure
    /// must never invalidate a durable runtime state transition.
    /// </remarks>
    public sealed class RedisAiRuntimeSignalPublisher : IAiRuntimeSignalPublisher
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IConnectionMultiplexer _multiplexer;
        private readonly ILogger<RedisAiRuntimeSignalPublisher> _logger;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="RedisAiRuntimeSignalPublisher"/> class.
        /// </summary>
        /// <param name="multiplexer">The Redis connection multiplexer.</param>
        /// <param name="logger">The publisher logger.</param>
        public RedisAiRuntimeSignalPublisher(
            IConnectionMultiplexer multiplexer,
            ILogger<RedisAiRuntimeSignalPublisher> logger)
        {
            ArgumentNullException.ThrowIfNull(multiplexer);
            ArgumentNullException.ThrowIfNull(logger);

            _multiplexer = multiplexer;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task PublishAsync(
            AiRuntimeSignal signal,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(signal);
            ArgumentException.ThrowIfNullOrWhiteSpace(signal.ControlPlaneId);

            cancellationToken.ThrowIfCancellationRequested();

            var subjectId = ResolveSubjectId(signal);

            var redisChannel = RedisAiRuntimeSignalChannel.Resolve(
                signal.Type,
                signal.ControlPlaneId,
                subjectId);

            var payload = JsonSerializer.Serialize(
                signal,
                SerializerOptions);

            _logger.LogInformation(
                "[AI RUNTIME SIGNAL][PUBLISH REQUESTED] " +
                "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', " +
                "SubjectId='{SubjectId}', ExecutionId='{ExecutionId}', " +
                "SharedRunId='{SharedRunId}', RuntimeInstanceId='{RuntimeInstanceId}', " +
                "CompletedStepCount='{CompletedStepCount}', TotalStepCount='{TotalStepCount}', " +
                "RedisChannel='{RedisChannel}', MultiplexerConnected='{MultiplexerConnected}'.",
                signal.Type,
                signal.ControlPlaneId,
                subjectId,
                signal.ExecutionId,
                signal.SharedRunId,
                signal.RuntimeInstanceId,
                signal.CompletedStepCount,
                signal.TotalStepCount,
                redisChannel.ToString(),
                _multiplexer.IsConnected);

            try
            {
                var redisSubscriber = _multiplexer.GetSubscriber();

                /*
                 * CommandFlags.None is intentional.
                 *
                 * The returned task completes only after Redis has processed the
                 * PUBLISH command and returned the number of matching subscribers.
                 * This is not FireAndForget.
                 */
                var subscriberCount = await redisSubscriber
                    .PublishAsync(
                        redisChannel,
                        payload,
                        CommandFlags.None)
                    .ConfigureAwait(false);

                if (subscriberCount == 0)
                {
                    _logger.LogWarning(
                        "[AI RUNTIME SIGNAL][PUBLISHED WITHOUT SUBSCRIBER] " +
                        "Redis processed the signal publication but reported no active subscriber. " +
                        "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', " +
                        "SubjectId='{SubjectId}', ExecutionId='{ExecutionId}', " +
                        "SharedRunId='{SharedRunId}', RedisChannel='{RedisChannel}'.",
                        signal.Type,
                        signal.ControlPlaneId,
                        subjectId,
                        signal.ExecutionId,
                        signal.SharedRunId,
                        redisChannel.ToString());

                    return;
                }

                _logger.LogInformation(
                    "[AI RUNTIME SIGNAL][PUBLISH COMPLETED] " +
                    "Redis processed the runtime signal publication. " +
                    "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', " +
                    "SubjectId='{SubjectId}', ExecutionId='{ExecutionId}', " +
                    "SharedRunId='{SharedRunId}', RedisChannel='{RedisChannel}', " +
                    "SubscriberCount='{SubscriberCount}'.",
                    signal.Type,
                    signal.ControlPlaneId,
                    subjectId,
                    signal.ExecutionId,
                    signal.SharedRunId,
                    redisChannel.ToString(),
                    subscriberCount);
            }
            catch (RedisException exception)
            {
                /*
                 * Publication is deliberately best-effort. Durable state remains
                 * authoritative and the hybrid waiter retains its durable fallback.
                 */
                _logger.LogWarning(
                    exception,
                    "[AI RUNTIME SIGNAL][PUBLISH FAILED] " +
                    "Redis signal publication failed. " +
                    "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', " +
                    "SubjectId='{SubjectId}', ExecutionId='{ExecutionId}', " +
                    "SharedRunId='{SharedRunId}', RedisChannel='{RedisChannel}'.",
                    signal.Type,
                    signal.ControlPlaneId,
                    subjectId,
                    signal.ExecutionId,
                    signal.SharedRunId,
                    redisChannel.ToString());
            }
        }

        /// <summary>
        /// Resolves the durable subject identifier associated with the signal.
        /// </summary>
        /// <param name="signal">The runtime signal.</param>
        /// <returns>The durable subject identifier.</returns>
        private static string ResolveSubjectId(
            AiRuntimeSignal signal)
        {
            return signal.Type switch
            {
                AiRuntimeSignalType.DagProgressChanged
                    when !string.IsNullOrWhiteSpace(signal.ExecutionId) =>
                    signal.ExecutionId,

                AiRuntimeSignalType.SharedRunDispatched
                    when !string.IsNullOrWhiteSpace(signal.SharedRunId) =>
                    signal.SharedRunId,

                AiRuntimeSignalType.DagProgressChanged =>
                    throw new InvalidOperationException(
                        "A DAG progress signal requires an execution identifier."),

                AiRuntimeSignalType.SharedRunDispatched =>
                    throw new InvalidOperationException(
                        "A shared-run dispatch signal requires a shared run identifier."),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(signal),
                    signal.Type,
                    "The runtime signal type is not supported.")
            };
        }
    }
}