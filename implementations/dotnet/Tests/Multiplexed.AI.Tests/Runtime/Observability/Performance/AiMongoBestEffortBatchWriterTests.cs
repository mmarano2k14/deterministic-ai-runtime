using Multiplexed.AI.Runtime.Observability.Performance;
using System.Collections.Concurrent;
using Xunit;

namespace Multiplexed.AI.Tests.Runtime.Observability.Performance
{
    public sealed class AiMongoBestEffortBatchWriterTests
    {
        [Fact]
        public async Task MaximumBatchSize_Should_Flush_One_Physical_Batch()
        {
            var flushes = new ConcurrentQueue<string[]>();

            await using var writer = CreateWriter(
                capacity: 16,
                maxBatchSize: 4,
                maxFlushInterval: TimeSpan.FromSeconds(10),
                flushAsync: (documents, _) =>
                {
                    flushes.Enqueue(documents.ToArray());
                    return Task.CompletedTask;
                });

            Assert.True(writer.TryEnqueue("a"));
            Assert.True(writer.TryEnqueue("b"));
            Assert.True(writer.TryEnqueue("c"));
            Assert.True(writer.TryEnqueue("d"));

            await writer.FlushAsync();

            var batch = Assert.Single(flushes);
            Assert.Equal(new[] { "a", "b", "c", "d" }, batch);
        }

        [Fact]
        public async Task FlushAsync_Should_Flush_All_Preceding_Accepted_Records()
        {
            var persisted = new ConcurrentQueue<string>();

            await using var writer = CreateWriter(
                capacity: 16,
                maxBatchSize: 8,
                maxFlushInterval: TimeSpan.FromSeconds(10),
                flushAsync: (documents, _) =>
                {
                    foreach (var document in documents)
                    {
                        persisted.Enqueue(document);
                    }

                    return Task.CompletedTask;
                });

            Assert.True(writer.TryEnqueue("one"));
            Assert.True(writer.TryEnqueue("two"));
            Assert.True(writer.TryEnqueue("three"));

            await writer.FlushAsync();

            Assert.Equal(
                new[] { "one", "two", "three" },
                persisted.ToArray());
        }

        [Fact]
        public async Task TimedFlush_Should_Not_Require_A_Full_Batch()
        {
            var persisted = new TaskCompletionSource<string[]>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await using var writer = CreateWriter(
                capacity: 16,
                maxBatchSize: 8,
                maxFlushInterval: TimeSpan.FromMilliseconds(20),
                flushAsync: (documents, _) =>
                {
                    persisted.TrySetResult(documents.ToArray());
                    return Task.CompletedTask;
                });

            Assert.True(writer.TryEnqueue("timed"));

            var batch = await persisted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(new[] { "timed" }, batch);
        }

        [Fact]
        public async Task FullQueue_Should_Drop_Without_Blocking_Producer()
        {
            var flushStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFlush = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await using var writer = CreateWriter(
                capacity: 2,
                maxBatchSize: 1,
                maxFlushInterval: TimeSpan.FromSeconds(10),
                flushAsync: async (_, cancellationToken) =>
                {
                    flushStarted.TrySetResult(true);
                    await releaseFlush.Task.WaitAsync(cancellationToken);
                });

            Assert.True(writer.TryEnqueue("one"));
            await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(writer.TryEnqueue("two"));
            Assert.True(writer.TryEnqueue("three"));
            Assert.False(writer.TryEnqueue("four"));

            releaseFlush.TrySetResult(true);
            await writer.FlushAsync();
        }

        [Fact]
        public async Task GracefulDispose_Should_Drain_Partial_Batch()
        {
            var persisted = new ConcurrentQueue<string>();

            var writer = CreateWriter(
                capacity: 16,
                maxBatchSize: 8,
                maxFlushInterval: TimeSpan.FromSeconds(10),
                flushAsync: (documents, _) =>
                {
                    foreach (var document in documents)
                    {
                        persisted.Enqueue(document);
                    }

                    return Task.CompletedTask;
                });

            Assert.True(writer.TryEnqueue("one"));
            Assert.True(writer.TryEnqueue("two"));

            await writer.DisposeAsync();

            Assert.Equal(new[] { "one", "two" }, persisted.ToArray());
        }

        [Fact]
        public async Task FlushFailure_Should_Not_Terminate_Worker()
        {
            var invocation = 0;
            var persisted = new ConcurrentQueue<string>();

            await using var writer = CreateWriter(
                capacity: 16,
                maxBatchSize: 1,
                maxFlushInterval: TimeSpan.FromSeconds(10),
                flushAsync: (documents, _) =>
                {
                    if (Interlocked.Increment(ref invocation) == 1)
                    {
                        throw new InvalidOperationException("synthetic best-effort flush failure");
                    }

                    foreach (var document in documents)
                    {
                        persisted.Enqueue(document);
                    }

                    return Task.CompletedTask;
                });

            Assert.True(writer.TryEnqueue("lost-best-effort"));
            await writer.FlushAsync();

            Assert.True(writer.TryEnqueue("survives"));
            await writer.FlushAsync();

            Assert.Equal(new[] { "survives" }, persisted.ToArray());
        }

        private static AiMongoBestEffortBatchWriter<string> CreateWriter(
            int capacity,
            int maxBatchSize,
            TimeSpan maxFlushInterval,
            Func<IReadOnlyList<string>, CancellationToken, Task> flushAsync)
        {
            return new AiMongoBestEffortBatchWriter<string>(
                storeName: "trace",
                capacity: capacity,
                maxBatchSize: maxBatchSize,
                maxFlushInterval: maxFlushInterval,
                flushAsync: flushAsync,
                shutdownTimeout: TimeSpan.FromSeconds(1));
        }
    }
}
