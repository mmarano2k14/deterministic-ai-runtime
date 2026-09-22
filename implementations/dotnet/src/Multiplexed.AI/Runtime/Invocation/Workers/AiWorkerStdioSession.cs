using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>
    /// Shared private-stdio worker protocol session. Physical process/container launch, attestation,
    /// termination and cleanup remain owned by the concrete transport.
    /// </summary>
    internal static class AiWorkerStdioSession
    {
        private static readonly byte[] NewLine = { (byte)'\n' };

        public static async Task<AiDurableInvocationResult> ExchangeAsync(
            AiWorkerInvocationRequest request,
            ReadOnlyMemory<byte> encodedRequest,
            Stream standardInput,
            Stream standardOutput,
            Action closeStandardInput,
            Func<CancellationToken, Task> heartbeat,
            Func<CancellationToken, Task> awaitTerminalBoundary,
            AiWorkerProcessTransportOptions options,
            TimeProvider timeProvider,
            CancellationToken cancellationToken,
            string prematureEndMessage)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(standardInput);
            ArgumentNullException.ThrowIfNull(standardOutput);
            ArgumentNullException.ThrowIfNull(closeStandardInput);
            ArgumentNullException.ThrowIfNull(heartbeat);
            ArgumentNullException.ThrowIfNull(awaitTerminalBoundary);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(timeProvider);
            if (string.IsNullOrWhiteSpace(prematureEndMessage))
                throw new ArgumentException("A protocol end-of-stream message is required.", nameof(prematureEndMessage));

            var reader = new AiWorkerJsonLineReader(standardOutput, options.MaxFrameBytes);
            var write = WriteRequestAsync();
            Observe(write);
            await write.WaitAsync(options.StartupTimeout, timeProvider, cancellationToken).ConfigureAwait(false);

            var first = await ReadFrameAsync(options.StartupTimeout).ConfigureAwait(false);
            if (first.Type != "ready")
                throw new InvalidOperationException("The worker must acknowledge readiness before results or heartbeats.");

            await heartbeat(cancellationToken).ConfigureAwait(false);

            for (var count = 1; count < options.MaxFrames; count++)
            {
                var frame = await ReadFrameAsync(options.HeartbeatTimeout).ConfigureAwait(false);
                if (frame.Type == "heartbeat")
                {
                    await heartbeat(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (frame.Type != "result" || frame.Result is null)
                    throw new InvalidOperationException("The worker emitted an invalid lifecycle sequence.");

                var eof = reader.ReadAsync(cancellationToken);
                Observe(eof);
                if (await eof.WaitAsync(options.ShutdownTimeout, timeProvider, cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("The worker emitted data after its terminal result.");

                await awaitTerminalBoundary(cancellationToken)
                    .WaitAsync(options.ShutdownTimeout, timeProvider, cancellationToken).ConfigureAwait(false);

                return frame.Result;
            }

            throw new InvalidOperationException("Worker frame count exceeds the configured limit.");

            async Task WriteRequestAsync()
            {
                await standardInput.WriteAsync(encodedRequest, cancellationToken).ConfigureAwait(false);
                await standardInput.WriteAsync(NewLine.AsMemory(), cancellationToken).ConfigureAwait(false);
                await standardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                closeStandardInput();
            }

            async Task<AiWorkerInvocationFrame> ReadFrameAsync(TimeSpan timeout)
            {
                var read = reader.ReadAsync(cancellationToken);
                Observe(read);
                var json = await read.WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false)
                    ?? throw new EndOfStreamException(prematureEndMessage);
                return AiWorkerInvocationProtocol.ReadFrame(json, request);
            }
        }

        private static void Observe(Task task) => _ = task.ContinueWith(
            t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
