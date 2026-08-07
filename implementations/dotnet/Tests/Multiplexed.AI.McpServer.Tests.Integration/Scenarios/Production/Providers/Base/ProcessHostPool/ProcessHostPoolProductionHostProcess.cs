using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    /// <summary>
    /// Owns one real external parent Process Host and the Runtime Pool children in its process tree.
    /// </summary>
    internal sealed class ProcessHostPoolProductionHostProcess : IAsyncDisposable
    {
        private const int MaximumCapturedLineCount = 2_000;

        private readonly Process process;
        private readonly ConcurrentQueue<string> capturedLines = new();
        private int capturedLineCount;

        private ProcessHostPoolProductionHostProcess(
            Process process,
            int ordinal,
            string poolId,
            string stableTransportEndpoint,
            int expectedRuntimeCount)
        {
            this.process = process;
            this.Ordinal = ordinal;
            this.PoolId = poolId;
            this.StablePort = new Uri(stableTransportEndpoint).Port;
            this.StableTransportEndpoint = stableTransportEndpoint;
            this.ExpectedRuntimeCount = expectedRuntimeCount;
        }

        /// <summary>
        /// Gets the one-based parent Process Host ordinal.
        /// </summary>
        public int Ordinal { get; }

        /// <summary>
        /// Gets the operating-system process identifier of the parent Process Host.
        /// </summary>
        public int ProcessId => this.process.Id;

        /// <summary>
        /// Gets the shared logical pool identifier.
        /// </summary>
        public string PoolId { get; }

        /// <summary>
        /// Gets the stable loopback port owned by this parent Process Host.
        /// </summary>
        public int StablePort { get; }

        /// <summary>
        /// Gets the stable router endpoint owned by this parent Process Host.
        /// </summary>
        public string StableTransportEndpoint { get; }

        /// <summary>
        /// Gets the expected number of runtime children owned by this host.
        /// </summary>
        public int ExpectedRuntimeCount { get; }

        /// <summary>
        /// Gets the immutable host incarnation returned by the readiness endpoint.
        /// </summary>
        public string HostId { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the exact runtime identities returned by the readiness endpoint.
        /// </summary>
        public IReadOnlySet<string> RuntimeInstanceIds { get; private set; } =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Gets a value indicating whether the parent process is still running.
        /// </summary>
        public bool IsRunning => !HasExited(this.process);

        /// <summary>
        /// Starts one external RuntimeInstanceOnly Process Host and waits for its exact local pool.
        /// </summary>
        public static async Task<ProcessHostPoolProductionHostProcess> StartAsync(
            ProcessHostPoolProductionScenarioProfile profile,
            IReadOnlyDictionary<string, string?> settings,
            string runtimeHostAssemblyPath,
            string poolId,
            int ordinal,
            int stablePort,
            int expectedRuntimeCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinal);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stablePort);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRuntimeCount);

            var stableTransportEndpoint =
                string.Concat(
                    "http://127.0.0.1:",
                    stablePort.ToString(CultureInfo.InvariantCulture));

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory =
                        Path.GetDirectoryName(runtimeHostAssemblyPath) ??
                        Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            startInfo.ArgumentList.Add(runtimeHostAssemblyPath);

            foreach (var pair in settings)
            {
                if (pair.Value is not null)
                {
                    startInfo.Environment[ToEnvironmentKey(pair.Key)] =
                        pair.Value;
                }
            }

            startInfo.Environment["ASPNETCORE_URLS"] =
                stableTransportEndpoint;
            startInfo.Environment["DOTNET_URLS"] =
                stableTransportEndpoint;
            startInfo.Environment["AiMcpHost__Port"] =
                stablePort.ToString(CultureInfo.InvariantCulture);

            if (profile.RequiresHttp2)
            {
                startInfo.Environment[
                    "Kestrel__EndpointDefaults__Protocols"] =
                    "Http2";
                startInfo.Environment[
                    "Kestrel__Endpoints__Grpc__Url"] =
                    stableTransportEndpoint;
                startInfo.Environment[
                    "Kestrel__Endpoints__Grpc__Protocols"] =
                    "Http2";
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            var host =
                new ProcessHostPoolProductionHostProcess(
                    process,
                    ordinal,
                    poolId,
                    stableTransportEndpoint,
                    expectedRuntimeCount);

            process.OutputDataReceived +=
                (_, eventArgs) => host.CaptureLine("OUT", eventArgs.Data);
            process.ErrorDataReceived +=
                (_, eventArgs) => host.CaptureLine("ERR", eventArgs.Data);

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        $"Process Host ordinal '{ordinal}' did not start.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await host
                    .WaitForReadinessAsync(
                        profile.RequiresHttp2,
                        timeout)
                    .ConfigureAwait(false);

                return host;
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Throws when the parent host is no longer alive.
        /// </summary>
        public void AssertRunning()
        {
            if (!this.IsRunning)
            {
                throw new InvalidOperationException(
                    $"Parent Process Host ordinal '{this.Ordinal}' exited unexpectedly. " +
                    this.BuildDiagnostics());
            }
        }

        /// <summary>
        /// Force-kills the complete parent Process Host tree without graceful pool shutdown.
        /// </summary>
        public async Task CrashAsync(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            if (HasExited(this.process))
            {
                throw new InvalidOperationException(
                    $"Parent Process Host ordinal '{this.Ordinal}' exited before the forced crash request. " +
                    this.BuildDiagnostics());
            }

            this.process.Kill(entireProcessTree: true);

            using var cancellation = new CancellationTokenSource(timeout);

            try
            {
                await this.process
                    .WaitForExitAsync(cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                throw new TimeoutException(
                    $"Parent Process Host ordinal '{this.Ordinal}' did not exit within '{timeout}'. " +
                    this.BuildDiagnostics(),
                    exception);
            }
        }

        /// <summary>
        /// Builds bounded parent-host output diagnostics.
        /// </summary>
        public string BuildDiagnostics()
        {
            var exit =
                HasExited(this.process)
                    ? this.process.ExitCode.ToString(CultureInfo.InvariantCulture)
                    : "running";

            return string.Concat(
                "Ordinal='",
                this.Ordinal.ToString(CultureInfo.InvariantCulture),
                "', ProcessId='",
                this.ProcessId.ToString(CultureInfo.InvariantCulture),
                "', ExitCode='",
                exit,
                "', Endpoint='",
                this.StableTransportEndpoint,
                "'.",
                Environment.NewLine,
                string.Join(Environment.NewLine, this.capturedLines));
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!HasExited(this.process))
                {
                    this.process.Kill(entireProcessTree: true);

                    using var timeout =
                        new CancellationTokenSource(
                            TimeSpan.FromSeconds(30));

                    await this.process
                        .WaitForExitAsync(timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
                when (HasExited(this.process))
            {
                // The process exited between the explicit check and the kill request.
            }
            catch (OperationCanceledException)
            {
                // Best-effort test cleanup. The process tree was already asked to terminate.
            }
            finally
            {
                this.process.Dispose();
            }
        }

        private async Task WaitForReadinessAsync(
            bool requiresHttp2,
            TimeSpan timeout)
        {
            AppContext.SetSwitch(
                "System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport",
                true);

            using var handler = new SocketsHttpHandler();
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            if (requiresHttp2)
            {
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy =
                    HttpVersionPolicy.RequestVersionExact;
            }

            var endpoint =
                string.Concat(
                    this.StableTransportEndpoint,
                    "/runtime-pool/readiness");

            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            string? lastResponse = null;
            Exception? lastException = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                if (HasExited(this.process))
                {
                    throw new InvalidOperationException(
                        $"Parent Process Host ordinal '{this.Ordinal}' exited before readiness. " +
                        this.BuildDiagnostics());
                }

                try
                {
                    using var response =
                        await client.GetAsync(endpoint).ConfigureAwait(false);

                    lastResponse =
                        await response.Content
                            .ReadAsStringAsync()
                            .ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        var readiness =
                            JsonSerializer.Deserialize<
                                ProcessHostPoolReadinessResponse>(
                                lastResponse,
                                new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });

                        if (readiness?.Ready == true &&
                            StringComparer.Ordinal.Equals(
                                readiness.PoolId,
                                this.PoolId) &&
                            !string.IsNullOrWhiteSpace(readiness.HostId) &&
                            readiness.RuntimeInstanceIds.Length ==
                                this.ExpectedRuntimeCount &&
                            readiness.RuntimeInstanceIds.All(
                                value => !string.IsNullOrWhiteSpace(value)))
                        {
                            this.HostId = readiness.HostId;
                            this.RuntimeInstanceIds =
                                readiness.RuntimeInstanceIds
                                    .ToHashSet(StringComparer.Ordinal);
                            return;
                        }
                    }
                }
                catch (Exception exception)
                    when (exception is HttpRequestException or TaskCanceledException)
                {
                    lastException = exception;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"Parent Process Host ordinal '{this.Ordinal}' did not expose exact Runtime Pool readiness within '{timeout}'. LastResponse='{lastResponse}', LastException='{lastException?.Message}'. " +
                this.BuildDiagnostics());
        }

        private void CaptureLine(
            string stream,
            string? line)
        {
            if (line is null)
            {
                return;
            }

            this.capturedLines.Enqueue(
                string.Concat(
                    "[",
                    stream,
                    "] ",
                    line));

            var count =
                Interlocked.Increment(
                    ref this.capturedLineCount);

            while (count > MaximumCapturedLineCount &&
                   this.capturedLines.TryDequeue(out _))
            {
                count =
                    Interlocked.Decrement(
                        ref this.capturedLineCount);
            }
        }

        private static string ToEnvironmentKey(string configurationKey)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
            return configurationKey.Replace(":", "__", StringComparison.Ordinal);
        }

        private static bool HasExited(Process process)
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

        private sealed record ProcessHostPoolReadinessResponse
        {
            public bool Ready { get; init; }

            public string PoolId { get; init; } = string.Empty;

            public string HostId { get; init; } = string.Empty;

            public string[] RuntimeInstanceIds { get; init; } =
                Array.Empty<string>();
        }
    }
}
