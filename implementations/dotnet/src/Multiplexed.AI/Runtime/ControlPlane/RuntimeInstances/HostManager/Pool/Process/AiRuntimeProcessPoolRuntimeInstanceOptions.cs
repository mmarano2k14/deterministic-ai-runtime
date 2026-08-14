using Multiplexed.Abstractions.Core.ExecutionContext;
using System;
using System.Collections.Generic;

namespace Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Process
{
    /// <summary>
    /// Provides the RuntimeInstanceOnly launch and readiness settings used by process-pool children.
    /// </summary>
    /// <remarks>
    /// These settings are opt-in and do not alter the existing Process or Kubernetes host creation
    /// strategies.
    /// </remarks>
    public sealed class AiRuntimeProcessPoolRuntimeInstanceOptions
    {
        /// <summary>
        /// Gets or sets the dotnet executable used to launch the runtime host assembly.
        /// </summary>
        public string DotnetExecutablePath { get; set; } = "dotnet";

        /// <summary>
        /// Gets or sets the RuntimeInstanceOnly host assembly path.
        /// </summary>
        public string RuntimeHostAssemblyPath { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the optional child-process working directory.
        /// </summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>
        /// Gets or sets the first local TCP port available to process-pool children.
        /// </summary>
        public int BasePort { get; set; } = 5900;

        /// <summary>
        /// Gets or sets the final local TCP port available to process-pool children.
        /// </summary>
        public int MaxPort { get; set; } = 5999;

        /// <summary>
        /// Gets or sets the host name used to publish child transport endpoints.
        /// </summary>
        public string EndpointHost { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the optional stable Runtime Pool endpoint published by every child
        /// registration.
        /// </summary>
        /// <remarks>
        /// The child process still binds and is probed through its exact allocated local endpoint.
        /// When this value is supplied, remote control planes dispatch through the stable parent
        /// Runtime Pool router instead of addressing the child process directly.
        /// </remarks>
        public string? PublishedTransportEndpoint { get; set; }

        /// <summary>
        /// Gets or sets the logical control-plane identifier discovered by every child runtime.
        /// </summary>
        public string ControlPlaneId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether child runtimes use Redis control-plane
        /// discovery.
        /// </summary>
        public bool EnableControlPlaneDiscovery { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether child startup must fail when control-plane
        /// discovery cannot be resolved.
        /// </summary>
        /// <remarks>
        /// A process pool hosted inside the logical control plane may disable this requirement when
        /// every child already receives the authoritative <see cref="ControlPlaneId"/> and shared
        /// store configuration directly.
        /// </remarks>
        public bool RequireControlPlaneDiscovery { get; set; } = true;

        /// <summary>
        /// Gets or sets the control-plane discovery resolution timeout.
        /// </summary>
        public TimeSpan DiscoveryResolutionTimeout { get; set; } =
            TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets or sets the delay between control-plane discovery resolution attempts.
        /// </summary>
        public TimeSpan DiscoveryResolutionPollInterval { get; set; } =
            TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Gets or sets the execution context carried through readiness validation.
        /// </summary>
        /// <remarks>
        /// This snapshot remains the durable authority for tenant and runtime isolation. Readiness
        /// must not derive tenant ownership from metadata when this snapshot is available.
        /// </remarks>
        public required ExecutionContextSnapshot ExecutionContextSnapshot { get; set; }

        /// <summary>
        /// Gets or sets the provider name published by child runtime registrations.
        /// </summary>
        public string ProviderName { get; set; } = "http";

        /// <summary>
        /// Gets or sets the transport name published by child runtime registrations.
        /// </summary>
        public string TransportName { get; set; } = "http";

        /// <summary>
        /// Gets or sets the child runtime version.
        /// </summary>
        public string RuntimeVersion { get; set; } = "process-pool-host";

        /// <summary>
        /// Gets or sets the number of local workers owned by each child runtime.
        /// </summary>
        public int WorkerCountPerInstance { get; set; } = 1;

        /// <summary>
        /// Gets or sets the maximum concurrent run count of each child runtime.
        /// </summary>
        public int MaxConcurrentRunsPerInstance { get; set; } = 1;

        /// <summary>
        /// Gets or sets the local queue capacity of each child runtime.
        /// </summary>
        public int LocalQueueCapacity { get; set; } = 16;

        /// <summary>
        /// Gets or sets the optional runtime isolation mode published for the pool.
        /// </summary>
        public string? IsolationMode { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether dedicated capacity is preferred.
        /// </summary>
        public bool PreferDedicatedCapacity { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether shared-capacity fallback is allowed.
        /// </summary>
        public bool AllowSharedFallback { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum duration allowed for registry, capacity, and transport readiness.
        /// </summary>
        public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the delay between runtime readiness checks.
        /// </summary>
        public TimeSpan ReadinessPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Gets or sets the child runtime heartbeat interval.
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Gets or sets a value indicating whether child output is redirected and drained.
        /// </summary>
        public bool RedirectOutput { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether child processes are created without a window.
        /// </summary>
        public bool CreateNoWindow { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether child shutdown terminates the full process tree.
        /// </summary>
        public bool KillEntireProcessTreeOnStop { get; set; } = true;

        /// <summary>
        /// Gets or sets the bounded child-process stop timeout in seconds.
        /// </summary>
        public int StopTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// Gets or sets optional non-authoritative child-process environment configuration.
        /// </summary>
        /// <remarks>
        /// Authoritative identity, runtime mode, registration, transport, and readiness settings are
        /// applied after these values and cannot be overridden by this dictionary.
        /// </remarks>
        public Dictionary<string, string> EnvironmentVariables { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
