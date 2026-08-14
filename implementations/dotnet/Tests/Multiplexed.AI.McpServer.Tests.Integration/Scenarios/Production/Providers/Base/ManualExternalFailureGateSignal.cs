using System.Text;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base
{
    /// <summary>
    /// Publishes the currently armed manual external-failure gate outside of the test runner output.
    /// This avoids relying on buffered xUnit output when a human must kill the selected boundary.
    /// </summary>
    internal static class ManualExternalFailureGateSignal
    {
        private const string KubernetesSignalFileName =
            "multiplexed-ai-manual-kubernetes-kill.txt";
        private const string ProcessHostSignalFileName =
            "multiplexed-ai-manual-processhost-kill.txt";

        internal const string KubernetesPowerShellWatchCommand =
            "Get-Content \"$env:TEMP\\multiplexed-ai-manual-kubernetes-kill.txt\" -Wait";
        internal const string ProcessHostPowerShellWatchCommand =
            "Get-Content \"$env:TEMP\\multiplexed-ai-manual-processhost-kill.txt\" -Wait";

        private static readonly object Sync = new();

        public static string PrepareKubernetesWatch()
        {
            return PrepareWatch(
                KubernetesSignalFileName,
                "KubernetesPod",
                KubernetesPowerShellWatchCommand);
        }

        public static string PrepareProcessHostWatch()
        {
            return PrepareWatch(
                ProcessHostSignalFileName,
                "ProcessHost",
                ProcessHostPowerShellWatchCommand);
        }

        public static string ArmKubernetesPod(
            int cycleNumber,
            string podUid,
            string podName,
            string namespaceName,
            string command)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(podUid);
            ArgumentException.ThrowIfNullOrWhiteSpace(podName);
            ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);

            return Arm(
                KubernetesSignalFileName,
                "KubernetesPod",
                cycleNumber,
                $"PodUid={podUid}{Environment.NewLine}PodName={podName}{Environment.NewLine}Namespace={namespaceName}",
                command);
        }

        public static string ArmProcessHost(
            int cycleNumber,
            string hostId,
            int processId,
            string command)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cycleNumber);
            ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);

            return Arm(
                ProcessHostSignalFileName,
                "ProcessHost",
                cycleNumber,
                $"HostId={hostId}{Environment.NewLine}ProcessId={processId}",
                command);
        }

        public static void MarkObserved(
            string signalPath,
            string observedBoundary)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(observedBoundary);

            var content =
                new StringBuilder()
                    .AppendLine()
                    .AppendLine("Status=OBSERVED")
                    .AppendLine($"ObservedBoundary={observedBoundary}")
                    .AppendLine($"ObservedAtUtc={DateTimeOffset.UtcNow:O}")
                    .ToString();

            lock (Sync)
            {
                File.AppendAllText(signalPath, content, Encoding.UTF8);
            }
        }

        private static string PrepareWatch(
            string fileName,
            string targetKind,
            string watchCommand)
        {
            var signalPath = Path.Combine(Path.GetTempPath(), fileName);
            var content =
                new StringBuilder()
                    .AppendLine("============================================================")
                    .AppendLine("MANUAL EXTERNAL FAILURE WATCH")
                    .AppendLine("============================================================")
                    .AppendLine("Status=READY")
                    .AppendLine($"TargetKind={targetKind}")
                    .AppendLine($"SignalFile={signalPath}")
                    .AppendLine()
                    .AppendLine("KEEP THIS POWERSHELL WATCHER OPEN:")
                    .AppendLine(watchCommand)
                    .AppendLine()
                    .AppendLine("The same watcher remains valid for every execution cycle.")
                    .AppendLine($"PreparedAtUtc={DateTimeOffset.UtcNow:O}")
                    .AppendLine("============================================================")
                    .ToString();

            lock (Sync)
            {
                File.WriteAllText(signalPath, content, Encoding.UTF8);
            }

            return signalPath;
        }

        private static string Arm(
            string fileName,
            string targetKind,
            int cycleNumber,
            string targetDetails,
            string command)
        {
            var signalPath = Path.Combine(Path.GetTempPath(), fileName);
            var content =
                new StringBuilder()
                    .AppendLine("============================================================")
                    .AppendLine("MANUAL EXTERNAL FAILURE GATE")
                    .AppendLine("============================================================")
                    .AppendLine("Status=WAITING")
                    .AppendLine($"TargetKind={targetKind}")
                    .AppendLine($"Cycle={cycleNumber}")
                    .AppendLine(targetDetails)
                    .AppendLine()
                    .AppendLine("KEEP THIS POWERSHELL WATCHER OPEN:")
                    .AppendLine(
                        targetKind == "KubernetesPod"
                            ? KubernetesPowerShellWatchCommand
                            : ProcessHostPowerShellWatchCommand)
                    .AppendLine()
                    .AppendLine("RUN THIS COMMAND NOW:")
                    .AppendLine(command)
                    .AppendLine()
                    .AppendLine($"ArmedAtUtc={DateTimeOffset.UtcNow:O}")
                    .AppendLine("============================================================")
                    .ToString();

            lock (Sync)
            {
                File.AppendAllText(
                    signalPath,
                    Environment.NewLine + content,
                    Encoding.UTF8);
            }

            return signalPath;
        }
    }
}
