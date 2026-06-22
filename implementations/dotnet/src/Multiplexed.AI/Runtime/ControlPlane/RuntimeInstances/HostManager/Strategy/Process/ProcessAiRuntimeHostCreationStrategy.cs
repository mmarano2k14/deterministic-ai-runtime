using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process
{
    /// <summary>
    /// Starts runtime hosts as external dotnet processes.
    /// </summary>
    public sealed class ProcessAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy, IAsyncDisposable
    {
        private readonly AiRuntimeProcessHostCreationOptions options;
        private readonly ILogger<ProcessAiRuntimeHostCreationStrategy> logger;
        private readonly ConcurrentDictionary<string, System.Diagnostics.Process> processesByRuntimeInstanceId = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim portAllocationLock = new(1, 1);
        private int nextPort;

        public ProcessAiRuntimeHostCreationStrategy(
            IOptions<AiRuntimeProcessHostCreationOptions> options,
            ILogger<ProcessAiRuntimeHostCreationStrategy> logger)
        {
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.nextPort = this.options.BasePort;
        }

        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Process;

        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!this.options.Enabled)
            {
                return AiRuntimeHostStartResult.Rejected(request.ExecutionContextSnapshot, request.RuntimeInstanceId, request.ProviderName, request.TransportName, request.TransportEndpoint, "process-host-creation-disabled");
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeHostAssemblyPath))
            {
                return AiRuntimeHostStartResult.Rejected(request.ExecutionContextSnapshot, request.RuntimeInstanceId, request.ProviderName, request.TransportName, request.TransportEndpoint, "process-runtime-host-assembly-path-missing");
            }

            if (!File.Exists(this.options.RuntimeHostAssemblyPath))
            {
                return AiRuntimeHostStartResult.Rejected(request.ExecutionContextSnapshot, request.RuntimeInstanceId, request.ProviderName, request.TransportName, request.TransportEndpoint, $"process-runtime-host-assembly-not-found:{this.options.RuntimeHostAssemblyPath}");
            }

            var port = await AllocatePortAsync(cancellationToken).ConfigureAwait(false);
            var endpoint = $"http://localhost:{port}";
            var metadata = CreateMetadata(request, endpoint, port);
            var startInfo = CreateStartInfo(request, endpoint, port, metadata);

            try
            {
                var process = System.Diagnostics.Process.Start(startInfo);

                if (process is null)
                {
                    return AiRuntimeHostStartResult.Rejected(request.ExecutionContextSnapshot, request.RuntimeInstanceId, request.ProviderName, request.TransportName, endpoint, "process-start-returned-null", retryable: true, metadata);
                }

                AttachOutputLogging(request, process);

                if (!this.processesByRuntimeInstanceId.TryAdd(request.RuntimeInstanceId, process))
                {
                    TryKillProcess(process);

                    return AiRuntimeHostStartResult.Rejected(request.ExecutionContextSnapshot, request.RuntimeInstanceId, request.ProviderName, request.TransportName, endpoint, $"process-runtime-instance-already-started:{request.RuntimeInstanceId}", retryable: false, metadata);
                }

                this.logger.LogInformation(
                    "PROCESS HOST STARTED RuntimeInstanceId={RuntimeInstanceId} ProcessId={ProcessId} Endpoint={Endpoint} HostAssembly={RuntimeHostAssemblyPath}.",
                    request.RuntimeInstanceId,
                    process.Id,
                    endpoint,
                    this.options.RuntimeHostAssemblyPath);

                await EnsureProcessDidNotExitImmediatelyAsync(request, process, endpoint, metadata, cancellationToken).ConfigureAwait(false);

                return AiRuntimeHostStartResult.Started(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName ?? AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                    endpoint,
                    metadata);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.logger.LogError(
                    exception,
                    "Failed to start runtime host process. RuntimeInstanceId={RuntimeInstanceId}, Endpoint={Endpoint}.",
                    request.RuntimeInstanceId,
                    endpoint);

                return AiRuntimeHostStartResult.Rejected(request.ExecutionContextSnapshot, request.RuntimeInstanceId, request.ProviderName, request.TransportName, endpoint, $"process-start-failed:{exception.GetType().Name}", retryable: true, metadata);
            }
        }

        public async ValueTask DisposeAsync()
        {
            this.portAllocationLock.Dispose();

            if (!this.options.KillOnDispose)
            {
                return;
            }

            foreach (var pair in this.processesByRuntimeInstanceId)
            {
                await StopProcessAsync(pair.Key, pair.Value).ConfigureAwait(false);
            }

            this.processesByRuntimeInstanceId.Clear();
        }

        private ProcessStartInfo CreateStartInfo(
            AiRuntimeHostStartRequest request,
            string endpoint,
            int port,
            IReadOnlyDictionary<string, string> metadata)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = this.options.DotnetExecutablePath,
                Arguments = $"\"{this.options.RuntimeHostAssemblyPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = this.options.RedirectOutput,
                RedirectStandardError = this.options.RedirectOutput,
                CreateNoWindow = true
            };

            var assemblyDirectory = Path.GetDirectoryName(this.options.RuntimeHostAssemblyPath);

            if (!string.IsNullOrWhiteSpace(this.options.WorkingDirectory))
            {
                startInfo.WorkingDirectory = this.options.WorkingDirectory;
            }
            else if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                startInfo.WorkingDirectory = assemblyDirectory;
            }

            ApplyEnvironment(startInfo, request, endpoint, port, metadata);

            return startInfo;
        }

        private void ApplyEnvironment(
            ProcessStartInfo startInfo,
            AiRuntimeHostStartRequest request,
            string endpoint,
            int port,
            IReadOnlyDictionary<string, string> metadata)
        {
            foreach (var pair in this.options.EnvironmentVariables)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    startInfo.Environment[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            startInfo.Environment["AiMcpHost__Mode"] = "RuntimeInstanceOnly";
            startInfo.Environment["AiMcpHost__Port"] = port.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["ASPNETCORE_URLS"] = endpoint;
            startInfo.Environment["DOTNET_URLS"] = endpoint;
            startInfo.Environment["AiMcpHost__EnableSharedQueuePump"] = "false";
            startInfo.Environment["AiMcpHost__EnableReplayTools"] = "false";
            startInfo.Environment["AiMcpHost__EnableObservabilityTools"] = "false";

            startInfo.Environment["AiLocalRuntimeInstancePool__Enabled"] = "false";
            startInfo.Environment["AiLocalRuntimeInstancePool__InstanceCount"] = "0";
            startInfo.Environment["AiLocalRuntimeInstancePool__WorkerCountPerInstance"] = "0";
            startInfo.Environment["AiLocalRuntimeInstancePool__MaxConcurrentRunsPerInstance"] = "0";
            startInfo.Environment["AiLocalRuntimeInstancePool__LocalQueueCapacity"] = "0";
            startInfo.Environment["AiLocalRuntimeInstancePool__RuntimeInstanceIdPrefix"] = "disabled";

            startInfo.Environment["AiEngine__ControlPlane__ControlPlaneId"] = request.ControlPlaneId;
            startInfo.Environment["AiEngine__ControlPlane__RedisDiscoveryKey"] = $"multiplexed-ai:{request.ControlPlaneId}";
            startInfo.Environment["AiEngine__ControlPlane__EnableDiscovery"] = "true";
            startInfo.Environment["AiEngine__ControlPlane__PublishDiscovery"] = "false";
            startInfo.Environment["AiEngine__ControlPlane__RequireDiscovery"] = "true";
            startInfo.Environment["AiEngine__ControlPlane__DiscoveryResolutionTimeout"] = "00:00:10";
            startInfo.Environment["AiEngine__ControlPlane__DiscoveryResolutionPollInterval"] = "00:00:00.100";

            startInfo.Environment["AiEngine__RuntimeInstanceId"] = request.RuntimeInstanceId;
            startInfo.Environment["AiEngine__PipelineBackgroundController__RuntimeInstanceId"] = request.RuntimeInstanceId;
            startInfo.Environment["AiEngine__PipelineBackgroundController__MaxConcurrentRuns"] = request.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiEngine__PipelineBackgroundController__QueueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiEngine__PipelineBackgroundController__Distributed__Enabled"] = "true";
            startInfo.Environment["AiEngine__PipelineBackgroundController__Distributed__WorkerCount"] = request.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiEngine__RuntimeInstanceWorker__RuntimeInstanceId"] = request.RuntimeInstanceId;

            startInfo.Environment["AiRuntimeInstanceRegistration__Enabled"] = "true";
            startInfo.Environment["AiRuntimeInstanceRegistration__ControlPlaneId"] = request.ControlPlaneId;
            startInfo.Environment["AiRuntimeInstanceRegistration__RuntimeInstanceId"] = request.RuntimeInstanceId;
            startInfo.Environment["AiRuntimeInstanceRegistration__ProviderName"] = request.ProviderName;
            startInfo.Environment["AiRuntimeInstanceRegistration__Role"] = "Runtime";
            startInfo.Environment["AiRuntimeInstanceRegistration__WorkerCount"] = request.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiRuntimeInstanceRegistration__MaxConcurrentRuns"] = request.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiRuntimeInstanceRegistration__QueueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiRuntimeInstanceRegistration__RuntimeVersion"] = "process-host";
            startInfo.Environment["AiRuntimeInstanceRegistration__HeartbeatInterval"] = "00:00:02";

            startInfo.Environment["AiRuntimeInstanceRegistration__ProviderMetadata__provider.name"] = request.ProviderName;
            startInfo.Environment["AiRuntimeInstanceRegistration__ProviderMetadata__transport.name"] = request.TransportName ?? AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            startInfo.Environment["AiRuntimeInstanceRegistration__ProviderMetadata__transport.endpoint"] = endpoint;
            startInfo.Environment["AiRuntimeInstanceRegistration__ProviderMetadata__runtime.instance.id"] = request.RuntimeInstanceId;

            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__provider.name"] = request.ProviderName;
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__transport.name"] = request.TransportName ?? AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName;
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__transport.endpoint"] = endpoint;
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__runtime.instance.id"] = request.RuntimeInstanceId;
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__hostType"] = "runtime-instance-process";
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__deployment"] = "process-host";
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__hostCreation.mode"] = AiRuntimeHostCreationMode.Process.ToString();

            startInfo.Environment["RuntimeInstanceId"] = request.RuntimeInstanceId;
            startInfo.Environment["AI_RUNTIME_INSTANCE_ID"] = request.RuntimeInstanceId;
            startInfo.Environment["MULTIPLEXED_AI_RUNTIME_INSTANCE_ID"] = request.RuntimeInstanceId;

            startInfo.Environment[$"AiRuntimeInstanceRegistration__Metadata__{AiRuntimeInstanceIsolationMetadataKeys.TenantId}"] = request.TenantId ?? string.Empty;
            startInfo.Environment[$"AiRuntimeInstanceRegistration__Metadata__{AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId}"] = request.TenantGroupId ?? string.Empty;
            startInfo.Environment[$"AiRuntimeInstanceRegistration__Metadata__{AiRuntimeInstanceIsolationMetadataKeys.IsolationMode}"] = request.IsolationMode ?? string.Empty;
            startInfo.Environment[$"AiRuntimeInstanceRegistration__Metadata__{AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity}"] = request.PreferDedicatedCapacity.ToString();
            startInfo.Environment[$"AiRuntimeInstanceRegistration__Metadata__{AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback}"] = request.AllowSharedFallback.ToString();

            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__runtime.maxRuntimeInstances"] = request.MaxRuntimeInstances?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__runtime.instanceIdPrefix"] = request.RuntimeInstanceIdPrefix ?? string.Empty;
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__runtime.workerCountPerInstance"] = request.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__runtime.maxConcurrentRunsPerInstance"] = request.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__runtime.localQueueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__hostCreation.mode"] = AiRuntimeHostCreationMode.Process.ToString();

            startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Test";
            startInfo.Environment["DOTNET_ENVIRONMENT"] = "Test";

            startInfo.Environment["AiPayloadStore__Enabled"] = "true";
            startInfo.Environment["AiPayloadStore__Provider"] = "mongo-redis";
            startInfo.Environment["AiPayloadStore__RequireReplaySafePayloads"] = "true";

            startInfo.Environment["AiEngine__PayloadStore__Enabled"] = "true";
            startInfo.Environment["AiEngine__PayloadStore__Provider"] = "mongo-redis";
            startInfo.Environment["AiEngine__PayloadStore__RequireReplaySafePayloads"] = "true";

            startInfo.Environment["AiEngine__Payloads__Enabled"] = "true";
            startInfo.Environment["AiEngine__Payloads__Provider"] = "mongo-redis";
            startInfo.Environment["AiEngine__Payloads__RequireReplaySafePayloads"] = "true";

            foreach (var pair in metadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    startInfo.Environment[$"AiRuntimeInstanceRegistration__Metadata__{pair.Key}"] = pair.Value ?? string.Empty;
                }
            }

            startInfo.Environment["AiRuntimeInstanceRegistration__Metadata__hostCreation.mode"] = AiRuntimeHostCreationMode.Process.ToString();


            startInfo.Environment["AiDecisionLedger__Provider"] = "mongo";
            startInfo.Environment["AiObservability__Ledger__Provider"] = "mongo";
        }

        private void AttachOutputLogging(
            AiRuntimeHostStartRequest request,
            System.Diagnostics.Process process)
        {
            if (!this.options.RedirectOutput)
            {
                return;
            }

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    this.logger.LogInformation("PROCESS HOST STDOUT RuntimeInstanceId={RuntimeInstanceId}: {Line}", request.RuntimeInstanceId, args.Data);
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                {
                    this.logger.LogError("PROCESS HOST STDERR RuntimeInstanceId={RuntimeInstanceId}: {Line}", request.RuntimeInstanceId, args.Data);
                }
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private static IReadOnlyDictionary<string, string> CreateMetadata(
            AiRuntimeHostStartRequest request,
            string endpoint,
            int port)
        {
            var metadata = new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["provider.name"] = request.ProviderName,
                ["transport.name"] = request.TransportName ?? AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                ["transport.endpoint"] = endpoint,
                ["runtime.instance.id"] = request.RuntimeInstanceId,
                ["hostCreation.mode"] = AiRuntimeHostCreationMode.Process.ToString(),
                ["hostCreation.strategy"] = nameof(ProcessAiRuntimeHostCreationStrategy),
                ["process.port"] = port.ToString(CultureInfo.InvariantCulture),
                [AiRuntimeInstanceIsolationMetadataKeys.IsolationMode] = request.IsolationMode,
                [AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity] = request.PreferDedicatedCapacity.ToString(),
                [AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback] = request.AllowSharedFallback.ToString(),
                ["runtime.maxRuntimeInstances"] = request.MaxRuntimeInstances?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ["runtime.instanceIdPrefix"] = request.RuntimeInstanceIdPrefix,
                ["runtime.workerCountPerInstance"] = request.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture),
                ["runtime.maxConcurrentRunsPerInstance"] = request.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture),
                ["runtime.localQueueCapacity"] = request.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture)
            };

            if (!string.IsNullOrWhiteSpace(request.TenantId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantId] = request.TenantId;
            }

            if (!string.IsNullOrWhiteSpace(request.TenantGroupId))
            {
                metadata[AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] = request.TenantGroupId;
            }

            return metadata;
        }

        private async Task<int> AllocatePortAsync(
            CancellationToken cancellationToken)
        {
            await this.portAllocationLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                for (var attempt = 0; attempt <= this.options.MaxPort - this.options.BasePort; attempt++)
                {
                    var candidate = this.nextPort;
                    this.nextPort = this.nextPort >= this.options.MaxPort ? this.options.BasePort : this.nextPort + 1;

                    if (IsPortAvailable(candidate))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException($"No available TCP port was found in range {this.options.BasePort}-{this.options.MaxPort}.");
            }
            finally
            {
                this.portAllocationLock.Release();
            }
        }

        private static bool IsPortAvailable(
            int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private async Task EnsureProcessDidNotExitImmediatelyAsync(
            AiRuntimeHostStartRequest request,
            System.Diagnostics.Process process,
            string endpoint,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

            if (!process.HasExited)
            {
                return;
            }

            this.processesByRuntimeInstanceId.TryRemove(request.RuntimeInstanceId, out _);

            throw new InvalidOperationException(
                $"Runtime host process exited immediately. RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{endpoint}', ExitCode='{process.ExitCode}', MetadataCount='{metadata.Count}'.");
        }

        private async Task StopProcessAsync(
            string runtimeInstanceId,
            System.Diagnostics.Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    process.Dispose();
                    return;
                }

                this.logger.LogInformation("Stopping runtime host process. RuntimeInstanceId={RuntimeInstanceId}, ProcessId={ProcessId}.", runtimeInstanceId, process.Id);

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
                process.Dispose();
            }
            catch (Exception exception)
            {
                this.logger.LogWarning(exception, "Failed to stop runtime host process. RuntimeInstanceId={RuntimeInstanceId}.", runtimeInstanceId);
            }
        }

        private static void TryKillProcess(
            System.Diagnostics.Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                process.Dispose();
            }
            catch
            {
            }
        }
    }
}