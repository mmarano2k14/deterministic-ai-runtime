using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.Observability;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Area;
using Multiplexed.Abstractions.AI.ControlPlane.Observability.Events;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers.Transport;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.AI.Runtime.ControlPlane.Observability;
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

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Strategy.Process
{
    /// <summary>
    /// Starts runtime hosts as external dotnet processes.
    /// </summary>
    public sealed class ProcessAiRuntimeHostCreationStrategy : IAiRuntimeHostCreationStrategy, IAiRuntimeHostProcessControl, IAsyncDisposable
    {
        private const string ProcessRuntimeHostCreationOperation = "runtime-process-host-creation";

        private static readonly SemaphoreSlim PortAllocationLock = new(1, 1);
        private static readonly ConcurrentDictionary<int, byte> ReservedPorts = new();

        private readonly AiRuntimeProcessHostCreationOptions options;
        private readonly ILogger<ProcessAiRuntimeHostCreationStrategy> logger;
        private readonly IAiControlPlaneObserver observer;
        private readonly ConcurrentDictionary<string, RuntimeHostProcessRegistration> processesByRuntimeInstanceId =
            new(StringComparer.OrdinalIgnoreCase);

        public ProcessAiRuntimeHostCreationStrategy(
            IOptions<AiRuntimeProcessHostCreationOptions> options,
            ILogger<ProcessAiRuntimeHostCreationStrategy> logger)
            : this(
                options,
                logger,
                new NoopAiControlPlaneObserver())
        {
        }

        public ProcessAiRuntimeHostCreationStrategy(
            IOptions<AiRuntimeProcessHostCreationOptions> options,
            ILogger<ProcessAiRuntimeHostCreationStrategy> logger,
            IAiControlPlaneObserver observer)
        {
            this.options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        }

        public AiRuntimeHostCreationMode Mode => AiRuntimeHostCreationMode.Process;

        public async Task<AiRuntimeHostStartResult> StartAsync(
            AiRuntimeHostStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var startedAtUtc = DateTimeOffset.UtcNow;

            await this.RecordProcessHostCreationEventAsync(
                    AiControlPlaneEventType.OperationStarted,
                    request,
                    null,
                    null,
                    null,
                    null,
                    BuildStartProperties(request),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!this.options.Enabled)
            {
                var disabledResult = AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    "process-host-creation-disabled");

                await this.RecordProcessHostCreationResultAsync(
                        request,
                        disabledResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return disabledResult;
            }

            if (string.IsNullOrWhiteSpace(this.options.RuntimeHostAssemblyPath))
            {
                var missingAssemblyPathResult = AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    "process-runtime-host-assembly-path-missing");

                await this.RecordProcessHostCreationResultAsync(
                        request,
                        missingAssemblyPathResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return missingAssemblyPathResult;
            }

            if (!File.Exists(this.options.RuntimeHostAssemblyPath))
            {
                var assemblyNotFoundResult = AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    request.TransportEndpoint,
                    $"process-runtime-host-assembly-not-found:{this.options.RuntimeHostAssemblyPath}");

                await this.RecordProcessHostCreationResultAsync(
                        request,
                        assemblyNotFoundResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return assemblyNotFoundResult;
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
                    ReleasePort(port);

                    var nullProcessResult = AiRuntimeHostStartResult.Rejected(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        endpoint,
                        "process-start-returned-null",
                        retryable: true,
                        metadata);

                    await this.RecordProcessHostCreationResultAsync(
                            request,
                            nullProcessResult,
                            startedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return nullProcessResult;
                }

                AttachOutputLogging(request, process);

                var registration = new RuntimeHostProcessRegistration(
                    process,
                    port,
                    endpoint);

                if (!this.processesByRuntimeInstanceId.TryAdd(request.RuntimeInstanceId, registration))
                {
                    TryKillProcess(process);
                    ReleasePort(port);

                    var duplicateProcessResult = AiRuntimeHostStartResult.Rejected(
                        request.ExecutionContextSnapshot,
                        request.RuntimeInstanceId,
                        request.ProviderName,
                        request.TransportName,
                        endpoint,
                        $"process-runtime-instance-already-started:{request.RuntimeInstanceId}",
                        retryable: false,
                        metadata);

                    await this.RecordProcessHostCreationResultAsync(
                            request,
                            duplicateProcessResult,
                            startedAtUtc,
                            cancellationToken)
                        .ConfigureAwait(false);

                    return duplicateProcessResult;
                }

                this.logger.LogInformation(
                    "PROCESS HOST STARTED RuntimeInstanceId={RuntimeInstanceId} ProcessId={ProcessId} Endpoint={Endpoint} HostAssembly={RuntimeHostAssemblyPath}.",
                    request.RuntimeInstanceId,
                    process.Id,
                    endpoint,
                    this.options.RuntimeHostAssemblyPath);

                await EnsureProcessDidNotExitImmediatelyAsync(
                        request,
                        registration,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);

                var startedResult = AiRuntimeHostStartResult.Started(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName ?? AiRuntimeInstanceCommandTransportMetadataKeys.HttpTransportName,
                    endpoint,
                    metadata);

                await this.RecordProcessHostCreationResultAsync(
                        request,
                        startedResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return startedResult;
            }
            catch (OperationCanceledException)
            {
                if (this.processesByRuntimeInstanceId.TryRemove(request.RuntimeInstanceId, out var cancelledRegistration))
                {
                    TryKillProcess(cancelledRegistration.Process);
                    ReleasePort(cancelledRegistration.Port);
                }
                else
                {
                    ReleasePort(port);
                }

                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (this.processesByRuntimeInstanceId.TryRemove(request.RuntimeInstanceId, out var failedRegistration))
                {
                    TryKillProcess(failedRegistration.Process);
                    ReleasePort(failedRegistration.Port);
                }
                else
                {
                    ReleasePort(port);
                }

                this.logger.LogError(
                    exception,
                    "Failed to start runtime host process. RuntimeInstanceId={RuntimeInstanceId}, Endpoint={Endpoint}.",
                    request.RuntimeInstanceId,
                    endpoint);

                var failedResult = AiRuntimeHostStartResult.Rejected(
                    request.ExecutionContextSnapshot,
                    request.RuntimeInstanceId,
                    request.ProviderName,
                    request.TransportName,
                    endpoint,
                    $"process-start-failed:{exception.GetType().Name}",
                    retryable: true,
                    metadata);

                await this.RecordProcessHostCreationResultAsync(
                        request,
                        failedResult,
                        startedAtUtc,
                        cancellationToken)
                    .ConfigureAwait(false);

                return failedResult;
            }
        }

        private async Task RecordProcessHostCreationResultAsync(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken)
        {
            var completedAtUtc = DateTimeOffset.UtcNow;
            var durationMs = CalculateDurationMs(startedAtUtc, completedAtUtc);
            var eventType = result.Success
                ? AiControlPlaneEventType.OperationCompleted
                : AiControlPlaneEventType.OperationFailed;
            var outcome = result.Success
                ? AiControlPlaneOperationOutcome.Succeeded
                : AiControlPlaneOperationOutcome.Denied;
            var failureReason = result.Success
                ? null
                : result.FailureReason;

            await this.RecordProcessHostCreationEventAsync(
                    eventType,
                    request,
                    result,
                    outcome,
                    failureReason,
                    durationMs,
                    BuildResultProperties(request, result, durationMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task RecordProcessHostCreationEventAsync(
            AiControlPlaneEventType eventType,
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult? result,
            AiControlPlaneOperationOutcome? outcome,
            string? failureReason,
            long? durationMs,
            IReadOnlyDictionary<string, object?>? properties,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.observer
                    .RecordAsync(
                        new AiControlPlaneEvent
                        {
                            EventType = eventType,
                            Area = AiControlPlaneArea.Scaling,
                            Operation = ProcessRuntimeHostCreationOperation,
                            Outcome = outcome,
                            FailureReason = failureReason,
                            DurationMs = durationMs,
                            Correlation = new AiRuntimeExecutionCorrelationContext
                            {
                                CorrelationId = string.IsNullOrWhiteSpace(request.RuntimeInstanceId)
                                    ? Guid.NewGuid().ToString("N")
                                    : request.RuntimeInstanceId,
                                RuntimeInstanceId = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                                PipelineKey = request.ExecutionContextSnapshot?.ContextKey
                            },
                            Properties = MergeEventProperties(
                                properties,
                                new Dictionary<string, object?>
                                {
                                    ["runtimeInstanceId"] = result?.RuntimeInstanceId ?? request.RuntimeInstanceId,
                                    ["providerName"] = result?.ProviderName ?? request.ProviderName,
                                    ["transportName"] = result?.TransportName ?? request.TransportName,
                                    ["transportEndpoint"] = result?.TransportEndpoint ?? request.TransportEndpoint,
                                    ["hostCreationMode"] = AiRuntimeHostCreationMode.Process.ToString(),
                                    ["tenantId"] = request.ExecutionContextSnapshot?.TenantId ?? request.TenantId,
                                    ["tenantGroupId"] = request.ExecutionContextSnapshot?.TenantGroupId ?? request.TenantGroupId,
                                    ["pipelineKey"] = request.ExecutionContextSnapshot?.ContextKey,
                                    ["success"] = result?.Success,
                                    ["failureReason"] = result?.FailureReason
                                })
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Control-plane observability must not break process host creation.
            }
        }

        private IReadOnlyDictionary<string, object?> BuildStartProperties(
            AiRuntimeHostStartRequest request)
        {
            return new Dictionary<string, object?>
            {
                ["runtimeInstanceId"] = request.RuntimeInstanceId,
                ["providerName"] = request.ProviderName,
                ["transportName"] = request.TransportName,
                ["transportEndpoint"] = request.TransportEndpoint,
                ["hostCreationMode"] = AiRuntimeHostCreationMode.Process.ToString(),
                ["enabled"] = this.options.Enabled,
                ["runtimeHostAssemblyPath"] = this.options.RuntimeHostAssemblyPath,
                ["dotnetExecutablePath"] = this.options.DotnetExecutablePath,
                ["basePort"] = this.options.BasePort,
                ["maxPort"] = this.options.MaxPort,
                ["redirectOutput"] = this.options.RedirectOutput,
                ["tenantId"] = request.TenantId,
                ["tenantGroupId"] = request.TenantGroupId,
                ["pipelineKey"] = request.ExecutionContextSnapshot?.ContextKey
            };
        }

        private static IReadOnlyDictionary<string, object?> BuildResultProperties(
            AiRuntimeHostStartRequest request,
            AiRuntimeHostStartResult result,
            long durationMs)
        {
            return new Dictionary<string, object?>
            {
                ["runtimeInstanceId"] = result.RuntimeInstanceId ?? request.RuntimeInstanceId,
                ["providerName"] = result.ProviderName ?? request.ProviderName,
                ["transportName"] = result.TransportName ?? request.TransportName,
                ["transportEndpoint"] = result.TransportEndpoint ?? request.TransportEndpoint,
                ["hostCreationMode"] = AiRuntimeHostCreationMode.Process.ToString(),
                ["success"] = result.Success,
                ["failureReason"] = result.FailureReason,
                ["durationMs"] = durationMs,
                ["tenantId"] = request.TenantId,
                ["tenantGroupId"] = request.TenantGroupId,
                ["pipelineKey"] = request.ExecutionContextSnapshot?.ContextKey
            };
        }

        private static IReadOnlyDictionary<string, object?> MergeEventProperties(
            IReadOnlyDictionary<string, object?>? properties,
            IReadOnlyDictionary<string, object?> additionalProperties)
        {
            var merged = new Dictionary<string, object?>();

            if (properties is not null)
            {
                foreach (var item in properties)
                {
                    merged[item.Key] = item.Value;
                }
            }

            foreach (var item in additionalProperties)
            {
                merged[item.Key] = item.Value;
            }

            return merged;
        }

        private static long CalculateDurationMs(
            DateTimeOffset startedAtUtc,
            DateTimeOffset completedAtUtc)
        {
            return (long)(completedAtUtc - startedAtUtc).TotalMilliseconds;
        }

        /// <summary>
        /// Kills a runtime host process by runtime instance identifier.
        /// </summary>
        /// <param name="runtimeInstanceId">The runtime instance identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> when a process was found and killed or was already exited; otherwise, <c>false</c>.</returns>
        public async Task<bool> KillAsync(
            string runtimeInstanceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            cancellationToken.ThrowIfCancellationRequested();

            if (!this.processesByRuntimeInstanceId.TryRemove(runtimeInstanceId, out var registration))
            {
                this.logger.LogWarning(
                    "Runtime host process kill requested but no process was registered. RuntimeInstanceId={RuntimeInstanceId}.",
                    runtimeInstanceId);

                return false;
            }

            try
            {
                if (registration.Process.HasExited)
                {
                    this.logger.LogInformation(
                        "Runtime host process kill requested but process had already exited. RuntimeInstanceId={RuntimeInstanceId}, ProcessId={ProcessId}, ExitCode={ExitCode}, Port={Port}.",
                        runtimeInstanceId,
                        registration.Process.Id,
                        registration.Process.ExitCode,
                        registration.Port);

                    registration.Process.Dispose();
                    ReleasePort(registration.Port);
                    return true;
                }

                this.logger.LogWarning(
                    "Killing runtime host process on demand. RuntimeInstanceId={RuntimeInstanceId}, ProcessId={ProcessId}, Port={Port}.",
                    runtimeInstanceId,
                    registration.Process.Id,
                    registration.Port);

                registration.Process.Kill(entireProcessTree: true);

                await registration.Process
                    .WaitForExitAsync(cancellationToken)
                    .ConfigureAwait(false);

                this.logger.LogWarning(
                    "Runtime host process killed on demand. RuntimeInstanceId={RuntimeInstanceId}, ProcessId={ProcessId}, ExitCode={ExitCode}, Port={Port}.",
                    runtimeInstanceId,
                    registration.Process.Id,
                    registration.Process.ExitCode,
                    registration.Port);

                registration.Process.Dispose();
                ReleasePort(registration.Port);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.logger.LogWarning(
                    exception,
                    "Failed to kill runtime host process on demand. RuntimeInstanceId={RuntimeInstanceId}.",
                    runtimeInstanceId);

                registration.Process.Dispose();
                ReleasePort(registration.Port);
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
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
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(metadata);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

            var startInfo = new ProcessStartInfo
            {
                FileName = this.options.DotnetExecutablePath,
                Arguments = $"\"{this.options.RuntimeHostAssemblyPath}\"",
                UseShellExecute = false,

                // Required to capture logs emitted by the external runtime process.
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                CreateNoWindow = true
            };

            var assemblyDirectory = Path.GetDirectoryName(
                this.options.RuntimeHostAssemblyPath);

            if (!string.IsNullOrWhiteSpace(this.options.WorkingDirectory))
            {
                startInfo.WorkingDirectory = this.options.WorkingDirectory;
            }
            else if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                startInfo.WorkingDirectory = assemblyDirectory;
            }

            ApplyEnvironment(
                startInfo,
                request,
                endpoint,
                port,
                metadata);

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
            await PortAllocationLock
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                for (var candidate = this.options.BasePort;
                     candidate <= this.options.MaxPort;
                     candidate++)
                {
                    if (ReservedPorts.ContainsKey(candidate))
                    {
                        continue;
                    }

                    if (!IsPortAvailable(candidate))
                    {
                        continue;
                    }

                    if (ReservedPorts.TryAdd(candidate, 0))
                    {
                        this.logger.LogInformation(
                            "Reserved process-host port. Port={Port}, Range={BasePort}-{MaxPort}.",
                            candidate,
                            this.options.BasePort,
                            this.options.MaxPort);

                        return candidate;
                    }
                }

                throw new InvalidOperationException(
                    $"No available TCP port was found in range {this.options.BasePort}-{this.options.MaxPort}.");
            }
            finally
            {
                PortAllocationLock.Release();
            }
        }

        private static void ReleasePort(
            int port)
        {
            ReservedPorts.TryRemove(port, out _);
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
            RuntimeHostProcessRegistration registration,
            IReadOnlyDictionary<string, string> metadata,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

            if (!registration.Process.HasExited)
            {
                return;
            }

            this.processesByRuntimeInstanceId.TryRemove(request.RuntimeInstanceId, out _);
            ReleasePort(registration.Port);

            var exitCode = registration.Process.ExitCode;
            registration.Process.Dispose();

            throw new InvalidOperationException(
                $"Runtime host process exited immediately. RuntimeInstanceId='{request.RuntimeInstanceId}', Endpoint='{registration.Endpoint}', Port='{registration.Port}', ExitCode='{exitCode}', MetadataCount='{metadata.Count}'.");
        }

        private async Task StopProcessAsync(
            string runtimeInstanceId,
            RuntimeHostProcessRegistration registration)
        {
            try
            {
                if (registration.Process.HasExited)
                {
                    registration.Process.Dispose();
                    ReleasePort(registration.Port);
                    return;
                }

                this.logger.LogInformation(
                    "Stopping runtime host process. RuntimeInstanceId={RuntimeInstanceId}, ProcessId={ProcessId}, Port={Port}.",
                    runtimeInstanceId,
                    registration.Process.Id,
                    registration.Port);

                registration.Process.Kill(entireProcessTree: true);
                await registration.Process.WaitForExitAsync().ConfigureAwait(false);
                registration.Process.Dispose();
                ReleasePort(registration.Port);
            }
            catch (Exception exception)
            {
                registration.Process.Dispose();
                ReleasePort(registration.Port);

                this.logger.LogWarning(
                    exception,
                    "Failed to stop runtime host process. RuntimeInstanceId={RuntimeInstanceId}, Port={Port}.",
                    runtimeInstanceId,
                    registration.Port);
            }
        }

        private sealed record RuntimeHostProcessRegistration(
            System.Diagnostics.Process Process,
            int Port,
            string Endpoint);

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