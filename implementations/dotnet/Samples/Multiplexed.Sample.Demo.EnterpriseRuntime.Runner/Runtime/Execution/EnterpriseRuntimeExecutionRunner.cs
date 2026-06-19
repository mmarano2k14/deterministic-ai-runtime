using Microsoft.Extensions.DependencyInjection;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Models;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Observability.Metrics;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Control;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Persistence;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Progress;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Replay;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Retention;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Retry;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Throttling;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution.Validation;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Scenarios;
using Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Scenarios.Chaos;

using RbacExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.Sample.Demo.EnterpriseRuntime.Runner.Runtime.Execution
{
    /// <summary>
    /// Executes enterprise runtime pipeline requests.
    /// </summary>
    public sealed class EnterpriseRuntimeExecutionRunner
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly EnterpriseRuntimeExecutionReporter _reporter;
        private readonly EnterpriseRuntimeExecutionPersistenceLoader _persistenceLoader;
        private readonly EnterpriseRuntimeRetryAnalyzer _retryAnalyzer;
        private readonly EnterpriseRuntimeRetentionAnalyzer _retentionAnalyzer;
        private readonly EnterpriseRuntimeThrottlingAnalyzer _throttlingAnalyzer;
        private readonly EnterpriseRuntimeExecutionProgressMonitor _progressMonitor;
        private readonly EnterpriseRuntimeExecutionControlHotkeyListener _controlHotkeyListener;

        /// <summary>
        /// Initializes a new instance of the <see cref="EnterpriseRuntimeExecutionRunner"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The service provider.
        /// </param>
        /// <param name="reporter">
        /// The execution reporter.
        /// </param>
        /// <param name="persistenceLoader">
        /// The execution persistence loader.
        /// </param>
        /// <param name="retryAnalyzer">
        /// The execution retry analyzer.
        /// </param>
        /// <param name="retentionAnalyzer">
        /// The execution retention analyzer.
        /// </param>
        /// <param name="throttlingAnalyzer">
        /// The execution throttling analyzer.
        /// </param>
        /// <param name="progressMonitor">
        /// The execution progress monitor.
        /// </param>
        /// <param name="controlHotkeyListener">
        /// The execution control hotkey listener.
        /// </param>
        public EnterpriseRuntimeExecutionRunner(
            IServiceProvider serviceProvider,
            EnterpriseRuntimeExecutionReporter reporter,
            EnterpriseRuntimeExecutionPersistenceLoader persistenceLoader,
            EnterpriseRuntimeRetryAnalyzer retryAnalyzer,
            EnterpriseRuntimeRetentionAnalyzer retentionAnalyzer,
            EnterpriseRuntimeThrottlingAnalyzer throttlingAnalyzer,
            EnterpriseRuntimeExecutionProgressMonitor progressMonitor,
            EnterpriseRuntimeExecutionControlHotkeyListener controlHotkeyListener)
        {
            _serviceProvider = serviceProvider;
            _reporter = reporter;
            _persistenceLoader = persistenceLoader;
            _retryAnalyzer = retryAnalyzer;
            _retentionAnalyzer = retentionAnalyzer;
            _throttlingAnalyzer = throttlingAnalyzer;
            _progressMonitor = progressMonitor;
            _controlHotkeyListener = controlHotkeyListener;
        }

        /// <summary>
        /// Runs an enterprise runtime execution request.
        /// </summary>
        /// <param name="request">
        /// The execution request.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The scenario execution result.
        /// </returns>
        public async Task<EnterpriseRuntimeScenarioResult> RunAsync(
            EnterpriseRuntimeExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            var startedAtUtc = DateTime.UtcNow;

            var controller = _serviceProvider
                .GetRequiredService<IAiRuntimePipelineBackgroundController>();

            var dagStore = _serviceProvider
                .GetRequiredService<IAiDagExecutionStore>();

            var resolver = _serviceProvider
                .GetRequiredService<IAiExecutionStepResolver>();

            var metrics = _serviceProvider
                .GetRequiredService<IAiRuntimeMetrics>();

            AiRuntimeWorkerRunHandle? handle = null;

            try
            {
                Console.WriteLine("Starting background controller...");

                await controller.StartAsync()
                    .ConfigureAwait(false);

                Console.WriteLine("Enqueuing enterprise runtime execution...");
                Console.WriteLine();

                var executionContextSnapshot = CreateExecutionContextSnapshot(
                    request.PipelineName);

                handle = await EnqueueAsync(
                        controller,
                        request,
                        executionContextSnapshot,
                        cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine($"RunId: {handle.RunId}");
                Console.WriteLine("ExecutionId: waiting for execution creation...");
                Console.WriteLine();

                using var runnerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

                using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

                var progressTask = _progressMonitor.MonitorAsync(
                    handle,
                    request,
                    dagStore,
                    metrics,
                    progressCancellation.Token);

                using var controlCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

                var controlTask = _controlHotkeyListener.ListenAsync(
                    handle,
                    runnerCancellation,
                    controlCancellation.Token);

                AiExecutionRecord final;

                try
                {
                    final = await handle.Completion.WaitAsync(
                            request.Timeout,
                            runnerCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    runnerCancellation.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    await progressCancellation.CancelAsync()
                        .ConfigureAwait(false);

                    await SuppressProgressMonitorCancellationAsync(
                            progressTask)
                        .ConfigureAwait(false);

                    await controlCancellation.CancelAsync()
                        .ConfigureAwait(false);

                    await SuppressControlListenerCancellationAsync(
                            controlTask)
                        .ConfigureAwait(false);

                    Console.WriteLine();

                    return EnterpriseRuntimeScenarioResult.Completed(
                        DateTime.UtcNow - startedAtUtc,
                        $"Scenario '{request.ScenarioName}' cancelled by console.");
                }

                await progressCancellation.CancelAsync()
                    .ConfigureAwait(false);

                await SuppressProgressMonitorCancellationAsync(
                        progressTask)
                    .ConfigureAwait(false);

                await controlCancellation.CancelAsync()
                    .ConfigureAwait(false);

                await SuppressControlListenerCancellationAsync(
                        controlTask)
                    .ConfigureAwait(false);

                Console.WriteLine();

                if (string.IsNullOrWhiteSpace(
                        handle.ExecutionId))
                {
                    throw new InvalidOperationException(
                        "The execution completed without producing an ExecutionId.");
                }

                var persistedRecord = await _persistenceLoader.LoadPersistedRecordAsync(
                        dagStore,
                        handle.ExecutionId)
                    .ConfigureAwait(false);

                var persistedState = await _persistenceLoader.LoadPersistedStateAsync(
                        dagStore,
                        handle.ExecutionId)
                    .ConfigureAwait(false);

                EnterpriseRuntimeRetentionValidator.ValidateHotStateLimit(
                    persistedState,
                    request);

                var retentionSummary = _retentionAnalyzer.Analyze(
                    persistedState,
                    request);

                var retrySummary = await _retryAnalyzer.AnalyzeAsync(
                        resolver,
                        handle.ExecutionId,
                        persistedState,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);

                var workerCycles = metrics.Worker.GetCyclesByRuntimeInstance();

                var throttlingSummary = _throttlingAnalyzer.Analyze(
                    persistedState,
                    request,
                    workerCycles.Count);

                EnterpriseRuntimeExecutionValidator.Validate(
                    final,
                    persistedRecord,
                    workerCycles.Count,
                    retrySummary.MinimumRetryCount,
                    request);

                _reporter.PrintExecutionSummary(
                    handle,
                    final);

                _reporter.PrintWorkerSummary(
                    workerCycles);

                _reporter.PrintRetrySummary(
                    request,
                    retrySummary);

                _reporter.PrintThrottlingSummary(
                    throttlingSummary);

                _reporter.PrintRetentionSummary(
                    retentionSummary);

                _reporter.PrintValidationSummary();

                if (request.ValidateReplay && request.ChaosScenario is not null)
                {
                    await ValidateReplayAsync(
                            request,
                            handle.ExecutionId,
                            persistedRecord,
                            persistedState,
                            resolver,
                            dagStore,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                return EnterpriseRuntimeScenarioResult.Completed(
                    DateTime.UtcNow - startedAtUtc,
                    $"Scenario '{request.ScenarioName}' completed.");
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("Stopping background controller...");

                await controller.StopAsync()
                    .ConfigureAwait(false);

                if (request.CleanupExecutionBundle &&
                    !string.IsNullOrWhiteSpace(
                        handle?.ExecutionId))
                {
                    Console.WriteLine("Cleaning up execution bundle...");

                    await dagStore.DeleteExecutionBundleAsync(
                            handle.ExecutionId)
                        .ConfigureAwait(false);
                }

                Console.WriteLine("Execution runner finished.");
            }
        }

        /// <summary>
        /// Enqueues an enterprise runtime pipeline execution.
        /// </summary>
        /// <param name="controller">
        /// The background controller.
        /// </param>
        /// <param name="request">
        /// The execution request.
        /// </param>
        /// <param name="executionContextSnapshot">
        /// The durable execution context snapshot to persist with the runtime run.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The created worker run handle.
        /// </returns>
        private static async Task<AiRuntimeWorkerRunHandle> EnqueueAsync(
            IAiRuntimePipelineBackgroundController controller,
            EnterpriseRuntimeExecutionRequest request,
            ExecutionContextSnapshot executionContextSnapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                controller);

            ArgumentNullException.ThrowIfNull(
                request);

            ArgumentNullException.ThrowIfNull(
                executionContextSnapshot);

            var pipelineRequest = request.PipelineInput switch
            {
                { PipelineJsonFilePath: not null } => new AiRuntimePipelineRunRequest
                {
                    PipelineName = request.PipelineName,
                    PipelineJsonFilePath = request.PipelineInput.PipelineJsonFilePath,
                    Input = request.Input,
                    ExecutionContextSnapshot = executionContextSnapshot
                },

                { PipelineJsonText: not null } => new AiRuntimePipelineRunRequest
                {
                    PipelineName = request.PipelineName,
                    PipelineJson = request.PipelineInput.PipelineJsonText,
                    Input = request.Input,
                    ExecutionContextSnapshot = executionContextSnapshot
                },

                { PipelineDefinition: not null } => new AiRuntimePipelineRunRequest
                {
                    PipelineName = request.PipelineName,
                    PipelineDefinition = request.PipelineInput.PipelineDefinition,
                    Input = request.Input,
                    ExecutionContextSnapshot = executionContextSnapshot
                },

                _ => throw new InvalidOperationException(
                    "The execution request does not contain a valid pipeline input.")
            };

            return await controller.EnqueueAsync(
                    pipelineRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Creates the durable execution context snapshot used by the enterprise runtime demo.
        /// </summary>
        /// <param name="pipelineName">
        /// The pipeline name.
        /// </param>
        /// <returns>
        /// The durable execution context snapshot.
        /// </returns>
        private ExecutionContextSnapshot CreateExecutionContextSnapshot(
            string pipelineName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                pipelineName);

            var executionContextAccessor = _serviceProvider
                .GetRequiredService<IExecutionContextAccessor>();

            var current = executionContextAccessor.Current;

            if (current is null)
            {
                throw new InvalidOperationException(
                    $"No RBAC execution context is available for enterprise runtime demo pipeline '{pipelineName}'.");
            }

            return CreateExecutionContextSnapshot(
                pipelineName,
                current);
        }

        /// <summary>
        /// Creates a durable runtime execution context snapshot from the current RBAC execution context.
        /// </summary>
        /// <param name="pipelineName">
        /// The pipeline name.
        /// </param>
        /// <param name="context">
        /// The current RBAC execution context.
        /// </param>
        /// <returns>
        /// The durable execution context snapshot.
        /// </returns>
        private static ExecutionContextSnapshot CreateExecutionContextSnapshot(
            string pipelineName,
            RbacExecutionContext context)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(
                pipelineName);

            ArgumentNullException.ThrowIfNull(
                context);

            var tenantId = string.IsNullOrWhiteSpace(
                    context.TenantId)
                ? "test-tenant"
                : context.TenantId;

            return new ExecutionContextSnapshot
            {
                ContextKey = string.IsNullOrWhiteSpace(
                        context.ContextKey)
                    ? $"ctx:{pipelineName}:enterprise-demo"
                    : context.ContextKey,

                Project = string.IsNullOrWhiteSpace(
                        context.Project)
                    ? "enterprise-runtime-demo"
                    : context.Project,

                UserId = string.IsNullOrWhiteSpace(
                        context.UserId)
                    ? "enterprise-runtime-demo"
                    : context.UserId,

                TenantId = tenantId,

                TenantGroupId = string.IsNullOrWhiteSpace(
                        context.TenantGroupId)
                    ? tenantId
                    : context.TenantGroupId,

                CurrentNamespace = string.IsNullOrWhiteSpace(
                        context.CurrentNamespace)
                    ? "enterprise-runtime-demo"
                    : context.CurrentNamespace,

                Namespaces = CreateNamespaceSnapshots(
                    context)
            };
        }

        /// <summary>
        /// Creates snapshot namespace entries from the RBAC execution context.
        /// </summary>
        /// <param name="context">
        /// The RBAC execution context.
        /// </param>
        /// <returns>
        /// The namespace snapshot entries.
        /// </returns>
        private static List<NamespaceEntry> CreateNamespaceSnapshots(
            RbacExecutionContext context)
        {
            ArgumentNullException.ThrowIfNull(
                context);

            if (context.Namespaces is not null &&
                context.Namespaces.Any())
            {
                return context.Namespaces
                    .Select(
                        ns => new NamespaceEntry
                        {
                            Name = ns.Name,
                            Trns = ns.Trns is null
                                ? new HashSet<string>()
                                : new HashSet<string>(
                                    ns.Trns)
                        })
                    .ToList();
            }

            return new List<NamespaceEntry>
            {
                new()
                {
                    Name = "enterprise-runtime-demo",
                    Trns = new HashSet<string>
                    {
                        "trn:enterprise-runtime-demo:execution:run",
                        "trn:enterprise-runtime-demo:execution:read",
                        "trn:enterprise-runtime-demo:execution:cancel",
                        "trn:enterprise-runtime-demo:execution:control",
                        "trn:enterprise-runtime-demo:replay:read",
                        "trn:enterprise-runtime-demo:observability:read"
                    }
                }
            };
        }

        /// <summary>
        /// Suppresses expected progress monitor cancellation.
        /// </summary>
        /// <param name="progressTask">
        /// The progress monitor task.
        /// </param>
        private static async Task SuppressProgressMonitorCancellationAsync(
            Task progressTask)
        {
            try
            {
                await progressTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when execution completes and the progress monitor is stopped.
            }
        }

        /// <summary>
        /// Suppresses expected control listener cancellation.
        /// </summary>
        /// <param name="controlTask">
        /// The control listener task.
        /// </param>
        private static async Task SuppressControlListenerCancellationAsync(
            Task controlTask)
        {
            try
            {
                await controlTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when execution completes and the control listener is stopped.
            }
        }

        /// <summary>
        /// Validates snapshot persistence, replay restoration, and replay fingerprint consistency.
        /// </summary>
        /// <param name="request">
        /// The execution request.
        /// </param>
        /// <param name="executionId">
        /// The execution identifier.
        /// </param>
        /// <param name="persistedRecord">
        /// The persisted execution record captured before replay.
        /// </param>
        /// <param name="persistedState">
        /// The persisted execution state captured before replay.
        /// </param>
        /// <param name="resolver">
        /// The execution step resolver.
        /// </param>
        /// <param name="dagStore">
        /// The DAG execution store.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        private async Task ValidateReplayAsync(
            EnterpriseRuntimeExecutionRequest request,
            string executionId,
            AiExecutionRecord persistedRecord,
            AiExecutionState persistedState,
            IAiExecutionStepResolver resolver,
            IAiDagExecutionStore dagStore,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(
                request);

            ArgumentException.ThrowIfNullOrWhiteSpace(
                executionId);

            ArgumentNullException.ThrowIfNull(
                persistedRecord);

            ArgumentNullException.ThrowIfNull(
                persistedState);

            ArgumentNullException.ThrowIfNull(
                resolver);

            ArgumentNullException.ThrowIfNull(
                dagStore);

            if (request.ChaosScenario is null)
            {
                return;
            }

            var finalizedHook = _serviceProvider
                .GetRequiredService<DistributedChaosRunFinalizedHook>();

            var replayService = _serviceProvider
                .GetRequiredService<IAiExecutionReplayService>();

            var snapshotStore = _serviceProvider
                .GetRequiredService<IAiExecutionSnapshotStore<ExecutionContextSnapshot>>();

            Console.WriteLine();
            Console.WriteLine("Replay validation");
            Console.WriteLine("-----------------");

            var finalized = await finalizedHook.WaitAsync(
                    request.ChaosScenario.SnapshotWaitTimeout)
                .ConfigureAwait(false);

            if (!string.Equals(
                    executionId,
                    finalized.ExecutionId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Finalized execution mismatch. Expected='{executionId}', Actual='{finalized.ExecutionId}'.");
            }

            var snapshot = await snapshotStore.GetAsync(
                    executionId)
                .ConfigureAwait(false);

            if (snapshot is null)
            {
                throw new InvalidOperationException(
                    $"Expected terminal snapshot for execution '{executionId}', but no snapshot was found.");
            }

            await resolver.WarmAsync(
                    executionId,
                    persistedState,
                    cancellationToken)
                .ConfigureAwait(false);

            var beforeReplayFingerprint =
                await EnterpriseRuntimeReplayValidator.CreateFingerprintAsync(
                        request.ChaosScenario,
                        executionId,
                        persistedRecord,
                        persistedState,
                        resolver,
                        cancellationToken)
                    .ConfigureAwait(false);

            await dagStore.DeleteExecutionBundleAsync(
                    executionId)
                .ConfigureAwait(false);

            var deletedRecord = await dagStore.GetRecordAsync(
                    executionId)
                .ConfigureAwait(false);

            var deletedState = await dagStore.GetStateAsync(
                    executionId)
                .ConfigureAwait(false);

            if (deletedRecord is not null || deletedState is not null)
            {
                throw new InvalidOperationException(
                    $"Expected live execution bundle '{executionId}' to be deleted before replay.");
            }

            var replayResult = await replayService.ReplayAsync(
                    new AiExecutionReplayRequest
                    {
                        ExecutionId = executionId
                    })
                .ConfigureAwait(false);

            if (!replayResult.ReplayValid)
            {
                throw new InvalidOperationException(
                    $"Expected replay to restore execution '{executionId}'.");
            }

            if (!replayResult.ExecutionFound)
            {
                throw new InvalidOperationException(
                    $"Expected replay to find execution '{executionId}'.");
            }

            if (!replayResult.SnapshotFound)
            {
                throw new InvalidOperationException(
                    $"Expected replay snapshot to exist for execution '{executionId}'.");
            }

            var restoredRecord = await _persistenceLoader.LoadPersistedRecordAsync(
                    dagStore,
                    executionId)
                .ConfigureAwait(false);

            var restoredState = await _persistenceLoader.LoadPersistedStateAsync(
                    dagStore,
                    executionId)
                .ConfigureAwait(false);

            await resolver.WarmAsync(
                    executionId,
                    restoredState,
                    cancellationToken)
                .ConfigureAwait(false);

            var afterReplayFingerprint =
                await EnterpriseRuntimeReplayValidator.CreateFingerprintAsync(
                        request.ChaosScenario,
                        executionId,
                        restoredRecord,
                        restoredState,
                        resolver,
                        cancellationToken)
                    .ConfigureAwait(false);

            EnterpriseRuntimeReplayValidator.ValidateMatch(
                beforeReplayFingerprint,
                afterReplayFingerprint);

            Console.WriteLine("Snapshot created: true");
            Console.WriteLine("Replay restored: true");
            Console.WriteLine("Fingerprint match: true");
            Console.WriteLine();
        }
    }
}