using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;
using System.Collections.Concurrent;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution
{
    /// <summary>
    /// Base class for AI execution engines.
    ///
    /// Responsibilities:
    /// - provide shared runtime dependencies
    /// - load persisted execution record and state
    /// - load the live RBAC execution context
    /// - create the global AI execution context
    ///
    /// Derived classes are responsible for:
    /// - execution mode validation
    /// - step scheduling
    /// - step execution strategy
    /// - context rotation policy
    /// - execution progression semantics
    /// </summary>
    public abstract class AiExecutionEngine : IAiExecutionEngine
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiExecutionEngine"/> class.
        /// </summary>
        protected AiExecutionEngine(
            IAiExecutionStore store,
            IContextStore contextStore,
            IExecutionContextAccessor accessor,
            IExecutionContextFactory contextFactory,
            IServiceProvider services,
            IAiSequentialPipelineExecutor pipelineExecutor,
            IAiRuntimeLogger logger,
            IAiExecutionStateReader stateReader,
            IAiExecutionStateWriter stateWriter)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(contextStore);
            ArgumentNullException.ThrowIfNull(accessor);
            ArgumentNullException.ThrowIfNull(contextFactory);
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(pipelineExecutor);
            ArgumentNullException.ThrowIfNull(logger);
            ArgumentNullException.ThrowIfNull(stateReader);
            ArgumentNullException.ThrowIfNull(stateWriter);

            Store = store;
            ContextStore = contextStore;
            Accessor = accessor;
            ContextFactory = contextFactory;
            Services = services;
            PipelineExecutor = pipelineExecutor;
            Logger = logger;
            StateReader = stateReader;
            StateWriter = stateWriter;
        }

        private readonly ConcurrentDictionary<string, ExecutionContext> _restoredExecutionContextsByExecutionId =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the durable AI execution store.
        /// </summary>
        protected IAiExecutionStore Store { get; }

        /// <summary>
        /// Gets the RBAC execution context store.
        /// </summary>
        protected IContextStore ContextStore { get; }

        /// <summary>
        /// Gets the live RBAC execution context accessor.
        /// </summary>
        protected IExecutionContextAccessor Accessor { get; }

        /// <summary>
        /// Gets the RBAC execution context factory.
        /// </summary>
        protected IExecutionContextFactory ContextFactory { get; }

        /// <summary>
        /// Gets the root runtime service provider.
        /// </summary>
        protected IServiceProvider Services { get; }

        /// <summary>
        /// Gets the pipeline executor.
        /// </summary>
        protected IAiSequentialPipelineExecutor PipelineExecutor { get; }

        /// <summary>
        /// Gets the centralized runtime logger.
        /// </summary>
        protected IAiRuntimeLogger Logger { get; }

        /// <summary>
        /// Gets the payload-aware execution state reader.
        /// </summary>
        protected IAiExecutionStateReader StateReader { get; }

        /// <summary>
        /// Gets the execution state writer.
        /// </summary>
        protected IAiExecutionStateWriter StateWriter { get; }

        /// <summary>
        /// Creates a new execution without an explicit pipeline name.
        /// This overload is intentionally unsupported.
        /// </summary>
        public virtual Task<AiExecutionRecord> CreateAsync(
            string input,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "CreateAsync(input) is no longer supported without an explicit pipeline name.");
        }

        /// <summary>
        /// Creates a new execution for the specified pipeline.
        /// Must be implemented by the derived engine.
        /// </summary>
        public abstract Task<AiExecutionRecord> CreateAsync(
            string pipelineName,
            string input,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a new execution for the specified pipeline.
        /// Must be implemented by the derived engine.
        /// </summary>
        public abstract Task<AiExecutionRecord> CreateAsync(
            string pipelineName,
            IDictionary<string, object?> input,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes the next unit of work for the specified execution.
        /// Must be implemented by the derived engine.
        /// </summary>
        public abstract Task<AiExecutionRecord> ExecuteNextAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes one or more ready units of work for the specified execution.
        /// Must be implemented by the derived engine.
        /// </summary>
        /// <param name="executionId">
        /// The unique execution identifier.
        /// </param>
        /// <param name="maxSteps">
        /// The maximum number of ready steps to execute.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The updated execution record after the batch execution attempt.
        /// </returns>
        public abstract Task<AiExecutionRecord> ExecuteBatchAsync(
            string executionId,
            int maxSteps,
            CancellationToken cancellationToken = default);


        /// <summary>
        /// Executes the remaining work until a terminal state is reached.
        /// Must be implemented by the derived engine.
        /// </summary>
        public abstract Task<AiExecutionRecord> ExecuteAllAsync(
            string executionId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates the supplied execution identifier.
        /// </summary>
        protected static void ValidateExecutionId(string executionId)
        {
            if (string.IsNullOrWhiteSpace(executionId))
            {
                throw new ArgumentException("Execution id cannot be null or empty.", nameof(executionId));
            }
        }

        /// <summary>
        /// Loads the persisted execution record and mutable execution state.
        /// </summary>
        protected async Task<(AiExecutionRecord Record, AiExecutionState State)> LoadExecutionAsync(
            string executionId,
            CancellationToken cancellationToken)
        {
            var record = await Store.GetRecordAsync(executionId, cancellationToken)
                ?? throw new InvalidOperationException("Execution not found.");

            var state = await Store.GetStateAsync(executionId, cancellationToken)
                ?? throw new InvalidOperationException("Execution state not found.");

            return (record, state);
        }

        /// <summary>
        /// Loads the live RBAC execution context associated with the supplied context key.
        /// </summary>
        protected async Task<ExecutionContext> LoadContextAsync(string contextKey)
        {
            var context = await ContextStore.GetAsync(contextKey);

            if (context is null)
            {
                throw new InvalidOperationException("RBAC execution context not found.");
            }

            return context;
        }

        /// <summary>
        /// Builds the global AI execution context used during orchestration.
        /// </summary>
        protected AiExecutionContext BuildExecutionContext(
            AiExecutionRecord record,
            AiExecutionState state,
            CancellationToken cancellationToken)
        {
            return new AiExecutionContext(
                record,
                state,
                Services,
                StateReader,
                StateWriter,
                cancellationToken);
        }

        /// <summary>
        /// Ensures the execution defines a pipeline name.
        /// </summary>
        protected static void EnsurePipelineName(AiExecutionRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.PipelineName))
            {
                throw new InvalidOperationException(
                    $"Execution '{record.ExecutionId}' does not define a pipeline name.");
            }
        }

        /// <summary>
        /// Persists an updated execution record and state using optimistic concurrency.
        /// </summary>
        protected async Task PersistAsync(
            AiExecutionRecord record,
            string expectedStepKey,
            AiExecutionState state,
            CancellationToken cancellationToken)
        {
            var updated = await Store.TryUpdateAsync(
                record.ExecutionId,
                expectedStepKey,
                record,
                state,
                cancellationToken);

            if (!updated)
            {
                throw new InvalidOperationException("Concurrency conflict on execution update.");
            }
        }

        /// <summary>
        /// Seeds a restored RBAC execution context into the engine-owned context store.
        /// </summary>
        /// <param name="context">The restored RBAC execution context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public Task SeedRestoredExecutionContextAsync(
            ExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            return SeedRestoredExecutionContextAsync(
                executionId: null,
                context,
                cancellationToken);
        }

        /// <summary>
        /// Seeds a restored RBAC execution context into the engine-owned context store and
        /// binds it to the durable execution identifier being resumed.
        /// </summary>
        /// <param name="executionId">The durable execution identifier being resumed.</param>
        /// <param name="context">The restored RBAC execution context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task SeedRestoredExecutionContextAsync(
            string? executionId,
            ExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(context.ContextKey))
            {
                throw new InvalidOperationException(
                    "Cannot seed restored execution context because ContextKey is missing.");
            }

            var stableContext =
                CloneExecutionContext(
                    context,
                    context.ContextKey);

            await ContextStore
                .SeedAsync(stableContext)
                .ConfigureAwait(false);

            Accessor.Set(
                stableContext);

            if (!string.IsNullOrWhiteSpace(executionId))
            {
                _restoredExecutionContextsByExecutionId[executionId] =
                    stableContext;
            }

            Logger.Engine.LogInformation(
                $"[AI EXECUTION] Restored RBAC execution context seeded. ExecutionId='{executionId ?? string.Empty}', ContextKey='{stableContext.ContextKey}', TenantId='{stableContext.TenantId}', UserId='{stableContext.UserId}'.");
        }

        /// <summary>
        /// Loads the RBAC execution context for the supplied execution and context key.
        /// </summary>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="contextKey">The context key requested by the execution record or DAG state.</param>
        /// <returns>The RBAC execution context.</returns>
        protected async Task<ExecutionContext> LoadContextForExecutionAsync(
            string executionId,
            string contextKey)
        {
            try
            {
                return await LoadContextAsync(
                        contextKey)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (
                IsRbacExecutionContextNotFound(exception))
            {
                ExecutionContext sourceContext;
                string source;

                if (TryGetRestoredExecutionContext(
                        executionId,
                        out var restoredContext))
                {
                    sourceContext = restoredContext;
                    source = "restored-execution-context";
                }
                else
                {
                    var record =
                        await Store
                            .GetRecordAsync(executionId)
                            .ConfigureAwait(false);

                    var snapshot = record?.ExecutionContextSnapshot;

                    if (snapshot is null ||
                        string.IsNullOrWhiteSpace(snapshot.TenantId))
                    {
                        throw new InvalidOperationException(
                            $"RBAC execution context not found and durable execution context snapshot is unavailable. ExecutionId='{executionId}', ContextKey='{contextKey}'.",
                            exception);
                    }

                    sourceContext =
                        ExecutionContextSnapshotMapper
                            .ToExecutionContext(snapshot);
                    source = "durable-execution-snapshot";
                }

                var reboundContext =
                    CloneExecutionContext(
                        sourceContext,
                        contextKey);

                await ContextStore
                    .SeedAsync(reboundContext)
                    .ConfigureAwait(false);

                Accessor.Set(
                    reboundContext);

                Logger.Engine.LogInformation(
                    $"[AI EXECUTION] RBAC execution context rebound to execution context key. ExecutionId='{executionId}', RequestedContextKey='{contextKey}', Source='{source}', SourceContextKey='{sourceContext.ContextKey}', TenantId='{reboundContext.TenantId}', UserId='{reboundContext.UserId}'.");

                return reboundContext;
            }
        }

        /// <summary>
        /// Gets a restored execution context previously bound to an execution identifier.
        /// </summary>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <param name="context">The restored execution context.</param>
        /// <returns><c>true</c> when a restored context is available; otherwise, <c>false</c>.</returns>
        protected bool TryGetRestoredExecutionContext(
            string executionId,
            out ExecutionContext context)
        {
            context =
                null!;

            if (string.IsNullOrWhiteSpace(executionId))
            {
                return false;
            }

            return _restoredExecutionContextsByExecutionId.TryGetValue(
                executionId,
                out context!);
        }

        /// <summary>
        /// Determines whether the exception is the known RBAC context-store miss.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <returns><c>true</c> when this is the known RBAC context-store miss; otherwise, <c>false</c>.</returns>
        protected static bool IsRbacExecutionContextNotFound(
            InvalidOperationException exception)
        {
            return string.Equals(
                exception.Message,
                "RBAC execution context not found.",
                StringComparison.Ordinal);
        }

        /// <summary>
        /// Creates a safe copy of an RBAC execution context with the specified context key.
        /// </summary>
        /// <param name="context">The source RBAC execution context.</param>
        /// <param name="contextKey">The context key to assign to the copy.</param>
        /// <returns>The cloned RBAC execution context.</returns>
        private static ExecutionContext CloneExecutionContext(
            ExecutionContext context,
            string contextKey)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(contextKey);

            return new ExecutionContext
            {
                ContextKey = contextKey,
                Project = context.Project,
                UserId = context.UserId,
                TenantId = context.TenantId,
                TenantGroupId = context.TenantGroupId,
                CurrentNamespace = context.CurrentNamespace,
                Namespaces = context.Namespaces
                    .Select(namespaceEntry => new NamespaceEntry
                    {
                        Name = namespaceEntry.Name,
                        Trns = new HashSet<string>(
                            namespaceEntry.Trns,
                            StringComparer.Ordinal)
                    })
                    .ToList(),
                InFlightCount = context.InFlightCount,
                TtlSeconds = context.TtlSeconds
            };
        }
    }
}