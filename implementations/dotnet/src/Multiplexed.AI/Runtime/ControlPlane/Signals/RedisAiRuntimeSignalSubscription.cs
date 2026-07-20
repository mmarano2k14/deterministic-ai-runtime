using Microsoft.Extensions.Logging;
using Multiplexed.Abstractions.AI.ControlPlane.Signals;
using StackExchange.Redis;
using System.Threading.Channels;

namespace Multiplexed.AI.Runtime.ControlPlane.Signals
{
    /// <summary>
    /// Represents an active Redis Pub/Sub runtime signal subscription.
    /// </summary>
    internal sealed class RedisAiRuntimeSignalSubscription : IAiRuntimeSignalSubscription
    {
        private readonly ISubscriber _subscriber;
        private readonly RedisChannel _redisChannel;
        private readonly Action<RedisChannel, RedisValue> _handler;
        private readonly Channel<AiRuntimeSignal> _buffer;
        private readonly ILogger _logger;
        private int _disposed;

        public RedisAiRuntimeSignalSubscription(
            ISubscriber subscriber,
            RedisChannel redisChannel,
            Action<RedisChannel, RedisValue> handler,
            Channel<AiRuntimeSignal> buffer,
            ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(subscriber);
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentNullException.ThrowIfNull(logger);

            _subscriber = subscriber;
            _redisChannel = redisChannel;
            _handler = handler;
            _buffer = buffer;
            _logger = logger;
        }

        /// <inheritdoc />
        public IAsyncEnumerable<AiRuntimeSignal> ReadAllAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);

            return _buffer.Reader.ReadAllAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _buffer.Writer.TryComplete();

            try
            {
                await _subscriber
                    .UnsubscribeAsync(
                        _redisChannel,
                        _handler)
                    .ConfigureAwait(false);
            }
            catch (RedisException exception)
            {
                _logger.LogWarning(
                    exception,
                    "[AI RUNTIME SIGNAL] Redis signal unsubscription failed. Channel='{Channel}'.",
                    _redisChannel);
            }
        }
    }
}