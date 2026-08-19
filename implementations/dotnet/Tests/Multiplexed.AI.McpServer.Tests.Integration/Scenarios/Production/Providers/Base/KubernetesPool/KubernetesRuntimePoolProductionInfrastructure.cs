using System.Diagnostics;
using System.Globalization;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager.ProcessControl;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.HostManager.Pool.Kubernetes.Failure;
using System.Text.Json;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Xunit;
using Xunit.Abstractions;
using TrackedPod = Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool.KubernetesRuntimePoolProductionScenarioTestsBase.TrackedPod;
using KubectlResult = Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool.KubernetesRuntimePoolProductionScenarioTestsBase.KubectlResult;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Owns Kubernetes command execution, physical Pod discovery, diagnostics, and deterministic cleanup
    /// for transport-specific Runtime Pool production scenarios.
    /// </summary>
    internal sealed class KubernetesRuntimePoolProductionInfrastructure
    {
        private readonly ITestOutputHelper output;
        private readonly string logPrefix;

        public KubernetesRuntimePoolProductionInfrastructure(
            ITestOutputHelper output,
            string logPrefix)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.logPrefix =
                !string.IsNullOrWhiteSpace(logPrefix)
                    ? logPrefix
                    : throw new ArgumentException(
                        "A Runtime Pool production infrastructure log prefix is required.",
                        nameof(logPrefix));
        }

        /// <summary>
        /// Creates the physical controller that kills one exact RuntimeInstanceOnly process inside a Runtime Pool Pod
        /// without deleting the Pod itself.
        /// </summary>
        /// <param name="registry">The shared runtime instance registry.</param>
        /// <param name="poolId">The expected Runtime Pool identifier.</param>
        /// <param name="childProcessLogPrefix">The log prefix used for the physical child-process kill.</param>
        /// <returns>The in-Pod child runtime process controller.</returns>
        public IAiRuntimeHostProcessControl CreateRuntimePoolChildProcessControl(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            string childProcessLogPrefix)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(childProcessLogPrefix);

            return new KubernetesRuntimePoolChildProcessControl(
                registry,
                poolId,
                this.output,
                childProcessLogPrefix);
        }

        /// <summary>
        /// Creates the physical controller that deletes the complete Kubernetes Pod containing one targeted
        /// RuntimeInstanceOnly process and delegates deterministic recovery to the existing Runtime Pool Pod
        /// failure coordinator.
        /// </summary>
        /// <param name="registry">The shared runtime instance registry.</param>
        /// <param name="recoveryCoordinator">The existing Kubernetes Runtime Pool Pod recovery coordinator.</param>
        /// <param name="poolId">The expected Runtime Pool identifier.</param>
        /// <param name="hostStartTemplateFactory">Creates the replacement host template from the failed runtime snapshot.</param>
        /// <param name="podFailureLogPrefix">The log prefix used for Pod deletion and recovery.</param>
        /// <returns>The Pod-level physical failure controller.</returns>
        public IAiRuntimeHostProcessControl CreateRuntimePoolPodFailureControl(
            IAiRuntimeInstanceRegistry registry,
            IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator recoveryCoordinator,
            string poolId,
            Func<AiRuntimeInstanceSnapshot, AiRuntimeHostStartRequest> hostStartTemplateFactory,
            string podFailureLogPrefix)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentNullException.ThrowIfNull(recoveryCoordinator);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentNullException.ThrowIfNull(hostStartTemplateFactory);
            ArgumentException.ThrowIfNullOrWhiteSpace(podFailureLogPrefix);

            return new KubernetesRuntimePoolPodFailureControl(
                registry,
                recoveryCoordinator,
                poolId,
                hostStartTemplateFactory,
                this.output,
                podFailureLogPrefix);
        }

        /// <summary>
        /// Creates the shared Kubernetes Runtime Pool replacement host template used by Pod failure proofs.
        /// </summary>
        /// <param name="snapshot">The runtime snapshot hosted by the failed Pod.</param>
        /// <param name="tenant">The owning production tenant definition.</param>
        /// <param name="controlPlaneId">The owning control-plane identifier.</param>
        /// <param name="poolId">The logical Runtime Pool identifier.</param>
        /// <param name="providerName">The transport provider name.</param>
        /// <param name="maximumRuntimeCapacity">The maximum logical runtime capacity for the scenario.</param>
        /// <param name="purpose">The short recovery purpose used only for deterministic test identifiers.</param>
        /// <returns>The provider-authoritative host start template.</returns>
        public static AiRuntimeHostStartRequest CreatePodRecoveryHostStartTemplate(
            AiRuntimeInstanceSnapshot snapshot,
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string poolId,
            string providerName,
            int maximumRuntimeCapacity,
            string purpose)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRuntimeCapacity);
            ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

            return new AiRuntimeHostStartRequest
            {
                RequestId =
                    string.Concat(
                        purpose,
                        "-pod-recovery-template-",
                        controlPlaneId),
                ControlPlaneId = controlPlaneId,
                HostCreationMode = AiRuntimeHostCreationMode.KubernetesPool,
                PoolId = poolId,
                RuntimeInstanceId = snapshot.RuntimeInstanceId,
                RuntimeInstanceIdPrefix = tenant.RuntimeInstanceIdPrefix,
                ProviderName = providerName,
                TransportName = providerName,
                TenantId = tenant.TenantId,
                TenantGroupId = tenant.TenantGroupId,
                IsolationMode = "Shared",
                PreferDedicatedCapacity = false,
                AllowSharedFallback = true,
                WorkerCountPerInstance = tenant.WorkerCountPerInstance,
                MaxConcurrentRunsPerInstance = tenant.MaxConcurrentRunsPerInstance,
                LocalQueueCapacity = tenant.LocalQueueCapacity,
                MaxRuntimeInstances = maximumRuntimeCapacity,
                ExecutionContextSnapshot =
                    new ExecutionContextSnapshot
                    {
                        ContextKey =
                            string.Concat(
                                "ctx-",
                                purpose,
                                "-pod-recovery-",
                                controlPlaneId),
                        Project =
                            string.Concat(
                                "mcp-kubernetes-runtime-pool-",
                                purpose,
                                "-pod-recovery"),
                        UserId = "system",
                        TenantId = tenant.TenantId,
                        TenantGroupId = tenant.TenantGroupId,
                        CurrentNamespace = "tests",
                        Namespaces = new List<NamespaceEntry>(),
                        TtlSeconds = 3600
                    },
                Metadata = new Dictionary<string, string>()
            };
        }

        public async Task AssertBoundedPhysicalPodCountAsync(
            IReadOnlyCollection<TrackedPod> trackedPods,
            int maximumPodCount)
        {
            ArgumentNullException.ThrowIfNull(trackedPods);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPodCount);

            var existenceResults =
                await Task.WhenAll(
                        trackedPods.Select(
                            trackedPod =>
                                RunKubectlAsync(
                                    CancellationToken.None,
                                    "get",
                                    "pod",
                                    trackedPod.PodName,
                                    "--namespace",
                                    trackedPod.Namespace,
                                    "--output=name")))
                    .ConfigureAwait(false);

            var existingPodCount =
                existenceResults.Count(result => result.ExitCode == 0);

            Assert.InRange(
                existingPodCount,
                1,
                maximumPodCount);
        }

        public async Task CaptureFailureDiagnosticsAsync(
            string controlPlaneId,
            string poolId,
            Exception exception,
            IReadOnlyCollection<TrackedPod> trackedPods)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(trackedPods);

            output.WriteLine(string.Empty);
            output.WriteLine(
                $"# {logPrefix} BOUNDED CAPACITY FAILURE DIAGNOSTICS");
            output.WriteLine($"ControlPlaneId='{controlPlaneId}'");
            output.WriteLine($"PoolId='{poolId}'");
            output.WriteLine($"ExceptionType='{exception.GetType().FullName}'");
            output.WriteLine($"ExceptionMessage='{exception.Message}'");

            IReadOnlyCollection<TrackedPod> discoveredPods;

            try
            {
                discoveredPods =
                    await DiscoverPoolPodsAsync(poolId)
                        .ConfigureAwait(false);
            }
            catch (Exception discoveryException)
            {
                output.WriteLine(
                    $"[DIAGNOSTIC DISCOVERY WARNING] Message='{discoveryException.Message}'.");

                discoveredPods =
                    Array.Empty<TrackedPod>();
            }

            var diagnosticPods =
                trackedPods
                    .Concat(discoveredPods)
                    .Distinct()
                    .OrderBy(
                        pod => pod.Namespace,
                        StringComparer.Ordinal)
                    .ThenBy(
                        pod => pod.PodName,
                        StringComparer.Ordinal)
                    .ToArray();

            output.WriteLine($"TrackedPodCount='{trackedPods.Count}'");
            output.WriteLine($"DiscoveredPodCount='{discoveredPods.Count}'");
            output.WriteLine($"DiagnosticPodCount='{diagnosticPods.Length}'");

            await WriteKubectlDiagnosticAsync(
                    "NAMESPACE POD INVENTORY",
                    "get",
                    "pods",
                    "--namespace",
                    KubernetesRuntimePoolScenarioConstants.Namespace,
                    "--output=wide")
                .ConfigureAwait(false);

            await WriteKubectlDiagnosticAsync(
                    "NAMESPACE POD EVENTS",
                    "get",
                    "events",
                    "--namespace",
                    KubernetesRuntimePoolScenarioConstants.Namespace,
                    "--field-selector",
                    "involvedObject.kind=Pod",
                    "--sort-by=.metadata.creationTimestamp")
                .ConfigureAwait(false);

            foreach (var pod in diagnosticPods)
            {
                await WriteKubectlDiagnosticAsync(
                        $"POD STATUS {pod.PodName}",
                        "get",
                        "pod",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--output=wide")
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD TERMINATION STATE {pod.PodName}",
                        "get",
                        "pod",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--output=jsonpath={range .status.containerStatuses[*]}container={.name} ready={.ready} restartCount={.restartCount} state={.state} lastState={.lastState}{\"\\n\"}{end}")
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD DESCRIPTION {pod.PodName}",
                        "describe",
                        "pod",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace)
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD CURRENT LOGS {pod.PodName}",
                        "logs",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--all-containers=true",
                        "--tail=250")
                    .ConfigureAwait(false);

                await WriteKubectlDiagnosticAsync(
                        $"POD PREVIOUS LOGS {pod.PodName}",
                        "logs",
                        pod.PodName,
                        "--namespace",
                        pod.Namespace,
                        "--all-containers=true",
                        "--previous",
                        "--tail=250")
                    .ConfigureAwait(false);
            }

            output.WriteLine(
                $"# {logPrefix} BOUNDED CAPACITY FAILURE DIAGNOSTICS END");
            output.WriteLine(string.Empty);
        }

        /// <summary>
        /// Cleans every Runtime Pool Pod discoverable for one control plane and pool.
        /// </summary>
        /// <param name="controlPlaneId">The scenario control-plane identifier.</param>
        /// <param name="poolId">The exact Runtime Pool identifier.</param>
        /// <returns>A task that completes when no matching Pod remains.</returns>
        public Task CleanupControlPlanePodsAsync(
            string controlPlaneId,
            string poolId)
        {
            return CleanupControlPlanePodsAsync(
                controlPlaneId,
                poolId,
                Array.Empty<TrackedPod>());
        }

        public async Task CleanupControlPlanePodsAsync(
            string controlPlaneId,
            string poolId,
            IReadOnlyCollection<TrackedPod> trackedPods)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentNullException.ThrowIfNull(trackedPods);

            IReadOnlyCollection<TrackedPod> discoveredPods;

            try
            {
                discoveredPods =
                    await DiscoverPoolPodsAsync(poolId)
                        .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                output.WriteLine(
                    $"[{logPrefix} CLEANUP DISCOVERY WARNING] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', Message='{exception.Message}'.");

                discoveredPods =
                    Array.Empty<TrackedPod>();
            }

            var podsToDelete =
                trackedPods
                    .Concat(discoveredPods)
                    .Distinct()
                    .ToArray();

            output.WriteLine(
                $"[{logPrefix} SCENARIO CLEANUP START] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', PodCount='{podsToDelete.Length}'.");

            foreach (var trackedPod in podsToDelete)
            {
                try
                {
                    var deleteResult =
                        await RunKubectlAsync(
                                CancellationToken.None,
                                "delete",
                                "pod",
                                trackedPod.PodName,
                                "--namespace",
                                trackedPod.Namespace,
                                "--ignore-not-found=true",
                                "--grace-period=0",
                                "--force",
                                "--wait=true",
                                "--timeout=90s")
                            .ConfigureAwait(false);

                    if (deleteResult.ExitCode != 0)
                    {
                        output.WriteLine(
                            $"[{logPrefix} CLEANUP WARNING] ControlPlaneId='{controlPlaneId}', Namespace='{trackedPod.Namespace}', PodName='{trackedPod.PodName}', StandardError='{deleteResult.StandardError}'.");
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"[{logPrefix} CLEANUP WARNING] ControlPlaneId='{controlPlaneId}', Namespace='{trackedPod.Namespace}', PodName='{trackedPod.PodName}', Message='{exception.Message}'.");
                }
            }

            var cleanupDeadline =
                DateTimeOffset.UtcNow.AddSeconds(90);

            IReadOnlyCollection<TrackedPod> remainingPods =
                Array.Empty<TrackedPod>();

            while (DateTimeOffset.UtcNow < cleanupDeadline)
            {
                try
                {
                    remainingPods =
                        await DiscoverPoolPodsAsync(poolId)
                            .ConfigureAwait(false);

                    if (remainingPods.Count == 0)
                    {
                        output.WriteLine(
                            $"[{logPrefix} SCENARIO CLEANUP COMPLETE] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', RemainingPodCount='0'.");

                        return;
                    }
                }
                catch (Exception exception)
                {
                    output.WriteLine(
                        $"[{logPrefix} CLEANUP VERIFY WARNING] ControlPlaneId='{controlPlaneId}', PoolId='{poolId}', Message='{exception.Message}'.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Concat(
                    "Kubernetes Runtime Pool scenario cleanup left Pods behind. ControlPlaneId='",
                    controlPlaneId,
                    "', PoolId='",
                    poolId,
                    "', RemainingPods='",
                    string.Join(
                        ",",
                        remainingPods.Select(pod => pod.PodName)),
                    "'."));
        }

        public static async Task<IReadOnlyCollection<TrackedPod>>
            DiscoverPoolPodsAsync(
                string poolId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);

            var result =
                await RunKubectlAsync(
                        CancellationToken.None,
                        "get",
                        "pods",
                        "--namespace",
                        KubernetesRuntimePoolScenarioConstants.Namespace,
                        "--output=json")
                    .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "Runtime Pool Pods could not be listed for cleanup. StandardError=",
                        result.StandardError));
            }

            using var document =
                JsonDocument.Parse(result.StandardOutput);

            var pods =
                new List<TrackedPod>();

            if (!document.RootElement.TryGetProperty(
                    "items",
                    out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return pods;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty(
                        "metadata",
                        out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty(
                        "annotations",
                        out var annotations) ||
                    annotations.ValueKind != JsonValueKind.Object ||
                    !annotations.TryGetProperty(
                        "multiplexed.ai/pool-id",
                        out var poolIdAnnotation) ||
                    poolIdAnnotation.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(
                        poolIdAnnotation.GetString(),
                        poolId) ||
                    !metadata.TryGetProperty(
                        "name",
                        out var podNameElement))
                {
                    continue;
                }

                var podName =
                    podNameElement.GetString();

                if (string.IsNullOrWhiteSpace(podName))
                {
                    continue;
                }

                var @namespace =
                    KubernetesRuntimePoolScenarioConstants.Namespace;

                if (metadata.TryGetProperty(
                        "namespace",
                        out var namespaceElement) &&
                    !string.IsNullOrWhiteSpace(namespaceElement.GetString()))
                {
                    @namespace = namespaceElement.GetString()!;
                }

                pods.Add(
                    new TrackedPod(
                        @namespace,
                        podName));
            }

            return pods;
        }

        public static async Task<KubectlResult> RunKubectlAsync(
            CancellationToken cancellationToken,
            params string[] arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "kubectl",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process =
                new System.Diagnostics.Process
                {
                    StartInfo = startInfo
                };

            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "kubectl could not be started.");
            }

            var standardOutputTask =
                process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardErrorTask =
                process.StandardError.ReadToEndAsync(cancellationToken);

            await process
                .WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);

            return new KubectlResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }

        private async Task WriteKubectlDiagnosticAsync(
            string title,
            params string[] arguments)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentNullException.ThrowIfNull(arguments);

            output.WriteLine(string.Empty);
            output.WriteLine($"## {title}");
            output.WriteLine($"Command='kubectl {string.Join(" ", arguments)}'");

            try
            {
                var result =
                    await RunKubectlAsync(
                            CancellationToken.None,
                            arguments)
                        .ConfigureAwait(false);

                output.WriteLine($"ExitCode='{result.ExitCode}'");

                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    output.WriteLine(result.StandardOutput.TrimEnd());
                }

                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    output.WriteLine("[STANDARD ERROR]");
                    output.WriteLine(result.StandardError.TrimEnd());
                }
            }
            catch (Exception diagnosticException)
            {
                output.WriteLine(
                    $"[DIAGNOSTIC COMMAND WARNING] Message='{diagnosticException.Message}'.");
            }
        }
        /// <summary>
        /// Kills one exact RuntimeInstanceOnly process inside a Kubernetes Runtime Pool Pod while preserving the Pod.
        /// </summary>
        private sealed class KubernetesRuntimePoolChildProcessControl :
            IAiRuntimeHostProcessControl
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly string poolId;
            private readonly ITestOutputHelper output;
            private readonly string logPrefix;

            /// <summary>
            /// Initializes the in-Pod child runtime process controller.
            /// </summary>
            /// <param name="registry">The shared runtime instance registry.</param>
            /// <param name="poolId">The expected Runtime Pool identifier.</param>
            /// <param name="output">The test output helper.</param>
            /// <param name="logPrefix">The scenario log prefix.</param>
            public KubernetesRuntimePoolChildProcessControl(
                IAiRuntimeInstanceRegistry registry,
                string poolId,
                ITestOutputHelper output,
                string logPrefix)
            {
                this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
                this.poolId = !string.IsNullOrWhiteSpace(poolId)
                    ? poolId
                    : throw new ArgumentException("A Runtime Pool identifier is required.", nameof(poolId));
                this.output = output ?? throw new ArgumentNullException(nameof(output));
                this.logPrefix = !string.IsNullOrWhiteSpace(logPrefix)
                    ? logPrefix
                    : throw new ArgumentException("A log prefix is required.", nameof(logPrefix));
            }

            /// <inheritdoc />
            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    await KubernetesRuntimePoolProductionTopology
                        .GetRequiredRuntimeSnapshotAsync(
                            this.registry,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                KubernetesRuntimePoolProductionTopology
                    .AssertRuntimePoolIdentity(
                        snapshot,
                        this.poolId);
                Assert.True(snapshot.ProcessId.HasValue);

                this.output.WriteLine(
                    $"[{this.logPrefix} KUBERNETES RUNTIME POOL CHILD PROCESS KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{snapshot.HostId}', PodName='{snapshot.KubernetesPodName}', ProcessId='{snapshot.ProcessId}'.");

                var result =
                    await RunKubectlAsync(
                            cancellationToken,
                            "exec",
                            snapshot.KubernetesPodName!,
                            "--namespace",
                            snapshot.KubernetesNamespace!,
                            "--container",
                            "runtime-pool",
                            "--",
                            "sh",
                            "-c",
                            string.Concat(
                                "kill -9 ",
                                snapshot.ProcessId.Value.ToString(
                                    CultureInfo.InvariantCulture)))
                        .ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The in-Pod runtime process could not be killed. StandardError=",
                            result.StandardError));
                }

                return true;
            }
        }

        /// <summary>
        /// Deletes the complete Pod that owns one targeted RuntimeInstanceOnly process and then invokes the
        /// existing production Pod failure coordinator for deterministic membership suppression and replacement.
        /// </summary>
        private sealed class KubernetesRuntimePoolPodFailureControl :
            IAiRuntimeHostProcessControl
        {
            private readonly IAiRuntimeInstanceRegistry registry;
            private readonly IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator recoveryCoordinator;
            private readonly string poolId;
            private readonly Func<AiRuntimeInstanceSnapshot, AiRuntimeHostStartRequest> hostStartTemplateFactory;
            private readonly ITestOutputHelper output;
            private readonly string logPrefix;

            /// <summary>
            /// Initializes the complete-Pod failure controller.
            /// </summary>
            /// <param name="registry">The shared runtime instance registry.</param>
            /// <param name="recoveryCoordinator">The existing production Pod failure recovery coordinator.</param>
            /// <param name="poolId">The expected Runtime Pool identifier.</param>
            /// <param name="hostStartTemplateFactory">Creates the replacement host template from the failed runtime snapshot.</param>
            /// <param name="output">The test output helper.</param>
            /// <param name="logPrefix">The scenario log prefix.</param>
            public KubernetesRuntimePoolPodFailureControl(
                IAiRuntimeInstanceRegistry registry,
                IAiKubernetesRuntimePoolPodFailureRecoveryCoordinator recoveryCoordinator,
                string poolId,
                Func<AiRuntimeInstanceSnapshot, AiRuntimeHostStartRequest> hostStartTemplateFactory,
                ITestOutputHelper output,
                string logPrefix)
            {
                this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
                this.recoveryCoordinator = recoveryCoordinator ?? throw new ArgumentNullException(nameof(recoveryCoordinator));
                this.poolId = !string.IsNullOrWhiteSpace(poolId)
                    ? poolId
                    : throw new ArgumentException("A Runtime Pool identifier is required.", nameof(poolId));
                this.hostStartTemplateFactory = hostStartTemplateFactory ?? throw new ArgumentNullException(nameof(hostStartTemplateFactory));
                this.output = output ?? throw new ArgumentNullException(nameof(output));
                this.logPrefix = !string.IsNullOrWhiteSpace(logPrefix)
                    ? logPrefix
                    : throw new ArgumentException("A log prefix is required.", nameof(logPrefix));
            }

            /// <inheritdoc />
            public async Task<bool> KillAsync(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
            {
                var snapshot =
                    await KubernetesRuntimePoolProductionTopology
                        .GetRequiredRuntimeSnapshotAsync(
                            this.registry,
                            runtimeInstanceId)
                        .ConfigureAwait(false);

                KubernetesRuntimePoolProductionTopology
                    .AssertRuntimePoolIdentity(
                        snapshot,
                        this.poolId);

                var podUid = snapshot.HostId!;
                var podName = snapshot.KubernetesPodName!;
                var namespaceName = snapshot.KubernetesNamespace!;

                this.output.WriteLine(
                    $"[{this.logPrefix} KUBERNETES RUNTIME POOL POD KILL] RuntimeInstanceId='{runtimeInstanceId}', PodUid='{podUid}', PodName='{podName}', Namespace='{namespaceName}'.");

                var deleteResult =
                    await RunKubectlAsync(
                            cancellationToken,
                            "delete",
                            "pod",
                            podName,
                            "--namespace",
                            namespaceName,
                            "--grace-period=0",
                            "--force",
                            "--wait=true",
                            "--timeout=90s")
                        .ConfigureAwait(false);

                if (deleteResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The Kubernetes Runtime Pool Pod could not be force-deleted. StandardError=",
                            deleteResult.StandardError));
                }

                var failureId =
                    string.Concat(
                        "child-dag-pod-failure-",
                        podUid);

                var recovery =
                    await this.recoveryCoordinator
                        .RecoverAsync(
                            new AiKubernetesRuntimePoolPodFailureRecoveryRequest
                            {
                                FailureId = failureId,
                                PoolId = this.poolId,
                                PodUid = podUid,
                                ClaimedBy = "mcp-child-dag-kubernetes-pod-failure",
                                FailureMessage =
                                    "Forced Kubernetes Runtime Pool Pod deletion during the focused Child DAG recovery proof.",
                                HostStartTemplate =
                                    this.hostStartTemplateFactory(snapshot)
                            },
                            cancellationToken)
                        .ConfigureAwait(false);

                if (recovery.Replacement is null)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The Kubernetes Runtime Pool Pod failure coordinator did not create replacement capacity. FailureId='",
                            failureId,
                            "', PodUid='",
                            podUid,
                            "'."));
                }

                this.output.WriteLine(
                    $"[{this.logPrefix} KUBERNETES RUNTIME POOL POD RECOVERED] FailureId='{failureId}', FailedPodUid='{podUid}', ReplacementPodUid='{recovery.Replacement.ReplacementPodUid}'.");

                return true;
            }
        }
    }

    /// <summary>
    /// Provides reusable Runtime Pool topology and readiness assertions independently of transport.
    /// </summary>
    internal static class KubernetesRuntimePoolProductionTopology
    {
        public static async Task<HashSet<string>> WaitForActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            int expectedHostCount,
            TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentOutOfRangeException.ThrowIfLessThan(expectedHostCount, 1);

            var deadline =
                DateTimeOffset.UtcNow.Add(timeout);

            HashSet<string> activeHostIds =
                new(StringComparer.Ordinal);

            while (DateTimeOffset.UtcNow < deadline)
            {
                activeHostIds =
                    await GetActiveHostIdsAsync(
                            registry,
                            poolId)
                        .ConfigureAwait(false);

                if (activeHostIds.Count == expectedHostCount)
                {
                    return activeHostIds;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                string.Concat(
                    "The bounded Runtime Pool did not expose the expected active Pod count before failure injection. PoolId='",
                    poolId,
                    "', ExpectedHostCount='",
                    expectedHostCount,
                    "', ActualHostCount='",
                    activeHostIds.Count,
                    "'."));
        }

        public static async Task AssertExactSiblingsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string hostId,
            IReadOnlyCollection<string> siblingRuntimeInstanceIds,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshots =
                    await Task.WhenAll(
                            siblingRuntimeInstanceIds.Select(
                                runtimeInstanceId =>
                                    registry.GetAsync(runtimeInstanceId)))
                        .ConfigureAwait(false);

                if (snapshots.All(
                        snapshot =>
                            snapshot is not null &&
                            StringComparer.Ordinal.Equals(
                                snapshot.HostId,
                                hostId) &&
                            snapshot.Status ==
                                AiRuntimeInstanceStatus.Ready))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "The healthy Runtime Pool siblings did not remain ready after the exact child-process kill.");
        }

        public static async Task AssertSurvivingHostsRemainReadyAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId,
            IReadOnlySet<string> survivingHostIds,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);

            while (DateTimeOffset.UtcNow < deadline)
            {
                var activeHostIds =
                    await GetSelectableHostIdsAsync(
                            registry,
                            poolId)
                        .ConfigureAwait(false);

                if (survivingHostIds.All(activeHostIds.Contains))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250))
                    .ConfigureAwait(false);
            }

            throw new TimeoutException(
                "At least one healthy Runtime Pool Pod lost selectable membership during another Pod's recovery.");
        }

        public static async Task<AiRuntimeInstanceSnapshot>
            GetRequiredRuntimeSnapshotAsync(
                IAiRuntimeInstanceRegistry registry,
                string runtimeInstanceId)
        {
            var snapshot =
                await registry
                    .GetAsync(runtimeInstanceId)
                    .ConfigureAwait(false);

            return snapshot
                ?? throw new InvalidOperationException(
                    string.Concat(
                        "Runtime instance '",
                        runtimeInstanceId,
                        "' was not found in the shared registry."));
        }

        public static void AssertRuntimePoolIdentity(
            AiRuntimeInstanceSnapshot snapshot,
            string poolId)
        {
            Assert.Equal(poolId, snapshot.PoolId);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.HostId));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.KubernetesNamespace));
            Assert.False(string.IsNullOrWhiteSpace(snapshot.KubernetesPodName));
        }

        private static async Task<HashSet<string>> GetActiveHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId)
        {
            var snapshots =
                await registry
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            return snapshots
                .Where(
                    snapshot =>
                        StringComparer.Ordinal.Equals(
                            snapshot.PoolId,
                            poolId) &&
                        snapshot.Status ==
                            AiRuntimeInstanceStatus.Ready &&
                        !string.IsNullOrWhiteSpace(snapshot.HostId))
                .Select(snapshot => snapshot.HostId!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static async Task<HashSet<string>> GetSelectableHostIdsAsync(
            IAiRuntimeInstanceRegistry registry,
            string poolId)
        {
            var snapshots =
                await registry
                    .ListAsync(includeStopped: false)
                    .ConfigureAwait(false);

            return snapshots
                .Where(
                    snapshot =>
                        StringComparer.Ordinal.Equals(
                            snapshot.PoolId,
                            poolId) &&
                        snapshot.Status ==
                            AiRuntimeInstanceStatus.Ready &&
                        snapshot.CanAcceptRun &&
                        !string.IsNullOrWhiteSpace(snapshot.HostId))
                .Select(snapshot => snapshot.HostId!)
                .ToHashSet(StringComparer.Ordinal);
        }
    }
}
