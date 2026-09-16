using System.Diagnostics;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.Abstractions.AI.Invocation.Workers;
using Multiplexed.AI.Runtime.Publication;

namespace Multiplexed.AI.Runtime.Invocation.Workers.Isolation
{
    /// <summary>
    /// One-assignment OCI transport using a deployment-owned Docker-compatible engine.
    /// The existing worker JSON-line protocol remains unchanged; the container image supplies
    /// the language worker entry point and receives the immutable request over private stdio.
    /// </summary>
    public sealed class AiContainerWorkerTransport : IAiWorkerInvocationTransport
    {
        private readonly IAiContainerWorkerCatalog _catalog;
        private readonly AiWorkerProcessTransportOptions _options;
        private readonly TimeProvider _time;
        private readonly AiWorkerExecutionAdmissionPolicy _executionPolicy;

        public AiContainerWorkerTransport(
            IAiContainerWorkerCatalog catalog,
            AiWorkerProcessTransportOptions options,
            TimeProvider? timeProvider = null,
            AiWorkerExecutionAdmissionPolicy? executionPolicy = null)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _time = timeProvider ?? TimeProvider.System;
            _executionPolicy = executionPolicy ?? new AiWorkerExecutionAdmissionPolicy();
            AiPublicationExecutionDescriptors.ValidateRequirements(_executionPolicy.MinimumRequirements);
        }

        public async Task<AiDurableInvocationResult> InvokeAsync(
            AiWorkerInvocationRequest request,
            Func<CancellationToken, Task> heartbeat,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(heartbeat);
            var bytes = AiWorkerInvocationProtocol.EncodeRequest(request, _options.MaxRequestBytes);
            var remaining = request.DeadlineUtc - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Worker invocation deadline has already elapsed.");

            using var deadline = new CancellationTokenSource(remaining, _time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var token = stop.Token;
            var profile = _catalog.Resolve(request.Code.Runtime);
            AiContainerWorkerExecutionAdmission.Require(request.Code, profile, _executionPolicy);

            var engineProfile = CreateEngineProfile(profile);
            await using var verifiedFiles = await AiWorkerVerifiedLaunchFiles.OpenAsync(engineProfile, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            var containerName = "multiplexed-ai-" + Guid.NewGuid().ToString("N");
            var plan = AiContainerWorkerLaunchPlan.Create(profile, containerName);
            using var process = new Process { StartInfo = CreateStartInfo(profile, plan) };
            var started = false;
            var containerMayStillRun = false;
            Task? stderr = null;
            Exception? stderrFailure = null;
            CancellationTokenRegistration cancellation = default;

            try
            {
                token.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException("The container engine did not start.");
                started = true;
                containerMayStillRun = true;
                cancellation = token.Register(() => RequestStop(process));
                stderr = DrainStderrAsync();
                await AwaitIsolationAttestationAsync(profile, containerName, process, token).ConfigureAwait(false);
                var exchange = ExchangeAsync();
                Observe(exchange);
                var result = await exchange.WaitAsync(token).ConfigureAwait(false);
                await stderr.WaitAsync(_options.ShutdownTimeout, _time, token).ConfigureAwait(false);
                containerMayStillRun = false;
                return result;
            }
            catch (OperationCanceledException) when (stderrFailure is not null)
            {
                throw new IOException("Container diagnostics exceeded their bound or could not be drained.", stderrFailure);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Worker invocation deadline elapsed.");
            }
            finally
            {
                cancellation.Dispose();
                Exception? cleanupFailure = null;
                if (started)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            containerMayStillRun = true;
                            RequestStop(process);
                            await process.WaitForExitAsync(CancellationToken.None)
                                .WaitAsync(_options.ShutdownTimeout, _time, CancellationToken.None).ConfigureAwait(false);
                        }
                        else
                        {
                            containerMayStillRun = false;
                            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        cleanupFailure = exception;
                    }
                    finally
                    {
                        try { process.StandardInput.Close(); } catch { }
                        try { process.StandardOutput.Close(); } catch { }
                        try { process.StandardError.Close(); } catch { }
                        if (stderr is not null) Observe(stderr);
                    }
                }

                if (containerMayStillRun)
                {
                    try { await ForceRemoveAsync(profile, containerName).ConfigureAwait(false); }
                    catch (Exception exception) { cleanupFailure ??= exception; }
                }

                if (cleanupFailure is not null)
                    throw new AiWorkerProcessCleanupException(cleanupFailure);
            }

            async Task<AiDurableInvocationResult> ExchangeAsync()
            {
                var reader = new AiWorkerJsonLineReader(process.StandardOutput.BaseStream, _options.MaxFrameBytes);
                var write = WriteRequestAsync(); Observe(write);
                await write.WaitAsync(_options.StartupTimeout, _time, token).ConfigureAwait(false);
                var first = await ReadFrameAsync(_options.StartupTimeout).ConfigureAwait(false);
                if (first.Type != "ready")
                    throw new InvalidOperationException("The worker must acknowledge readiness before results or heartbeats.");
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
                    await process.WaitForExitAsync(token)
                        .WaitAsync(_options.ShutdownTimeout, _time, token).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        throw new IOException("Container engine exited unsuccessfully after the worker result.");
                    return frame.Result;
                }
                throw new InvalidOperationException("Worker frame count exceeds the configured limit.");

                async Task<AiWorkerInvocationFrame> ReadFrameAsync(TimeSpan timeout)
                {
                    var read = reader.ReadAsync(token); Observe(read);
                    var json = await read.WaitAsync(timeout, _time, token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException("Container worker exited before its required protocol frame.");
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
                            throw new IOException("Container engine stderr exceeded its configured byte limit.");
                    }
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref stderrFailure, exception, null);
                    stop.Cancel(); throw;
                }
            }
        }

        public static ProcessStartInfo CreateStartInfo(AiContainerWorkerProfile profile, AiContainerWorkerLaunchPlan plan)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(plan);
            var info = new ProcessStartInfo
            {
                FileName = profile.EngineExecutablePath,
                WorkingDirectory = profile.EngineWorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var item in profile.EngineEnvironment) info.Environment.Add(item.Key, item.Value);
            foreach (var argument in plan.Arguments) info.ArgumentList.Add(argument);
            return info;
        }

        private static AiWorkerProcessProfile CreateEngineProfile(AiContainerWorkerProfile profile) => new(
            profile.Runtime,
            profile.EngineExecutablePath,
            profile.EngineExecutableSha256,
            Array.Empty<string>(),
            profile.EngineWorkingDirectory,
            profile.EngineEnvironment,
            executionDescriptor: profile.ExecutionDescriptor,
            approvedLaunchRoots: profile.ApprovedLaunchRoots);


        private async Task AwaitIsolationAttestationAsync(
            AiContainerWorkerProfile profile,
            string containerName,
            Process attachedRunProcess,
            CancellationToken cancellationToken)
        {
            var expires = _time.GetUtcNow() + _options.StartupTimeout;
            Exception? lastFailure = null;
            while (_time.GetUtcNow() < expires)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attachedRunProcess.HasExited)
                    throw new IOException("Container engine exited before isolation attestation completed.", lastFailure);

                using var inspect = new Process
                {
                    StartInfo = CreateEngineControlStartInfo(profile, new[] { "inspect", "--type", "container", containerName })
                };
                if (!inspect.Start()) throw new IOException("Container inspection process did not start.");
                var stdoutTask = inspect.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = inspect.StandardError.ReadToEndAsync(cancellationToken);
                var remaining = expires - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;
                try
                {
                    await inspect.WaitForExitAsync(cancellationToken)
                        .WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                    RequestStop(inspect);
                    Observe(stdoutTask);
                    Observe(stderrTask);
                    continue;
                }

                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderrText = await stderrTask.ConfigureAwait(false);
                if (stdout.Length > _options.MaxFrameBytes || stderrText.Length > _options.MaxStderrBytes)
                    throw new IOException("Container inspection output exceeded configured bounds.");
                if (inspect.ExitCode == 0)
                {
                    AiContainerWorkerIsolationAttestation.Validate(stdout, profile);
                    return;
                }

                lastFailure = new IOException("Container inspection did not yet identify the launched container.");
                remaining = expires - _time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25),
                    _time, cancellationToken).ConfigureAwait(false);
            }
            throw new IOException("Container isolation attestation did not complete before worker startup timeout.", lastFailure);
        }

        private async Task ForceRemoveAsync(AiContainerWorkerProfile profile, string containerName)
        {
            using var cleanup = new Process
            {
                StartInfo = CreateEngineControlStartInfo(profile, new[] { "rm", "--force", containerName })
            };
            if (!cleanup.Start()) throw new IOException("Container cleanup process did not start.");
            var stdout = cleanup.StandardOutput.ReadToEndAsync();
            var stderr = cleanup.StandardError.ReadToEndAsync();
            await cleanup.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(_options.ShutdownTimeout, _time, CancellationToken.None).ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (cleanup.ExitCode != 0)
                throw new IOException("Container cleanup could not be confirmed.");
        }

        private static ProcessStartInfo CreateEngineControlStartInfo(AiContainerWorkerProfile profile, IEnumerable<string> arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = profile.EngineExecutablePath,
                WorkingDirectory = profile.EngineWorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var item in profile.EngineEnvironment) info.Environment.Add(item.Key, item.Value);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            return info;
        }

        private static void RequestStop(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        }

        private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
