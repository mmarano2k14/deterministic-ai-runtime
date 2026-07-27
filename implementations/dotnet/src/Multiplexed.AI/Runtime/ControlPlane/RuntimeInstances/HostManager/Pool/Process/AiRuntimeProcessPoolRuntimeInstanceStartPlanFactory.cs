using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Readiness;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Projects one process-pool child into a real RuntimeInstanceOnly launch and readiness plan.
    /// </summary>
    public sealed class AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory :
        IAiRuntimeProcessPoolRuntimeInstanceStartPlanFactory
    {
        private readonly AiRuntimeProcessPoolRuntimeInstanceOptions options;
        private readonly IAiRuntimeProcessPoolPortAllocator portAllocator;

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory"/> class.
        /// </summary>
        /// <param name="options">The RuntimeInstanceOnly child options.</param>
        /// <param name="portAllocator">The child transport port allocator.</param>
        public AiRuntimeProcessPoolRuntimeInstanceStartPlanFactory(
            AiRuntimeProcessPoolRuntimeInstanceOptions options,
            IAiRuntimeProcessPoolPortAllocator portAllocator)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(portAllocator);

            this.options = CopyOptions(options);
            AiRuntimeProcessPoolRuntimeInstanceOptionsValidator.Validate(this.options);
            this.portAllocator = portAllocator;
        }

        /// <inheritdoc />
        public async Task<AiRuntimeProcessPoolRuntimeInstanceStartPlan> CreateAsync(
            AiRuntimeProcessPoolChildStartRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);

            var portLease =
                await this.portAllocator
                    .ReserveAsync(
                        this.options.BasePort,
                        this.options.MaxPort,
                        cancellationToken)
                    .ConfigureAwait(false);

            try
            {
                var endpoint =
                    string.Concat(
                        "http://",
                        this.options.EndpointHost,
                        ":",
                        portLease.Port.ToString(CultureInfo.InvariantCulture));

                var processOptions =
                    this.CreateProcessOptions(
                        request,
                        endpoint,
                        portLease.Port);

                return new AiRuntimeProcessPoolRuntimeInstanceStartPlan
                {
                    PortLease = portLease,
                    TransportEndpoint = endpoint,
                    ProcessOptions = processOptions,
                    ReadinessRequest = new AiRuntimeInstanceReadinessRequest
                    {
                        ControlPlaneId = this.options.ControlPlaneId,
                        ExecutionContextSnapshot = this.options.ExecutionContextSnapshot,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        ProviderName = this.options.ProviderName,
                        TransportName = this.options.TransportName,
                        RequireTransportEndpoint = true,
                        Timeout = this.options.StartupTimeout,
                        PollInterval = this.options.ReadinessPollInterval,
                        TransportEndpoint = endpoint
                    }
                };
            }
            catch
            {
                await portLease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Creates the child process options and applies authoritative configuration last.
        /// </summary>
        /// <param name="request">The child start request.</param>
        /// <param name="endpoint">The allocated transport endpoint.</param>
        /// <param name="port">The allocated transport port.</param>
        /// <returns>The child process options.</returns>
        private AiRuntimeProcessPoolChildProcessOptions CreateProcessOptions(
            AiRuntimeProcessPoolChildStartRequest request,
            string endpoint,
            int port)
        {
            var environment =
                new Dictionary<string, string>(
                    this.options.EnvironmentVariables,
                    StringComparer.OrdinalIgnoreCase);

            ApplyAuthoritativeEnvironment(
                environment,
                request,
                endpoint,
                port,
                this.options);

            var assemblyDirectory =
                Path.GetDirectoryName(this.options.RuntimeHostAssemblyPath);

            return new AiRuntimeProcessPoolChildProcessOptions
            {
                ExecutablePath = this.options.DotnetExecutablePath,
                Arguments =
                {
                    this.options.RuntimeHostAssemblyPath
                },
                WorkingDirectory =
                    !string.IsNullOrWhiteSpace(this.options.WorkingDirectory)
                        ? this.options.WorkingDirectory
                        : assemblyDirectory,
                EnvironmentVariables = environment,
                RedirectOutput = this.options.RedirectOutput,
                CreateNoWindow = this.options.CreateNoWindow,
                KillEntireProcessTreeOnStop =
                    this.options.KillEntireProcessTreeOnStop,
                StopTimeoutSeconds = this.options.StopTimeoutSeconds
            };
        }

        /// <summary>
        /// Applies configuration required for deterministic RuntimeInstanceOnly startup.
        /// </summary>
        private static void ApplyAuthoritativeEnvironment(
            IDictionary<string, string> environment,
            AiRuntimeProcessPoolChildStartRequest request,
            string endpoint,
            int port,
            AiRuntimeProcessPoolRuntimeInstanceOptions options)
        {
            environment["AiMcpHost__Mode"] = "RuntimeInstanceOnly";
            environment["AiMcpHost__Port"] = port.ToString(CultureInfo.InvariantCulture);
            environment["ASPNETCORE_URLS"] = endpoint;
            environment["DOTNET_URLS"] = endpoint;

            ApplyAuthoritativeTransportEnvironment(
                environment,
                endpoint,
                options.TransportName);

            environment["AiMcpHost__EnableSharedQueuePump"] = "false";
            environment["AiMcpHost__EnableReplayTools"] = "false";
            environment["AiMcpHost__EnableObservabilityTools"] = "false";

            environment["AiLocalRuntimeInstancePool__Enabled"] = "false";
            environment["AiLocalRuntimeInstancePool__InstanceCount"] = "0";
            environment["AiLocalRuntimeInstancePool__WorkerCountPerInstance"] = "0";
            environment["AiLocalRuntimeInstancePool__MaxConcurrentRunsPerInstance"] = "0";
            environment["AiLocalRuntimeInstancePool__LocalQueueCapacity"] = "0";
            environment["AiLocalRuntimeInstancePool__RuntimeInstanceIdPrefix"] = "disabled";

            environment["AiEngine__ControlPlane__ControlPlaneId"] = options.ControlPlaneId;
            environment["AiEngine__ControlPlane__RedisDiscoveryKey"] =
                string.Concat("multiplexed-ai:", options.ControlPlaneId);
            environment["AiEngine__ControlPlane__EnableDiscovery"] =
                options.EnableControlPlaneDiscovery.ToString(
                    CultureInfo.InvariantCulture);
            environment["AiEngine__ControlPlane__PublishDiscovery"] = "false";
            environment["AiEngine__ControlPlane__RequireDiscovery"] =
                options.RequireControlPlaneDiscovery.ToString(
                    CultureInfo.InvariantCulture);
            environment["AiEngine__ControlPlane__DiscoveryResolutionTimeout"] =
                options.DiscoveryResolutionTimeout.ToString(
                    "c",
                    CultureInfo.InvariantCulture);
            environment["AiEngine__ControlPlane__DiscoveryResolutionPollInterval"] =
                options.DiscoveryResolutionPollInterval.ToString(
                    "c",
                    CultureInfo.InvariantCulture);

            environment["AiEngine__RuntimeInstanceId"] = request.RuntimeInstanceId;
            environment["AiEngine__PipelineBackgroundController__RuntimeInstanceId"] =
                request.RuntimeInstanceId;
            environment["AiEngine__PipelineBackgroundController__MaxConcurrentRuns"] =
                options.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture);
            environment["AiEngine__PipelineBackgroundController__QueueCapacity"] =
                options.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture);
            environment["AiEngine__PipelineBackgroundController__Distributed__Enabled"] = "true";
            environment["AiEngine__PipelineBackgroundController__Distributed__WorkerCount"] =
                options.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture);
            environment["AiEngine__RuntimeInstanceWorker__RuntimeInstanceId"] =
                request.RuntimeInstanceId;

            environment["AiRuntimeInstanceRegistration__Enabled"] = "true";
            environment["AiRuntimeInstanceRegistration__PoolId"] = request.PoolId;
            environment["AiRuntimeInstanceRegistration__HostId"] = request.HostId;
            environment["AiRuntimeInstanceRegistration__RuntimeInstanceId"] =
                request.RuntimeInstanceId;
            environment["AiRuntimeInstanceRegistration__ProviderName"] = options.ProviderName;
            environment["AiRuntimeInstanceRegistration__Role"] = "Runtime";
            environment["AiRuntimeInstanceRegistration__WorkerCount"] =
                options.WorkerCountPerInstance.ToString(CultureInfo.InvariantCulture);
            environment["AiRuntimeInstanceRegistration__MaxConcurrentRuns"] =
                options.MaxConcurrentRunsPerInstance.ToString(CultureInfo.InvariantCulture);
            environment["AiRuntimeInstanceRegistration__QueueCapacity"] =
                options.LocalQueueCapacity.ToString(CultureInfo.InvariantCulture);
            environment["AiRuntimeInstanceRegistration__RuntimeVersion"] = options.RuntimeVersion;
            environment["AiRuntimeInstanceRegistration__HeartbeatInterval"] =
                options.HeartbeatInterval.ToString("c", CultureInfo.InvariantCulture);

            environment["AiRuntimeInstanceRegistration__ProviderMetadata__provider.name"] =
                options.ProviderName;
            environment["AiRuntimeInstanceRegistration__ProviderMetadata__transport.name"] =
                options.TransportName;
            environment["AiRuntimeInstanceRegistration__ProviderMetadata__transport.endpoint"] =
                endpoint;
            environment["AiRuntimeInstanceRegistration__ProviderMetadata__runtime.instance.id"] =
                request.RuntimeInstanceId;

            environment["AiRuntimeInstanceRegistration__Metadata__provider.name"] =
                options.ProviderName;
            environment["AiRuntimeInstanceRegistration__Metadata__transport.name"] =
                options.TransportName;
            environment["AiRuntimeInstanceRegistration__Metadata__transport.endpoint"] = endpoint;
            environment["AiRuntimeInstanceRegistration__Metadata__runtime.instance.id"] =
                request.RuntimeInstanceId;
            environment["AiRuntimeInstanceRegistration__Metadata__hostType"] =
                "runtime-process-pool";
            environment["AiRuntimeInstanceRegistration__Metadata__deployment"] =
                "process-pool";

            environment[
                string.Concat(
                    "AiRuntimeInstanceRegistration__Metadata__",
                    AiRuntimeInstanceIsolationMetadataKeys.TenantId)] =
                options.ExecutionContextSnapshot.TenantId;
            environment[
                string.Concat(
                    "AiRuntimeInstanceRegistration__Metadata__",
                    AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId)] =
                options.ExecutionContextSnapshot.TenantGroupId;
            environment[
                string.Concat(
                    "AiRuntimeInstanceRegistration__Metadata__",
                    AiRuntimeInstanceIsolationMetadataKeys.IsolationMode)] =
                options.IsolationMode ?? string.Empty;
            environment[
                string.Concat(
                    "AiRuntimeInstanceRegistration__Metadata__",
                    AiRuntimeInstanceIsolationMetadataKeys.PreferDedicatedCapacity)] =
                options.PreferDedicatedCapacity.ToString(CultureInfo.InvariantCulture);
            environment[
                string.Concat(
                    "AiRuntimeInstanceRegistration__Metadata__",
                    AiRuntimeInstanceIsolationMetadataKeys.AllowSharedFallback)] =
                options.AllowSharedFallback.ToString(CultureInfo.InvariantCulture);

            environment["RuntimeInstanceId"] = request.RuntimeInstanceId;
            environment["AI_RUNTIME_INSTANCE_ID"] = request.RuntimeInstanceId;
            environment["MULTIPLEXED_AI_RUNTIME_INSTANCE_ID"] = request.RuntimeInstanceId;
        }

        /// <summary>
        /// Applies transport-specific server settings to the exact allocated child endpoint.
        /// </summary>
        private static void ApplyAuthoritativeTransportEnvironment(
            IDictionary<string, string> environment,
            string endpoint,
            string transportName)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(
                    transportName,
                    "grpc"))
            {
                return;
            }

            environment["Kestrel__EndpointDefaults__Protocols"] =
                "Http2";

            environment["Kestrel__Endpoints__Grpc__Url"] =
                endpoint;

            environment["Kestrel__Endpoints__Grpc__Protocols"] =
                "Http2";
        }

        /// <summary>
        /// Validates one authoritative child identity request.
        /// </summary>
        private static void ValidateRequest(
            AiRuntimeProcessPoolChildStartRequest request)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.PoolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.HostId);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.RuntimeInstanceId);

            if (request.Ordinal <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request));
            }
        }

        /// <summary>
        /// Copies mutable options so later caller mutation cannot alter launch correctness.
        /// </summary>
        private static AiRuntimeProcessPoolRuntimeInstanceOptions CopyOptions(
            AiRuntimeProcessPoolRuntimeInstanceOptions options)
        {
            return new AiRuntimeProcessPoolRuntimeInstanceOptions
            {
                DotnetExecutablePath = options.DotnetExecutablePath,
                RuntimeHostAssemblyPath = options.RuntimeHostAssemblyPath,
                WorkingDirectory = options.WorkingDirectory,
                BasePort = options.BasePort,
                MaxPort = options.MaxPort,
                EndpointHost = options.EndpointHost,
                ControlPlaneId = options.ControlPlaneId,
                EnableControlPlaneDiscovery =
                    options.EnableControlPlaneDiscovery,
                RequireControlPlaneDiscovery =
                    options.RequireControlPlaneDiscovery,
                DiscoveryResolutionTimeout =
                    options.DiscoveryResolutionTimeout,
                DiscoveryResolutionPollInterval =
                    options.DiscoveryResolutionPollInterval,
                ExecutionContextSnapshot = options.ExecutionContextSnapshot,
                ProviderName = options.ProviderName,
                TransportName = options.TransportName,
                RuntimeVersion = options.RuntimeVersion,
                WorkerCountPerInstance = options.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = options.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = options.LocalQueueCapacity,
                IsolationMode = options.IsolationMode,
                PreferDedicatedCapacity = options.PreferDedicatedCapacity,
                AllowSharedFallback = options.AllowSharedFallback,
                StartupTimeout = options.StartupTimeout,
                ReadinessPollInterval = options.ReadinessPollInterval,
                HeartbeatInterval = options.HeartbeatInterval,
                RedirectOutput = options.RedirectOutput,
                CreateNoWindow = options.CreateNoWindow,
                KillEntireProcessTreeOnStop = options.KillEntireProcessTreeOnStop,
                StopTimeoutSeconds = options.StopTimeoutSeconds,
                EnvironmentVariables = options.EnvironmentVariables is null
                    ? null!
                    : new(
                        options.EnvironmentVariables,
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
