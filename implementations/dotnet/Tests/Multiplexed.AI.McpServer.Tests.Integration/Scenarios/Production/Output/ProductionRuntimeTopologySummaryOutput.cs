using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.HostManager;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Output
{
    /// <summary>
    /// Captures the initial and current runtime placement of one shared run.
    /// </summary>
    public sealed record ProductionRuntimeRunPlacement
    {
        /// <summary>
        /// Gets the tenant identifier that owns the run.
        /// </summary>
        public required string TenantId { get; init; }

        /// <summary>
        /// Gets the scenario role of the tenant, for example Impacted or Safe.
        /// </summary>
        public required string TenantRole { get; init; }

        /// <summary>
        /// Gets the shared run identifier.
        /// </summary>
        public required string SharedRunId { get; init; }

        /// <summary>
        /// Gets the logical work kind, for example InFlightExecution or LocalQueued.
        /// </summary>
        public required string WorkKind { get; init; }

        /// <summary>
        /// Gets the pipeline name.
        /// </summary>
        public string? PipelineName { get; init; }

        /// <summary>
        /// Gets the runtime instance that initially owned the run.
        /// </summary>
        public string? InitialRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the initial local run identifier.
        /// </summary>
        public string? InitialLocalRunId { get; init; }

        /// <summary>
        /// Gets the initial durable execution identifier when it already existed.
        /// </summary>
        public string? InitialExecutionId { get; init; }

        /// <summary>
        /// Gets the runtime instance that owns the current durable binding.
        /// </summary>
        public string? CurrentRuntimeInstanceId { get; init; }

        /// <summary>
        /// Gets the current local run identifier.
        /// </summary>
        public string? CurrentLocalRunId { get; init; }

        /// <summary>
        /// Gets the current durable execution identifier.
        /// </summary>
        public string? CurrentExecutionId { get; init; }
    }

    /// <summary>
    /// Writes a provider-neutral physical-host, runtime, tenant, and run placement summary.
    /// </summary>
    /// <remarks>
    /// The formatter uses first-class runtime registry fields. Kubernetes pods, process-pool hosts,
    /// standalone processes, and local runtimes are projected into one generic physical-host model.
    /// </remarks>
    public static class ProductionRuntimeTopologySummaryOutput
    {
        /// <summary>
        /// Queries the runtime registry and writes one atomic summary block.
        /// </summary>
        /// <param name="output">The test output helper.</param>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="hostCreationMode">The configured host creation mode.</param>
        /// <param name="runPlacements">The initial and current run placements.</param>
        /// <param name="cancellationToken">A token used to cancel the registry query.</param>
        public static async Task WriteAsync(
            ITestOutputHelper output,
            IAiRuntimeInstanceRegistry registry,
            string controlPlaneId,
            AiRuntimeHostCreationMode hostCreationMode,
            IReadOnlyCollection<ProductionRuntimeRunPlacement> runPlacements,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot>? historicalRuntimeSnapshots = null)
        {
            ArgumentNullException.ThrowIfNull(output);

            output.WriteLine(
                await CreateAsync(
                        registry,
                        controlPlaneId,
                        hostCreationMode,
                        runPlacements,
                        cancellationToken,
                        historicalRuntimeSnapshots)
                    .ConfigureAwait(false));
        }

        /// <summary>
        /// Queries the runtime registry and creates one atomic summary block.
        /// </summary>
        /// <param name="registry">The runtime instance registry.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="hostCreationMode">The configured host creation mode.</param>
        /// <param name="runPlacements">The initial and current run placements.</param>
        /// <param name="cancellationToken">A token used to cancel the registry query.</param>
        /// <param name="historicalRuntimeSnapshots">
        /// Runtime snapshots captured before a physical failure removed a process or Pod from the registry.
        /// </param>
        /// <returns>The complete summary block.</returns>
        public static async Task<string> CreateAsync(
            IAiRuntimeInstanceRegistry registry,
            string controlPlaneId,
            AiRuntimeHostCreationMode hostCreationMode,
            IReadOnlyCollection<ProductionRuntimeRunPlacement> runPlacements,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot>? historicalRuntimeSnapshots = null)
        {
            ArgumentNullException.ThrowIfNull(registry);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(runPlacements);

            try
            {
                var runtimeSnapshots =
                    await registry
                        .ListAsync(
                            includeStopped: true,
                            cancellationToken)
                        .ConfigureAwait(false);

                return Build(
                    controlPlaneId,
                    hostCreationMode,
                    runtimeSnapshots,
                    runPlacements,
                    historicalRuntimeSnapshots);
            }
            catch (Exception exception)
            {
                return string.Concat(
                    "[RUNTIME TOPOLOGY SUMMARY UNAVAILABLE] ControlPlaneId='",
                    controlPlaneId,
                    "', HostCreationMode='",
                    hostCreationMode,
                    "', ExceptionType='",
                    exception.GetType().FullName,
                    "', Message='",
                    exception.Message,
                    "'.");
            }
        }

        /// <summary>
        /// Builds a provider-neutral physical-host, runtime, tenant, and run placement summary.
        /// </summary>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="hostCreationMode">The configured host creation mode.</param>
        /// <param name="runtimeSnapshots">The runtime registry snapshots.</param>
        /// <param name="runPlacements">The initial and current run placements.</param>
        /// <param name="historicalRuntimeSnapshots">
        /// Runtime snapshots captured before a physical failure removed a process or Pod from the registry.
        /// </param>
        /// <returns>The complete summary block.</returns>
        public static string Build(
            string controlPlaneId,
            AiRuntimeHostCreationMode hostCreationMode,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> runtimeSnapshots,
            IReadOnlyCollection<ProductionRuntimeRunPlacement> runPlacements,
            IReadOnlyCollection<AiRuntimeInstanceSnapshot>? historicalRuntimeSnapshots = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentNullException.ThrowIfNull(runtimeSnapshots);
            ArgumentNullException.ThrowIfNull(runPlacements);

            var placements =
                runPlacements
                    .OrderBy(placement => placement.TenantId, StringComparer.Ordinal)
                    .ThenBy(placement => placement.SharedRunId, StringComparer.Ordinal)
                    .ToArray();

            var combinedRuntimeSnapshots =
                (historicalRuntimeSnapshots ?? Array.Empty<AiRuntimeInstanceSnapshot>())
                    .Concat(runtimeSnapshots)
                    .ToArray();

            var relevantSnapshots =
                SelectRelevantSnapshots(
                    combinedRuntimeSnapshots,
                    controlPlaneId,
                    placements);

            var currentRuntimeIds =
                runtimeSnapshots
                    .Select(snapshot => snapshot.RuntimeInstanceId)
                    .ToHashSet(StringComparer.Ordinal);

            var runtimeHosts =
                relevantSnapshots
                    .Select(snapshot =>
                        CreateRuntimeHostProjection(
                            snapshot,
                            currentRuntimeIds.Contains(snapshot.RuntimeInstanceId)))
                    .ToArray();

            var runtimeById =
                runtimeHosts
                    .GroupBy(item => item.Snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .OrderByDescending(item => item.Snapshot.SnapshotAtUtc)
                            .First(),
                        StringComparer.Ordinal);

            var hostGroups =
                runtimeHosts
                    .GroupBy(item => item.HostKey, StringComparer.Ordinal)
                    .Select(group => CreateHostGroup(group.Key, group.ToArray()))
                    .OrderBy(group => group.Kind, StringComparer.Ordinal)
                    .ThenBy(group => group.DisplayName, StringComparer.Ordinal)
                    .ToArray();

            var activeHostCount =
                hostGroups.Count(group => group.RuntimeItems.Any(item =>
                    item.IsCurrentRegistrySnapshot &&
                    IsRuntimeActive(item.Snapshot.Status)));

            var kubernetesPodCount =
                hostGroups.Count(group => string.Equals(group.Kind, "KubernetesPod", StringComparison.Ordinal));

            var activeKubernetesPodCount =
                hostGroups.Count(group =>
                    string.Equals(group.Kind, "KubernetesPod", StringComparison.Ordinal) &&
                    group.RuntimeItems.Any(item =>
                        item.IsCurrentRegistrySnapshot &&
                        IsRuntimeActive(item.Snapshot.Status)));

            var historicalOnlyHostCount =
                hostGroups.Count(group =>
                    group.RuntimeItems.All(item => !item.IsCurrentRegistrySnapshot));

            var historicalOnlyKubernetesPodCount =
                hostGroups.Count(group =>
                    string.Equals(group.Kind, "KubernetesPod", StringComparison.Ordinal) &&
                    group.RuntimeItems.All(item => !item.IsCurrentRegistrySnapshot));

            var builder = new StringBuilder();

            builder.AppendLine();
            builder.AppendLine("# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY");
            builder.AppendLine($"ControlPlaneId='{controlPlaneId}'");
            builder.AppendLine($"HostCreationMode='{hostCreationMode}'");
            builder.AppendLine("Scope='All matching runtime registry snapshots, including stopped and unhealthy runtimes, captured before scenario cleanup.'");
            builder.AppendLine($"ObservedPhysicalHostCount='{hostGroups.Length.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"ActivePhysicalHostCount='{activeHostCount.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"ObservedKubernetesPodCount='{kubernetesPodCount.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"ActiveKubernetesPodCount='{activeKubernetesPodCount.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"HistoricalOnlyPhysicalHostCount='{historicalOnlyHostCount.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"HistoricalOnlyKubernetesPodCount='{historicalOnlyKubernetesPodCount.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"ObservedRuntimeInstanceCount='{relevantSnapshots.Length.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"RunPlacementCount='{placements.Length.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine();
            builder.AppendLine("Physical hosts and runtime membership:");

            if (hostGroups.Length == 0)
            {
                builder.AppendLine("  (no matching runtime registry snapshots)");
            }

            for (var hostIndex = 0; hostIndex < hostGroups.Length; hostIndex++)
            {
                var host = hostGroups[hostIndex];
                var runtimeIds =
                    host.RuntimeItems
                        .Select(item => item.Snapshot.RuntimeInstanceId)
                        .ToHashSet(StringComparer.Ordinal);

                var initialRunCount =
                    placements.Count(placement =>
                        !string.IsNullOrWhiteSpace(placement.InitialRuntimeInstanceId) &&
                        runtimeIds.Contains(placement.InitialRuntimeInstanceId));

                var currentRunCount =
                    placements.Count(placement =>
                        !string.IsNullOrWhiteSpace(placement.CurrentRuntimeInstanceId) &&
                        runtimeIds.Contains(placement.CurrentRuntimeInstanceId));

                var tenantIds =
                    host.RuntimeItems
                        .Select(item => item.Snapshot.TenantId)
                        .Concat(
                            placements
                                .Where(placement =>
                                    (!string.IsNullOrWhiteSpace(placement.InitialRuntimeInstanceId) && runtimeIds.Contains(placement.InitialRuntimeInstanceId)) ||
                                    (!string.IsNullOrWhiteSpace(placement.CurrentRuntimeInstanceId) && runtimeIds.Contains(placement.CurrentRuntimeInstanceId)))
                                .Select(placement => placement.TenantId))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                var runtimeStatuses =
                    host.RuntimeItems
                        .Select(item => item.Snapshot.Status.ToString())
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray();

                var hostLifecycle =
                    host.RuntimeItems.All(item => !item.IsCurrentRegistrySnapshot)
                        ? "HistoricalOnly"
                        : host.RuntimeItems.All(item => item.IsCurrentRegistrySnapshot)
                            ? "CurrentRegistry"
                            : "Mixed";

                var firstRegisteredAtUtc =
                    host.RuntimeItems.Min(item => item.Snapshot.RegisteredAtUtc);

                var lastRegisteredAtUtc =
                    host.RuntimeItems.Max(item => item.Snapshot.RegisteredAtUtc);

                builder.AppendLine(
                    string.Concat(
                        "  Host[",
                        (hostIndex + 1).ToString("D2", CultureInfo.InvariantCulture),
                        "] Kind='",
                        host.Kind,
                        "', DisplayName='",
                        host.DisplayName,
                        "', Lifecycle='",
                        hostLifecycle,
                        "', HostId='",
                        host.HostId ?? string.Empty,
                        "', PoolId='",
                        host.PoolId ?? string.Empty,
                        "', Namespace='",
                        host.KubernetesNamespace ?? string.Empty,
                        "', PodName='",
                        host.KubernetesPodName ?? string.Empty,
                        "', NodeName='",
                        host.KubernetesNodeName ?? string.Empty,
                        "', HostName='",
                        host.HostName ?? string.Empty,
                        "', RuntimeCount='",
                        host.RuntimeItems.Length.ToString(CultureInfo.InvariantCulture),
                        "', ActiveRuntimeCount='",
                        host.RuntimeItems.Count(item =>
                            item.IsCurrentRegistrySnapshot &&
                            IsRuntimeActive(item.Snapshot.Status)).ToString(CultureInfo.InvariantCulture),
                        "', CurrentRegistryRuntimeCount='",
                        host.RuntimeItems.Count(item => item.IsCurrentRegistrySnapshot).ToString(CultureInfo.InvariantCulture),
                        "', HistoricalOnlyRuntimeCount='",
                        host.RuntimeItems.Count(item => !item.IsCurrentRegistrySnapshot).ToString(CultureInfo.InvariantCulture),
                        "', InitialRunCount='",
                        initialRunCount.ToString(CultureInfo.InvariantCulture),
                        "', CurrentRunCount='",
                        currentRunCount.ToString(CultureInfo.InvariantCulture),
                        "', TenantIds='",
                        string.Join(",", tenantIds),
                        "', RuntimeStatuses='",
                        string.Join(",", runtimeStatuses),
                        "', FirstRegisteredAtUtc='",
                        firstRegisteredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        "', LastRegisteredAtUtc='",
                        lastRegisteredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        "'."));

                var orderedRuntimes =
                    host.RuntimeItems
                        .OrderBy(item => item.Snapshot.RuntimeId, StringComparer.Ordinal)
                        .ThenBy(item => item.Snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                        .ToArray();

                for (var runtimeIndex = 0; runtimeIndex < orderedRuntimes.Length; runtimeIndex++)
                {
                    var runtimeProjection = orderedRuntimes[runtimeIndex];
                    var runtime = runtimeProjection.Snapshot;
                    var runtimeInitialRunCount =
                        placements.Count(placement => string.Equals(
                            placement.InitialRuntimeInstanceId,
                            runtime.RuntimeInstanceId,
                            StringComparison.Ordinal));
                    var runtimeCurrentRunCount =
                        placements.Count(placement => string.Equals(
                            placement.CurrentRuntimeInstanceId,
                            runtime.RuntimeInstanceId,
                            StringComparison.Ordinal));

                    builder.AppendLine(
                        string.Concat(
                            "    Runtime[",
                            (runtimeIndex + 1).ToString("D2", CultureInfo.InvariantCulture),
                            "] RuntimeInstanceId='",
                            runtime.RuntimeInstanceId,
                            "', RuntimeId='",
                            runtime.RuntimeId ?? string.Empty,
                            "', Status='",
                            runtime.Status,
                            "', SnapshotSource='",
                            runtimeProjection.IsCurrentRegistrySnapshot
                                ? "CurrentRegistry"
                                : "HistoricalBeforeFailure",
                            "', TenantId='",
                            runtime.TenantId ?? string.Empty,
                            "', TenantGroupId='",
                            runtime.TenantGroupId ?? string.Empty,
                            "', ProcessId='",
                            runtime.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                            "', QueuedRuns='",
                            runtime.QueuedRunCount.ToString(CultureInfo.InvariantCulture),
                            "', RunningRuns='",
                            runtime.RunningRunCount.ToString(CultureInfo.InvariantCulture),
                            "', InitialRunCount='",
                            runtimeInitialRunCount.ToString(CultureInfo.InvariantCulture),
                            "', CurrentRunCount='",
                            runtimeCurrentRunCount.ToString(CultureInfo.InvariantCulture),
                            "', RegisteredAtUtc='",
                            runtime.RegisteredAtUtc.ToString("O", CultureInfo.InvariantCulture),
                            "'."));
                }
            }

            builder.AppendLine();
            builder.AppendLine("Tenant run placement:");

            if (placements.Length == 0)
            {
                builder.AppendLine("  (no run placements supplied)");
            }

            for (var placementIndex = 0; placementIndex < placements.Length; placementIndex++)
            {
                var placement = placements[placementIndex];
                var initialHost = ResolveHost(runtimeById, placement.InitialRuntimeInstanceId);
                var currentHost = ResolveHost(runtimeById, placement.CurrentRuntimeInstanceId);
                var moved =
                    !string.Equals(
                        placement.InitialRuntimeInstanceId,
                        placement.CurrentRuntimeInstanceId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        initialHost.HostKey,
                        currentHost.HostKey,
                        StringComparison.Ordinal);

                builder.AppendLine(
                    string.Concat(
                        "  Run[",
                        (placementIndex + 1).ToString("D2", CultureInfo.InvariantCulture),
                        "] TenantId='",
                        placement.TenantId,
                        "', TenantRole='",
                        placement.TenantRole,
                        "', WorkKind='",
                        placement.WorkKind,
                        "', SharedRunId='",
                        placement.SharedRunId,
                        "', Pipeline='",
                        placement.PipelineName ?? string.Empty,
                        "', InitialHostKind='",
                        initialHost.Kind,
                        "', InitialHost='",
                        initialHost.DisplayName,
                        "', InitialRuntimeInstanceId='",
                        placement.InitialRuntimeInstanceId ?? string.Empty,
                        "', InitialLocalRunId='",
                        placement.InitialLocalRunId ?? string.Empty,
                        "', InitialExecutionId='",
                        placement.InitialExecutionId ?? string.Empty,
                        "', CurrentHostKind='",
                        currentHost.Kind,
                        "', CurrentHost='",
                        currentHost.DisplayName,
                        "', CurrentRuntimeInstanceId='",
                        placement.CurrentRuntimeInstanceId ?? string.Empty,
                        "', CurrentLocalRunId='",
                        placement.CurrentLocalRunId ?? string.Empty,
                        "', CurrentExecutionId='",
                        placement.CurrentExecutionId ?? string.Empty,
                        "', Moved='",
                        moved.ToString().ToLowerInvariant(),
                        "'."));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Builds one final parallel summary containing every completed scenario summary.
        /// </summary>
        /// <param name="scenarioSummaries">The already atomic per-scenario summary blocks.</param>
        /// <param name="expectedScenarioCount">The number of scenarios expected by the parallel harness.</param>
        /// <returns>The complete grouped parallel summary.</returns>
        public static string BuildParallel(
            IReadOnlyCollection<string> scenarioSummaries,
            int expectedScenarioCount)
        {
            ArgumentNullException.ThrowIfNull(scenarioSummaries);
            ArgumentOutOfRangeException.ThrowIfLessThan(expectedScenarioCount, 1);

            var orderedSummaries =
                scenarioSummaries
                    .Where(summary => !string.IsNullOrWhiteSpace(summary))
                    .OrderBy(ExtractControlPlaneId, StringComparer.Ordinal)
                    .ToArray();

            var builder = new StringBuilder();

            builder.AppendLine();
            builder.AppendLine("# PARALLEL RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY");
            builder.AppendLine($"ExpectedScenarioCount='{expectedScenarioCount.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"CapturedScenarioCount='{orderedSummaries.Length.ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine($"MissingScenarioCount='{(expectedScenarioCount - orderedSummaries.Length).ToString(CultureInfo.InvariantCulture)}'");
            builder.AppendLine("Scope='One grouped final section written after all parallel scenarios have completed and before the final test result is returned.'");

            foreach (var summary in orderedSummaries)
            {
                builder.AppendLine();
                builder.AppendLine(
                    summary
                        .Trim()
                        .Replace(
                            "# RUNTIME TOPOLOGY AND RUN PLACEMENT SUMMARY",
                            "## SCENARIO RUNTIME TOPOLOGY AND RUN PLACEMENT",
                            StringComparison.Ordinal));
            }

            return builder.ToString();
        }

        private static string ExtractControlPlaneId(
            string summary)
        {
            const string prefix = "ControlPlaneId='";
            var start = summary.IndexOf(prefix, StringComparison.Ordinal);

            if (start < 0)
            {
                return summary;
            }

            start += prefix.Length;
            var end = summary.IndexOf('\'', start);

            return end < 0
                ? summary[start..]
                : summary[start..end];
        }

        private static AiRuntimeInstanceSnapshot[] SelectRelevantSnapshots(
            IReadOnlyCollection<AiRuntimeInstanceSnapshot> runtimeSnapshots,
            string controlPlaneId,
            IReadOnlyCollection<ProductionRuntimeRunPlacement> runPlacements)
        {
            var knownRuntimeIds =
                runPlacements
                    .SelectMany(placement => new[]
                    {
                        placement.InitialRuntimeInstanceId,
                        placement.CurrentRuntimeInstanceId
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToHashSet(StringComparer.Ordinal);

            var exactControlPlaneSnapshots =
                runtimeSnapshots
                    .Where(snapshot => string.Equals(
                        snapshot.ControlPlaneId,
                        controlPlaneId,
                        StringComparison.Ordinal))
                    .ToArray();

            IEnumerable<AiRuntimeInstanceSnapshot> selected =
                exactControlPlaneSnapshots
                    .Concat(runtimeSnapshots.Where(snapshot => knownRuntimeIds.Contains(snapshot.RuntimeInstanceId)));

            if (exactControlPlaneSnapshots.Length == 0)
            {
                var knownSnapshots =
                    selected
                        .GroupBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                        .Select(group => group.OrderByDescending(snapshot => snapshot.SnapshotAtUtc).First())
                        .ToArray();

                var knownPoolIds =
                    knownSnapshots
                        .Select(snapshot => snapshot.PoolId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .ToHashSet(StringComparer.Ordinal);

                var knownHostIds =
                    knownSnapshots
                        .Select(snapshot => snapshot.HostId)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .ToHashSet(StringComparer.Ordinal);

                selected =
                    selected.Concat(
                        runtimeSnapshots.Where(snapshot =>
                            (!string.IsNullOrWhiteSpace(snapshot.PoolId) && knownPoolIds.Contains(snapshot.PoolId)) ||
                            (!string.IsNullOrWhiteSpace(snapshot.HostId) && knownHostIds.Contains(snapshot.HostId))));
            }

            return selected
                .GroupBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(snapshot => snapshot.SnapshotAtUtc).First())
                .OrderBy(snapshot => snapshot.RegisteredAtUtc)
                .ThenBy(snapshot => snapshot.RuntimeInstanceId, StringComparer.Ordinal)
                .ToArray();
        }

        private static RuntimeHostProjection CreateRuntimeHostProjection(
            AiRuntimeInstanceSnapshot snapshot,
            bool isCurrentRegistrySnapshot)
        {
            var isKubernetes =
                !string.IsNullOrWhiteSpace(snapshot.KubernetesPodName) ||
                !string.IsNullOrWhiteSpace(snapshot.KubernetesNamespace) ||
                !string.IsNullOrWhiteSpace(snapshot.KubernetesNodeName);

            if (isKubernetes)
            {
                var podIdentity =
                    FirstNonEmpty(
                        snapshot.HostId,
                        JoinNonEmpty("/", snapshot.KubernetesNamespace, snapshot.KubernetesPodName),
                        snapshot.RuntimeInstanceId);

                return new RuntimeHostProjection(
                    snapshot,
                    string.Concat("kubernetes:", podIdentity),
                    "KubernetesPod",
                    JoinNonEmpty("/", snapshot.KubernetesNamespace, snapshot.KubernetesPodName) ?? podIdentity,
                    isCurrentRegistrySnapshot);
            }

            if (!string.IsNullOrWhiteSpace(snapshot.HostId))
            {
                var hostKind =
                    !string.IsNullOrWhiteSpace(snapshot.PoolId)
                        ? "RuntimePoolHost"
                        : snapshot.ProcessId.HasValue
                            ? "ProcessHost"
                            : "RuntimeHost";

                return new RuntimeHostProjection(
                    snapshot,
                    string.Concat("host:", snapshot.HostId),
                    hostKind,
                    snapshot.HostId!,
                    isCurrentRegistrySnapshot);
            }

            if (snapshot.ProcessId.HasValue)
            {
                var processDisplayName =
                    string.Concat(
                        string.IsNullOrWhiteSpace(snapshot.HostName) ? "process" : snapshot.HostName,
                        ":",
                        snapshot.ProcessId.Value.ToString(CultureInfo.InvariantCulture));

                return new RuntimeHostProjection(
                    snapshot,
                    string.Concat("process:", processDisplayName),
                    "Process",
                    processDisplayName,
                    isCurrentRegistrySnapshot);
            }

            return new RuntimeHostProjection(
                snapshot,
                string.Concat("runtime:", snapshot.RuntimeInstanceId),
                "RuntimeHost",
                snapshot.RuntimeInstanceId,
                isCurrentRegistrySnapshot);
        }

        private static RuntimeHostGroup CreateHostGroup(
            string hostKey,
            RuntimeHostProjection[] runtimeItems)
        {
            var representative =
                runtimeItems
                    .OrderByDescending(item => item.Snapshot.SnapshotAtUtc)
                    .First();

            return new RuntimeHostGroup(
                hostKey,
                representative.Kind,
                representative.DisplayName,
                FirstNonEmpty(runtimeItems.Select(item => item.Snapshot.HostId).ToArray()),
                FirstNonEmpty(runtimeItems.Select(item => item.Snapshot.PoolId).ToArray()),
                FirstNonEmpty(runtimeItems.Select(item => item.Snapshot.KubernetesNamespace).ToArray()),
                FirstNonEmpty(runtimeItems.Select(item => item.Snapshot.KubernetesPodName).ToArray()),
                FirstNonEmpty(runtimeItems.Select(item => item.Snapshot.KubernetesNodeName).ToArray()),
                FirstNonEmpty(runtimeItems.Select(item => item.Snapshot.HostName).ToArray()),
                runtimeItems);
        }

        private static RuntimeHostReference ResolveHost(
            IReadOnlyDictionary<string, RuntimeHostProjection> runtimeById,
            string? runtimeInstanceId)
        {
            if (!string.IsNullOrWhiteSpace(runtimeInstanceId) &&
                runtimeById.TryGetValue(runtimeInstanceId, out var projection))
            {
                return new RuntimeHostReference(
                    projection.HostKey,
                    projection.Kind,
                    projection.DisplayName);
            }

            return new RuntimeHostReference(
                string.Empty,
                "Unknown",
                string.Empty);
        }

        private static bool IsRuntimeActive(
            AiRuntimeInstanceStatus status)
        {
            return status != AiRuntimeInstanceStatus.Unhealthy &&
                   status != AiRuntimeInstanceStatus.Stopped;
        }

        private static string? JoinNonEmpty(
            string separator,
            params string?[] values)
        {
            var selected =
                values
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();

            return selected.Length == 0
                ? null
                : string.Join(separator, selected);
        }

        private static string FirstNonEmpty(
            params string?[] values)
        {
            return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        }

        private sealed record RuntimeHostProjection(
            AiRuntimeInstanceSnapshot Snapshot,
            string HostKey,
            string Kind,
            string DisplayName,
            bool IsCurrentRegistrySnapshot);

        private sealed record RuntimeHostGroup(
            string HostKey,
            string Kind,
            string DisplayName,
            string? HostId,
            string? PoolId,
            string? KubernetesNamespace,
            string? KubernetesPodName,
            string? KubernetesNodeName,
            string? HostName,
            RuntimeHostProjection[] RuntimeItems);

        private sealed record RuntimeHostReference(
            string HostKey,
            string Kind,
            string DisplayName);
    }
}
