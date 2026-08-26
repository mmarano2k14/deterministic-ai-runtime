using System.Globalization;
using System.Net;
using Multiplexed.Abstractions.AI.ControlPlane.Admission.Placement;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Controller;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.Tests.Integration.Fixtures;
using Multiplexed.AI.McpServer.Tests.Integration.Helpers;
using Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Definitions;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Production.Providers.Base.Runners
{
    /// <summary>
    /// Executes the transport-neutral QueueFirst admission portion of a Runtime Pool production cycle.
    /// </summary>
    internal static class RuntimePoolProductionCycleExecutor
    {
        /// <summary>
        /// Submits every full-capacity wave through MCP while honoring transient HTTP 429 backpressure.
        /// </summary>
        /// <param name="mcp">The configured MCP client.</param>
        /// <param name="tenant">The production tenant definition.</param>
        /// <param name="scenarioName">The scenario name used to build unique pipeline names.</param>
        /// <param name="controlPlaneId">The logical control-plane identifier.</param>
        /// <param name="requestedBy">The MCP caller identity.</param>
        /// <param name="source">The MCP request source.</param>
        /// <param name="runsPerIteration">The number of runs submitted in every wave.</param>
        /// <param name="submissionIterationCount">The number of full-capacity waves.</param>
        /// <param name="maximumConcurrentSubmissions">The maximum number of concurrent MCP submissions.</param>
        /// <param name="maximumAdmissionAttemptCount">The maximum number of admission attempts per run.</param>
        /// <param name="cycleNumber">The optional one-based warm production cycle number.</param>
        /// <param name="startingIterationNumber">The one-based wave number used for the first submitted iteration. This allows one logical cycle to defer a configured wave without reusing correlation identities.</param>
        /// <param name="crashCheckpoint">Optional test-only durable checkpoint embedded into every DAG submitted by this call. The default remains unchanged for all existing callers.</param>
        /// <param name="admissionBackpressureTimeout">
        /// Optional wall-clock budget for transient HTTP 429 admission backpressure. When omitted,
        /// the historical fixed-attempt contract is preserved exactly. When supplied, HTTP 429
        /// remains retryable until this deadline and each MCP call is bounded by the remaining budget.
        /// </param>
        /// <param name="placementFactory">
        /// Optional test-only placement factory invoked once per logical wave/run identity.
        /// When omitted, admission preserves the historical unpinned selection behavior.
        /// </param>
        /// <param name="crashCheckpointFactory">
        /// Optional test-only checkpoint factory invoked once per logical wave/run identity.
        /// When omitted, <paramref name="crashCheckpoint"/> preserves the historical all-runs behavior.
        /// When supplied, the factory is authoritative and may return <see langword="null"/> for runs
        /// that must remain ungated while selected failure-target runs are held at the checkpoint.
        /// </param>
        /// <param name="startingRunNumber">
        /// The one-based run number used for the first run in each submitted wave. The default of one
        /// preserves every historical caller. This allows one logical wave to be submitted in deterministic
        /// phases without reusing correlation identities.
        /// </param>
        /// <param name="childCrashCheckpoint">Optional test-only durable checkpoint embedded at one recursive child depth.</param>
        /// <param name="childCrashCheckpointDepth">The one-based recursive child depth receiving <paramref name="childCrashCheckpoint"/>.</param>
        /// <param name="childCrashCheckpointFactory">
        /// Optional test-only child checkpoint factory invoked once per logical wave/run identity. When supplied,
        /// it is authoritative and may select exactly one parent whose nested child definition receives the gate.
        /// </param>
        /// <param name="submissionOrdering">
        /// Optional deterministic test-only ordering for starting logical submissions inside each wave.
        /// Returned proof results are always normalized back to ascending logical identity order.
        /// </param>
        /// <returns>The exact admission results, SharedRun identifiers, and 429 retry count.</returns>
        public static async Task<RuntimePoolProductionCycleAdmissionProof>
            SubmitQueueFirstWavesAsync(
                McpTestClient mcp,
                ProductionTenantScenarioDefinition tenant,
                string scenarioName,
                string controlPlaneId,
                string requestedBy,
                string source,
                int runsPerIteration,
                int submissionIterationCount,
                int maximumConcurrentSubmissions,
                int maximumAdmissionAttemptCount,
                int? cycleNumber = null,
                int startingIterationNumber = 1,
                McpTestCrashCheckpointDefinition? crashCheckpoint = null,
                TimeSpan? admissionBackpressureTimeout = null,
                Func<int, int, AiRunPlacementDirective?>? placementFactory = null,
                Func<int, int, McpTestCrashCheckpointDefinition?>? crashCheckpointFactory = null,
                int startingRunNumber = 1,
                McpTestCrashCheckpointDefinition? childCrashCheckpoint = null,
                int childCrashCheckpointDepth = 0,
                Func<int, int, McpTestCrashCheckpointDefinition?>? childCrashCheckpointFactory = null,
                ProductionChildDagSubmissionOrdering submissionOrdering =
                    ProductionChildDagSubmissionOrdering.Natural)
        {
            ArgumentNullException.ThrowIfNull(mcp);
            ArgumentNullException.ThrowIfNull(tenant);
            ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
            ArgumentException.ThrowIfNullOrWhiteSpace(controlPlaneId);
            ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runsPerIteration);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(submissionIterationCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumConcurrentSubmissions);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAdmissionAttemptCount);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startingIterationNumber);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startingRunNumber);
            ArgumentOutOfRangeException.ThrowIfNegative(childCrashCheckpointDepth);

            var hasChildCrashCheckpoint =
                childCrashCheckpoint is not null ||
                childCrashCheckpointFactory is not null;

            if (hasChildCrashCheckpoint &&
                (childCrashCheckpointDepth <= 0 ||
                 childCrashCheckpointDepth > tenant.Run.ChildDepth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childCrashCheckpointDepth),
                    childCrashCheckpointDepth,
                    $"Child crash checkpoint depth must be between 1 and configured ChildDepth '{tenant.Run.ChildDepth}'.");
            }

            if (!hasChildCrashCheckpoint && childCrashCheckpointDepth != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(childCrashCheckpointDepth),
                    childCrashCheckpointDepth,
                    "Child crash checkpoint depth must be zero when no child crash checkpoint is configured.");
            }

            if (admissionBackpressureTimeout.HasValue &&
                admissionBackpressureTimeout.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(admissionBackpressureTimeout),
                    admissionBackpressureTimeout,
                    "The admission backpressure timeout must be greater than zero.");
            }

            if (cycleNumber.HasValue && cycleNumber.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cycleNumber));
            }

            var tooManyRequestsRetryCount = 0;

            async Task<AiSharedRuntimeControllerResult>
                SubmitSingleRunWithBackpressureAsync(
                    AiSharedRuntimeControllerRequest request)
            {
                var retryDelay =
                    TimeSpan.FromMilliseconds(100);

                if (admissionBackpressureTimeout.HasValue)
                {
                    var backpressureDeadline =
                        DateTimeOffset.UtcNow.Add(
                            admissionBackpressureTimeout.Value);

                    var attempt = 0;

                    while (DateTimeOffset.UtcNow < backpressureDeadline)
                    {
                        attempt++;

                        var remaining =
                            backpressureDeadline - DateTimeOffset.UtcNow;

                        if (remaining <= TimeSpan.Zero)
                        {
                            break;
                        }

                        using var attemptCancellation =
                            new CancellationTokenSource(remaining);

                        try
                        {
                            var result =
                                await mcp
                                    .SubmitManyRunsAsync(
                                        request,
                                        1,
                                        attemptCancellation.Token)
                                    .ConfigureAwait(false);

                            return Assert.Single(result);
                        }
                        catch (HttpRequestException exception)
                            when (
                                exception.StatusCode ==
                                    HttpStatusCode.TooManyRequests)
                        {
                            Interlocked.Increment(
                                ref tooManyRequestsRetryCount);

                            var remainingAfterBackpressure =
                                backpressureDeadline -
                                DateTimeOffset.UtcNow;

                            if (remainingAfterBackpressure <= TimeSpan.Zero)
                            {
                                break;
                            }

                            var boundedDelay =
                                retryDelay < remainingAfterBackpressure
                                    ? retryDelay
                                    : remainingAfterBackpressure;

                            await Task
                                .Delay(boundedDelay)
                                .ConfigureAwait(false);

                            retryDelay =
                                TimeSpan.FromMilliseconds(
                                    Math.Min(
                                        retryDelay.TotalMilliseconds * 2,
                                        2_000));
                        }
                        catch (OperationCanceledException exception)
                            when (attemptCancellation.IsCancellationRequested)
                        {
                            throw CreateAdmissionBackpressureTimeoutException(
                                attempt,
                                exception);
                        }
                    }

                    throw CreateAdmissionBackpressureTimeoutException(
                        attempt,
                        innerException: null);
                }

                for (var attempt = 1;
                     attempt <= maximumAdmissionAttemptCount;
                     attempt++)
                {
                    try
                    {
                        var result =
                            await mcp
                                .SubmitManyRunsAsync(
                                    request,
                                    1)
                                .ConfigureAwait(false);

                        return Assert.Single(result);
                    }
                    catch (HttpRequestException exception)
                        when (
                            exception.StatusCode ==
                                HttpStatusCode.TooManyRequests &&
                            attempt < maximumAdmissionAttemptCount)
                    {
                        Interlocked.Increment(
                            ref tooManyRequestsRetryCount);

                        await Task
                            .Delay(retryDelay)
                            .ConfigureAwait(false);

                        retryDelay =
                            TimeSpan.FromMilliseconds(
                                Math.Min(
                                    retryDelay.TotalMilliseconds * 2,
                                    2_000));
                    }
                }

                var cycleSuffix =
                    cycleNumber.HasValue
                        ? $" in warm reuse cycle '{cycleNumber.Value}'"
                        : string.Empty;

                throw new TimeoutException(
                    "MCP QueueFirst admission remained throttled " +
                    $"after '{maximumAdmissionAttemptCount}' attempts{cycleSuffix}.");
            }

            TimeoutException CreateAdmissionBackpressureTimeoutException(
                int observedAttemptCount,
                Exception? innerException)
            {
                var cycleSuffix =
                    cycleNumber.HasValue
                        ? $" in warm reuse cycle '{cycleNumber.Value}'"
                        : string.Empty;

                var message =
                    "MCP QueueFirst admission remained throttled until the configured " +
                    $"backpressure deadline{cycleSuffix}. " +
                    $"Timeout='{admissionBackpressureTimeout}', " +
                    $"ObservedAttemptCount='{observedAttemptCount}', " +
                    $"TooManyRequestsRetryCount='{Volatile.Read(ref tooManyRequestsRetryCount)}'.";

                return innerException is null
                    ? new TimeoutException(message)
                    : new TimeoutException(message, innerException);
            }

            using var submissionGate =
                new SemaphoreSlim(
                    maximumConcurrentSubmissions,
                    maximumConcurrentSubmissions);

            var runSubmissionOffsets =
                ResolveRunSubmissionOffsets(
                    runsPerIteration,
                    submissionOrdering);

            var submissionTasks =
                Enumerable
                    .Range(startingIterationNumber, submissionIterationCount)
                    .SelectMany(
                        iteration =>
                        {
                            var pipelineName =
                                BuildPipelineName(
                                    scenarioName,
                                    iteration,
                                    cycleNumber);

                            return runSubmissionOffsets
                                .Select(
                                    async runOffset =>
                                    {
                                        var runNumber =
                                            checked(startingRunNumber + runOffset);

                                        var runCrashCheckpoint =
                                            crashCheckpointFactory is null
                                                ? crashCheckpoint
                                                : crashCheckpointFactory(
                                                    iteration,
                                                    runNumber);

                                        var runChildCrashCheckpoint =
                                            childCrashCheckpointFactory is null
                                                ? childCrashCheckpoint
                                                : childCrashCheckpointFactory(
                                                    iteration,
                                                    runNumber);

                                        var request =
                                            CreateSubmitRequest(
                                                tenant,
                                                controlPlaneId,
                                                pipelineName,
                                                requestedBy,
                                                source,
                                                BuildCorrelationId(
                                                    controlPlaneId,
                                                    iteration,
                                                    runNumber,
                                                    cycleNumber),
                                                runCrashCheckpoint,
                                                runChildCrashCheckpoint,
                                                runChildCrashCheckpoint is null
                                                    ? 0
                                                    : childCrashCheckpointDepth,
                                                placementFactory?.Invoke(
                                                    iteration,
                                                    runNumber));

                                        await submissionGate
                                            .WaitAsync()
                                            .ConfigureAwait(false);

                                        try
                                        {
                                            var result =
                                                await SubmitSingleRunWithBackpressureAsync(
                                                        request)
                                                    .ConfigureAwait(false);

                                            return (
                                                Iteration: iteration,
                                                RunNumber: runNumber,
                                                Result: result);
                                        }
                                        finally
                                        {
                                            submissionGate.Release();
                                        }
                                    });
                        })
                    .ToArray();

            var submittedRuns =
                await Task
                    .WhenAll(submissionTasks)
                    .ConfigureAwait(false);

            // Physical submission invocation follows the selected deterministic ordering, but every downstream
            // proof keeps the historical logical result order so existing target-selection contracts remain stable.
            var submissionResults =
                NormalizeSubmissionResults(submittedRuns);

            var expectedSubmissionCount =
                checked(runsPerIteration * submissionIterationCount);

            Assert.Equal(
                expectedSubmissionCount,
                submissionResults.Count);

            Assert.All(
                submissionResults,
                result => Assert.True(
                    result.Success,
                    result.FailureReason ?? result.Message));

            var submittedSharedRunIds =
                submissionResults
                    .Select(result => result.SharedRunId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);

            Assert.Equal(
                expectedSubmissionCount,
                submittedSharedRunIds.Count);

            return new RuntimePoolProductionCycleAdmissionProof(
                submissionResults,
                submittedSharedRunIds,
                Volatile.Read(ref tooManyRequestsRetryCount));
        }

        /// <summary>
        /// Resolves the exact run-offset invocation order for one deterministic submission segment.
        /// </summary>
        internal static IReadOnlyList<int> ResolveRunSubmissionOffsets(
            int runsPerIteration,
            ProductionChildDagSubmissionOrdering submissionOrdering)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(runsPerIteration);

            var naturalOffsets =
                Enumerable
                    .Range(0, runsPerIteration)
                    .ToArray();

            return submissionOrdering switch
            {
                ProductionChildDagSubmissionOrdering.Natural => naturalOffsets,
                ProductionChildDagSubmissionOrdering.Reverse => naturalOffsets.Reverse().ToArray(),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(submissionOrdering),
                    submissionOrdering,
                    "The deterministic submission ordering is not supported.")
            };
        }

        /// <summary>
        /// Restores historical ascending logical identity order after physical invocation order has been varied.
        /// </summary>
        internal static IReadOnlyList<TResult> NormalizeSubmissionResults<TResult>(
            IEnumerable<(int Iteration, int RunNumber, TResult Result)> submittedRuns)
        {
            ArgumentNullException.ThrowIfNull(submittedRuns);

            return submittedRuns
                .OrderBy(value => value.Iteration)
                .ThenBy(value => value.RunNumber)
                .Select(value => value.Result)
                .ToArray();
        }

        /// <summary>
        /// Combines multiple non-overlapping admission segments into one exact logical-cycle proof.
        /// </summary>
        public static RuntimePoolProductionCycleAdmissionProof CombineAdmissionProofs(
            params RuntimePoolProductionCycleAdmissionProof[] proofs)
        {
            ArgumentNullException.ThrowIfNull(proofs);

            if (proofs.Length == 0)
            {
                throw new ArgumentException(
                    "At least one admission proof is required.",
                    nameof(proofs));
            }

            Assert.All(proofs, proof => ArgumentNullException.ThrowIfNull(proof));

            var results =
                proofs
                    .SelectMany(proof => proof.Results)
                    .ToArray();
            var sharedRunIds =
                proofs
                    .SelectMany(proof => proof.SharedRunIds)
                    .ToHashSet(StringComparer.Ordinal);
            var tooManyRequestsRetryCount =
                proofs.Aggregate(
                    0,
                    (current, proof) =>
                        checked(
                            current +
                            proof.TooManyRequestsRetryCount));

            Assert.Equal(results.Length, sharedRunIds.Count);

            return new RuntimePoolProductionCycleAdmissionProof(
                results,
                sharedRunIds,
                tooManyRequestsRetryCount);
        }

        /// <summary>
        /// Selects only recovered SharedRuns that belong to the submitted workload while proving that every
        /// recovered SharedRun outside that workload is an explicitly expected supplemental recovery identity.
        /// </summary>
        /// <remarks>
        /// Durable dispatch proof is scoped to submitted workload SharedRuns. Adversarial schedules may recover
        /// a durable control SharedRun such as an external-wait continuation. That supplemental recovery must be
        /// proved separately and must never be allowed to broaden the submitted-workload dispatch proof.
        /// </remarks>
        internal static IReadOnlySet<string>
            SelectRecoveredSubmittedSharedRunIdsForDispatchProof(
                IReadOnlySet<string> submittedSharedRunIds,
                IReadOnlySet<string> recoveredSharedRunIds,
                IReadOnlySet<string> expectedSupplementalRecoveredSharedRunIds,
                string proofName)
        {
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentNullException.ThrowIfNull(recoveredSharedRunIds);
            ArgumentNullException.ThrowIfNull(expectedSupplementalRecoveredSharedRunIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            var recoveredSubmittedSharedRunIds =
                recoveredSharedRunIds
                    .Intersect(
                        submittedSharedRunIds,
                        StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

            var actualSupplementalRecoveredSharedRunIds =
                recoveredSharedRunIds
                    .Except(
                        submittedSharedRunIds,
                        StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

            AssertSameIdentitySet(
                expectedSupplementalRecoveredSharedRunIds,
                actualSupplementalRecoveredSharedRunIds,
                $"{proofName} supplemental recovered SharedRun scope");

            return recoveredSubmittedSharedRunIds;
        }

        /// <summary>
        /// Selects the recovered execution identifiers that belong to one exact proof scope and
        /// verifies that every recovered execution outside that scope is explicitly expected.
        /// </summary>
        /// <remarks>
        /// Adversarial recursive-child schedules may recover a child execution while a parent-only
        /// ledger proof is being evaluated. The child recovery remains part of the global recovery
        /// authority, but it must not broaden the parent proof or be silently discarded.
        /// </remarks>
        internal static IReadOnlySet<string>
            SelectRecoveredExecutionIdsForExpectedProofScope(
                IReadOnlySet<string> expectedExecutionIds,
                IReadOnlySet<string> recoveredExecutionIds,
                IReadOnlySet<string> expectedSupplementalRecoveredExecutionIds,
                string proofName)
        {
            ArgumentNullException.ThrowIfNull(expectedExecutionIds);
            ArgumentNullException.ThrowIfNull(recoveredExecutionIds);
            ArgumentNullException.ThrowIfNull(expectedSupplementalRecoveredExecutionIds);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            var recoveredExpectedExecutionIds =
                recoveredExecutionIds
                    .Intersect(
                        expectedExecutionIds,
                        StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

            var actualSupplementalRecoveredExecutionIds =
                recoveredExecutionIds
                    .Except(
                        expectedExecutionIds,
                        StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

            AssertSameIdentitySet(
                expectedSupplementalRecoveredExecutionIds,
                actualSupplementalRecoveredExecutionIds,
                $"{proofName} supplemental recovered execution scope");

            return recoveredExpectedExecutionIds;
        }

        /// <summary>
        /// Proves that every submitted shared run exposes durable dispatch evidence.
        /// A run whose initial dispatch success ledger entry is absent is accepted only when
        /// that exact SharedRunId belongs to the already-proven recovery set.
        /// </summary>
        public static RuntimePoolProductionDispatchLedgerProof
            AssertDurableDispatchEvidence(
                IReadOnlySet<string> submittedSharedRunIds,
                IReadOnlySet<string> recoveredSharedRunIds,
                IReadOnlyCollection<AiDecisionLedgerEntry> controlPlaneLedgerEntries,
                string proofName)
        {
            ArgumentNullException.ThrowIfNull(submittedSharedRunIds);
            ArgumentNullException.ThrowIfNull(recoveredSharedRunIds);
            ArgumentNullException.ThrowIfNull(controlPlaneLedgerEntries);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            var initialDispatchSucceededSharedRunIds =
                controlPlaneLedgerEntries
                    .Where(
                        entry => entry.EventType.Contains(
                            "remote-shared-run-dispatch.succeeded",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.CorrelationContext.RunId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.Ordinal);

            var unexpectedInitialDispatchSharedRunIds =
                initialDispatchSucceededSharedRunIds
                    .Except(submittedSharedRunIds, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                unexpectedInitialDispatchSharedRunIds.Length == 0,
                $"{proofName} contains dispatch-success ledger evidence for unexpected SharedRunIds. Unexpected='{string.Join(",", unexpectedInitialDispatchSharedRunIds)}'.");

            var unexpectedRecoveredSharedRunIds =
                recoveredSharedRunIds
                    .Except(submittedSharedRunIds, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                unexpectedRecoveredSharedRunIds.Length == 0,
                $"{proofName} recovery set contains SharedRunIds outside the submitted cycle. Unexpected='{string.Join(",", unexpectedRecoveredSharedRunIds)}'.");

            var missingInitialDispatchSharedRunIds =
                submittedSharedRunIds
                    .Except(
                        initialDispatchSucceededSharedRunIds,
                        StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var missingOutsideRecoverySharedRunIds =
                missingInitialDispatchSharedRunIds
                    .Except(recoveredSharedRunIds, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                missingOutsideRecoverySharedRunIds.Length == 0,
                $"{proofName} is missing initial dispatch-success ledger evidence for SharedRunIds that were not part of the exact recovery set. Missing='{string.Join(",", missingOutsideRecoverySharedRunIds)}', Recovered='{string.Join(",", recoveredSharedRunIds.OrderBy(value => value, StringComparer.Ordinal))}'.");

            var recoveryCoveredSharedRunIds =
                missingInitialDispatchSharedRunIds
                    .Intersect(recoveredSharedRunIds, StringComparer.Ordinal)
                    .ToHashSet(StringComparer.Ordinal);

            var durableDispatchProvenSharedRunIds =
                initialDispatchSucceededSharedRunIds
                    .Concat(recoveryCoveredSharedRunIds)
                    .ToHashSet(StringComparer.Ordinal);

            AssertSameIdentitySet(
                submittedSharedRunIds,
                durableDispatchProvenSharedRunIds,
                proofName);

            return new RuntimePoolProductionDispatchLedgerProof(
                initialDispatchSucceededSharedRunIds,
                recoveryCoveredSharedRunIds,
                durableDispatchProvenSharedRunIds);
        }

        /// <summary>
        /// Proves exact logical step-completion ledger coverage while preserving raw append-only
        /// evidence produced around a recovered failure boundary.
        /// </summary>
        /// <remarks>
        /// Every expected execution must expose exactly the configured number of distinct logical
        /// completed steps. Additional raw <c>step.completed</c> entries are accepted only when
        /// their logical step belongs to an execution already proven to have been recovered.
        /// </remarks>
        public static RuntimePoolProductionStepLedgerProof
            AssertLogicalStepCompletionEvidence(
                IReadOnlyCollection<AiDecisionLedgerEntry> executionLedgerEntries,
                IReadOnlySet<string> expectedExecutionIds,
                IReadOnlySet<string> recoveredExecutionIds,
                int expectedStepCountPerExecution,
                string proofName)
        {
            ArgumentNullException.ThrowIfNull(executionLedgerEntries);
            ArgumentNullException.ThrowIfNull(expectedExecutionIds);
            ArgumentNullException.ThrowIfNull(recoveredExecutionIds);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
                expectedStepCountPerExecution);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            var unexpectedRecoveredExecutionIds =
                recoveredExecutionIds
                    .Except(expectedExecutionIds, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                unexpectedRecoveredExecutionIds.Length == 0,
                $"{proofName} recovery set contains execution identifiers outside the expected cycle. Unexpected='{string.Join(",", unexpectedRecoveredExecutionIds)}'.");

            var completedEntries =
                executionLedgerEntries
                    .Where(
                        entry => string.Equals(
                            entry.EventType,
                            AiEngineEvents.Step.Completed,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            var evidence =
                completedEntries
                    .Select(
                        entry =>
                            new RuntimePoolProductionStepCompletionEvidence(
                                entry,
                                entry.CorrelationContext.ExecutionId,
                                ResolveLogicalStepId(entry)))
                    .ToArray();

            var invalidEvidence =
                evidence
                    .Where(item =>
                        string.IsNullOrWhiteSpace(item.ExecutionId) ||
                        string.IsNullOrWhiteSpace(item.LogicalStepId))
                    .Select(item =>
                        $"EntryId='{item.Entry.EntryId}', ExecutionId='{item.ExecutionId}', StepId='{item.Entry.CorrelationContext.StepId}', StepKey='{item.Entry.CorrelationContext.StepKey}'")
                    .Take(20)
                    .ToArray();

            Assert.True(
                invalidEvidence.Length == 0,
                $"{proofName} contains step-completion ledger entries without a stable logical identity. Invalid='{string.Join(" | ", invalidEvidence)}'.");

            var unexpectedEvidenceExecutionIds =
                evidence
                    .Select(item => item.ExecutionId)
                    .Distinct(StringComparer.Ordinal)
                    .Except(expectedExecutionIds, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                unexpectedEvidenceExecutionIds.Length == 0,
                $"{proofName} contains step-completion evidence for unexpected executions. Unexpected='{string.Join(",", unexpectedEvidenceExecutionIds)}'.");

            var logicalStepGroups =
                evidence
                    .GroupBy(
                        item =>
                            new RuntimePoolProductionLogicalStepIdentity(
                                item.ExecutionId,
                                item.LogicalStepId))
                    .ToArray();

            var distinctStepCountsByExecution =
                logicalStepGroups
                    .GroupBy(group => group.Key.ExecutionId)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Count(),
                        StringComparer.Ordinal);

            var mismatchedExecutions =
                expectedExecutionIds
                    .Select(
                        executionId =>
                        {
                            distinctStepCountsByExecution.TryGetValue(
                                executionId,
                                out var observedStepCount);

                            return new
                            {
                                ExecutionId = executionId,
                                ObservedStepCount = observedStepCount
                            };
                        })
                    .Where(item =>
                        item.ObservedStepCount != expectedStepCountPerExecution)
                    .OrderBy(item => item.ExecutionId, StringComparer.Ordinal)
                    .Take(20)
                    .ToArray();

            Assert.True(
                mismatchedExecutions.Length == 0,
                $"{proofName} did not expose the exact logical step count for every execution. ExpectedPerExecution='{expectedStepCountPerExecution}', Mismatches='{string.Join(" | ", mismatchedExecutions.Select(item => $"ExecutionId='{item.ExecutionId}', Observed='{item.ObservedStepCount}'"))}'.");

            var expectedLogicalStepCount =
                checked(expectedExecutionIds.Count * expectedStepCountPerExecution);

            Assert.Equal(
                expectedLogicalStepCount,
                logicalStepGroups.Length);

            var duplicateGroups =
                logicalStepGroups
                    .Where(group => group.Count() > 1)
                    .ToArray();

            var duplicateGroupsOutsideRecovery =
                duplicateGroups
                    .Where(group =>
                        !recoveredExecutionIds.Contains(
                            group.Key.ExecutionId))
                    .Select(FormatDuplicateStepEvidence)
                    .Take(20)
                    .ToArray();

            Assert.True(
                duplicateGroupsOutsideRecovery.Length == 0,
                $"{proofName} contains duplicate raw step-completion evidence outside the exact recovered execution set. Duplicates='{string.Join(" | ", duplicateGroupsOutsideRecovery)}'.");

            var duplicateEntryCount =
                duplicateGroups.Sum(group => group.Count() - 1);

            var duplicateEvidenceExecutionIds =
                duplicateGroups
                    .Select(group => group.Key.ExecutionId)
                    .ToHashSet(StringComparer.Ordinal);

            return new RuntimePoolProductionStepLedgerProof(
                completedEntries.Length,
                logicalStepGroups.Length,
                duplicateEntryCount,
                duplicateEvidenceExecutionIds);
        }

        /// <summary>
        /// Proves that two Runtime Pool membership snapshots expose exactly the same identities.
        /// </summary>
        public static void AssertSameIdentitySet(
            IReadOnlySet<string> expected,
            IReadOnlySet<string> actual,
            string proofName)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(actual);
            ArgumentException.ThrowIfNullOrWhiteSpace(proofName);

            var missing =
                expected
                    .Except(actual, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            var unexpected =
                actual
                    .Except(expected, StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();

            Assert.True(
                missing.Length == 0 && unexpected.Length == 0,
                $"{proofName} identity mismatch. Missing='{string.Join(",", missing)}', Unexpected='{string.Join(",", unexpected)}'.");
        }

        private static string ResolveLogicalStepId(
            AiDecisionLedgerEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (!string.IsNullOrWhiteSpace(
                    entry.CorrelationContext.StepId))
            {
                return entry.CorrelationContext.StepId;
            }

            if (entry.Metadata is not null &&
                entry.Metadata.TryGetValue(
                    "step.name",
                    out var stepName) &&
                !string.IsNullOrWhiteSpace(stepName))
            {
                return stepName;
            }

            if (!string.IsNullOrWhiteSpace(
                    entry.CorrelationContext.StepKey))
            {
                return entry.CorrelationContext.StepKey;
            }

            if (entry.Metadata is not null &&
                entry.Metadata.TryGetValue(
                    "step.key",
                    out var stepKey) &&
                !string.IsNullOrWhiteSpace(stepKey))
            {
                return stepKey;
            }

            return string.Empty;
        }

        private static string FormatDuplicateStepEvidence(
            IGrouping<RuntimePoolProductionLogicalStepIdentity,
                RuntimePoolProductionStepCompletionEvidence> group)
        {
            var entries =
                string.Join(
                    ",",
                    group
                        .OrderBy(item => item.Entry.TimestampUtc)
                        .ThenBy(item => item.Entry.Sequence)
                        .Select(
                            item =>
                                $"EntryId='{item.Entry.EntryId}';Runtime='{item.Entry.CorrelationContext.RuntimeInstanceId}';Worker='{item.Entry.CorrelationContext.WorkerId}';Claim='{item.Entry.CorrelationContext.ClaimToken}'"));

            return
                $"ExecutionId='{group.Key.ExecutionId}', LogicalStepId='{group.Key.LogicalStepId}', Entries=[{entries}]";
        }

        private sealed record RuntimePoolProductionLogicalStepIdentity(
            string ExecutionId,
            string LogicalStepId);

        private sealed record RuntimePoolProductionStepCompletionEvidence(
            AiDecisionLedgerEntry Entry,
            string ExecutionId,
            string LogicalStepId);

        private static string BuildPipelineName(
            string scenarioName,
            int iteration,
            int? cycleNumber)
        {
            if (cycleNumber.HasValue)
            {
                return string.Concat(
                    scenarioName,
                    "-cycle-",
                    cycleNumber.Value.ToString(
                        "000",
                        CultureInfo.InvariantCulture),
                    "-wave-",
                    iteration.ToString(
                        "000",
                        CultureInfo.InvariantCulture),
                    "-",
                    Guid.NewGuid().ToString("N"));
            }

            return string.Concat(
                scenarioName,
                "-wave-",
                iteration.ToString(
                    "000",
                    CultureInfo.InvariantCulture),
                "-",
                Guid.NewGuid().ToString("N"));
        }

        private static string BuildCorrelationId(
            string controlPlaneId,
            int iteration,
            int runNumber,
            int? cycleNumber)
        {
            if (cycleNumber.HasValue)
            {
                return string.Concat(
                    controlPlaneId,
                    ":cycle:",
                    cycleNumber.Value.ToString(
                        CultureInfo.InvariantCulture),
                    ":wave:",
                    iteration.ToString(
                        CultureInfo.InvariantCulture),
                    ":run:",
                    runNumber.ToString(
                        CultureInfo.InvariantCulture));
            }

            return string.Concat(
                controlPlaneId,
                ":wave:",
                iteration.ToString(
                    CultureInfo.InvariantCulture),
                ":run:",
                runNumber.ToString(
                    CultureInfo.InvariantCulture));
        }

        private static AiSharedRuntimeControllerRequest CreateSubmitRequest(
            ProductionTenantScenarioDefinition tenant,
            string controlPlaneId,
            string pipelineName,
            string requestedBy,
            string source,
            string correlationId,
            McpTestCrashCheckpointDefinition? crashCheckpoint,
            McpTestCrashCheckpointDefinition? childCrashCheckpoint,
            int childCrashCheckpointDepth,
            AiRunPlacementDirective? placement)
        {
            var input =
                new Dictionary<string, object?>(
                    tenant.Run.Input,
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                        tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                        tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["delayMs"] = tenant.Run.DelayMs,
                    ["stepCount"] = tenant.Run.StepCount
                };

            var metadata =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantId] =
                        tenant.TenantId,
                    [AiRuntimeInstanceIsolationMetadataKeys.TenantGroupId] =
                        tenant.TenantGroupId,
                    ["pipelineName"] = pipelineName,
                    ["runtimeInstanceIdPrefix"] =
                        tenant.RuntimeInstanceIdPrefix,
                    ["logicalControlPlaneId"] = controlPlaneId,
                    ["controlPlaneId"] = controlPlaneId,
                    ["control-plane.id"] = controlPlaneId,
                    ["controlplane.id"] = controlPlaneId,
                    ["runtime.controlPlaneId"] = controlPlaneId,
                    ["runtime.control-plane.id"] = controlPlaneId,
                    ["runtime.controlplane.id"] = controlPlaneId,
                    ["scenario.controlPlaneId"] = controlPlaneId,
                    ["scenario.control-plane.id"] = controlPlaneId,
                    ["scenario.controlplane.id"] = controlPlaneId,
                    ["scaleout.controlPlaneId"] = controlPlaneId,
                    ["scaleout.control-plane.id"] = controlPlaneId,
                    ["scaleout.controlplane.id"] = controlPlaneId
                };

            return new AiSharedRuntimeControllerRequest
            {
                Operation =
                    AiSharedRuntimeControllerOperation.SubmitRun,
                PipelineKey = pipelineName,
                TenantId = tenant.TenantId,
                RequestedBy = requestedBy,
                Source = source,
                CorrelationId = correlationId,
                Placement = placement,
                Metadata = metadata,
                RunRequest =
                    McpTestPipelineFactory.CreateRunRequest(
                        pipelineName,
                        stepCount: tenant.Run.StepCount,
                        input: input,
                        enableRetention: tenant.Run.EnableRetention,
                        flakyStepInterval:
                            tenant.Run.FlakyStepInterval,
                        crashCheckpoint: crashCheckpoint,
                        childDepth: tenant.Run.ChildDepth,
                        childCrashCheckpoint: childCrashCheckpoint,
                        childCrashCheckpointDepth: childCrashCheckpointDepth)
            };
        }
    }

    /// <summary>
    /// Captures the exact QueueFirst admission proof produced for one Runtime Pool production cycle.
    /// </summary>
    internal sealed record RuntimePoolProductionCycleAdmissionProof(
        IReadOnlyList<AiSharedRuntimeControllerResult> Results,
        IReadOnlySet<string> SharedRunIds,
        int TooManyRequestsRetryCount);


    /// <summary>
    /// Captures exact durable dispatch evidence for one Runtime Pool production cycle.
    /// </summary>
    internal sealed record RuntimePoolProductionDispatchLedgerProof(
        IReadOnlySet<string> InitialDispatchSucceededSharedRunIds,
        IReadOnlySet<string> RecoveryCoveredSharedRunIds,
        IReadOnlySet<string> DurableDispatchProvenSharedRunIds);

    /// <summary>
    /// Captures raw and distinct logical step-completion ledger evidence for one production cycle.
    /// </summary>
    internal sealed record RuntimePoolProductionStepLedgerProof(
        int RawStepCompletedEntryCount,
        int DistinctLogicalStepCompletedCount,
        int DuplicateStepCompletedEntryCount,
        IReadOnlySet<string> DuplicateEvidenceExecutionIds);
}
