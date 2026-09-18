using System.Text.Json;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Sdk.Contracts.Pipelines;
using Multiplexed.AI.Stores;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Recovery;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Queue;
using Multiplexed.AI.McpServer.Host.Configuration;

namespace Multiplexed.AI.McpServer.Host.Bootstrap
{
    /// <summary>
    /// Drives matrix-only recovery evidence through the production recovery reconciler and durable stores.
    /// </summary>
    internal sealed class MatrixRecoveryProbe
    {
        private readonly IAiRuntimeExecutionRecoveryReconciler recoveryReconciler;
        private readonly IAiRuntimeInstanceRegistry runtimeRegistry;
        private readonly IAiRuntimeRunExecutionIndex runtimeRunIndex;
        private readonly IAiSharedRunStore sharedRunStore;
        private readonly IAiSharedQueue sharedQueue;
        private readonly IAiDagExecutionStore dagExecutions;
        private readonly AiMatrixHarnessOptions options;
        private readonly string controlPlaneId;

        public MatrixRecoveryProbe(
            IAiRuntimeExecutionRecoveryReconciler recoveryReconciler,
            IAiRuntimeInstanceRegistry runtimeRegistry,
            IAiRuntimeRunExecutionIndex runtimeRunIndex,
            IAiSharedRunStore sharedRunStore,
            IAiSharedQueue sharedQueue,
            IAiDagExecutionStore dagExecutions,
            IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(recoveryReconciler);
            ArgumentNullException.ThrowIfNull(runtimeRegistry);
            ArgumentNullException.ThrowIfNull(runtimeRunIndex);
            ArgumentNullException.ThrowIfNull(sharedRunStore);
            ArgumentNullException.ThrowIfNull(sharedQueue);
            ArgumentNullException.ThrowIfNull(dagExecutions);
            ArgumentNullException.ThrowIfNull(configuration);

            this.recoveryReconciler = recoveryReconciler;
            this.runtimeRegistry = runtimeRegistry;
            this.runtimeRunIndex = runtimeRunIndex;
            this.sharedRunStore = sharedRunStore;
            this.sharedQueue = sharedQueue;
            this.dagExecutions = dagExecutions;
            this.options = configuration.GetSection("AiMatrixHarness").Get<AiMatrixHarnessOptions>()
                ?? new AiMatrixHarnessOptions();
            this.controlPlaneId =
                configuration["AiMcpHost:ControlPlaneId"]
                ?? configuration["AiEngine:ControlPlane:ControlPlaneId"]
                ?? "matrix-control";
        }

        public Task<object> RunAsync(
            string recoveryCase,
            string executionId,
            MatrixRecoverySeedRequest seed,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(recoveryCase);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

            return recoveryCase switch
            {
                "in-flight-resume" => RunInFlightResumeAsync(executionId, cancellationToken),
                "local-queued-redispatch" => RunLocalQueuedRedispatchAsync(executionId, seed, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(recoveryCase),
                    recoveryCase,
                    "Unsupported matrix recovery case.")
            };
        }

        private async Task<object> RunInFlightResumeAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            var entries = await this.runtimeRunIndex
                .ListRecoverableAsync(cancellationToken)
                .ConfigureAwait(false);

            var original = entries.FirstOrDefault(entry =>
                string.Equals(entry.ExecutionId, executionId, StringComparison.Ordinal));
            if (original is null || string.IsNullOrWhiteSpace(original.RuntimeInstanceId))
            {
                throw new InvalidOperationException(
                    $"No recoverable runtime ownership exists for execution '{executionId}'.");
            }

            var sharedRuns = await this.sharedRunStore
                .ListAsync(
                    includeCancelled: true,
                    includeCompleted: true,
                    includeFailed: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var sharedRun = sharedRuns.FirstOrDefault(run =>
                string.Equals(run.ExecutionId, executionId, StringComparison.Ordinal) ||
                string.Equals(run.LocalRunId, original.RunId, StringComparison.Ordinal));
            if (sharedRun is null)
            {
                throw new InvalidOperationException(
                    $"No shared-run ownership exists for execution '{executionId}'.");
            }

            var runtimeInstances = await this.runtimeRegistry
                .ListAsync(includeStopped: true, cancellationToken)
                .ConfigureAwait(false);
            var healthyReplacement = runtimeInstances.FirstOrDefault(runtime =>
                !string.Equals(runtime.RuntimeInstanceId, original.RuntimeInstanceId, StringComparison.Ordinal) &&
                runtime.Status == AiRuntimeInstanceStatus.Ready &&
                runtime.CanAcceptRun);
            if (healthyReplacement is null)
            {
                throw new InvalidOperationException(
                    $"In-flight recovery requires a healthy replacement runtime distinct from '{original.RuntimeInstanceId}'.");
            }

            var outcome = await RecoverOwnedRunAsync(
                    original,
                    sharedRun.SharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(outcome.IndexStatus, AiRuntimeRunExecutionIndexStatuses.RequeuedForRecovery, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"In-flight execution '{executionId}' was not marked requeued-for-recovery.");
            }

            AiSharedRunRecord? reassigned = null;
            var reassignmentDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < reassignmentDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reassigned = await this.sharedRunStore
                    .GetAsync(sharedRun.SharedRunId, cancellationToken)
                    .ConfigureAwait(false);
                if (reassigned is not null &&
                    string.Equals(reassigned.ExecutionId, executionId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(reassigned.AssignedRuntimeInstanceId) &&
                    !string.Equals(reassigned.AssignedRuntimeInstanceId, original.RuntimeInstanceId, StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            if (reassigned is null ||
                string.IsNullOrWhiteSpace(reassigned.AssignedRuntimeInstanceId) ||
                string.Equals(reassigned.AssignedRuntimeInstanceId, original.RuntimeInstanceId, StringComparison.Ordinal))
            {
                throw new TimeoutException(
                    $"In-flight recovery for execution '{executionId}' was not reassigned to distinct healthy runtime capacity within 45 seconds.");
            }

            return new
            {
                recoveryCase = "in-flight-resume",
                executionId,
                sharedRunId = sharedRun.SharedRunId,
                failedRuntimeInstanceId = original.RuntimeInstanceId,
                failedLocalRunId = original.RunId,
                availableReplacementRuntimeInstanceId = healthyReplacement.RuntimeInstanceId,
                replacementRuntimeInstanceId = reassigned.AssignedRuntimeInstanceId,
                replacementLocalRunId = reassigned.LocalRunId,
                indexStatus = outcome.IndexStatus,
                recoveryAction = outcome.Decision?.Action ?? "requeue-shared-run",
                recoveryReason = outcome.Decision?.Reason ?? outcome.IndexFailureReason,
                recoveryChanged = true,
                observedBy = outcome.Decision is null ? "background-reconciler" : "explicit-reconciler"
            };
        }

        private async Task<object> RunLocalQueuedRedispatchAsync(
            string seedIdentity,
            MatrixRecoverySeedRequest seed,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(seed);
            if (seed.Definition is null)
            {
                throw new InvalidOperationException(
                    "Local-queued recovery requires an explicit public SDK pipeline definition seed.");
            }

            var definition = AiPublicSdkContractMapper.ToInternal(seed.Definition);
            var suffix = Guid.NewGuid().ToString("N");
            var sharedRunId = $"matrix-recovery-shared-{suffix}";
            var failedLocalRunId = $"matrix-recovery-local-{suffix}";
            var failedRuntimeInstanceId = $"matrix-recovery-failed-runtime-{suffix}";
            var now = DateTimeOffset.UtcNow;
            var executionContextSnapshot = CreateMatrixExecutionContextSnapshot(suffix);
            var metadata = new Dictionary<string, string>(seed.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["matrix.recoveryCase"] = "local-queued-redispatch",
                ["matrix.seeded"] = "true",
                [AiPipelineMetadataKeys.CamelCasePipelineName] = definition.Name,
                [AiPipelineMetadataKeys.Name] = definition.Name,
                ["tenantId"] = executionContextSnapshot.TenantId,
                ["tenantGroupId"] = executionContextSnapshot.TenantGroupId
            };

            await this.runtimeRegistry
                .RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = failedRuntimeInstanceId,
                        RuntimeId = failedRuntimeInstanceId,
                        ControlPlaneId = this.controlPlaneId,
                        TenantId = executionContextSnapshot.TenantId,
                        TenantGroupId = executionContextSnapshot.TenantGroupId,
                        WorkerCount = 1,
                        MaxConcurrentRuns = 1,
                        QueueCapacity = 16,
                        RegisteredAtUtc = now,
                        Metadata = new Dictionary<string, string>
                        {
                            ["matrix.recoveryCase"] = "local-queued-redispatch",
                            ["matrix.seeded"] = "true"
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var seededRunRequest = new AiRuntimePipelineRunRequest
            {
                PipelineName = definition.Name,
                RequestedExecutionId = null,
                ExternalWaitContinuation = null,
                PipelineDefinitionSnapshot = null,
                ExecutionContextSnapshot = executionContextSnapshot,
                PipelineDefinition = definition,
                Input = seed.Input.ValueKind == JsonValueKind.Undefined ? "{}" : seed.Input.GetRawText(),
                Metadata = new Dictionary<string, string>(seed.Metadata, StringComparer.OrdinalIgnoreCase)
            };

            var seededRun = await this.sharedRunStore
                .CreateAsync(
                    new AiSharedRunRecord
                    {
                        SharedRunId = sharedRunId,
                        Status = AiSharedRunStatus.QueuedGlobally,
                        RunRequest = seededRunRequest,
                        ExecutionContextSnapshot = executionContextSnapshot,
                        LocalRunId = null,
                        ExecutionId = null,
                        AssignedRuntimeInstanceId = null,
                        AdmissionDecision = null,
                        Placement = null,
                        PipelineKey = definition.Name,
                        CorrelationId = $"matrix-recovery-{suffix}",
                        RequestedBy = "matrix-recovery-probe",
                        Source = "matrix-recovery-probe",
                        Reason = "matrix-local-queued-recovery-seed",
                        FailureReason = null,
                        SubmittedAtUtc = now.AddMinutes(-5),
                        UpdatedAtUtc = now,
                        Metadata = metadata,
                        ControlPlaneId = this.controlPlaneId
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // Seed the crash-surviving queue ownership atomically as already dispatched.
            // A Pending -> Claim sequence would race the production shared-queue pump that is
            // intentionally active in this matrix topology. The shared queue contract supports
            // durable non-pending ownership records and excludes them from pending indexes.
            var seedClaimToken = $"matrix-recovery-claim-{suffix}";
            await this.sharedQueue
                .EnqueueAsync(
                    new AiSharedQueueItem
                    {
                        SharedRunId = seededRun.SharedRunId,
                        ControlPlaneId = seededRun.ControlPlaneId,
                        Status = AiSharedQueueItemStatus.Dispatched,
                        ExecutionContextSnapshot = seededRun.ExecutionContextSnapshot,
                        PipelineKey = seededRun.PipelineKey,
                        Priority = 0,
                        ClaimedByRuntimeInstanceId = failedRuntimeInstanceId,
                        ClaimedByWorkerId = "matrix-recovery-seed-worker",
                        ClaimToken = seedClaimToken,
                        EnqueuedAtUtc = now.AddMinutes(-5),
                        UpdatedAtUtc = now,
                        ClaimedAtUtc = now.AddMinutes(-4),
                        ClaimExpiresAtUtc = now.AddMinutes(5),
                        Reason = "matrix-local-queued-recovery-seed",
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["matrix.recoveryCase"] = "local-queued-redispatch",
                            ["inventory.kind"] = "local-queued",
                            ["matrix.seeded"] = "true",
                            [AiRunMetadataKeys.CamelCaseSharedRunId] = seededRun.SharedRunId,
                            [AiRunMetadataKeys.SharedRunId] = seededRun.SharedRunId,
                            [AiPipelineMetadataKeys.CamelCasePipelineName] = seededRun.RunRequest.PipelineName
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await this.sharedRunStore
                .MarkDispatchedAsync(
                    seededRun.SharedRunId,
                    failedRuntimeInstanceId,
                    failedLocalRunId,
                    executionId: null,
                    reason: "matrix-local-queued-recovery-seed",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await this.runtimeRunIndex
                .RegisterQueuedAsync(
                    new AiRuntimeRunExecutionIndexEntry
                    {
                        RunId = failedLocalRunId,
                        ExecutionId = null,
                        RuntimeInstanceId = failedRuntimeInstanceId,
                        Status = "queued",
                        CreatedAtUtc = now,
                        ExecutionContextSnapshot = seededRun.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["matrix.recoveryCase"] = "local-queued-redispatch",
                            ["inventory.kind"] = "local-queued",
                            ["matrix.seeded"] = "true",
                            [AiRunMetadataKeys.CamelCaseSharedRunId] = seededRun.SharedRunId,
                            [AiRunMetadataKeys.SharedRunId] = seededRun.SharedRunId,
                            [AiPipelineMetadataKeys.CamelCasePipelineName] = seededRun.RunRequest.PipelineName
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // The recovery loop runs every second. Mark the synthetic owner unavailable only after
            // all durable queue/shared-run/index ownership has been seeded, matching a real crash.
            await this.runtimeRegistry
                .MarkUnhealthyAsync(failedRuntimeInstanceId, cancellationToken)
                .ConfigureAwait(false);

            var seededIndex = await this.runtimeRunIndex
                .GetAsync(failedLocalRunId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The matrix local-queued recovery index seed is missing.");

            var recovery = await RecoverOwnedRunAsync(
                    seededIndex,
                    seededRun.SharedRunId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(recovery.IndexStatus, AiRuntimeRunExecutionIndexStatuses.RequeuedForRecovery, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The seeded local-queued run was not marked requeued-for-recovery.");
            }

            AiSharedRunRecord? replacement = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                replacement = await this.sharedRunStore
                    .GetAsync(seededRun.SharedRunId, cancellationToken)
                    .ConfigureAwait(false);
                if (replacement is not null &&
                    !string.IsNullOrWhiteSpace(replacement.LocalRunId) &&
                    !string.IsNullOrWhiteSpace(replacement.AssignedRuntimeInstanceId) &&
                    !string.Equals(replacement.AssignedRuntimeInstanceId, failedRuntimeInstanceId, StringComparison.Ordinal))
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            if (replacement is null ||
                string.IsNullOrWhiteSpace(replacement.LocalRunId) ||
                string.IsNullOrWhiteSpace(replacement.AssignedRuntimeInstanceId))
            {
                throw new TimeoutException(
                    "Recovered local-queued work was not redispatched to a healthy runtime within 45 seconds.");
            }

            // Shared-run dispatch becomes authoritative as soon as replacement runtime ownership and the
            // replacement LocalRunId are durable. A brand-new local-queued execution id is created
            // asynchronously by that runtime worker and is therefore authoritative in the runtime-run
            // execution index, not in the shared-run dispatch result.
            AiRuntimeRunExecutionIndexEntry? replacementIndex = null;
            var executionIdentityDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < executionIdentityDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                replacementIndex = await this.runtimeRunIndex
                    .GetAsync(replacement.LocalRunId, cancellationToken)
                    .ConfigureAwait(false);
                if (replacementIndex is not null &&
                    !string.IsNullOrWhiteSpace(replacementIndex.ExecutionId) &&
                    string.Equals(
                        replacementIndex.RuntimeInstanceId,
                        replacement.AssignedRuntimeInstanceId,
                        StringComparison.Ordinal))
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            if (replacementIndex is null || string.IsNullOrWhiteSpace(replacementIndex.ExecutionId))
            {
                throw new TimeoutException(
                    $"Redispatched local run '{replacement.LocalRunId}' did not acquire a new execution id within 45 seconds.");
            }

            var redispatchedExecutionId = replacementIndex.ExecutionId;
            AiExecutionRecord? terminalRecord = null;
            var terminalDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < terminalDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                terminalRecord = await this.dagExecutions
                    .GetRecordAsync(redispatchedExecutionId, cancellationToken)
                    .ConfigureAwait(false);
                if (terminalRecord?.IsTerminal == true)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            if (terminalRecord?.Status != AiExecutionStatus.Completed)
            {
                throw new TimeoutException(
                    $"Recovered local-queued execution '{redispatchedExecutionId}' did not converge to Completed within 90 seconds.");
            }

            AiRuntimeRunExecutionIndexEntry? completedIndex = null;
            var indexCompletionDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < indexCompletionDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                completedIndex = await this.runtimeRunIndex
                    .GetAsync(replacement.LocalRunId, cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(
                    completedIndex?.Status,
                    AiRuntimeRunExecutionIndexStatuses.Completed,
                    StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.Equals(
                    completedIndex?.Status,
                    AiRuntimeRunExecutionIndexStatuses.Failed,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Redispatched local run '{replacement.LocalRunId}' was marked failed after DAG completion. " +
                        $"FailureReason='{completedIndex?.FailureReason}'.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(
                completedIndex?.Status,
                AiRuntimeRunExecutionIndexStatuses.Completed,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new TimeoutException(
                    $"Redispatched local run '{replacement.LocalRunId}' did not reach completed runtime-index state within 15 seconds.");
            }

            return new
            {
                recoveryCase = "local-queued-redispatch",
                seedIdentity,
                preRecoveryExecutionId = (string?)null,
                sharedRunId = seededRun.SharedRunId,
                failedRuntimeInstanceId,
                failedLocalRunId,
                indexStatus = recovery.IndexStatus,
                recoveryAction = recovery.Decision?.Action ?? "requeue-shared-run",
                recoveryReason = recovery.Decision?.Reason ?? recovery.IndexFailureReason,
                recoveryChanged = true,
                redispatchedExecutionId,
                replacementRuntimeInstanceId = replacement.AssignedRuntimeInstanceId,
                replacementLocalRunId = replacement.LocalRunId,
                replacementRuntimeIndexStatus = completedIndex.Status,
                terminalStatus = terminalRecord.Status.ToString(),
                observedBy = recovery.Decision is null ? "background-reconciler" : "explicit-reconciler"
            };
        }

        private ExecutionContextSnapshot CreateMatrixExecutionContextSnapshot(string suffix)
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"matrix-recovery-context-{suffix}",
                Project = this.options.Project,
                UserId = this.options.UserId,
                TenantId = this.options.TenantId,
                TenantGroupId = this.options.TenantGroupId,
                CurrentNamespace = this.options.Namespace,
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = this.options.Namespace,
                        Trns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 300,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private async Task<RecoveryOutcome> RecoverOwnedRunAsync(
            AiRuntimeRunExecutionIndexEntry original,
            string sharedRunId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(original.RuntimeInstanceId))
            {
                throw new InvalidOperationException("Recovery ownership is missing its runtime instance id.");
            }

            AiRuntimeExecutionRecoveryDecision? matchingDecision = null;
            AiRuntimeRunExecutionIndexEntry? current = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await this.runtimeRegistry
                    .MarkUnhealthyAsync(original.RuntimeInstanceId, cancellationToken)
                    .ConfigureAwait(false);

                var result = await this.recoveryReconciler
                    .ReconcileAsync(cancellationToken)
                    .ConfigureAwait(false);
                matchingDecision = result.Decisions.FirstOrDefault(decision =>
                    decision.Changed &&
                    string.Equals(decision.RuntimeInstanceId, original.RuntimeInstanceId, StringComparison.Ordinal) &&
                    string.Equals(decision.LocalRunId, original.RunId, StringComparison.Ordinal) &&
                    (string.IsNullOrWhiteSpace(decision.SharedRunId) ||
                     string.Equals(decision.SharedRunId, sharedRunId, StringComparison.Ordinal)))
                    ?? matchingDecision;

                current = await this.runtimeRunIndex
                    .GetAsync(original.RunId, cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(current?.Status, AiRuntimeRunExecutionIndexStatuses.RequeuedForRecovery, StringComparison.OrdinalIgnoreCase))
                {
                    return new RecoveryOutcome(
                        current!.Status!,
                        current.FailureReason,
                        matchingDecision);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            return new RecoveryOutcome(
                current?.Status ?? string.Empty,
                current?.FailureReason,
                matchingDecision);
        }

        private sealed record RecoveryOutcome(
            string IndexStatus,
            string? IndexFailureReason,
            AiRuntimeExecutionRecoveryDecision? Decision);
    }

    internal sealed class MatrixRecoverySeedRequest
    {
        public MatrixRecoverySeedRequest()
        {
        }

        public AiSdkPipelineDefinition? Definition { get; init; }

        public JsonElement Input { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

}
