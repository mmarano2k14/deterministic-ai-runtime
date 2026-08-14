using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.McpServer.Tests.Integration.Fixtures.Http.RuntimePool
{
    /// <summary>
    /// Owns one kubectl Service port-forward used by host-side Kubernetes proofs.
    /// </summary>
    internal sealed class KubernetesServicePortForward :
        IAsyncDisposable
    {
        private static readonly Regex ForwardingAddressPattern =
            new(
                @"Forwarding from 127\.0\.0\.1:(?<port>\d+)\s+->",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly Process process;

        private KubernetesServicePortForward(
            Process process,
            Uri endpoint)
        {
            this.process = process;
            this.Endpoint = endpoint;
        }

        /// <summary>
        /// Gets the host-local endpoint forwarded to the Kubernetes Service.
        /// </summary>
        public Uri Endpoint { get; }

        /// <summary>
        /// Starts a host-local port-forward to one stable Kubernetes Service port.
        /// </summary>
        public static async Task<KubernetesServicePortForward> StartAsync(
            string @namespace,
            string serviceName,
            int remotePort,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

            if (remotePort is <= 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(remotePort));
            }

            var endpointSource =
                new TaskCompletionSource<Uri>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var diagnostics = new System.Text.StringBuilder();

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "kubectl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            startInfo.ArgumentList.Add("port-forward");
            startInfo.ArgumentList.Add("--namespace");
            startInfo.ArgumentList.Add(@namespace.Trim());
            startInfo.ArgumentList.Add(
                string.Concat(
                    "service/",
                    serviceName.Trim()));
            startInfo.ArgumentList.Add(
                string.Concat(
                    "0:",
                    remotePort.ToString(
                        CultureInfo.InvariantCulture)));
            startInfo.ArgumentList.Add("--address");
            startInfo.ArgumentList.Add("127.0.0.1");

            var process =
                new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

            void ObserveLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                lock (diagnostics)
                {
                    diagnostics.AppendLine(line);
                }

                var match = ForwardingAddressPattern.Match(line);
                if (!match.Success ||
                    !int.TryParse(
                        match.Groups["port"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var localPort))
                {
                    return;
                }

                endpointSource.TrySetResult(
                    new Uri(
                        string.Concat(
                            "http://127.0.0.1:",
                            localPort.ToString(
                                CultureInfo.InvariantCulture),
                            "/")));
            }

            process.OutputDataReceived +=
                (_, eventArgs) => ObserveLine(eventArgs.Data);
            process.ErrorDataReceived +=
                (_, eventArgs) => ObserveLine(eventArgs.Data);
            process.Exited +=
                (_, _) =>
                {
                    string output;
                    lock (diagnostics)
                    {
                        output = diagnostics.ToString();
                    }

                    endpointSource.TrySetException(
                        new InvalidOperationException(
                            string.Concat(
                                "kubectl port-forward exited before exposing an endpoint. ExitCode=",
                                process.ExitCode,
                                "; Output=",
                                output)));
                };

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "kubectl port-forward could not be started.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var endpoint =
                    await endpointSource.Task
                        .WaitAsync(cancellationToken)
                        .ConfigureAwait(false);

                await WaitUntilTcpEndpointAcceptsConnectionsAsync(
                        endpoint,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new KubernetesServicePortForward(
                    process,
                    endpoint);
            }
            catch
            {
                StopProcess(process);
                process.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            StopProcess(this.process);

            try
            {
                await this.process
                    .WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                this.process.Dispose();
            }
        }

        private static async Task WaitUntilTcpEndpointAcceptsConnectionsAsync(
            Uri endpoint,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var tcpClient = new TcpClient();

                try
                {
                    await tcpClient
                        .ConnectAsync(
                            IPAddress.Loopback,
                            endpoint.Port,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(100),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        private static void StopProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
