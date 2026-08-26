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

        internal static string[] CreatePrearmedRuntimeProcessKillArguments(
            string podName,
            string @namespace,
            int processId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(podName);
            ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

            var processIdText =
                processId.ToString(CultureInfo.InvariantCulture);

            var command =
                string.Concat(
                    "pid=", processIdText, "; ",
                    "read_identity() { ",
                    "[ -r \"/proc/$pid/stat\" ] || return 1; ",
                    "stat_line=$(cat \"/proc/$pid/stat\" 2>/dev/null) || return 1; ",
                    "rest=${stat_line##*) }; set -- $rest; state=$1; shift 19; starttime=$1; ",
                    "printf '%s|%s\n' \"$state\" \"$starttime\"; ",
                    "}; ",
                    "armed_identity=$(read_identity) || { printf 'ARM_FAIL=TARGET_MISSING PID=%s\n' \"$pid\"; exit 66; }; ",
                    "armed_state=${armed_identity%%|*}; armed_starttime=${armed_identity#*|}; ",
                    "printf 'READY PID=%s STARTTIME=%s STATE=%s\n' \"$pid\" \"$armed_starttime\" \"$armed_state\"; ",
                    "IFS= read -r command || exit 65; ",
                    "if [ \"$command\" = \"KILL\" ]; then ",
                    "current_identity=$(read_identity) || { printf 'KILL_FAIL=TARGET_MISSING_BEFORE_TRIGGER PID=%s STARTTIME=%s\n' \"$pid\" \"$armed_starttime\"; exit 67; }; ",
                    "current_starttime=${current_identity#*|}; ",
                    "if [ \"$current_starttime\" != \"$armed_starttime\" ]; then ",
                    "printf 'KILL_FAIL=PID_REUSED_BEFORE_TRIGGER PID=%s ARMED_STARTTIME=%s OBSERVED_STARTTIME=%s\n' \"$pid\" \"$armed_starttime\" \"$current_starttime\"; exit 68; fi; ",
                    "kill -9 \"$pid\"; status=$?; printf 'KILL_EXIT=%s\n' \"$status\"; [ \"$status\" -eq 0 ] || exit \"$status\"; ",
                    "i=0; while [ \"$i\" -lt 100 ]; do ",
                    "post_identity=$(read_identity) || { printf 'DEAD PID=%s STARTTIME=%s PROOF=PROC_ABSENT\n' \"$pid\" \"$armed_starttime\"; exit 0; }; ",
                    "post_state=${post_identity%%|*}; post_starttime=${post_identity#*|}; ",
                    "if [ \"$post_starttime\" != \"$armed_starttime\" ]; then ",
                    "printf 'DEAD PID=%s STARTTIME=%s PROOF=PID_REUSED OBSERVED_STARTTIME=%s STATE=%s\n' \"$pid\" \"$armed_starttime\" \"$post_starttime\" \"$post_state\"; exit 0; fi; ",
                    "if [ \"$post_state\" = \"Z\" ]; then ",
                    "printf 'DEAD PID=%s STARTTIME=%s PROOF=ZOMBIE STATE=Z\n' \"$pid\" \"$armed_starttime\"; exit 0; fi; ",
                    "i=$((i+1)); sleep 0.02; done; ",
                    "printf 'KILL_FAIL=EXACT_PROCESS_STILL_ALIVE PID=%s STARTTIME=%s STATE=%s\n' \"$pid\" \"$armed_starttime\" \"$post_state\"; exit 69; ",
                    "fi; printf 'CANCELLED\n'; exit 0");

            return
            [
                "exec",
                "-i",
                podName,
                "--namespace",
                @namespace,
                "--container",
                "runtime-pool",
                "--",
                "sh",
                "-c",
                command
            ];
        }

        internal static string CreatePrearmedRuntimeProcessControlFrame(
            string command)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);

            if (!StringComparer.Ordinal.Equals(command, "KILL") &&
                !StringComparer.Ordinal.Equals(command, "CANCEL"))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command,
                    "Only KILL and CANCEL are valid pre-armed runtime process control commands.");
            }

            // The test host runs on Windows while kubectl exec feeds a Linux sh.
            // Do not use StreamWriter.WriteLineAsync here: on Windows it emits CRLF,
            // leaving a trailing \r in POSIX `read -r` and turning KILL into KILL\r.
            return string.Concat(command, "\n");
        }

        public static async Task<KubernetesRuntimePoolPrearmedProcessKillSession>
            PrearmRuntimeProcessKillAsync(
                string runtimeInstanceId,
                string podName,
                string @namespace,
                int processId,
                TimeSpan readyTimeout,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(podName);
            ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

            if (readyTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(readyTimeout),
                    readyTimeout,
                    "The pre-armed kubectl readiness timeout must be greater than zero.");
            }

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = "kubectl",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

            foreach (var argument in
                     CreatePrearmedRuntimeProcessKillArguments(
                         podName,
                         @namespace,
                         processId))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process =
                new Process
                {
                    StartInfo = startInfo
                };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException(
                    "The pre-armed kubectl exec process could not be started.");
            }

            var armedAtUtc = DateTimeOffset.UtcNow;
            var standardErrorTask =
                process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                using var readyCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                readyCancellation.CancelAfter(readyTimeout);

                var readyLine =
                    await process.StandardOutput
                        .ReadLineAsync(readyCancellation.Token)
                        .ConfigureAwait(false);

                var armedProcessIdentity =
                    ParsePrearmedRuntimeProcessReadyMarker(
                        readyLine,
                        processId);

                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The pre-armed kubectl exec session exited immediately after READY. RuntimeInstanceId='",
                            runtimeInstanceId,
                            "', ExitCode='",
                            process.ExitCode.ToString(CultureInfo.InvariantCulture),
                            "'."));
                }

                return new KubernetesRuntimePoolPrearmedProcessKillSession(
                    runtimeInstanceId,
                    podName,
                    @namespace,
                    processId,
                    armedProcessIdentity.StartTimeTicks,
                    armedProcessIdentity.State,
                    armedAtUtc,
                    process,
                    standardErrorTask);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }

                process.Dispose();
                throw;
            }
        }

        internal static KubernetesLinuxProcessIdentity
            ParsePrearmedRuntimeProcessReadyMarker(
                string? marker,
                int expectedProcessId)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedProcessId);

            if (string.IsNullOrWhiteSpace(marker) ||
                !marker.StartsWith("READY ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The pre-armed kubectl exec session did not expose a valid Linux process READY marker. ExpectedProcessId='",
                        expectedProcessId.ToString(CultureInfo.InvariantCulture),
                        "', ObservedMarker='",
                        marker ?? "<null>",
                        "'."));
            }

            var fields = ParseMarkerFields(marker);

            if (!fields.TryGetValue("PID", out var processIdText) ||
                !int.TryParse(
                    processIdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var observedProcessId) ||
                observedProcessId != expectedProcessId ||
                !fields.TryGetValue("STARTTIME", out var startTimeText) ||
                !long.TryParse(
                    startTimeText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var startTimeTicks) ||
                startTimeTicks <= 0 ||
                !fields.TryGetValue("STATE", out var state) ||
                string.IsNullOrWhiteSpace(state))
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The pre-armed kubectl exec Linux process READY marker was incomplete or inconsistent. ExpectedProcessId='",
                        expectedProcessId.ToString(CultureInfo.InvariantCulture),
                        "', ObservedMarker='",
                        marker,
                        "'."));
            }

            return new KubernetesLinuxProcessIdentity(
                observedProcessId,
                startTimeTicks,
                state);
        }

        internal static KubernetesLinuxProcessDeathProof
            ParsePrearmedRuntimeProcessDeathMarker(
                string standardOutput,
                int expectedProcessId,
                long expectedStartTimeTicks)
        {
            ArgumentNullException.ThrowIfNull(standardOutput);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedProcessId);

            if (expectedStartTimeTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(expectedStartTimeTicks));
            }

            var deathMarker =
                standardOutput
                    .Split(
                        ['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
                    .FirstOrDefault(line =>
                        line.StartsWith("DEAD ", StringComparison.Ordinal));

            if (deathMarker is null)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The pre-armed kubectl exec kill completed without an exact Linux process death marker. ExpectedProcessId='",
                        expectedProcessId.ToString(CultureInfo.InvariantCulture),
                        "', ExpectedStartTimeTicks='",
                        expectedStartTimeTicks.ToString(CultureInfo.InvariantCulture),
                        "', StandardOutput='",
                        standardOutput,
                        "'."));
            }

            var fields = ParseMarkerFields(deathMarker);

            if (!fields.TryGetValue("PID", out var processIdText) ||
                !int.TryParse(
                    processIdText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var observedProcessId) ||
                observedProcessId != expectedProcessId ||
                !fields.TryGetValue("STARTTIME", out var startTimeText) ||
                !long.TryParse(
                    startTimeText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var observedStartTimeTicks) ||
                observedStartTimeTicks != expectedStartTimeTicks ||
                !fields.TryGetValue("PROOF", out var proof) ||
                (proof != "PROC_ABSENT" &&
                 proof != "ZOMBIE" &&
                 proof != "PID_REUSED"))
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The exact Linux process death marker was incomplete or inconsistent with the armed process incarnation. ExpectedProcessId='",
                        expectedProcessId.ToString(CultureInfo.InvariantCulture),
                        "', ExpectedStartTimeTicks='",
                        expectedStartTimeTicks.ToString(CultureInfo.InvariantCulture),
                        "', ObservedMarker='",
                        deathMarker,
                        "'."));
            }

            fields.TryGetValue("STATE", out var state);
            fields.TryGetValue("OBSERVED_STARTTIME", out var reusedStartTimeText);

            long? reusedStartTimeTicks = null;
            if (!string.IsNullOrWhiteSpace(reusedStartTimeText) &&
                long.TryParse(
                    reusedStartTimeText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedReusedStartTimeTicks))
            {
                reusedStartTimeTicks = parsedReusedStartTimeTicks;
            }

            return new KubernetesLinuxProcessDeathProof(
                observedProcessId,
                observedStartTimeTicks,
                proof,
                state,
                reusedStartTimeTicks);
        }

        private static IReadOnlyDictionary<string, string>
            ParseMarkerFields(
                string marker)
        {
            var fields =
                new Dictionary<string, string>(
                    StringComparer.Ordinal);

            foreach (var token in marker.Split(
                         ' ',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                var separatorIndex = token.IndexOf('=');
                if (separatorIndex <= 0 ||
                    separatorIndex == token.Length - 1)
                {
                    continue;
                }

                fields[token[..separatorIndex]] =
                    token[(separatorIndex + 1)..];
            }

            return fields;
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

    internal sealed class KubernetesRuntimePoolPrearmedProcessKillSession :
        IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardErrorTask;
        private int terminalActionStarted;

        public KubernetesRuntimePoolPrearmedProcessKillSession(
            string runtimeInstanceId,
            string podName,
            string @namespace,
            int processId,
            long processStartTimeTicks,
            string processStateAtArm,
            DateTimeOffset armedAtUtc,
            Process process,
            Task<string> standardErrorTask)
        {
            RuntimeInstanceId = runtimeInstanceId;
            PodName = podName;
            Namespace = @namespace;
            ProcessId = processId;
            ProcessStartTimeTicks = processStartTimeTicks;
            ProcessStateAtArm = processStateAtArm;
            ArmedAtUtc = armedAtUtc;
            this.process = process;
            this.standardErrorTask = standardErrorTask;
        }

        public string RuntimeInstanceId { get; }

        public string PodName { get; }

        public string Namespace { get; }

        public int ProcessId { get; }

        public long ProcessStartTimeTicks { get; }

        public string ProcessStateAtArm { get; }

        public DateTimeOffset ArmedAtUtc { get; }

        public bool HasExited => this.process.HasExited;

        public void AssertTargets(
            AiRuntimeInstanceSnapshot runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);

            if (!StringComparer.Ordinal.Equals(
                    runtime.RuntimeInstanceId,
                    RuntimeInstanceId) ||
                !StringComparer.Ordinal.Equals(
                    runtime.KubernetesPodName,
                    PodName) ||
                !StringComparer.Ordinal.Equals(
                    runtime.KubernetesNamespace,
                    Namespace) ||
                runtime.ProcessId.GetValueOrDefault() != ProcessId)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The continuation-consume runtime changed after the physical kill session was pre-armed. RuntimeInstanceId='",
                        runtime.RuntimeInstanceId,
                        "', ArmedRuntimeInstanceId='",
                        RuntimeInstanceId,
                        "', CurrentProcessId='",
                        runtime.ProcessId.GetValueOrDefault().ToString(CultureInfo.InvariantCulture),
                        "', ArmedProcessId='",
                        ProcessId.ToString(CultureInfo.InvariantCulture),
                        "'."));
            }

            if (HasExited)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "The continuation-consume pre-armed kubectl exec session exited before the target boundary. RuntimeInstanceId='",
                        RuntimeInstanceId,
                        "'."));
            }
        }

        public async Task<KubernetesRuntimePoolPrearmedProcessKillResult>
            TriggerKillAsync(
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "The pre-armed runtime kill timeout must be greater than zero.");
            }

            if (Interlocked.CompareExchange(
                    ref this.terminalActionStarted,
                    1,
                    0) != 0)
            {
                throw new InvalidOperationException(
                    "The pre-armed runtime kill session was already triggered or cancelled.");
            }

            if (this.process.HasExited)
            {
                var stderr =
                    await this.standardErrorTask.ConfigureAwait(false);

                throw new InvalidOperationException(
                    string.Concat(
                        "The pre-armed kubectl exec session exited before the physical kill trigger. RuntimeInstanceId='",
                        RuntimeInstanceId,
                        "', ExitCode='",
                        this.process.ExitCode.ToString(CultureInfo.InvariantCulture),
                        "', StandardError='",
                        stderr,
                        "'."));
            }

            using var timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeoutCancellation.CancelAfter(timeout);

            try
            {
                var killRequestedAtUtc = DateTimeOffset.UtcNow;

                await this.process.StandardInput
                    .WriteAsync(
                        KubernetesRuntimePoolProductionInfrastructure
                            .CreatePrearmedRuntimeProcessControlFrame("KILL")
                            .AsMemory(),
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
                await this.process.StandardInput
                    .FlushAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);
                this.process.StandardInput.Close();

                var standardOutputTask =
                    this.process.StandardOutput
                        .ReadToEndAsync(timeoutCancellation.Token);

                await this.process
                    .WaitForExitAsync(timeoutCancellation.Token)
                    .ConfigureAwait(false);

                var killCompletedAtUtc = DateTimeOffset.UtcNow;
                var standardOutput =
                    await standardOutputTask.ConfigureAwait(false);
                var standardError =
                    await this.standardErrorTask.ConfigureAwait(false);

                if (this.process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.Concat(
                            "The pre-armed kubectl exec session failed while proving exact Linux process death. RuntimeInstanceId='",
                            RuntimeInstanceId,
                            "', ProcessId='",
                            ProcessId.ToString(CultureInfo.InvariantCulture),
                            "', ProcessStartTimeTicks='",
                            ProcessStartTimeTicks.ToString(CultureInfo.InvariantCulture),
                            "', ExitCode='",
                            this.process.ExitCode.ToString(CultureInfo.InvariantCulture),
                            "', StandardOutput='",
                            standardOutput,
                            "', StandardError='",
                            standardError,
                            "'."));
                }

                var deathProof =
                    KubernetesRuntimePoolProductionInfrastructure
                        .ParsePrearmedRuntimeProcessDeathMarker(
                            standardOutput,
                            ProcessId,
                            ProcessStartTimeTicks);

                return new KubernetesRuntimePoolPrearmedProcessKillResult(
                    killRequestedAtUtc,
                    killCompletedAtUtc,
                    this.process.ExitCode,
                    standardOutput,
                    standardError,
                    deathProof);
            }
            catch
            {
                await KillLocalKubectlProcessAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(
                    ref this.terminalActionStarted,
                    1,
                    0) == 0)
            {
                try
                {
                    if (!this.process.HasExited)
                    {
                        using var cancellation =
                            new CancellationTokenSource(
                                TimeSpan.FromSeconds(5));

                        await this.process.StandardInput
                            .WriteAsync(
                                KubernetesRuntimePoolProductionInfrastructure
                                    .CreatePrearmedRuntimeProcessControlFrame("CANCEL")
                                    .AsMemory(),
                                cancellation.Token)
                            .ConfigureAwait(false);
                        await this.process.StandardInput
                            .FlushAsync(cancellation.Token)
                            .ConfigureAwait(false);
                        this.process.StandardInput.Close();

                        await this.process
                            .WaitForExitAsync(cancellation.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch
                {
                    await KillLocalKubectlProcessAsync().ConfigureAwait(false);
                }
            }
            else if (!this.process.HasExited)
            {
                await KillLocalKubectlProcessAsync().ConfigureAwait(false);
            }

            this.process.Dispose();
        }

        private async Task KillLocalKubectlProcessAsync()
        {
            if (this.process.HasExited)
            {
                return;
            }

            this.process.Kill(entireProcessTree: true);
            await this.process
                .WaitForExitAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    internal sealed record KubernetesRuntimePoolPrearmedProcessKillResult(
        DateTimeOffset KillRequestedAtUtc,
        DateTimeOffset KillCompletedAtUtc,
        int ExitCode,
        string StandardOutput,
        string StandardError,
        KubernetesLinuxProcessDeathProof ExactProcessDeathProof);

    internal sealed record KubernetesLinuxProcessIdentity(
        int ProcessId,
        long StartTimeTicks,
        string State);

    internal sealed record KubernetesLinuxProcessDeathProof(
        int ProcessId,
        long StartTimeTicks,
        string Proof,
        string? State,
        long? ObservedReplacementStartTimeTicks);

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
