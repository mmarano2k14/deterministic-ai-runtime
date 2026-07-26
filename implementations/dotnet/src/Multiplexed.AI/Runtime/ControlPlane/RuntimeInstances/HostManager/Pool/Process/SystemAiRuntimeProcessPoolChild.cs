using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Represents one real operating-system runtime child process.
    /// </summary>
    public sealed class SystemAiRuntimeProcessPoolChild :
        IAiRuntimeProcessPoolChild
    {
        private readonly System.Diagnostics.Process process;
        private readonly bool killEntireProcessTreeOnStop;
        private readonly TimeSpan stopTimeout;
        private readonly SemaphoreSlim stopGate = new(1, 1);
        private readonly Task<string>? standardOutputDrain;
        private readonly Task<string>? standardErrorDrain;
        private readonly Task<AiRuntimeProcessPoolChildExit> completion;
        private int stopRequested;
        private int status =
            (int)AiRuntimeProcessPoolChildStatus.Running;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="SystemAiRuntimeProcessPoolChild"/> class.
        /// </summary>
        /// <param name="process">The started operating-system process.</param>
        /// <param name="request">The authoritative child start request.</param>
        /// <param name="redirectOutput">
        /// A value indicating whether output streams were redirected.
        /// </param>
        /// <param name="killEntireProcessTreeOnStop">
        /// A value indicating whether stop should terminate the full process tree.
        /// </param>
        /// <param name="stopTimeout">The bounded stop timeout.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="process"/> or <paramref name="request"/> is
        /// <see langword="null"/>.
        /// </exception>
        internal SystemAiRuntimeProcessPoolChild(
            System.Diagnostics.Process process,
            AiRuntimeProcessPoolChildStartRequest request,
            bool redirectOutput,
            bool killEntireProcessTreeOnStop,
            TimeSpan stopTimeout)
        {
            ArgumentNullException.ThrowIfNull(process);
            ArgumentNullException.ThrowIfNull(request);

            this.process = process;
            this.PoolId = request.PoolId;
            this.HostId = request.HostId;
            this.RuntimeInstanceId = request.RuntimeInstanceId;
            this.Ordinal = request.Ordinal;
            this.ProcessId = process.Id;
            this.killEntireProcessTreeOnStop =
                killEntireProcessTreeOnStop;
            this.stopTimeout = stopTimeout;

            if (redirectOutput)
            {
                this.standardOutputDrain =
                    process.StandardOutput.ReadToEndAsync();

                this.standardErrorDrain =
                    process.StandardError.ReadToEndAsync();
            }

            this.completion = this.ObserveCompletionAsync();
        }

        /// <inheritdoc />
        public string PoolId { get; }

        /// <inheritdoc />
        public string HostId { get; }

        /// <inheritdoc />
        public string RuntimeInstanceId { get; }

        /// <inheritdoc />
        public int Ordinal { get; }

        /// <summary>
        /// Gets the operating-system process identifier for diagnostics.
        /// </summary>
        /// <remarks>
        /// This identifier is not used for pool membership or recovery correctness.
        /// </remarks>
        public int ProcessId { get; }

        /// <inheritdoc />
        public AiRuntimeProcessPoolChildStatus Status =>
            (AiRuntimeProcessPoolChildStatus)
                Volatile.Read(ref this.status);

        /// <inheritdoc />
        public Task<AiRuntimeProcessPoolChildExit> Completion =>
            this.completion;

        /// <inheritdoc />
        public async Task StopAsync(
            CancellationToken cancellationToken = default)
        {
            await this.stopGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                if (this.completion.IsCompleted)
                {
                    await this.completion
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                if (HasExited(this.process))
                {
                    await this.completion
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }

                Interlocked.Exchange(ref this.stopRequested, 1);
                Volatile.Write(
                    ref this.status,
                    (int)AiRuntimeProcessPoolChildStatus.Stopping);

                try
                {
                    this.process.Kill(
                        this.killEntireProcessTreeOnStop);
                }
                catch (InvalidOperationException)
                    when (HasExited(this.process))
                {
                    // The child completed between the explicit exit check and the kill request.
                }

                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                timeout.CancelAfter(this.stopTimeout);

                try
                {
                    await this.completion
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    Volatile.Write(
                        ref this.status,
                        (int)AiRuntimeProcessPoolChildStatus.Faulted);

                    throw new TimeoutException(
                        $"Runtime pool child '{this.RuntimeInstanceId}' did not stop within {this.stopTimeout}.");
                }
            }
            finally
            {
                this.stopGate.Release();
            }
        }

        /// <summary>
        /// Observes process completion and creates one typed completion result.
        /// </summary>
        /// <returns>The typed child completion.</returns>
        private async Task<AiRuntimeProcessPoolChildExit> ObserveCompletionAsync()
        {
            try
            {
                await this.process
                    .WaitForExitAsync(CancellationToken.None)
                    .ConfigureAwait(false);

                if (this.standardOutputDrain is not null)
                {
                    await this.standardOutputDrain.ConfigureAwait(false);
                }

                if (this.standardErrorDrain is not null)
                {
                    await this.standardErrorDrain.ConfigureAwait(false);
                }

                var requested =
                    Volatile.Read(ref this.stopRequested) == 1;

                Volatile.Write(
                    ref this.status,
                    requested
                        ? (int)AiRuntimeProcessPoolChildStatus.Stopped
                        : (int)AiRuntimeProcessPoolChildStatus.Faulted);

                return new AiRuntimeProcessPoolChildExit
                {
                    Kind = requested
                        ? AiRuntimeProcessPoolChildExitKind.Requested
                        : AiRuntimeProcessPoolChildExitKind.Unexpected,
                    ExitCode = this.process.ExitCode
                };
            }
            catch (Exception exception)
            {
                Volatile.Write(
                    ref this.status,
                    (int)AiRuntimeProcessPoolChildStatus.Faulted);

                return new AiRuntimeProcessPoolChildExit
                {
                    Kind = AiRuntimeProcessPoolChildExitKind.Faulted,
                    FailureMessage = exception.Message
                };
            }
            finally
            {
                this.process.Dispose();
            }
        }

        /// <summary>
        /// Safely determines whether a process has already exited.
        /// </summary>
        /// <param name="process">The process to inspect.</param>
        /// <returns>
        /// <see langword="true"/> when the process has exited or has already been disposed;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        private static bool HasExited(
            System.Diagnostics.Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
