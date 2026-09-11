using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.Rbac.Core.ExecutionContext;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.Execution.Instance.Worker
{
    /// <summary>
    /// Behavioural tests for the parallelism-gate invariant of
    /// <see cref="AiRuntimePipelineBackgroundController"/>: each admitted run acquires exactly one
    /// concurrency slot and releases it exactly once, whether it completes, faults, or parks
    /// (transitions to external waiting).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are pure in-memory unit tests — no Redis. They intentionally use the controller's
    /// recovery-resume admission path so DAG creation is not part of the test surface: execution
    /// outcomes are driven only by the scripted worker fake. The concrete DAG engine is present
    /// because the controller constructor requires it, but its creation runtime is never invoked.
    /// </para>
    /// <para>
    /// The gate is private, so correctness is asserted through observable behaviour:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Over-release (a slot released twice) manifests as observed worker concurrency
    /// exceeding <c>MaxConcurrentRuns</c>.</description></item>
    /// <item><description>Under-release (a slot never released) manifests as a subsequent run never
    /// starting within the timeout.</description></item>
    /// </list>
    /// <para>
    /// The single-slot tests pin each release path in isolation. The fixture deliberately configures
    /// the independent local worker budget above the controller gate so worker reservation cannot
    /// hide either over-release or under-release of the controller semaphore. The multi-slot test pins
    /// the admission ceiling at N&gt;1. The mixed-outcome stress test bursts many runs with no
    /// await-until-started barrier, so all three release paths (complete, fault, park) execute under
    /// real concurrent scheduling; it asserts the ceiling is never exceeded (no over-release) and that
    /// every run is eventually admitted (no leaked slot / under-release), repeated across iterations to
    /// shake out scheduling variation.
    /// </para>
    /// </remarks>
    public sealed class AiRuntimePipelineBackgroundControllerCapacityGateTests
    {
        private static readonly TimeSpan ExpectStart = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ExpectNoStart = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);

        private static int nextExecutionOrdinal;

        /// <summary>
        /// Baseline: with a single slot, admitted runs never overlap. Guards against a gate that
        /// admits beyond <c>MaxConcurrentRuns</c>.
        /// </summary>
        [Fact]
        public async Task Enqueue_NeverExceeds_MaxConcurrentRuns()
        {
            var worker = new ScriptedRuntimeInstanceWorker(
                RunScript.Hold(),   // A: occupies the only slot until released
                RunScript.Hold(),   // B: must wait for A
                RunScript.Hold());  // C: must wait for B

            var controller = CreateController(worker, maxConcurrentRuns: 1);
            await controller.StartAsync(default).ConfigureAwait(false);

            await EnqueueAndAwaitStartAsync(controller, worker, expectedStarted: 1).ConfigureAwait(false);
            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // B
            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // C

            Assert.False(
                await worker.TryWaitForStartsAsync(2, ExpectNoStart).ConfigureAwait(false),
                "A second run started while the single slot was held — the gate over-admitted.");

            worker.ReleaseAll();
            await controller.StopAsync(default).ConfigureAwait(false);

            Assert.Equal(1, worker.MaxObservedConcurrency);
        }

        /// <summary>
        /// A faulted run must release its slot exactly once: the next run starts (≥1 release) and no
        /// third run ever overlaps it (≤1 release on the fault path).
        /// </summary>
        [Fact]
        public async Task FaultedRun_ReleasesCapacity_ExactlyOnce()
        {
            var worker = new ScriptedRuntimeInstanceWorker(
                RunScript.Throw(),  // A: faults inside the worker
                RunScript.Hold(),   // B: should be admitted after A's slot is released
                RunScript.Hold());  // C: must not overlap B

            var controller = CreateController(worker, maxConcurrentRuns: 1);
            await controller.StartAsync(default).ConfigureAwait(false);

            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // A (faults)
            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // B
            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // C

            Assert.True(
                await worker.TryWaitForHoldingAsync(1, ExpectStart).ConfigureAwait(false),
                "No run started after the faulted run — the slot was never released (under-release).");

            Assert.False(
                await worker.TryWaitForHoldingAsync(2, ExpectNoStart).ConfigureAwait(false),
                "Two runs held the single slot after a fault — the gate released twice (over-release).");

            worker.ReleaseAll();
            await controller.StopAsync(default).ConfigureAwait(false);

            Assert.True(worker.MaxObservedConcurrency <= 1);
        }

        /// <summary>
        /// A parked run releases capacity immediately after durable waiting is committed, while its
        /// post-transition bookkeeping is still running, and the later task continuation does not
        /// release the same slot a second time.
        /// </summary>
        [Fact]
        public async Task ParkedRun_ReleasesCapacity_ExactlyOnce()
        {
            var worker = new ScriptedRuntimeInstanceWorker(
                RunScript.Park(),   // A: returns Waiting -> early release after MarkWaitingAsync
                RunScript.Hold(),   // B: must start while A's suspension ledger write is blocked
                RunScript.Hold());  // C: must never overlap B

            var ledger = new BlockingSuspensionDecisionLedgerRecorder();
            var observability = new LedgerOverrideRuntimeObservability(ledger);
            var controller = CreateController(
                worker,
                maxConcurrentRuns: 1,
                observability: observability);

            await controller.StartAsync(default).ConfigureAwait(false);

            await EnqueueAndAwaitStartAsync(controller, worker, expectedStarted: 1).ConfigureAwait(false); // A

            // The controller releases external-wait capacity before recording Run.Suspended.
            // Blocking that ledger write keeps A's task alive and makes early-release observable.
            await ledger.SuspensionRecordStarted
                .WaitAsync(ExpectStart)
                .ConfigureAwait(false);

            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // B

            Assert.True(
                await worker.TryWaitForHoldingAsync(1, ExpectStart).ConfigureAwait(false),
                "B did not start while A remained blocked after durable waiting — external-wait capacity was not released early.");

            await EnqueueResumeAsync(controller).ConfigureAwait(false);   // C

            Assert.False(
                await worker.TryWaitForHoldingAsync(2, ExpectNoStart).ConfigureAwait(false),
                "C overlapped B before A finished post-wait bookkeeping — the gate over-released.");

            // Let A's task finish. Its normal continuation must consume the once-only marker and
            // therefore must NOT release the semaphore again while B still owns the single slot.
            ledger.ReleaseSuspensionRecord();

            Assert.False(
                await worker.TryWaitForHoldingAsync(2, ExpectNoStart).ConfigureAwait(false),
                "C overlapped B after A's task continuation completed — the external-wait slot was released twice.");

            worker.ReleaseAll();
            await controller.StopAsync(default).ConfigureAwait(false);

            Assert.True(worker.MaxObservedConcurrency <= 1);
        }

        /// <summary>
        /// With three slots, exactly three runs may hold concurrently and a fourth must wait. Guards
        /// against a counting drift that admits beyond <c>MaxConcurrentRuns</c> when N &gt; 1.
        /// </summary>
        [Fact]
        public async Task MultiSlot_NeverExceeds_MaxConcurrentRuns()
        {
            const int maxConcurrent = 3;

            var worker = new ScriptedRuntimeInstanceWorker(
                RunScript.Hold(), RunScript.Hold(), RunScript.Hold(),
                RunScript.Hold(), RunScript.Hold(), RunScript.Hold());

            var controller = CreateController(worker, maxConcurrentRuns: maxConcurrent);
            await controller.StartAsync(default).ConfigureAwait(false);

            for (var i = 0; i < 6; i++)
            {
                await EnqueueResumeAsync(controller).ConfigureAwait(false);
            }

            Assert.True(
                await worker.TryWaitForHoldingAsync(maxConcurrent, ExpectStart).ConfigureAwait(false),
                $"Fewer than {maxConcurrent} runs were admitted — the gate under-admitted.");

            Assert.False(
                await worker.TryWaitForHoldingAsync(maxConcurrent + 1, ExpectNoStart).ConfigureAwait(false),
                $"A {maxConcurrent + 1}th run overlapped — the gate admitted beyond {maxConcurrent}.");

            worker.ReleaseAll();
            await controller.StopAsync(default).ConfigureAwait(false);

            Assert.Equal(maxConcurrent, worker.MaxObservedConcurrency);
        }

        /// <summary>
        /// Race probe: burst many runs with a mix of self-draining outcomes (busy-complete, fault,
        /// park) at three slots, with no await-until-started barrier, so all three release paths run
        /// under real concurrent scheduling.
        /// </summary>
        /// <remarks>
        /// Over-release would let observed concurrency exceed the ceiling. Under-release (a leaked
        /// slot) would stall the drain so fewer than <c>total</c> runs are ever admitted. Repeated
        /// across iterations because a scheduling race may not reproduce on a single pass.
        /// </remarks>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public async Task MixedOutcomes_UnderConcurrency_ReleaseExactlyOnce(int iteration)
        {
            const int total = 24;
            const int maxConcurrent = 3;

            var scripts = new RunScript[total];
            for (var i = 0; i < total; i++)
            {
                scripts[i] = (i % 3) switch
                {
                    0 => RunScript.Busy(),   // completes after a short delay -> normal release
                    1 => RunScript.Park(),   // returns Waiting -> external-wait early release
                    _ => RunScript.Throw()   // faults -> continuation release
                };
            }

            var worker = new ScriptedRuntimeInstanceWorker(scripts);
            var controller = CreateController(
                worker,
                maxConcurrentRuns: maxConcurrent,
                queueCapacity: total + 8);

            await controller.StartAsync(default).ConfigureAwait(false);

            for (var i = 0; i < total; i++)
            {
                await EnqueueResumeAsync(controller).ConfigureAwait(false);
            }

            Assert.True(
                await worker.TryWaitForStartsAsync(total, DrainTimeout).ConfigureAwait(false),
                $"[iteration {iteration}] Only {worker.StartedCount}/{total} runs were admitted — a release path leaked a slot (under-release / stalled drain).");

            await controller.StopAsync(default).ConfigureAwait(false);

            Assert.True(
                worker.MaxObservedConcurrency <= maxConcurrent,
                $"[iteration {iteration}] Observed concurrency {worker.MaxObservedConcurrency} exceeded {maxConcurrent} — the gate over-released under load.");
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>
        /// Enqueues one existing-execution recovery resume with a unique durable identity.
        /// The recovery path intentionally bypasses DAG creation so this test remains focused on
        /// controller capacity ownership rather than execution-engine composition.
        /// </summary>
        private static async Task EnqueueResumeAsync(
            AiRuntimePipelineBackgroundController controller)
        {
            var ordinal = Interlocked.Increment(ref nextExecutionOrdinal);
            var executionId = $"capacity-gate-execution-{ordinal:D4}";
            var sharedRunId = $"capacity-gate-shared-run-{ordinal:D4}";
            var failedLocalRunId = $"capacity-gate-failed-local-run-{ordinal:D4}";
            var recoveryOwnerId =
                $"runtime-recovery:{executionId}:{sharedRunId}:{failedLocalRunId}";

            await controller
                .EnqueueResumeAsync(
                    new AiRuntimePipelineRunRequest
                    {
                        PipelineName = "pipeline-1",
                        ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                        PipelineDefinition = CreatePipelineDefinition(),
                        Metadata = new Dictionary<string, string>
                        {
                            ["recovery.mode"] = "resume-existing-execution",
                            ["recovery.forensicsId"] = recoveryOwnerId,
                            ["recovery.failedExecutionId"] = executionId,
                            ["recovery.failedRuntimeInstanceId"] = "runtime-instance-failed-capacity-gate",
                            ["recovery.failedLocalRunId"] = failedLocalRunId,
                            ["shared.run.id"] = sharedRunId
                        }
                    },
                    executionId,
                    default)
                .ConfigureAwait(false);
        }

        private static async Task EnqueueAndAwaitStartAsync(
            AiRuntimePipelineBackgroundController controller,
            ScriptedRuntimeInstanceWorker worker,
            int expectedStarted)
        {
            await EnqueueResumeAsync(controller).ConfigureAwait(false);

            Assert.True(
                await worker.TryWaitForStartsAsync(expectedStarted, ExpectStart).ConfigureAwait(false),
                $"Expected {expectedStarted} run(s) to have started within {ExpectStart}.");
        }

        private static AiRuntimePipelineBackgroundController CreateController(
            ScriptedRuntimeInstanceWorker worker,
            int maxConcurrentRuns,
            IAiRuntimeObservability? observability = null,
            int queueCapacity = 16)
        {
            // The controller requires a concrete AiDagExecutionEngine. The recovery-resume path used
            // by these tests never invokes runtime.Creator, so only the base engine dependencies must
            // be non-null. Recursive no-op proxies provide exactly that contract.
            var engine = new AiDagExecutionEngine(
                NullProxy.Create<IAiDagExecutionEngineServices>(),
                NullProxy.Create<IAiDagExecutionEngineRuntimeServices>());

            // The controller has two independent local capacity controls:
            //   1. _parallelismGate / MaxConcurrentRuns controls admitted pipeline runs.
            //   2. ReserveWorkersForExecutionAsync controls the local execution-worker budget.
            //
            // This test suite targets (1). Therefore (2) must be deliberately non-limiting.
            // Keep one worker per execution, but expose at least one worker slot beyond the
            // controller ceiling so any semaphore over-release can actually reach the scripted
            // worker and become observable as MaxObservedConcurrency > MaxConcurrentRuns.
            var workerBudget = Math.Max(2, maxConcurrentRuns + 1);

            return new AiRuntimePipelineBackgroundController(
                engine,
                worker,
                new ScriptedWorkerGroup(),
                new ScriptedWorkerFactory(worker),
                new StaticPipelineRunDefinitionResolver(),
                new NoopPipelineRunDefinitionPublisher(),
                NullProxy.Create<IAiRuntimePipelineRunLifecycleHook>(),
                new RecoveryExecutionControlService(),
                new TestRuntimeInstanceIdentity("runtime-instance-1"),
                NullProxy.Create<IAiRuntimeLogger>(),
                observability ?? NullProxy.Create<IAiRuntimeObservability>(),
                NullProxy.Create<IAiExecutionAssistanceCandidateStore>(),
                new InMemoryAiRuntimeRunExecutionIndex(),
                new TestExecutionContextAccessor(),
                Options.Create(new AiRuntimePipelineBackgroundControllerOptions
                {
                    QueueCapacity = queueCapacity,
                    MaxConcurrentRuns = maxConcurrentRuns,
                    MaxLocalWorkersPerExecution = 1,
                    RejectEnqueueWhenStopped = true,
                    Distributed = new AiRuntimeDistributedExecutionOptions
                    {
                        Enabled = true,
                        WorkerCount = workerBudget,
                        StopOnFirstTerminal = true,
                        TerminalObservationTimeout = TimeSpan.FromSeconds(5)
                    }
                }));
        }

        private static AiPipelineDefinition CreatePipelineDefinition()
        {
            return new AiPipelineDefinition
            {
                Name = "pipeline-1",
                ExecutionMode = AiExecutionMode.Dag,
                Version = "unit-test",
                Steps = new[]
                {
                    new AiPipelineStepDefinition { Name = "step-1", StepKey = "noop", Order = 0 }
                }
            };
        }

        private static ExecutionContextSnapshot CreateExecutionContextSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = $"unit-test-context-{Guid.NewGuid():N}",
                TenantId = "unit-test-tenant",
                TenantGroupId = "unit-test-tenant-group",
                Project = "deterministic-ai-runtime-tests",
                UserId = "unit-test-user",
                CurrentNamespace = "default",
                Namespaces = new List<NamespaceEntry>
                {
                    new()
                    {
                        Name = "default",
                        Trns = new HashSet<string>
                        {
                            "trn:deterministic-ai-runtime-tests:runtime:run:read",
                            "trn:deterministic-ai-runtime-tests:runtime:run:write",
                            "trn:deterministic-ai-runtime-tests:runtime:execution:read"
                        }
                    }
                },
                InFlightCount = 0,
                TtlSeconds = 300
            };
        }

        // ---- controller test doubles ----------------------------------------------------------

        /// <summary>
        /// Provides the recovery-owned execution-control transitions required by resume runs.
        /// State is kept per execution so an over-admission bug cannot be masked by the test fake.
        /// </summary>
        private sealed class RecoveryExecutionControlService : IAiExecutionControlService
        {
            private readonly ConcurrentDictionary<string, AiExecutionControlState> states =
                new(StringComparer.Ordinal);

            public Task<AiExecutionControlState> ResumeExecutionFromRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                ArgumentException.ThrowIfNullOrWhiteSpace(recoveryOwnerId);
                cancellationToken.ThrowIfCancellationRequested();

                var state = new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Resuming,
                    PendingAction = AiExecutionControlAction.Resume,
                    RequestedBy = recoveryOwnerId,
                    Reason = "capacity-gate unit-test recovery resume",
                    ResumeRequestedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                states[executionId] = state;
                return Task.FromResult(state);
            }

            public Task<AiExecutionControlState> MarkRunningAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                var state = new AiExecutionControlState
                {
                    ExecutionId = executionId,
                    Status = AiExecutionControlStatus.Running,
                    PendingAction = AiExecutionControlAction.None,
                    RequestedBy = requestedBy,
                    Reason = "capacity-gate unit-test recovery running",
                    UpdatedAtUtc = DateTime.UtcNow
                };

                states[executionId] = state;
                return Task.FromResult(state);
            }

            public Task<AiExecutionControlState?> GetStateAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                states.TryGetValue(executionId, out var state);
                return Task.FromResult(state);
            }

            public Task<AiExecutionControlState> PauseExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> ResumeExecutionAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> CancelExecutionAsync(
                string executionId,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> MarkCancelledAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> MarkWaitingForInputAsync(
                string executionId,
                string waitingKey,
                string? waitingStepName = null,
                string? reason = null,
                string? requestedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> SubmitHumanInputAsync(
                string executionId,
                string waitingKey,
                IReadOnlyDictionary<string, object?> input,
                string? submittedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlDecision> CheckCanAdvanceAsync(
                string executionId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> MarkPausedAsync(
                string executionId,
                string? requestedBy = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<AiExecutionControlState> PauseExecutionForRecoveryAsync(
                string executionId,
                string recoveryOwnerId,
                string? reason = null,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }

        /// <summary>
        /// Observability facade that delegates everything except the decision ledger recorder.
        /// </summary>
        private sealed class LedgerOverrideRuntimeObservability : IAiRuntimeObservability
        {
            private readonly IAiRuntimeObservability fallback =
                NullProxy.Create<IAiRuntimeObservability>();

            public LedgerOverrideRuntimeObservability(
                IAiDecisionLedgerRecorder ledger)
            {
                Ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            }

            public IAiRuntimeMetrics Metrics => fallback.Metrics;

            public IAiRuntimeTracer Tracer => fallback.Tracer;

            public IAiDecisionLedgerRecorder Ledger { get; }

            public IAiRuntimeCorrelationAccessor Correlation => fallback.Correlation;
        }

        /// <summary>
        /// Blocks only the Run.Suspended ledger event so the parked run remains active after its
        /// durable waiting transition. This makes the early-release boundary observable.
        /// </summary>
        private sealed class BlockingSuspensionDecisionLedgerRecorder : IAiDecisionLedgerRecorder
        {
            private readonly TaskCompletionSource<bool> suspensionRecordStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private readonly TaskCompletionSource<bool> releaseSuspensionRecord =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task SuspensionRecordStarted => suspensionRecordStarted.Task;

            public void ReleaseSuspensionRecord() =>
                releaseSuspensionRecord.TrySetResult(true);

            public async Task RecordAsync(
                AiRuntimeLedgerEventCorrelationContext context,
                AiDecisionLedgerCategory category,
                string eventType,
                AiDecisionLedgerOutcome outcome,
                string? reason = null,
                IReadOnlyDictionary<string, string>? metadata = null,
                CancellationToken cancellationToken = default)
            {
                if (!string.Equals(
                        eventType,
                        AiEngineEvents.Run.Suspended,
                        StringComparison.Ordinal))
                {
                    return;
                }

                suspensionRecordStarted.TrySetResult(true);

                await releaseSuspensionRecord.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // ---- scripted worker -----------------------------------------------------------------

        private enum RunKind { Complete, Hold, Throw, Park, Busy }

        private sealed class RunScript
        {
            private RunScript(RunKind kind)
            {
                Kind = kind;
            }

            public RunKind Kind { get; }

            public static RunScript Complete() => new(RunKind.Complete);

            public static RunScript Hold() => new(RunKind.Hold);

            public static RunScript Throw() => new(RunKind.Throw);

            public static RunScript Park() => new(RunKind.Park);

            public static RunScript Busy() => new(RunKind.Busy);
        }

        /// <summary>
        /// Worker fake that consumes one <see cref="RunScript"/> per invocation (in arrival order),
        /// tracks the maximum number of concurrently executing invocations, and — for
        /// <see cref="RunKind.Hold"/> — blocks until the test releases it, thereby occupying a
        /// concurrency slot for the duration. <see cref="RunKind.Busy"/> holds a slot for a short
        /// randomized delay so runs overlap under load and then self-drain.
        /// </summary>
        private sealed class ScriptedRuntimeInstanceWorker : IAiRuntimeInstanceWorker
        {
            private readonly ConcurrentQueue<RunScript> scripts;
            private readonly TaskCompletionSource releaseAll =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int started;
            private int holding;
            private int current;
            private int maxObserved;

            public ScriptedRuntimeInstanceWorker(params RunScript[] scripts)
            {
                this.scripts = new ConcurrentQueue<RunScript>(scripts);
            }

            public int MaxObservedConcurrency => Volatile.Read(ref maxObserved);

            public int StartedCount => Volatile.Read(ref started);

            public void ReleaseAll() => releaseAll.TrySetResult();

            public async Task<AiExecutionRecord> RunExecutionAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

                var now = Interlocked.Increment(ref current);
                UpdateMax(now);
                Interlocked.Increment(ref started);

                try
                {
                    var script = scripts.TryDequeue(out var s) ? s : RunScript.Complete();

                    switch (script.Kind)
                    {
                        case RunKind.Throw:
                            throw new InvalidOperationException(
                                $"Scripted fault for execution '{executionId}'.");

                        case RunKind.Hold:
                            Interlocked.Increment(ref holding);
                            try
                            {
                                await releaseAll.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref holding);
                            }

                            return Record(executionId, AiExecutionStatus.Completed);

                        case RunKind.Busy:
                            await Task.Delay(Random.Shared.Next(15, 45), cancellationToken)
                                .ConfigureAwait(false);
                            return Record(executionId, AiExecutionStatus.Completed);

                        case RunKind.Park:
                            return Record(executionId, AiExecutionStatus.Waiting);

                        default:
                            return Record(executionId, AiExecutionStatus.Completed);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref current);
                }
            }

            public Task<bool> TryWaitForStartsAsync(int count, TimeSpan timeout) =>
                WaitForAsync(() => Volatile.Read(ref started) >= count, timeout);

            public Task<bool> TryWaitForHoldingAsync(int count, TimeSpan timeout) =>
                WaitForAsync(() => Volatile.Read(ref holding) >= count, timeout);

            private static async Task<bool> WaitForAsync(Func<bool> predicate, TimeSpan timeout)
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    if (predicate())
                    {
                        return true;
                    }

                    await Task.Delay(15).ConfigureAwait(false);
                }

                return predicate();
            }

            private void UpdateMax(int candidate)
            {
                int observed;
                do
                {
                    observed = Volatile.Read(ref maxObserved);
                    if (candidate <= observed)
                    {
                        return;
                    }
                }
                while (Interlocked.CompareExchange(ref maxObserved, candidate, observed) != observed);
            }

            private static AiExecutionRecord Record(string executionId, AiExecutionStatus status) =>
                new()
                {
                    ExecutionId = executionId,
                    PipelineName = "pipeline-1",
                    Status = status,
                    CompletedAtUtc = status == AiExecutionStatus.Completed ? DateTime.UtcNow : default
                };
        }

        private sealed class ScriptedWorkerGroup : IAiRuntimeInstanceWorkerGroup
        {
            public Task<AiExecutionRecord> RunExecutionAsync(
                string executionId,
                IReadOnlyCollection<IAiRuntimeInstanceWorker> workers,
                CancellationToken cancellationToken = default) =>
                workers.First().RunExecutionAsync(executionId, cancellationToken);
        }

        private sealed class ScriptedWorkerFactory : IAiRuntimeInstanceWorkerFactory
        {
            private readonly IAiRuntimeInstanceWorker worker;

            public ScriptedWorkerFactory(IAiRuntimeInstanceWorker worker) => this.worker = worker;

            public IReadOnlyCollection<IAiRuntimeInstanceWorker> CreateWorkers(int workerCount) =>
                Enumerable.Range(0, Math.Max(1, workerCount)).Select(_ => worker).ToArray();
        }

        private sealed class StaticPipelineRunDefinitionResolver : IAiRuntimePipelineRunDefinitionResolver
        {
            public Task<AiPipelineDefinition> ResolveAsync(
                AiRuntimePipelineRunRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(request.PipelineDefinition ?? CreatePipelineDefinition());
            }
        }

        private sealed class NoopPipelineRunDefinitionPublisher : IAiRuntimePipelineRunDefinitionPublisher
        {
            public Task PublishAsync(
                AiPipelineDefinition definition,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(definition);
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }

        private sealed class TestRuntimeInstanceIdentity : IAiRuntimeInstanceIdentityDescriptor
        {
            public TestRuntimeInstanceIdentity(string runtimeInstanceId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(runtimeInstanceId);
                RuntimeInstanceId = runtimeInstanceId;
            }

            public string RuntimeInstanceId { get; }

            public string HostName => "unit-test-host";

            public int ProcessId => Environment.ProcessId;

            public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        }

        private sealed class TestExecutionContextAccessor : IExecutionContextAccessor
        {
            public Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext? Current { get; private set; }

            public void Set(Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext context) =>
                Current = context;

            public void Clear() => Current = null;
        }

        /// <summary>
        /// Dynamic no-op proxy factory used only for interfaces that are irrelevant to this test.
        /// Recursively returns non-null proxies for interface-valued members and executes tracing
        /// callbacks so the worker is actually invoked through the traced execution path.
        /// </summary>
        private static class NullProxy
        {
            public static T Create<T>()
                where T : class => DispatchProxy.Create<T, NullDispatchProxy>();

            public static object? Create(Type type)
            {
                var method = typeof(NullProxy)
                    .GetMethod(
                        nameof(Create),
                        BindingFlags.Public | BindingFlags.Static,
                        Type.EmptyTypes)!
                    .MakeGenericMethod(type);

                return method.Invoke(null, null);
            }
        }

        private class NullDispatchProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null)
                {
                    return null;
                }

                var returnType = targetMethod.ReturnType;

                if (string.Equals(
                        targetMethod.Name,
                        "TraceExecutionAsync",
                        StringComparison.Ordinal) &&
                    args is not null)
                {
                    var callback = args.OfType<Delegate>().FirstOrDefault();
                    if (callback is not null)
                    {
                        return callback.DynamicInvoke();
                    }
                }

                if (returnType == typeof(void))
                {
                    return null;
                }

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                if (returnType.IsGenericType &&
                    returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultType = returnType.GetGenericArguments()[0];
                    var fromResult = typeof(Task)
                        .GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(resultType);

                    return fromResult.Invoke(null, new[] { GetDefaultValue(resultType) });
                }

                if (returnType == typeof(ValueTask))
                {
                    return default(ValueTask);
                }

                if (returnType.IsGenericType &&
                    returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    return Activator.CreateInstance(
                        returnType,
                        GetDefaultValue(returnType.GetGenericArguments()[0]));
                }

                if (returnType.IsInterface)
                {
                    return NullProxy.Create(returnType);
                }

                if (returnType == typeof(bool))
                {
                    return false;
                }

                if (returnType == typeof(string))
                {
                    return string.Empty;
                }

                return GetDefaultValue(returnType);
            }

            private static object? GetDefaultValue(Type type) =>
                type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
