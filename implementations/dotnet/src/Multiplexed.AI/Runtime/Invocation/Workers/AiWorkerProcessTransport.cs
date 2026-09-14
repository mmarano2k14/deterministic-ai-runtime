using System.Diagnostics;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers
{
    /// <summary>The host must quarantine its capacity when root-process termination cannot be confirmed.</summary>
    public sealed class AiWorkerProcessCleanupException : IOException
    {
        public AiWorkerProcessCleanupException(Exception inner)
            : base("The worker process could not be confirmed stopped; its capacity must remain quarantined.", inner) { }
    }

    /// <summary>
    /// Real one-assignment process transport over private redirected pipes. No network listener,
    /// shell expansion, inherited engine environment, tenant-selected executable or transport retry.
    /// Process isolation is not an OS sandbox; this provider requires a trusted worker deployment.
    /// </summary>
    public sealed class AiWorkerProcessTransport : IAiWorkerInvocationTransport
    {
        private readonly IAiWorkerProcessCatalog _catalog;
        private readonly AiWorkerProcessTransportOptions _options;
        private readonly TimeProvider _time;
        private readonly AiWorkerExecutionAdmissionPolicy _executionPolicy;
        public AiWorkerProcessTransport(IAiWorkerProcessCatalog catalog, AiWorkerProcessTransportOptions options,
            TimeProvider? timeProvider = null, AiWorkerExecutionAdmissionPolicy? executionPolicy = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _time = timeProvider ?? TimeProvider.System;
            // Preserve historical trusted-process callers. Hosts can supply the strict policy
            // to refuse legacy requests and any requirement this provider cannot enforce.
            _executionPolicy = executionPolicy ?? AiWorkerExecutionAdmissionPolicy.LegacyCompatible;
            AiPublicationExecutionDescriptors.ValidateRequirements(_executionPolicy.MinimumRequirements);
        }

        public async Task<AiDurableInvocationResult> InvokeAsync(AiWorkerInvocationRequest request,
            Func<CancellationToken, Task> heartbeat, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(heartbeat);
            var bytes = AiWorkerInvocationProtocol.EncodeRequest(request, _options.MaxRequestBytes);
            var remaining = request.DeadlineUtc - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Worker invocation deadline has already elapsed.");
            using var deadline = new CancellationTokenSource(remaining, _time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var token = stop.Token;
            var profile = _catalog.Resolve(request.Code.Runtime);
            // Check required policy and provider facts before reading launch files or starting a process.
            AiWorkerExecutionAdmission.Require(request.Code, profile, _executionPolicy);
            await using var verifiedFiles = await AiWorkerVerifiedLaunchFiles.OpenAsync(profile, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(profile.WorkingDirectory)) throw new DirectoryNotFoundException("Worker working directory is not installed.");
            using var process = new Process { StartInfo = CreateStartInfo(profile) };
            var started = false;
            Task? stderr = null;
            Exception? stderrFailure = null;
            CancellationTokenRegistration cancellation = default;
            try
            {
                AiWorkerLaunchPaths.ValidateProfile(profile);
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException("The installed worker process did not start.");
                started = true;
                // Cancellation requests termination immediately, including while a store renewal is awaiting I/O.
                cancellation = token.Register(() => RequestStop(process));
                stderr = DrainStderrAsync();
                var exchange = ExchangeAsync();
                Observe(exchange);
                var result = await exchange.WaitAsync(token).ConfigureAwait(false);
                await stderr.WaitAsync(_options.ShutdownTimeout, _time, token).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (stderrFailure is not null)
            {
                throw new IOException("Worker diagnostics exceeded their bound or could not be drained.", stderrFailure);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Worker invocation deadline elapsed.");
            }
            finally
            {
                // Always reap the root before returning a slot. Descendant containment still belongs to
                // the OS/container provider; Process.WaitForExitAsync does not prove descendant exit.
                cancellation.Dispose();
                if (started)
                {
                    try
                    {
                        RequestStop(process);
                        await process.WaitForExitAsync(CancellationToken.None)
                            .WaitAsync(_options.ShutdownTimeout, _time, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception) { throw new AiWorkerProcessCleanupException(exception); }
                    finally
                    {
                        try { process.StandardInput.Close(); } catch { }
                        try { process.StandardOutput.Close(); } catch { }
                        try { process.StandardError.Close(); } catch { }
                        if (stderr is not null) Observe(stderr);
                    }
                }
            }

            async Task<AiDurableInvocationResult> ExchangeAsync()
            {
                var reader = new AiWorkerJsonLineReader(process.StandardOutput.BaseStream, _options.MaxFrameBytes);
                var write = WriteRequestAsync(); Observe(write);
                await write.WaitAsync(_options.StartupTimeout, _time, token).ConfigureAwait(false);
                var first = await ReadFrameAsync(_options.StartupTimeout).ConfigureAwait(false);
                if (first.Type != "ready") throw new InvalidOperationException("The worker must acknowledge readiness before results or heartbeats.");
                await heartbeat(token).ConfigureAwait(false);
                for (var count = 1; count < _options.MaxFrames; count++)
                {
                    var frame = await ReadFrameAsync(_options.HeartbeatTimeout).ConfigureAwait(false);
                    if (frame.Type == "heartbeat") { await heartbeat(token).ConfigureAwait(false); continue; }
                    if (frame.Type != "result" || frame.Result is null)
                        throw new InvalidOperationException("The worker emitted an invalid lifecycle sequence.");
                    var eof = reader.ReadAsync(token); Observe(eof);
                    if (await eof.WaitAsync(_options.ShutdownTimeout, _time, token).ConfigureAwait(false) is not null)
                        throw new InvalidOperationException("The worker emitted data after its terminal result.");
                    await process.WaitForExitAsync(token).WaitAsync(_options.ShutdownTimeout, _time, token).ConfigureAwait(false);
                    if (process.ExitCode != 0) throw new IOException("Worker process exited unsuccessfully after its result.");
                    return frame.Result;
                }
                throw new InvalidOperationException("Worker frame count exceeds the configured limit.");

                async Task<AiWorkerInvocationFrame> ReadFrameAsync(TimeSpan timeout)
                {
                    var read = reader.ReadAsync(token); Observe(read);
                    var json = await read.WaitAsync(timeout, _time, token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Worker exited before its required protocol frame.");
                    return AiWorkerInvocationProtocol.ReadFrame(json, request);
                }
            }
            async Task WriteRequestAsync()
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'\n' }.AsMemory(), token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            async Task DrainStderrAsync()
            {
                try
                {
                    var buffer = new byte[4096]; long total = 0;
                    while (true)
                    {
                        var read = await process.StandardError.BaseStream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                        if (read == 0) return;
                        if ((total += read) > _options.MaxStderrBytes)
                            throw new IOException("Worker stderr exceeded its configured byte limit.");
                        // Deliberately discard content: tenant diagnostics must not leak into host logs.
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref stderrFailure, exception, null);
                    stop.Cancel(); throw;
                }
            }
        }

        public static ProcessStartInfo CreateStartInfo(AiWorkerProcessProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var info = new ProcessStartInfo
            {
                FileName = profile.ExecutablePath, WorkingDirectory = profile.WorkingDirectory,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var item in profile.Environment) info.Environment.Add(item.Key, item.Value);
            foreach (var argument in profile.Arguments) info.ArgumentList.Add(argument);
            return info;
        }
        private static void RequestStop(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* The awaited cleanup path reports failure and quarantines capacity. */ }
        }
        private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
