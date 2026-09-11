using System.Threading.Channels;

namespace Multiplexed.AI.Runtime.Observability.Performance
{
    /// <summary>
    /// Provides bounded, non-blocking best-effort batching for observational MongoDB records.
    /// </summary>
    /// <typeparam name="TDocument">The MongoDB document type.</typeparam>
    /// <remarks>
    /// <para>
    /// Hot-path producers use <see cref="TryEnqueue"/> and never wait for queue capacity.
    /// When the bounded queue is full, the record is deliberately dropped and a bounded
    /// diagnostic metric is emitted.
    /// </para>
    /// <para>
    /// Reads may call <see cref="FlushAsync"/> to establish a local read-after-enqueue
    /// barrier. Graceful disposal completes the queue and drains the remaining batch.
    /// Physical process termination may lose records still buffered in this best-effort
    /// queue; the maximum in-memory backlog is bounded by the configured capacity.
    /// </para>
    /// </remarks>
    public sealed class AiMongoBestEffortBatchWriter<TDocument> : IDisposable, IAsyncDisposable
        where TDocument : class
    {
        private readonly string _storeName;
        private readonly Channel<WorkItem> _channel;
        private readonly int _maxBatchSize;
        private readonly TimeSpan _maxFlushInterval;
        private readonly TimeSpan _shutdownTimeout;
        private readonly Func<IReadOnlyList<TDocument>, CancellationToken, Task> _flushAsync;
        private readonly CancellationTokenSource _shutdownCancellation = new();
        private readonly Task _workerTask;
        private int _disposeStarted;

        /// <summary>
        /// Initializes a new bounded best-effort batch writer.
        /// </summary>
        public AiMongoBestEffortBatchWriter(
            string storeName,
            int capacity,
            int maxBatchSize,
            TimeSpan maxFlushInterval,
            Func<IReadOnlyList<TDocument>, CancellationToken, Task> flushAsync,
            TimeSpan? shutdownTimeout = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
            ArgumentNullException.ThrowIfNull(flushAsync);

            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Batch queue capacity must be greater than zero.");
            }

            if (maxBatchSize <= 0 || maxBatchSize > capacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxBatchSize),
                    maxBatchSize,
                    "Batch size must be greater than zero and no larger than the queue capacity.");
            }

            if (maxFlushInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFlushInterval),
                    maxFlushInterval,
                    "Flush interval must be greater than zero.");
            }

            var resolvedShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);

            if (resolvedShutdownTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shutdownTimeout),
                    resolvedShutdownTimeout,
                    "Shutdown timeout must be greater than zero.");
            }

            _storeName = storeName.Trim();
            _maxBatchSize = maxBatchSize;
            _maxFlushInterval = maxFlushInterval;
            _shutdownTimeout = resolvedShutdownTimeout;
            _flushAsync = flushAsync;

            _channel = Channel.CreateBounded<WorkItem>(
                new BoundedChannelOptions(capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                    FullMode = BoundedChannelFullMode.Wait
                });

            _workerTask = Task.Run(
                () => RunAsync(_shutdownCancellation.Token));
        }

        /// <summary>
        /// Attempts to enqueue one observational document without waiting for capacity.
        /// </summary>
        /// <param name="document">The document to enqueue.</param>
        /// <returns>
        /// <c>true</c> when the record was accepted; <c>false</c> when the queue was full
        /// or already completed.
        /// </returns>
        public bool TryEnqueue(
            TDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            if (Volatile.Read(ref _disposeStarted) != 0 ||
                !_channel.Writer.TryWrite(WorkItem.ForDocument(document)))
            {
                AiMongoBestEffortBatchingMetrics.RecordDropped(_storeName, 1);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Flushes all records accepted before this barrier.
        /// </summary>
        /// <remarks>
        /// This path may wait for queue capacity because it is intended for reads,
        /// diagnostics, and shutdown rather than the execution hot path.
        /// </remarks>
        public async Task FlushAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                await ObserveWorkerCompletionAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await _channel.Writer
                    .WriteAsync(
                        WorkItem.ForBarrier(completion),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                await ObserveWorkerCompletionAsync(cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            DisposeAsync()
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _channel.Writer.TryComplete();

            try
            {
                await _workerTask
                    .WaitAsync(_shutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _shutdownCancellation.Cancel();

                try
                {
                    await _workerTask
                        .WaitAsync(TimeSpan.FromSeconds(1))
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // The flush callback is required to honor cancellation. If an external
                    // implementation does not, disposal remains bounded and the process may
                    // terminate with a best-effort batch still in flight.
                }
                catch (OperationCanceledException)
                {
                    // Expected after bounded shutdown cancellation.
                }
            }
            catch (OperationCanceledException)
            {
                // Expected only when bounded shutdown cancellation wins the race.
            }
            finally
            {
                if (_workerTask.IsCompleted)
                {
                    _shutdownCancellation.Dispose();
                }
            }
        }

        private async Task RunAsync(
            CancellationToken cancellationToken)
        {
            var batch = new List<TDocument>(_maxBatchSize);
            DateTimeOffset? flushDeadlineUtc = null;

            try
            {
                while (true)
                {
                    if (batch.Count == 0)
                    {
                        var canRead = await _channel.Reader
                            .WaitToReadAsync(cancellationToken)
                            .ConfigureAwait(false);

                        if (!canRead)
                        {
                            break;
                        }

                        await DrainAvailableAsync(
                                batch,
                                deadline => flushDeadlineUtc = deadline,
                                cancellationToken)
                            .ConfigureAwait(false);

                        continue;
                    }

                    var remaining = flushDeadlineUtc.GetValueOrDefault() - DateTimeOffset.UtcNow;

                    if (remaining <= TimeSpan.Zero)
                    {
                        await FlushBatchBestEffortAsync(batch, cancellationToken)
                            .ConfigureAwait(false);
                        flushDeadlineUtc = null;
                        continue;
                    }

                    var readTask = _channel.Reader
                        .WaitToReadAsync(cancellationToken)
                        .AsTask();
                    var delayTask = Task.Delay(remaining, cancellationToken);
                    var completedTask = await Task
                        .WhenAny(readTask, delayTask)
                        .ConfigureAwait(false);

                    if (ReferenceEquals(completedTask, delayTask))
                    {
                        await delayTask.ConfigureAwait(false);
                        await FlushBatchBestEffortAsync(batch, cancellationToken)
                            .ConfigureAwait(false);
                        flushDeadlineUtc = null;
                        continue;
                    }

                    if (!await readTask.ConfigureAwait(false))
                    {
                        await FlushBatchBestEffortAsync(batch, cancellationToken)
                            .ConfigureAwait(false);
                        flushDeadlineUtc = null;
                        break;
                    }

                    await DrainAvailableAsync(
                            batch,
                            deadline => flushDeadlineUtc = deadline,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (batch.Count > 0)
                {
                    await FlushBatchBestEffortAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (batch.Count > 0)
                {
                    AiMongoBestEffortBatchingMetrics.RecordDropped(
                        _storeName,
                        batch.Count);
                    batch.Clear();
                }

                CancelPendingBarriers(cancellationToken);
            }
            catch
            {
                if (batch.Count > 0)
                {
                    AiMongoBestEffortBatchingMetrics.RecordDropped(
                        _storeName,
                        batch.Count);
                    batch.Clear();
                }

                FailPendingBarriers();
            }
        }

        private async Task DrainAvailableAsync(
            List<TDocument> batch,
            Action<DateTimeOffset?> setFlushDeadlineUtc,
            CancellationToken cancellationToken)
        {
            while (_channel.Reader.TryRead(out var item))
            {
                if (item.Barrier is not null)
                {
                    await FlushBatchBestEffortAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                    setFlushDeadlineUtc(null);
                    item.Barrier.TrySetResult(true);
                    continue;
                }

                if (item.Document is null)
                {
                    continue;
                }

                if (batch.Count == 0)
                {
                    setFlushDeadlineUtc(DateTimeOffset.UtcNow + _maxFlushInterval);
                }

                batch.Add(item.Document);

                if (batch.Count >= _maxBatchSize)
                {
                    await FlushBatchBestEffortAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                    setFlushDeadlineUtc(null);
                }
            }
        }

        private async Task FlushBatchBestEffortAsync(
            List<TDocument> batch,
            CancellationToken cancellationToken)
        {
            if (batch.Count == 0)
            {
                return;
            }

            var documents = batch.ToArray();
            batch.Clear();

            try
            {
                await _flushAsync(documents, cancellationToken)
                    .ConfigureAwait(false);
                AiMongoBestEffortBatchingMetrics.RecordBatchSize(
                    _storeName,
                    documents.LongLength);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                AiMongoBestEffortBatchingMetrics.RecordDropped(
                    _storeName,
                    documents.LongLength);
                throw;
            }
            catch
            {
                AiMongoBestEffortBatchingMetrics.RecordFlushFailure(_storeName);
                AiMongoBestEffortBatchingMetrics.RecordDropped(
                    _storeName,
                    documents.LongLength);
            }
        }

        private async Task ObserveWorkerCompletionAsync(
            CancellationToken cancellationToken)
        {
            if (_workerTask.IsCompleted)
            {
                await _workerTask.ConfigureAwait(false);
                return;
            }

            await _workerTask
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private void CancelPendingBarriers(
            CancellationToken cancellationToken)
        {
            while (_channel.Reader.TryRead(out var item))
            {
                if (item.Barrier is not null)
                {
                    item.Barrier.TrySetCanceled(cancellationToken);
                }
                else if (item.Document is not null)
                {
                    AiMongoBestEffortBatchingMetrics.RecordDropped(_storeName, 1);
                }
            }
        }

        private void FailPendingBarriers()
        {
            var exception = new InvalidOperationException(
                "The best-effort MongoDB batch worker terminated unexpectedly.");

            while (_channel.Reader.TryRead(out var item))
            {
                if (item.Barrier is not null)
                {
                    item.Barrier.TrySetException(exception);
                }
                else if (item.Document is not null)
                {
                    AiMongoBestEffortBatchingMetrics.RecordDropped(_storeName, 1);
                }
            }
        }

        private readonly record struct WorkItem(
            TDocument? Document,
            TaskCompletionSource<bool>? Barrier)
        {
            public static WorkItem ForDocument(
                TDocument document)
            {
                return new WorkItem(document, null);
            }

            public static WorkItem ForBarrier(
                TaskCompletionSource<bool> completion)
            {
                return new WorkItem(null, completion);
            }
        }
    }
}
