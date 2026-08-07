using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit.Abstractions;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.ProcessHostPool
{
    /// <summary>
    /// Owns several independent external parent Process Hosts under one logical ProcessPool.
    /// </summary>
    internal sealed class ProcessHostPoolProductionCluster : IAsyncDisposable
    {
        private readonly ProcessHostPoolProductionScenarioProfile profile;
        private readonly IReadOnlyDictionary<string, string?> controlPlaneSettings;
        private readonly string controlPlaneId;
        private readonly string runtimeHostAssemblyPath;
        private readonly ProductionTenantScenarioDefinition tenant;
        private readonly ITestOutputHelper output;
        private readonly TimeSpan timeoutPerHost;
        private readonly List<ProcessHostPoolProductionHostProcess> hosts;
        private readonly List<ProcessHostPoolProductionHostProcess> retiredHosts = new();
        private readonly HashSet<int> reservedRangeBases = new();

        private ProcessHostPoolProductionCluster(
            ProcessHostPoolProductionScenarioProfile profile,
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            string controlPlaneId,
            string poolId,
            string runtimeHostAssemblyPath,
            int runtimeCountPerHost,
            ProductionTenantScenarioDefinition tenant,
            ITestOutputHelper output,
            TimeSpan timeoutPerHost)
        {
            this.profile = profile;
            this.controlPlaneSettings = controlPlaneSettings;
            this.controlPlaneId = controlPlaneId;
            this.PoolId = poolId;
            this.runtimeHostAssemblyPath = runtimeHostAssemblyPath;
            this.RuntimeCountPerHost = runtimeCountPerHost;
            this.tenant = tenant;
            this.output = output;
            this.timeoutPerHost = timeoutPerHost;
            this.hosts = new List<ProcessHostPoolProductionHostProcess>();
        }

        /// <summary>
        /// Gets the shared logical ProcessPool identifier.
        /// </summary>
        public string PoolId { get; }

        /// <summary>
        /// Gets the exact number of runtime children owned by each parent Process Host.
        /// </summary>
        public int RuntimeCountPerHost { get; }

        /// <summary>
        /// Gets the current external parent Process Hosts.
        /// </summary>
        public IReadOnlyList<ProcessHostPoolProductionHostProcess> Hosts =>
            this.hosts;

        /// <summary>
        /// Gets the exact total runtime capacity.
        /// </summary>
        public int TotalRuntimeCount =>
            checked(this.hosts.Count * this.RuntimeCountPerHost);

        /// <summary>
        /// Starts every parent Process Host sequentially so topology ownership is deterministic.
        /// </summary>
        public static async Task<ProcessHostPoolProductionCluster> StartAsync(
            ProcessHostPoolProductionScenarioProfile profile,
            IReadOnlyDictionary<string, string?> controlPlaneSettings,
            string controlPlaneId,
            string poolId,
            string runtimeHostAssemblyPath,
            int maximumProcessHostCount,
            int runtimeCountPerHost,
            ProductionTenantScenarioDefinition tenant,
            ITestOutputHelper output,
            TimeSpan timeoutPerHost)
        {
            ArgumentNullException.ThrowIfNull(profile);
            ArgumentNullException.ThrowIfNull(controlPlaneSettings);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(poolId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeHostAssemblyPath);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumProcessHostCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runtimeCountPerHost);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentNullException.ThrowIfNull(output);

            var cluster =
                new ProcessHostPoolProductionCluster(
                    profile,
                    controlPlaneSettings,
                    controlPlaneId,
                    poolId,
                    runtimeHostAssemblyPath,
                    runtimeCountPerHost,
                    tenant,
                    output,
                    timeoutPerHost);

            try
            {
                for (var ordinal = 1;
                     ordinal <= maximumProcessHostCount;
                     ordinal++)
                {
                    cluster.hosts.Add(
                        await cluster.StartHostAsync(ordinal)
                            .ConfigureAwait(false));
                }

                cluster.AssertExactCurrentIdentityCardinality(
                    maximumProcessHostCount);

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Force-kills one exact current parent Process Host and its complete child process tree.
        /// </summary>
        public async Task<ProcessHostPoolProductionHostProcess> CrashHostAsync(
            string hostId,
            TimeSpan timeout)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            var host = this.GetCurrentHost(hostId);

            await host.CrashAsync(timeout).ConfigureAwait(false);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} PARENT HOST CRASHED] HostOrdinal='{host.Ordinal}', ParentProcessId='{host.ProcessId}', HostId='{host.HostId}', LostRuntimeCount='{host.RuntimeInstanceIds.Count}'.");

            return host;
        }

        /// <summary>
        /// Replaces one already crashed parent Host with one fresh host incarnation in the same slot.
        /// </summary>
        public async Task<ProcessHostPoolProductionHostReplacement>
            ReplaceCrashedHostAsync(string failedHostId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(failedHostId);

            var index =
                this.hosts.FindIndex(
                    host => StringComparer.Ordinal.Equals(
                        host.HostId,
                        failedHostId));

            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Current ProcessHostPool does not own HostId '{failedHostId}'.");
            }

            var failedHost = this.hosts[index];

            if (failedHost.IsRunning)
            {
                throw new InvalidOperationException(
                    $"HostId '{failedHostId}' is still running and cannot be replaced as a crashed host.");
            }

            var replacement =
                await this.StartHostAsync(failedHost.Ordinal)
                    .ConfigureAwait(false);

            this.retiredHosts.Add(failedHost);
            this.hosts[index] = replacement;

            Assert.NotEqual(failedHost.HostId, replacement.HostId);
            Assert.NotEqual(failedHost.ProcessId, replacement.ProcessId);
            Assert.Empty(
                failedHost.RuntimeInstanceIds.Intersect(
                    replacement.RuntimeInstanceIds,
                    StringComparer.Ordinal));

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} PARENT HOST REPLACED] HostOrdinal='{replacement.Ordinal}', FailedParentProcessId='{failedHost.ProcessId}', ReplacementParentProcessId='{replacement.ProcessId}', FailedHostId='{failedHost.HostId}', ReplacementHostId='{replacement.HostId}', ReplacementRuntimeCount='{replacement.RuntimeInstanceIds.Count}', ReplacementStableTransportEndpoint='{replacement.StableTransportEndpoint}'.");

            return new ProcessHostPoolProductionHostReplacement(
                failedHost,
                replacement);
        }

        /// <summary>
        /// Gets one current parent host by immutable HostId.
        /// </summary>
        public ProcessHostPoolProductionHostProcess GetCurrentHost(
            string hostId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);

            return this.hosts.Single(
                host => StringComparer.Ordinal.Equals(
                    host.HostId,
                    hostId));
        }

        /// <summary>
        /// Proves that every current parent Process Host is still alive.
        /// </summary>
        public void AssertAllHostsRunning()
        {
            Assert.All(
                this.hosts,
                host => host.AssertRunning());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            var allHosts =
                this.hosts
                    .Concat(this.retiredHosts)
                    .Distinct()
                    .Reverse()
                    .ToArray();

            foreach (var host in allHosts)
            {
                try
                {
                    await host.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    "One or more external Process Hosts could not be cleaned up.",
                    failures);
            }
        }

        private async Task<ProcessHostPoolProductionHostProcess>
            StartHostAsync(int ordinal)
        {
            var rangeLength = checked(this.RuntimeCountPerHost + 18);
            var rangeBase =
                FindFreePortRange(
                    rangeLength,
                    this.reservedRangeBases);

            this.reservedRangeBases.Add(rangeBase);

            var stablePort = rangeBase;
            var childBasePort = checked(rangeBase + 1);
            var stableTransportEndpoint =
                string.Concat(
                    "http://127.0.0.1:",
                    stablePort.ToString(CultureInfo.InvariantCulture));

            var settings =
                ProcessHostPoolProductionScenarioSettingsComposer
                    .BuildProcessHostSettings(
                        this.profile,
                        this.controlPlaneSettings,
                        this.controlPlaneId,
                        this.PoolId,
                        this.runtimeHostAssemblyPath,
                        stableTransportEndpoint,
                        childBasePort,
                        ordinal,
                        this.RuntimeCountPerHost,
                        this.tenant);

            var host =
                await ProcessHostPoolProductionHostProcess
                    .StartAsync(
                        this.profile,
                        settings,
                        this.runtimeHostAssemblyPath,
                        this.PoolId,
                        ordinal,
                        stablePort,
                        this.RuntimeCountPerHost,
                        this.timeoutPerHost)
                    .ConfigureAwait(false);

            this.output.WriteLine(
                $"[{this.profile.LogPrefix} HOST READY] HostOrdinal='{ordinal}', ParentProcessId='{host.ProcessId}', HostId='{host.HostId}', StableTransportEndpoint='{host.StableTransportEndpoint}', RuntimeCount='{host.RuntimeInstanceIds.Count}'.");

            return host;
        }

        private void AssertExactCurrentIdentityCardinality(
            int expectedHostCount)
        {
            Assert.Equal(expectedHostCount, this.hosts.Count);
            Assert.Equal(
                this.hosts.Count,
                this.hosts.Select(host => host.HostId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            Assert.Equal(
                this.hosts.Count,
                this.hosts.Select(host => host.ProcessId)
                    .Distinct()
                    .Count());
            Assert.Equal(
                this.hosts.Count,
                this.hosts.Select(host => host.StableTransportEndpoint)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());

            var allRuntimeInstanceIds =
                this.hosts
                    .SelectMany(host => host.RuntimeInstanceIds)
                    .ToArray();

            Assert.Equal(
                checked(expectedHostCount * this.RuntimeCountPerHost),
                allRuntimeInstanceIds.Length);
            Assert.Equal(
                allRuntimeInstanceIds.Length,
                allRuntimeInstanceIds
                    .Distinct(StringComparer.Ordinal)
                    .Count());
        }

        private static int FindFreePortRange(
            int length,
            IReadOnlySet<int> reservedRangeBases)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            ArgumentNullException.ThrowIfNull(reservedRangeBases);

            for (var basePort = 20_000;
                 basePort <= 60_000 - length;
                 basePort += Math.Max(17, length + 1))
            {
                if (reservedRangeBases.Contains(basePort))
                {
                    continue;
                }

                var listeners = new List<TcpListener>(length);

                try
                {
                    for (var offset = 0;
                         offset < length;
                         offset++)
                    {
                        var listener =
                            new TcpListener(
                                IPAddress.Loopback,
                                basePort + offset);

                        listener.Start();
                        listeners.Add(listener);
                    }

                    return basePort;
                }
                catch (SocketException)
                {
                    // Continue until one complete consecutive range is free.
                }
                finally
                {
                    foreach (var listener in listeners)
                    {
                        listener.Stop();
                    }
                }
            }

            throw new InvalidOperationException(
                $"No consecutive loopback port range of length '{length}' was available.");
        }
    }

    /// <summary>
    /// Describes one exact parent-host replacement.
    /// </summary>
    internal sealed record ProcessHostPoolProductionHostReplacement(
        ProcessHostPoolProductionHostProcess FailedHost,
        ProcessHostPoolProductionHostProcess ReplacementHost);
}
