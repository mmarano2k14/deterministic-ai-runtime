using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.AI.Stores;
using MultiplexedRbac.Sample.Crm.Api.AI.Models;

namespace MultiplexedRbac.Sample.Crm.Api.AI.Runtime
{
    /// <summary>
    /// Continues approval and browser-execution boundaries owned by a child
    /// execution, while always returning the root runtime-analysis projection.
    /// </summary>
    public sealed class RuntimeAnalysisChildActionService
    {
        // Browser-facing child continuation calls must not be held for the
        // full runtime execution timeout. The durable DAG keeps progressing
        // after this bounded projection window and the UI refresh endpoint
        // observes the next approval/terminal boundary.
        private static readonly TimeSpan ChildDecisionProjectionTimeout =
            TimeSpan.FromSeconds(20);

        private readonly IAiRuntimePipelineBackgroundController _controller;
        private readonly IAiDagExecutionStore _dagStore;
        private readonly IRuntimeAnalysisHumanApprovalStore _approvalStore;
        private readonly IRuntimeAnalysisScenarioExecutionStore _executionStore;
        private readonly RuntimeAnalysisExecutionResultReader _resultReader;
        private readonly RuntimeAnalysisRuntimeOptions _options;

        public RuntimeAnalysisChildActionService(
            IAiRuntimePipelineBackgroundController controller,
            IAiDagExecutionStore dagStore,
            IRuntimeAnalysisHumanApprovalStore approvalStore,
            IRuntimeAnalysisScenarioExecutionStore executionStore,
            RuntimeAnalysisExecutionResultReader resultReader,
            RuntimeAnalysisRuntimeOptions options)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _dagStore = dagStore ?? throw new ArgumentNullException(nameof(dagStore));
            _approvalStore = approvalStore ?? throw new ArgumentNullException(nameof(approvalStore));
            _executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));
            _resultReader = resultReader ?? throw new ArgumentNullException(nameof(resultReader));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public Task<RuntimeAnalysisRuntimeExecutionResult> GetRootAsync(
            string rootExecutionId,
            string rootRunId,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);

            return ReadRootAsync(
                rootExecutionId,
                rootRunId,
                cancellationToken);
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult> DecideApprovalAsync(
            string rootExecutionId,
            string childExecutionId,
            string rootRunId,
            string decision,
            string decidedBy,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(childExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(decidedBy);

            await EnsureChildBelongsToRootAsync(
                    rootExecutionId,
                    childExecutionId,
                    rootRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            var targetStatus = NormalizeDecision(decision);

            var approvalRecord = await _approvalStore.DecideAsync(
                    childExecutionId,
                    targetStatus,
                    decidedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var childState = await GetChildStateAsync(
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var stepStatus = GetStepStatus(
                childState,
                RuntimeAnalysisChildDagDefinitionFactory.AwaitHumanApprovalStepName,
                childExecutionId);

            if (stepStatus == AiStepExecutionStatus.Completed)
            {
                return await ReadRootAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (stepStatus != AiStepExecutionStatus.WaitingForExternal)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Child approval step for execution '{childExecutionId}' cannot continue from status '{stepStatus}'.");
            }

            var childRecord = await GetChildRecordAsync(
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = childRecord.PipelineName
                            ?? throw new RuntimeAnalysisRuntimeExecutionException(
                                $"Child execution '{childExecutionId}' has no pipeline name."),
                        ExternalWaitContinuation = new AiRuntimeExternalWaitContinuation
                        {
                            ExecutionId = childExecutionId,
                            StepName = approvalRecord.StepName,
                            ContinuationId = approvalRecord.ContinuationId
                        },
                        ExecutionContextSnapshot = approvalRecord.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-child-api",
                            ["operation"] = "child-human-approval-continuation",
                            ["rootExecutionId"] = rootExecutionId,
                            ["approval.status"] = targetStatus
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await WaitForBoundaryAsync(
                    handle,
                    childExecutionId,
                    "Child approval continuation",
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.Equals(
                    targetStatus,
                    RuntimeAnalysisHumanApprovalStatuses.Rejected,
                    StringComparison.Ordinal))
            {
                return await WaitForRootTerminalAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await ReadRootAsync(
                    rootExecutionId,
                    rootRunId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<RuntimeAnalysisRuntimeExecutionResult>
            CompleteScenarioExecutionAsync(
                string rootExecutionId,
                string childExecutionId,
                string rootRunId,
                RuntimeAnalysisScenarioExecutionObservation observation,
                string completedBy,
                CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rootExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(childExecutionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(completedBy);

            await EnsureChildBelongsToRootAsync(
                    rootExecutionId,
                    childExecutionId,
                    rootRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            var executionRecord = await _executionStore.CompleteAsync(
                    childExecutionId,
                    observation,
                    completedBy,
                    cancellationToken)
                .ConfigureAwait(false);

            var childState = await GetChildStateAsync(
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var stepStatus = GetStepStatus(
                childState,
                RuntimeAnalysisChildDagDefinitionFactory.ExecuteApprovedScenarioStepName,
                childExecutionId);

            if (stepStatus == AiStepExecutionStatus.Completed)
            {
                return await ReadRootAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (stepStatus != AiStepExecutionStatus.WaitingForExternal)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Child scenario execution step for execution '{childExecutionId}' cannot continue from status '{stepStatus}'.");
            }

            var childRecord = await GetChildRecordAsync(
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);

            var handle = await _controller.EnqueueAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = childRecord.PipelineName
                            ?? throw new RuntimeAnalysisRuntimeExecutionException(
                                $"Child execution '{childExecutionId}' has no pipeline name."),
                        ExternalWaitContinuation = new AiRuntimeExternalWaitContinuation
                        {
                            ExecutionId = childExecutionId,
                            StepName = executionRecord.StepName,
                            ContinuationId = executionRecord.ContinuationId
                        },
                        ExecutionContextSnapshot = executionRecord.ExecutionContextSnapshot,
                        Metadata = new Dictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["source"] = "runtime-analysis-child-api",
                            ["operation"] = "child-scenario-execution-continuation",
                            ["rootExecutionId"] = rootExecutionId,
                            ["client.state"] = observation.ClientState
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await WaitForBoundaryAsync(
                    handle,
                    childExecutionId,
                    "Child scenario continuation",
                    cancellationToken)
                .ConfigureAwait(false);

            return await WaitForNextChildDecisionOrRootTerminalAsync(
                    rootExecutionId,
                    rootRunId,
                    childExecutionId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<RuntimeAnalysisRuntimeExecutionResult>
            WaitForNextChildDecisionOrRootTerminalAsync(
                string rootExecutionId,
                string rootRunId,
                string completedChildExecutionId,
                CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + ChildDecisionProjectionTimeout;

            var startingProjection = await ReadRootAsync(
                    rootExecutionId,
                    rootRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            var completedDepth = startingProjection.ChildDag.Relations
                .Where(relation => string.Equals(
                    relation.ChildExecutionId,
                    completedChildExecutionId,
                    StringComparison.Ordinal))
                .Select(relation => relation.Depth)
                .DefaultIfEmpty(0)
                .Max();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rootRecord = await _dagStore.GetRecordAsync(
                        rootExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Root runtime analysis execution '{rootExecutionId}' does not exist.");

                var projection = await ReadRootAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                var next = projection.ChildDag.Relations
                    .Where(relation => relation.Depth > completedDepth)
                    .OrderByDescending(relation => relation.Depth)
                    .FirstOrDefault();

                if (next?.HumanApproval is not null
                    && string.Equals(
                        next.HumanApproval.Status,
                        RuntimeAnalysisHumanApprovalStatuses.Pending,
                        StringComparison.Ordinal))
                {
                    return projection;
                }

                if (next?.ScenarioExecution is not null
                    && string.Equals(
                        next.ScenarioExecution.Status,
                        RuntimeAnalysisScenarioExecutionStatuses.Pending,
                        StringComparison.Ordinal))
                {
                    return projection;
                }

                if (rootRecord.IsTerminal)
                {
                    return await WaitForRootContinuationProofAsync(
                            rootExecutionId,
                            rootRunId,
                            projection,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return projection;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task<RuntimeAnalysisRuntimeExecutionResult>
            WaitForRootTerminalAsync(
                string rootExecutionId,
                string rootRunId,
                CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + ChildDecisionProjectionTimeout;
            RuntimeAnalysisRuntimeExecutionResult projection;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var rootRecord = await _dagStore.GetRecordAsync(
                        rootExecutionId,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new RuntimeAnalysisRuntimeExecutionException(
                        $"Root runtime analysis execution '{rootExecutionId}' does not exist.");

                projection = await ReadRootAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (rootRecord.IsTerminal)
                {
                    return await WaitForRootContinuationProofAsync(
                            rootExecutionId,
                            rootRunId,
                            projection,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    return projection;
                }

                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task<RuntimeAnalysisRuntimeExecutionResult>
            WaitForRootContinuationProofAsync(
                string rootExecutionId,
                string rootRunId,
                RuntimeAnalysisRuntimeExecutionResult initialProjection,
                CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
            var projection = initialProjection;

            while (projection.ChildDag.Relations.Count > 0
                   && !projection.ChildDag.AllContinuationsResumed
                   && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken)
                    .ConfigureAwait(false);

                projection = await ReadRootAsync(
                        rootExecutionId,
                        rootRunId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return projection;
        }

        private async Task EnsureChildBelongsToRootAsync(
            string rootExecutionId,
            string childExecutionId,
            string rootRunId,
            CancellationToken cancellationToken)
        {
            var root = await ReadRootAsync(
                    rootExecutionId,
                    rootRunId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!root.ChildDag.Relations.Any(
                    relation => string.Equals(
                        relation.ChildExecutionId,
                        childExecutionId,
                        StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    $"Execution '{childExecutionId}' is not a durable child of root runtime-analysis execution '{rootExecutionId}'.",
                    nameof(childExecutionId));
            }
        }

        private async Task<RuntimeAnalysisRuntimeExecutionResult> ReadRootAsync(
            string rootExecutionId,
            string rootRunId,
            CancellationToken cancellationToken)
        {
            var rootRecord = await _dagStore.GetRecordAsync(
                    rootExecutionId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Root runtime analysis execution '{rootExecutionId}' does not exist.");

            return await _resultReader.ReadAsync(
                    rootRunId,
                    continuationRunId: null,
                    rootExecutionId,
                    rootRecord.PipelineName
                        ?? RuntimeAnalysisPipelineDefinitionFactory.PipelineName,
                    rootRecord.Status.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<AiExecutionState> GetChildStateAsync(
            string childExecutionId,
            CancellationToken cancellationToken)
        {
            return await _dagStore.GetStateAsync(
                       childExecutionId,
                       cancellationToken)
                   .ConfigureAwait(false)
                   ?? throw new RuntimeAnalysisRuntimeExecutionException(
                       $"Child execution '{childExecutionId}' has no persisted DAG state.");
        }

        private async Task<AiExecutionRecord> GetChildRecordAsync(
            string childExecutionId,
            CancellationToken cancellationToken)
        {
            return await _dagStore.GetRecordAsync(
                       childExecutionId,
                       cancellationToken)
                   .ConfigureAwait(false)
                   ?? throw new RuntimeAnalysisRuntimeExecutionException(
                       $"Child execution '{childExecutionId}' has no persisted execution record.");
        }

        private static AiStepExecutionStatus GetStepStatus(
            AiExecutionState state,
            string stepName,
            string executionId)
        {
            if (!state.Steps.TryGetValue(stepName, out var step))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"Execution '{executionId}' does not contain step '{stepName}'.");
            }

            return step.Status;
        }

        private async Task WaitForBoundaryAsync(
            AiRuntimeWorkerRunHandle handle,
            string expectedExecutionId,
            string label,
            CancellationToken cancellationToken)
        {
            AiExecutionRecord record;

            try
            {
                record = await handle.Completion.WaitAsync(
                        _options.ExecutionTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"{label} did not reach its next durable wait/terminal boundary within {_options.ExecutionTimeout.TotalSeconds:0} seconds.",
                    exception);
            }

            if (!string.Equals(
                    record.ExecutionId,
                    expectedExecutionId,
                    StringComparison.Ordinal))
            {
                throw new RuntimeAnalysisRuntimeExecutionException(
                    $"{label} changed durable execution identity from '{expectedExecutionId}' to '{record.ExecutionId}'.");
            }
        }

        private static string NormalizeDecision(
            string decision)
        {
            if (string.Equals(
                    decision,
                    RuntimeAnalysisHumanApprovalDecisions.Approve,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeAnalysisHumanApprovalStatuses.Approved;
            }

            if (string.Equals(
                    decision,
                    RuntimeAnalysisHumanApprovalDecisions.Reject,
                    StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeAnalysisHumanApprovalStatuses.Rejected;
            }

            throw new ArgumentException(
                "Human approval decision must be 'approve' or 'reject'.",
                nameof(decision));
        }
    }
}
