using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using StackExchange.Redis;
using System.Text.Json;
using System.Threading.Channels;

namespace Multiplexed.AI.Runtime.ControlPlane.Signals
{
    /// <summary>
    /// Subscribes to lightweight internal runtime signals through Redis Pub/Sub.
    /// </summary>
    public sealed class RedisAiRuntimeSignalSubscriber : IAiRuntimeSignalSubscriber
    {
        private static readonly JsonSerializerOptions SerializerOptions =
            new(JsonSerializerDefaults.Web);

        private readonly IConnectionMultiplexer _multiplexer;
        private readonly ILogger<RedisAiRuntimeSignalSubscriber> _logger;

        public RedisAiRuntimeSignalSubscriber(
            IConnectionMultiplexer multiplexer,
            ILogger<RedisAiRuntimeSignalSubscriber> logger)
        {
            ArgumentNullException.ThrowIfNull(multiplexer);
            ArgumentNullException.ThrowIfNull(logger);

            _multiplexer = multiplexer;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<IAiRuntimeSignalSubscription> SubscribeAsync(
            AiRuntimeSignalType signalType,
            string controlPlaneId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

            cancellationToken.ThrowIfCancellationRequested();

            var redisChannel = RedisAiRuntimeSignalChannel.Resolve(
                signalType,
                controlPlaneId,
                subjectId);

            _logger.LogInformation(
                "[AI RUNTIME SIGNAL][SUBSCRIBE REQUESTED] " +
                "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                signalType,
                controlPlaneId,
                subjectId,
                redisChannel.ToString());

            var buffer = Channel.CreateBounded<AiRuntimeSignal>(
                new BoundedChannelOptions(64)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.DropOldest
                });

            Action<RedisChannel, RedisValue> handler =
                (receivedChannel, rawPayload) =>
                {
                    try
                    {
                        if (!rawPayload.HasValue)
                        {
                            _logger.LogDebug(
                                "[AI RUNTIME SIGNAL][EMPTY PAYLOAD] " +
                                "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                                signalType,
                                controlPlaneId,
                                subjectId,
                                receivedChannel.ToString());

                            return;
                        }

                        var signal = JsonSerializer.Deserialize<AiRuntimeSignal>(
                            (string)rawPayload!,
                            SerializerOptions);

                        if (signal is null)
                        {
                            _logger.LogWarning(
                                "[AI RUNTIME SIGNAL][NULL PAYLOAD] " +
                                "A Redis signal payload was deserialized as null. " +
                                "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                                signalType,
                                controlPlaneId,
                                subjectId,
                                receivedChannel.ToString());

                            return;
                        }

                        var typeMatches =
                            signal.Type == signalType;

                        var controlPlaneMatches =
                            string.Equals(
                                signal.ControlPlaneId,
                                controlPlaneId,
                                StringComparison.Ordinal);

                        var subjectMatches =
                            MatchesSubject(
                                signal,
                                signalType,
                                subjectId);

                        if (!typeMatches ||
                            !controlPlaneMatches ||
                            !subjectMatches)
                        {
                            _logger.LogWarning(
                                "[AI RUNTIME SIGNAL][PAYLOAD MISMATCH] " +
                                "A signal was received on a targeted Redis channel but did not match the expected identity. " +
                                "ExpectedSignalType='{ExpectedSignalType}', ActualSignalType='{ActualSignalType}', " +
                                "ExpectedControlPlaneId='{ExpectedControlPlaneId}', ActualControlPlaneId='{ActualControlPlaneId}', " +
                                "ExpectedSubjectId='{ExpectedSubjectId}', ActualExecutionId='{ActualExecutionId}', ActualSharedRunId='{ActualSharedRunId}', " +
                                "RedisChannel='{RedisChannel}', TypeMatches='{TypeMatches}', ControlPlaneMatches='{ControlPlaneMatches}', SubjectMatches='{SubjectMatches}'.",
                                signalType,
                                signal.Type,
                                controlPlaneId,
                                signal.ControlPlaneId,
                                subjectId,
                                signal.ExecutionId,
                                signal.SharedRunId,
                                receivedChannel.ToString(),
                                typeMatches,
                                controlPlaneMatches,
                                subjectMatches);

                            return;
                        }

                        var accepted =
                            buffer.Writer.TryWrite(signal);

                        _logger.LogDebug(
                            "[AI RUNTIME SIGNAL][SIGNAL ACCEPTED] " +
                            "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', " +
                            "ExecutionId='{ExecutionId}', SharedRunId='{SharedRunId}', RuntimeInstanceId='{RuntimeInstanceId}', " +
                            "CompletedStepCount='{CompletedStepCount}', TotalStepCount='{TotalStepCount}', " +
                            "RedisChannel='{RedisChannel}', BufferAccepted='{BufferAccepted}'.",
                            signal.Type,
                            signal.ControlPlaneId,
                            subjectId,
                            signal.ExecutionId,
                            signal.SharedRunId,
                            signal.RuntimeInstanceId,
                            signal.CompletedStepCount,
                            signal.TotalStepCount,
                            receivedChannel.ToString(),
                            accepted);

                        if (!accepted)
                        {
                            _logger.LogWarning(
                                "[AI RUNTIME SIGNAL][BUFFER REJECTED] " +
                                "A matching runtime signal could not be written to the local subscription buffer. " +
                                "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                                signalType,
                                controlPlaneId,
                                subjectId,
                                receivedChannel.ToString());
                        }
                    }
                    catch (JsonException exception)
                    {
                        _logger.LogWarning(
                            exception,
                            "[AI RUNTIME SIGNAL][INVALID PAYLOAD] " +
                            "Invalid Redis signal payload ignored. " +
                            "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                            signalType,
                            controlPlaneId,
                            subjectId,
                            receivedChannel.ToString());
                    }
                    catch (Exception exception)
                    {
                        /*
                         * Redis Pub/Sub callbacks must not throw back into the
                         * subscription infrastructure. Invalid notification handling
                         * cannot affect the durable runtime execution path.
                         */
                        _logger.LogWarning(
                            exception,
                            "[AI RUNTIME SIGNAL][HANDLER FAILED] " +
                            "Runtime signal callback failed and the notification was ignored. " +
                            "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                            signalType,
                            controlPlaneId,
                            subjectId,
                            receivedChannel.ToString());
                    }
                };

            var redisSubscriber =
                _multiplexer.GetSubscriber();

            try
            {
                await redisSubscriber
                    .SubscribeAsync(
                        redisChannel,
                        handler)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "[AI RUNTIME SIGNAL][SUBSCRIBE FAILED] " +
                    "Redis runtime signal subscription could not be activated. " +
                    "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                    signalType,
                    controlPlaneId,
                    subjectId,
                    redisChannel.ToString());

                throw;
            }

            _logger.LogInformation(
                "[AI RUNTIME SIGNAL][SUBSCRIBE ACTIVE] " +
                "Redis confirmed the targeted runtime signal subscription. " +
                "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}', MultiplexerConnected='{MultiplexerConnected}'.",
                signalType,
                controlPlaneId,
                subjectId,
                redisChannel.ToString(),
                _multiplexer.IsConnected);

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "[AI RUNTIME SIGNAL][CANCELLED AFTER SUBSCRIBE] " +
                    "The subscription was activated after cancellation had already been requested. Unsubscribing immediately. " +
                    "SignalType='{SignalType}', ControlPlaneId='{ControlPlaneId}', SubjectId='{SubjectId}', RedisChannel='{RedisChannel}'.",
                    signalType,
                    controlPlaneId,
                    subjectId,
                    redisChannel.ToString());

                await redisSubscriber
                    .UnsubscribeAsync(
                        redisChannel,
                        handler)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
            }

            return new RedisAiRuntimeSignalSubscription(
                redisSubscriber,
                redisChannel,
                handler,
                buffer,
                _logger);
        }

        /// <summary>
        /// Determines whether a signal belongs to the requested durable subject.
        /// </summary>
        /// <param name="signal">The received runtime signal.</param>
        /// <param name="signalType">The expected runtime signal type.</param>
        /// <param name="subjectId">The expected durable subject identifier.</param>
        /// <returns>
        /// <see langword="true"/> when the signal belongs to the requested subject;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        private static bool MatchesSubject(
            AiRuntimeSignal signal,
            AiRuntimeSignalType signalType,
            string subjectId)
        {
            return signalType switch
            {
                AiRuntimeSignalType.DagProgressChanged =>
                    string.Equals(
                        signal.ExecutionId,
                        subjectId,
                        StringComparison.Ordinal),

                AiRuntimeSignalType.SharedRunDispatched =>
                    string.Equals(
                        signal.SharedRunId,
                        subjectId,
                        StringComparison.Ordinal),

                _ => false
            };
        }
    }
}