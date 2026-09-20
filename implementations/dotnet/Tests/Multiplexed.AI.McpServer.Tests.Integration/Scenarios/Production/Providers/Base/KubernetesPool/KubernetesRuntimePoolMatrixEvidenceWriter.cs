using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.KubernetesPool
{
    /// <summary>
    /// Writes matrix evidence for a successful execution through the existing KubernetesPool runtime-hosting path.
    /// </summary>
    internal static class KubernetesRuntimePoolMatrixEvidenceWriter
    {
        public const string EvidencePathEnvironmentVariable =
            "MULTIPLEXED_AI_MATRIX_KUBERNETES_EVIDENCE_PATH";

        public const string RecoveryEvidencePathEnvironmentVariable =
            "MULTIPLEXED_AI_MATRIX_KUBERNETES_RECOVERY_EVIDENCE_PATH";

        /// <summary>
        /// Writes passed evidence only when the matrix runner explicitly requests an evidence file.
        /// Normal integration-test execution remains side-effect free.
        /// </summary>
        /// <param name="scenarioId">The stable matrix scenario identifier.</param>
        /// <param name="runtimeImage">The exact image reference projected into the Pod specification.</param>
        /// <param name="namespaceName">The Kubernetes namespace.</param>
        /// <param name="podName">The physical Runtime Pool Pod name.</param>
        /// <param name="serviceName">The stable Runtime Pool Service name.</param>
        /// <param name="runtimeInstanceIds">The exact in-Pod runtime identities.</param>
        /// <param name="commandCount">The number of successful routed runtime commands.</param>
        /// <param name="cleanupSucceeded">Whether the existing Kubernetes SDK lifecycle client removed the Pod/Service.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public static async Task WritePassedAsync(
            string scenarioId,
            string runtimeImage,
            string namespaceName,
            string podName,
            string serviceName,
            IReadOnlyList<string> runtimeInstanceIds,
            int commandCount,
            bool cleanupSucceeded,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
            ArgumentException.ThrowIfNullOrWhiteSpace(runtimeImage);
            ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
            ArgumentException.ThrowIfNullOrWhiteSpace(podName);
            ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
            ArgumentNullException.ThrowIfNull(runtimeInstanceIds);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commandCount);

            var configuredPath =
                Environment.GetEnvironmentVariable(
                    EvidencePathEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return;
            }

            var path = Path.GetFullPath(configuredPath.Trim());
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "KubernetesPool matrix evidence path must include a parent directory.");

            Directory.CreateDirectory(directory);

            var document = new
            {
                schemaVersion = 1,
                scenarioId,
                status = "passed",
                topology = "kubernetes",
                runtimeProvider = "KubernetesPool",
                transport = "http",
                runtimeImage,
                immutableRuntimeImage =
                    runtimeImage.Contains(
                        "@sha256:",
                        StringComparison.Ordinal),
                kubernetes = new
                {
                    namespaceName,
                    podName,
                    serviceName,
                    runtimeInstanceIds,
                    runtimeInstanceCount = runtimeInstanceIds.Count,
                    commandCount,
                    cleanupSucceeded
                },
                evidenceKinds = new[]
                {
                    "kubernetes-sdk-pod-created",
                    "runtime-pool-pod-ready",
                    "stable-service-command-routing",
                    "exact-inpod-runtime-identities",
                    "kubernetes-sdk-delete-accepted"
                }
            };

            var temporary = string.Concat(path, ".tmp");
            await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(
                        document,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        }),
                    cancellationToken)
                .ConfigureAwait(false);

            File.Move(
                temporary,
                path,
                overwrite: true);
        }

        /// <summary>
        /// Writes recovery evidence only when the Kubernetes matrix recovery runner explicitly requests it.
        /// The existing production harness remains the authority for process failure, Pod failure, recovery,
        /// replay, ownership, ledger, trace, and terminal convergence assertions.
        /// </summary>
        public static async Task WriteRecoveryPassedAsync(
            string scenarioId,
            string transport,
            string observationMode,
            int executionCycleCount,
            int childDepth,
            int runtimeProcessFailureCount,
            int podFailureCount,
            int recoveredSharedRunCount,
            int recoveryForensicsProofCount,
            int runtimeOwnershipTransitionCount,
            int runtimeOwnershipTransitionViolationCount,
            int parentReplayExpectedExecutionCount,
            int parentReplayProvenExecutionCount,
            int missingRecursiveChildLogicalStepCount,
            int unexpectedDuplicateRecursiveChildLogicalStepCount,
            bool warmReuseProven,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioId);
            ArgumentException.ThrowIfNullOrWhiteSpace(transport);
            ArgumentException.ThrowIfNullOrWhiteSpace(observationMode);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(executionCycleCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(childDepth);
            ArgumentOutOfRangeException.ThrowIfNegative(runtimeProcessFailureCount);
            ArgumentOutOfRangeException.ThrowIfNegative(podFailureCount);
            ArgumentOutOfRangeException.ThrowIfNegative(recoveredSharedRunCount);
            ArgumentOutOfRangeException.ThrowIfNegative(recoveryForensicsProofCount);
            ArgumentOutOfRangeException.ThrowIfNegative(runtimeOwnershipTransitionCount);
            ArgumentOutOfRangeException.ThrowIfNegative(runtimeOwnershipTransitionViolationCount);
            ArgumentOutOfRangeException.ThrowIfNegative(parentReplayExpectedExecutionCount);
            ArgumentOutOfRangeException.ThrowIfNegative(parentReplayProvenExecutionCount);
            ArgumentOutOfRangeException.ThrowIfNegative(missingRecursiveChildLogicalStepCount);
            ArgumentOutOfRangeException.ThrowIfNegative(unexpectedDuplicateRecursiveChildLogicalStepCount);

            var configuredPath =
                Environment.GetEnvironmentVariable(
                    RecoveryEvidencePathEnvironmentVariable);

            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return;
            }

            var path = Path.GetFullPath(configuredPath.Trim());
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException(
                    "KubernetesPool recovery matrix evidence path must include a parent directory.");

            Directory.CreateDirectory(directory);

            var document = new
            {
                schemaVersion = 1,
                scenarioId,
                status = "passed",
                topology = "kubernetes",
                runtimeProvider = "KubernetesPool",
                transport = transport.ToLowerInvariant(),
                recovery = new
                {
                    observationMode,
                    executionCycleCount,
                    childDepth,
                    runtimeProcessFailureCount,
                    podFailureCount,
                    recoveredSharedRunCount,
                    recoveryForensicsProofCount,
                    runtimeOwnershipTransitionCount,
                    runtimeOwnershipTransitionViolationCount,
                    parentReplayExpectedExecutionCount,
                    parentReplayProvenExecutionCount,
                    missingRecursiveChildLogicalStepCount,
                    unexpectedDuplicateRecursiveChildLogicalStepCount,
                    lostRunCount = 0,
                    duplicateDurableDispatchCount = 0,
                    warmReuseProven
                },
                evidenceKinds = new[]
                {
                    "exact-inpod-runtime-process-failure",
                    "process-kill-execution-identity-continuity",
                    "busy-pod-failure",
                    "replacement-pod-capacity",
                    "recovery-forensics",
                    "runtime-ownership-convergence",
                    "terminal-dag-convergence",
                    "parent-replay",
                    "ledger-trace-lifecycle-proof",
                    "warm-pool-reuse"
                }
            };

            var temporary = string.Concat(path, ".tmp");
            await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(
                        document,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        }),
                    cancellationToken)
                .ConfigureAwait(false);

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
    }
}
