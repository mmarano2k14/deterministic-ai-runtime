using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Kubernetes.Gateway.Transport
{
    /// <summary>
    /// Resolves the shared Kubernetes Gateway transport endpoint and owns an optional local kubectl port-forward.
    /// </summary>
    /// <remarks>
    /// One live port-forward is shared per Gateway Service inside the current control-plane process.
    /// Each dependency-injection root acquires the shared registration only once and releases it during disposal.
    ///
    /// Dynamic local port allocation is delegated directly to kubectl by using <c>:remotePort</c>.
    /// This avoids the free-port time-of-check/time-of-use race created by probing a port before kubectl binds it.
    /// </remarks>
    public sealed class KubectlAiKubernetesGatewayTransportEndpointManager :
        IAiKubernetesGatewayTransportEndpointManager
    {
        private const int MaximumCapturedOutputLineCount = 64;
        private static readonly Regex ForwardingAddressRegex =
            new(
                @"Forwarding\s+from\s+(?:127\.0\.0\.1|localhost|\[::1\]):(?<port>\d+)\s+->\s+(?<remotePort>\d+)",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> SharedLifecycleGates =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, SharedPortForwardRegistration> SharedRegistrations =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly AiKubernetesRuntimeHostOptions options;
        private readonly ILogger<KubectlAiKubernetesGatewayTransportEndpointManager> logger;
        private readonly ConcurrentDictionary<string, SharedPortForwardRegistration> acquiredRegistrations;
        private int disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="KubectlAiKubernetesGatewayTransportEndpointManager"/> class.
        /// </summary>
        /// <param name="options">The Kubernetes runtime host options.</param>
        /// <param name="logger">The logger.</param>
        public KubectlAiKubernetesGatewayTransportEndpointManager(
            IOptions<AiKubernetesRuntimeHostOptions> options,
            ILogger<KubectlAiKubernetesGatewayTransportEndpointManager> logger)
        {
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.acquiredRegistrations =
                new ConcurrentDictionary<string, SharedPortForwardRegistration>(StringComparer.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public async Task<AiKubernetesGatewayTransportEndpoint> ResolveAsync(
            AiKubernetesGatewayEndpoint gatewayEndpoint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(gatewayEndpoint);
            this.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateGatewayEndpoint(gatewayEndpoint);

            if (!this.options.UsePortForwardTransportEndpoint)
            {
                return CreateInternalTransportEndpoint(gatewayEndpoint);
            }

            var registrationKey =
                CreateRegistrationKey(
                    this.options.KubectlPath,
                    gatewayEndpoint.ServiceNamespace,
                    gatewayEndpoint.ServiceName,
                    gatewayEndpoint.ServicePort,
                    this.options.PortForwardLocalPort);

            var lifecycleGate =
                SharedLifecycleGates.GetOrAdd(
                    registrationKey,
                    static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

            await lifecycleGate
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                this.ThrowIfDisposed();

                if (this.acquiredRegistrations.TryGetValue(registrationKey, out var ownedRegistration) &&
                    SharedRegistrations.TryGetValue(registrationKey, out var currentOwnedRegistration) &&
                    ReferenceEquals(ownedRegistration, currentOwnedRegistration) &&
                    IsProcessAlive(currentOwnedRegistration.Process))
                {
                    return CreatePortForwardTransportEndpoint(
                        gatewayEndpoint,
                        currentOwnedRegistration.LocalPort);
                }

                if (SharedRegistrations.TryGetValue(registrationKey, out var existingRegistration))
                {
                    if (IsProcessAlive(existingRegistration.Process))
                    {
                        checked
                        {
                            existingRegistration.ReferenceCount++;
                        }

                        this.acquiredRegistrations[registrationKey] = existingRegistration;

                        this.logger.LogInformation(
                            "KUBERNETES GATEWAY PORT-FORWARD REUSED GatewayName={GatewayName} Namespace={Namespace} ServiceName={ServiceName} ServicePort={ServicePort} LocalPort={LocalPort} ProcessId={ProcessId} ReferenceCount={ReferenceCount}",
                            gatewayEndpoint.GatewayName,
                            gatewayEndpoint.ServiceNamespace,
                            gatewayEndpoint.ServiceName,
                            gatewayEndpoint.ServicePort,
                            existingRegistration.LocalPort,
                            existingRegistration.Process.Id,
                            existingRegistration.ReferenceCount);

                        return CreatePortForwardTransportEndpoint(
                            gatewayEndpoint,
                            existingRegistration.LocalPort);
                    }

                    if (SharedRegistrations.TryRemove(registrationKey, out var removedRegistration))
                    {
                        DisposeRegistration(removedRegistration);
                    }
                }

                var startedRegistration =
                    await this.StartPortForwardAsync(
                            gatewayEndpoint,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (!SharedRegistrations.TryAdd(registrationKey, startedRegistration))
                {
                    DisposeRegistration(startedRegistration);

                    throw new InvalidOperationException(
                        $"kubernetes-gateway-port-forward-registration-conflict: A shared port-forward registration already exists for Gateway Service '{gatewayEndpoint.ServiceNamespace}/{gatewayEndpoint.ServiceName}:{gatewayEndpoint.ServicePort}'.");
                }

                this.acquiredRegistrations[registrationKey] = startedRegistration;

                return CreatePortForwardTransportEndpoint(
                    gatewayEndpoint,
                    startedRegistration.LocalPort);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            foreach (var item in this.acquiredRegistrations.ToArray())
            {
                this.ReleaseRegistration(item.Key, item.Value);
            }

            this.acquiredRegistrations.Clear();
        }

        /// <summary>
        /// Starts one kubectl port-forward targeting the controller-managed Gateway Service.
        /// </summary>
        private async Task<SharedPortForwardRegistration> StartPortForwardAsync(
            AiKubernetesGatewayEndpoint gatewayEndpoint,
            CancellationToken cancellationToken)
        {
            var kubectlPath =
                string.IsNullOrWhiteSpace(this.options.KubectlPath)
                    ? "kubectl"
                    : this.options.KubectlPath.Trim();

            var configuredLocalPort =
                this.options.PortForwardLocalPort;

            if (configuredLocalPort < 0 || configuredLocalPort > IPEndPoint.MaxPort)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-port-forward-local-port-invalid: Configured local port '{configuredLocalPort}' is outside the valid TCP port range.");
            }

            var portMapping =
                configuredLocalPort > 0
                    ? $"{configuredLocalPort}:{gatewayEndpoint.ServicePort}"
                    : $":{gatewayEndpoint.ServicePort}";

            var processStartInfo =
                new ProcessStartInfo
                {
                    FileName = kubectlPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            processStartInfo.ArgumentList.Add("port-forward");
            processStartInfo.ArgumentList.Add("-n");
            processStartInfo.ArgumentList.Add(gatewayEndpoint.ServiceNamespace);
            processStartInfo.ArgumentList.Add($"service/{gatewayEndpoint.ServiceName}");
            processStartInfo.ArgumentList.Add(portMapping);

            var process =
                new DiagnosticsProcess
                {
                    StartInfo = processStartInfo,
                    EnableRaisingEvents = true
                };

            var selectedPortSource =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var processExitSource =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            var outputLines =
                new ConcurrentQueue<string>();

            void CaptureOutput(string streamName, string? message)
            {
                if (string.IsNullOrWhiteSpace(message))
                {
                    return;
                }

                var line =
                    $"{streamName}:{message}";

                outputLines.Enqueue(line);

                while (outputLines.Count > MaximumCapturedOutputLineCount &&
                       outputLines.TryDequeue(out _))
                {
                }

                this.logger.LogDebug(
                    "KUBERNETES GATEWAY PORT-FORWARD OUTPUT GatewayName={GatewayName} Namespace={Namespace} ServiceName={ServiceName} Stream={Stream} Message={Message}",
                    gatewayEndpoint.GatewayName,
                    gatewayEndpoint.ServiceNamespace,
                    gatewayEndpoint.ServiceName,
                    streamName,
                    message);

                var match =
                    ForwardingAddressRegex.Match(message);

                if (!match.Success ||
                    !int.TryParse(
                        match.Groups["port"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var selectedPort) ||
                    !int.TryParse(
                        match.Groups["remotePort"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var reportedRemotePort) ||
                    reportedRemotePort != gatewayEndpoint.ServicePort)
                {
                    return;
                }

                selectedPortSource.TrySetResult(selectedPort);
            }

            process.OutputDataReceived +=
                (_, eventArgs) => CaptureOutput("stdout", eventArgs.Data);

            process.ErrorDataReceived +=
                (_, eventArgs) => CaptureOutput("stderr", eventArgs.Data);

            process.Exited +=
                (_, _) =>
                {
                    try
                    {
                        processExitSource.TrySetResult(process.ExitCode);
                    }
                    catch (InvalidOperationException)
                    {
                        processExitSource.TrySetResult(-1);
                    }
                };

            this.logger.LogInformation(
                "KUBERNETES GATEWAY PORT-FORWARD START BEGIN GatewayName={GatewayName} Namespace={Namespace} ServiceName={ServiceName} ServicePort={ServicePort} ConfiguredLocalPort={ConfiguredLocalPort} KubectlPath={KubectlPath}",
                gatewayEndpoint.GatewayName,
                gatewayEndpoint.ServiceNamespace,
                gatewayEndpoint.ServiceName,
                gatewayEndpoint.ServicePort,
                configuredLocalPort,
                kubectlPath);

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException(
                        "kubernetes-gateway-port-forward-process-not-started: kubectl port-forward did not start.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var selectedPort =
                    await WaitForSelectedPortAsync(
                            process,
                            selectedPortSource.Task,
                            processExitSource.Task,
                            outputLines,
                            this.options.PortForwardStartupTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);

                if (configuredLocalPort > 0 && selectedPort != configuredLocalPort)
                {
                    throw new InvalidOperationException(
                        $"kubernetes-gateway-port-forward-local-port-mismatch: kubectl selected local port '{selectedPort}', expected configured port '{configuredLocalPort}'.");
                }

                await WaitForLocalPortOpenAsync(
                        process,
                        selectedPort,
                        this.options.PortForwardStartupTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogInformation(
                    "KUBERNETES GATEWAY PORT-FORWARD STARTED GatewayName={GatewayName} Namespace={Namespace} ServiceName={ServiceName} ServicePort={ServicePort} LocalPort={LocalPort} ProcessId={ProcessId} Endpoint={Endpoint}",
                    gatewayEndpoint.GatewayName,
                    gatewayEndpoint.ServiceNamespace,
                    gatewayEndpoint.ServiceName,
                    gatewayEndpoint.ServicePort,
                    selectedPort,
                    process.Id,
                    $"http://127.0.0.1:{selectedPort}");

                return new SharedPortForwardRegistration(
                    process,
                    selectedPort,
                    referenceCount: 1);
            }
            catch
            {
                KillProcess(process);
                process.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Releases one process-local owner of a shared Gateway port-forward registration.
        /// </summary>
        private void ReleaseRegistration(
            string registrationKey,
            SharedPortForwardRegistration ownedRegistration)
        {
            var lifecycleGate =
                SharedLifecycleGates.GetOrAdd(
                    registrationKey,
                    static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

            lifecycleGate.Wait();

            try
            {
                if (!SharedRegistrations.TryGetValue(registrationKey, out var registration) ||
                    !ReferenceEquals(registration, ownedRegistration))
                {
                    return;
                }

                registration.ReferenceCount--;

                if (registration.ReferenceCount > 0)
                {
                    this.logger.LogInformation(
                        "KUBERNETES GATEWAY PORT-FORWARD RELEASED RegistrationKey={RegistrationKey} LocalPort={LocalPort} ProcessId={ProcessId} ReferenceCount={ReferenceCount}",
                        registrationKey,
                        registration.LocalPort,
                        TryGetProcessId(registration.Process),
                        registration.ReferenceCount);

                    return;
                }

                SharedRegistrations.TryRemove(registrationKey, out _);

                this.logger.LogInformation(
                    "KUBERNETES GATEWAY PORT-FORWARD STOPPING RegistrationKey={RegistrationKey} LocalPort={LocalPort} ProcessId={ProcessId}",
                    registrationKey,
                    registration.LocalPort,
                    TryGetProcessId(registration.Process));

                DisposeRegistration(registration);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        /// <summary>
        /// Waits until kubectl reports the actual selected local port.
        /// </summary>
        private static async Task<int> WaitForSelectedPortAsync(
            DiagnosticsProcess process,
            Task<int> selectedPortTask,
            Task<int> processExitTask,
            ConcurrentQueue<string> outputLines,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-port-forward-startup-timeout-invalid: PortForwardStartupTimeout must be greater than zero. Actual='{timeout}'.");
            }

            using var timeoutCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            timeoutCancellationTokenSource.CancelAfter(timeout);

            var timeoutTask =
                Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    timeoutCancellationTokenSource.Token);

            var completedTask =
                await Task.WhenAny(
                        selectedPortTask,
                        processExitTask,
                        timeoutTask)
                    .ConfigureAwait(false);

            if (completedTask == selectedPortTask)
            {
                return await selectedPortTask.ConfigureAwait(false);
            }

            if (completedTask == processExitTask)
            {
                var exitCode =
                    await processExitTask.ConfigureAwait(false);

                throw new InvalidOperationException(
                    $"kubernetes-gateway-port-forward-exited-before-startup: kubectl exited before reporting a forwarding address. ProcessId='{TryGetProcessId(process)}', ExitCode='{exitCode}', Output='{CreateOutputSummary(outputLines)}'.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            throw new TimeoutException(
                $"kubernetes-gateway-port-forward-startup-timeout: kubectl did not report a forwarding address within '{timeout}'. ProcessId='{TryGetProcessId(process)}', HasExited='{HasProcessExited(process)}', Output='{CreateOutputSummary(outputLines)}'.");
        }

        /// <summary>
        /// Waits until the selected local port accepts TCP connections and kubectl is still alive.
        /// </summary>
        private static async Task WaitForLocalPortOpenAsync(
            DiagnosticsProcess process,
            int localPort,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!IsProcessAlive(process))
                {
                    throw new InvalidOperationException(
                        $"kubernetes-gateway-port-forward-exited-before-probe: kubectl exited before local endpoint '127.0.0.1:{localPort}' became reachable. ProcessId='{TryGetProcessId(process)}'.");
                }

                try
                {
                    using var probeCancellationTokenSource =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    probeCancellationTokenSource.CancelAfter(
                        TimeSpan.FromMilliseconds(500));

                    using var tcpClient =
                        new TcpClient();

                    await tcpClient
                        .ConnectAsync(
                            IPAddress.Loopback,
                            localPort,
                            probeCancellationTokenSource.Token)
                        .ConfigureAwait(false);

                    if (tcpClient.Connected && IsProcessAlive(process))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                $"kubernetes-gateway-port-forward-endpoint-timeout: Local endpoint '127.0.0.1:{localPort}' did not become reachable within '{timeout}'. ProcessId='{TryGetProcessId(process)}', HasExited='{HasProcessExited(process)}'.");
        }

        /// <summary>
        /// Creates the transport result used when the control plane can resolve Kubernetes Service DNS directly.
        /// </summary>
        private static AiKubernetesGatewayTransportEndpoint CreateInternalTransportEndpoint(
            AiKubernetesGatewayEndpoint gatewayEndpoint)
        {
            return new AiKubernetesGatewayTransportEndpoint
            {
                Endpoint = gatewayEndpoint.InternalEndpoint,
                InternalEndpoint = gatewayEndpoint.InternalEndpoint,
                Namespace = gatewayEndpoint.ServiceNamespace,
                GatewayName = gatewayEndpoint.GatewayName,
                ServiceName = gatewayEndpoint.ServiceName,
                ServicePort = gatewayEndpoint.ServicePort,
                UsesPortForward = false,
                LocalPort = null
            };
        }

        /// <summary>
        /// Creates the transport result used by a local control plane.
        /// </summary>
        private static AiKubernetesGatewayTransportEndpoint CreatePortForwardTransportEndpoint(
            AiKubernetesGatewayEndpoint gatewayEndpoint,
            int localPort)
        {
            return new AiKubernetesGatewayTransportEndpoint
            {
                Endpoint = $"http://127.0.0.1:{localPort}",
                InternalEndpoint = gatewayEndpoint.InternalEndpoint,
                Namespace = gatewayEndpoint.ServiceNamespace,
                GatewayName = gatewayEndpoint.GatewayName,
                ServiceName = gatewayEndpoint.ServiceName,
                ServicePort = gatewayEndpoint.ServicePort,
                UsesPortForward = true,
                LocalPort = localPort
            };
        }

        /// <summary>
        /// Creates the process-wide identity of one Gateway Service tunnel.
        /// </summary>
        private static string CreateRegistrationKey(
            string? kubectlPath,
            string namespaceName,
            string serviceName,
            int servicePort,
            int configuredLocalPort)
        {
            var normalizedKubectlPath =
                string.IsNullOrWhiteSpace(kubectlPath)
                    ? "kubectl"
                    : kubectlPath.Trim();

            var kubeConfigIdentity =
                System.Environment.GetEnvironmentVariable("KUBECONFIG") ??
                string.Empty;

            return string.Join(
                "|",
                normalizedKubectlPath,
                kubeConfigIdentity,
                namespaceName,
                serviceName,
                servicePort.ToString(CultureInfo.InvariantCulture),
                configuredLocalPort.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Validates a resolved Gateway endpoint.
        /// </summary>
        private static void ValidateGatewayEndpoint(
            AiKubernetesGatewayEndpoint gatewayEndpoint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(gatewayEndpoint.Namespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(gatewayEndpoint.ServiceNamespace);
            ArgumentException.ThrowIfNullOrWhiteSpace(gatewayEndpoint.GatewayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(gatewayEndpoint.ServiceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(gatewayEndpoint.InternalEndpoint);

            if (gatewayEndpoint.ServicePort <= 0 ||
                gatewayEndpoint.ServicePort > IPEndPoint.MaxPort)
            {
                throw new InvalidOperationException(
                    $"kubernetes-gateway-service-port-invalid: Gateway Service port '{gatewayEndpoint.ServicePort}' is outside the valid TCP port range.");
            }
        }

        /// <summary>
        /// Throws when the manager has already been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref this.disposed) != 0,
                this);
        }

        /// <summary>
        /// Disposes a shared registration and its kubectl process.
        /// </summary>
        private static void DisposeRegistration(
            SharedPortForwardRegistration registration)
        {
            KillProcess(registration.Process);
            registration.Process.Dispose();
        }

        /// <summary>
        /// Kills a kubectl process when it is still alive.
        /// </summary>
        private static void KillProcess(
            DiagnosticsProcess process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(milliseconds: 2000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        /// <summary>
        /// Determines whether a process is alive.
        /// </summary>
        private static bool IsProcessAlive(
            DiagnosticsProcess process)
        {
            try
            {
                return !process.HasExited;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Determines whether a process has exited without throwing for disposed processes.
        /// </summary>
        private static bool HasProcessExited(
            DiagnosticsProcess process)
        {
            return !IsProcessAlive(process);
        }

        /// <summary>
        /// Gets a process id without throwing for an unstarted or disposed process.
        /// </summary>
        private static int TryGetProcessId(
            DiagnosticsProcess process)
        {
            try
            {
                return process.Id;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }

        /// <summary>
        /// Creates bounded startup diagnostics from kubectl output.
        /// </summary>
        private static string CreateOutputSummary(
            ConcurrentQueue<string> outputLines)
        {
            var lines =
                outputLines.ToArray();

            return lines.Length == 0
                ? "none"
                : string.Join(" | ", lines);
        }

        /// <summary>
        /// Represents one process-wide shared kubectl port-forward registration.
        /// </summary>
        private sealed class SharedPortForwardRegistration
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SharedPortForwardRegistration"/> class.
            /// </summary>
            public SharedPortForwardRegistration(
                DiagnosticsProcess process,
                int localPort,
                int referenceCount)
            {
                Process = process ?? throw new ArgumentNullException(nameof(process));
                LocalPort = localPort;
                ReferenceCount = referenceCount;
            }

            /// <summary>
            /// Gets the kubectl process.
            /// </summary>
            public DiagnosticsProcess Process { get; }

            /// <summary>
            /// Gets the selected local port.
            /// </summary>
            public int LocalPort { get; }

            /// <summary>
            /// Gets or sets the number of manager instances owning the registration.
            /// </summary>
            public int ReferenceCount { get; set; }
        }
    }
}
