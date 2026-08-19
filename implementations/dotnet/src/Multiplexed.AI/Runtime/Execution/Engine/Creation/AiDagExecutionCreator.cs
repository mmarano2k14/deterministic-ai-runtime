using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Payloads.Models;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.Abstractions.AI.Observability.Tracing;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Steps;
using Multiplexed.AI.Abstractions.AI.Policies;
using Multiplexed.AI.Abstractions.AI.Retry;
using Multiplexed.AI.Runtime.AI.Retry;
using Multiplexed.AI.Runtime.Execution.Context;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Engine.Helpers;
using Multiplexed.AI.Runtime.Execution.Payloads.Serialization;
using Multiplexed.AI.Runtime.Execution.Payloads.Immutable;
using Multiplexed.AI.Stores.Creation;

namespace Multiplexed.AI.Runtime.Execution.Engine.Creation
{
    /// <summary>
    /// Creates DAG execution records and initializes their execution state.
    /// </summary>
    public sealed class AiDagExecutionCreator
    {
        private readonly IAiDagExecutionEngineServices _services;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionCreator"/> class.
        /// </summary>
        /// <param name="services">The DAG execution engine services.</param>
        public AiDagExecutionCreator(
            IAiDagExecutionEngineServices services)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
        }

        /// <summary>
        /// Creates a new DAG execution using a string input payload.
        /// </summary>
        /// <param name="pipelineName">The pipeline name to execute.</param>
        /// <param name="input">The input payload.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created execution record.</returns>
        public Task<AiExecutionRecord> CreateAsync(
            string pipelineName,
            string input,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);

            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("Input cannot be null or empty.", nameof(input));
            }

            return CreateInternalAsync(
                pipelineName,
                token => _services.PipelineExecutor.PrepareAsync(pipelineName, token),
                state => _services.StateWriter.SetData(state, AiExecutionKeys.Input, input),
                requestedExecutionId: null,
                pipelineDefinitionSnapshot: null,
                createIfAbsent: false,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates a new DAG execution using structured state input.
        /// </summary>
        /// <param name="pipelineName">The pipeline name to execute.</param>
        /// <param name="input">The structured input values to seed into execution state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created execution record.</returns>
        public Task<AiExecutionRecord> CreateAsync(
            string pipelineName,
            IDictionary<string, object?> input,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentNullException.ThrowIfNull(input);

            return CreateInternalAsync(
                pipelineName,
                token => _services.PipelineExecutor.PrepareAsync(pipelineName, token),
                state => SeedStructuredInput(state, input),
                requestedExecutionId: null,
                pipelineDefinitionSnapshot: null,
                createIfAbsent: false,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates the exact preallocated DAG execution when it does not already exist.
        /// </summary>
        /// <remarks>
        /// The supplied declarative definition is resolved directly and is never replaced by a newer provider
        /// definition with the same pipeline name. Repeated calls using the same execution identifier converge
        /// on the already persisted execution instead of overwriting it.
        /// </remarks>
        /// <param name="executionId">The exact preallocated execution identifier.</param>
        /// <param name="definition">The exact declarative DAG definition frozen by the caller.</param>
        /// <param name="pipelineDefinitionSnapshot">The immutable definition descriptor to bind to the execution.</param>
        /// <param name="input">The string input payload.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The newly created or already existing authoritative execution record.</returns>
        public Task<AiExecutionRecord> CreateIfAbsentAsync(
            string executionId,
            AiPipelineDefinition definition,
            AiStoredPayload pipelineDefinitionSnapshot,
            string input,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(pipelineDefinitionSnapshot);
            EnsureValidPipelineDefinitionSnapshot(pipelineDefinitionSnapshot);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);

            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("Input cannot be null or empty.", nameof(input));
            }

            return CreateInternalAsync(
                definition.Name,
                token => PreparePinnedPipelineAsync(definition, pipelineDefinitionSnapshot, token),
                state => _services.StateWriter.SetData(state, AiExecutionKeys.Input, input),
                executionId,
                pipelineDefinitionSnapshot,
                createIfAbsent: true,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates the exact preallocated DAG execution when it does not already exist.
        /// </summary>
        /// <remarks>
        /// The supplied declarative definition is resolved directly and is never replaced by a newer provider
        /// definition with the same pipeline name. Repeated calls using the same execution identifier converge
        /// on the already persisted execution instead of overwriting it.
        /// </remarks>
        /// <param name="executionId">The exact preallocated execution identifier.</param>
        /// <param name="definition">The exact declarative DAG definition frozen by the caller.</param>
        /// <param name="pipelineDefinitionSnapshot">The immutable definition descriptor to bind to the execution.</param>
        /// <param name="input">The structured input values to seed into execution state.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The newly created or already existing authoritative execution record.</returns>
        public Task<AiExecutionRecord> CreateIfAbsentAsync(
            string executionId,
            AiPipelineDefinition definition,
            AiStoredPayload pipelineDefinitionSnapshot,
            IDictionary<string, object?> input,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(pipelineDefinitionSnapshot);
            EnsureValidPipelineDefinitionSnapshot(pipelineDefinitionSnapshot);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Version);
            ArgumentNullException.ThrowIfNull(input);

            return CreateInternalAsync(
                definition.Name,
                token => PreparePinnedPipelineAsync(definition, pipelineDefinitionSnapshot, token),
                state => SeedStructuredInput(state, input),
                executionId,
                pipelineDefinitionSnapshot,
                createIfAbsent: true,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Creates the execution record, seeds the execution state, initializes DAG step state,
        /// creates the AI-owned RBAC context, and persists the execution.
        /// </summary>
        /// <param name="pipelineName">The pipeline name to execute.</param>
        /// <param name="preparePipeline">The callback that resolves the exact pipeline definition to execute.</param>
        /// <param name="seedState">The callback used to seed initial execution state values.</param>
        /// <param name="requestedExecutionId">The optional preallocated execution identifier.</param>
        /// <param name="pipelineDefinitionSnapshot">The optional immutable definition descriptor to persist on the execution.</param>
        /// <param name="createIfAbsent">Whether persistence must use strict non-overwriting creation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The created execution record.</returns>
        private async Task<AiExecutionRecord> CreateInternalAsync(
            string pipelineName,
            Func<CancellationToken, Task<ResolvedAiPipeline>> preparePipeline,
            Action<AiExecutionState> seedState,
            string? requestedExecutionId,
            AiStoredPayload? pipelineDefinitionSnapshot,
            bool createIfAbsent,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pipelineName);
            ArgumentNullException.ThrowIfNull(preparePipeline);
            ArgumentNullException.ThrowIfNull(seedState);

            if (createIfAbsent)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(requestedExecutionId);
                ArgumentNullException.ThrowIfNull(pipelineDefinitionSnapshot);
                EnsureValidPipelineDefinitionSnapshot(pipelineDefinitionSnapshot);
            }
            else if (pipelineDefinitionSnapshot is not null)
            {
                throw new InvalidOperationException(
                    "An immutable pipeline definition snapshot may only be bound through exact create-if-absent creation.");
            }

            var current = _services.Accessor.Current
                ?? throw new InvalidOperationException("No active RBAC context is available.");

            var preparedPipeline = await preparePipeline(cancellationToken).ConfigureAwait(false);

            if (preparedPipeline.ExecutionMode != AiExecutionMode.Dag)
            {
                throw new InvalidOperationException(
                    $"Pipeline '{pipelineName}' is configured for mode '{preparedPipeline.ExecutionMode}' and cannot be created by the DAG engine.");
            }

            if (preparedPipeline.Steps.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Pipeline '{pipelineName}' does not contain any resolved steps.");
            }

            var executionId = requestedExecutionId ?? Guid.NewGuid().ToString("N");
            var newContextKey = createIfAbsent
                ? $"execution-{executionId}"
                : Guid.NewGuid().ToString("N");
            var aiOwnedContext = _services.ContextFactory.CreateCopy(current, newContextKey);

            newContextKey = await _services.ContextStore.SeedAsync(aiOwnedContext);

            var record = new AiExecutionRecord
            {
                ExecutionId = executionId,
                PipelineName = pipelineName,
                PipelineDefinitionSnapshot = pipelineDefinitionSnapshot,
                ExecutionMode = preparedPipeline.ExecutionMode,
                ContextKey = newContextKey,
                Status = AiExecutionStatus.Pending,
                ExecutionContextSnapshot = _services.ContextFactory.CreateSnapshot(current),
                Steps = preparedPipeline.Steps.Select(x => x.Name).ToList(),
                CurrentStep = string.Empty,
                CurrentStepIndex = 0
            };

            var state = new AiExecutionState
            {
                ExecutionId = record.ExecutionId,
                PipelineName = pipelineName,
                PipelineConfig = new Dictionary<string, object?>(
                    preparedPipeline.Config,
                    StringComparer.Ordinal)
            };

            if (createIfAbsent)
            {
                state.Metadata["pipeline.definition.version"] = preparedPipeline.Version ?? string.Empty;
            }

            seedState(state);

            var executionContext = new AiExecutionContext(
                record,
                state,
                _services.Services,
                _services.StateReader,
                _services.StateWriter,
                cancellationToken);

            foreach (var step in preparedPipeline.Steps)
            {
                _services.StateWriter.EnsureStepInitialized(state, step);

                var stepState = _services.StateWriter.GetOrCreateStep(state, step.Name);
                stepState.DependsOn = step.DependsOn?.ToList() ?? new List<string>();

                var stepContext = new AiStepExecutionContext(
                    executionContext,
                    step);

                var retryDefinition = await _services.ObservabilityService.Tracer.TraceStepAsync(
                    new AiStepTraceContext
                    {
                        ExecutionId = record.ExecutionId,
                        StepId = step.Name,
                        StepType = step.Step.GetType().Name,
                        StepKey = step.StepKey,
                        RetryCount = stepState.RetryState?.RetryCount ?? 0,
                        RecoveryCount = stepState.RecoveryCount,
                        WorkerId = _services.ObservabilityService?.Correlation?.Current?.WorkerId ?? String.Empty,
                        ClaimToken = null,
                        Status = "ResolvingRetryPolicy",
                        Operation = "retry.definition"
                    },
                    async () =>
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        try
                        {
                            var definition = await _services.PolicyEngineFactory
                                .Create<IAiRetryEngine>(AiPolicyKind.Retry, stepContext)
                                .ResolveRetryDefinitionAsync(cancellationToken);

                            sw.Stop();

                            _services.ObservabilityService.Metrics.Policy.RecordExecution(
                                record.ExecutionId,
                                "retry.definition",
                                success: true,
                                duration: sw.Elapsed);

                            _services.ObservabilityService.Metrics.Policy.RecordDecision(
                                record.ExecutionId,
                                "retry.definition",
                                definition is null
                                    ? AiPolicyResultKind.Block
                                    : AiPolicyResultKind.Success);

                            return definition;
                        }
                        catch
                        {
                            sw.Stop();

                            _services.ObservabilityService.Metrics.Policy.RecordExecution(
                                record.ExecutionId,
                                "retry.definition",
                                success: false,
                                duration: sw.Elapsed);

                            _services.ObservabilityService.Metrics.Policy.RecordFailure(
                                record.ExecutionId,
                                "retry.definition");

                            throw;
                        }
                    });

                stepState.Retry = retryDefinition;
                stepState.RetryState ??= new AiStepRetryState();
            }

            if (createIfAbsent)
            {
                var createStore = ResolveCreateIfAbsentStore();
                var created = await createStore
                    .TryCreateIfAbsentAsync(record, state, cancellationToken)
                    .ConfigureAwait(false);

                if (!created)
                {
                    return await LoadAndValidateExistingAsync(
                            record.ExecutionId,
                            preparedPipeline,
                            pipelineDefinitionSnapshot!,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else if (_services.DagStore is not null)
            {
                await _services.DagStore
                    .CreateAsync(record, state, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _services.Store
                    .CreateAsync(record, state, cancellationToken)
                    .ConfigureAwait(false);
            }

            var pipelineKey = $"{preparedPipeline.Name}:{preparedPipeline.Version}";
            var runtimeInstanceId = _services.RuntimeInstanceIdentity.RuntimeInstanceId;

            await AiDagExecutionHelpers.RecordDagLedgerEventAsync(
                    _services,
                    record.ExecutionId,
                    pipelineKey,
                    "_execution",
                    "_execution",
                    runtimeInstanceId,
                    claimToken: null,
                    concurrencyContext: null,
                    AiDecisionLedgerCategory.Execution,
                    AiDecisionLedgerEvents.Execution.Created,
                    AiDecisionLedgerOutcome.Persisted,
                    "DAG execution created and persisted.",
                    new Dictionary<string, string>
                    {
                        ["pipeline.name"] = record.PipelineName ?? string.Empty,
                        ["pipeline.version"] = preparedPipeline.Version ?? string.Empty,
                        ["execution.mode"] = record.ExecutionMode.ToString(),
                        ["step.count"] = preparedPipeline.Steps.Count.ToString(),
                        ["context.key"] = record.ContextKey ?? string.Empty
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            _services.Logger.Engine.ExecutionCreated(record);

            _services.ObservabilityService.Metrics.Execution.RecordExecutionStarted(
                record.ExecutionId);

            _services.Logger.Engine.LogInformation(
                $"[AI DAG] Execution created. ExecutionId='{record.ExecutionId}', Pipeline='{record.PipelineName}', Mode='{record.ExecutionMode}', StepCount='{preparedPipeline.Steps.Count}', ContextKey='{record.ContextKey}'.");

            return record;
        }

        /// <summary>
        /// Resolves the strict create-if-absent capability for the active execution store.
        /// </summary>
        /// <returns>The strict create-if-absent store.</returns>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the configured store does not support exact non-overwriting creation.
        /// </exception>
        private IAiExecutionCreateIfAbsentStore ResolveCreateIfAbsentStore()
        {
            if (_services.DagStore is IAiExecutionCreateIfAbsentStore dagCreateStore)
            {
                return dagCreateStore;
            }

            if (_services.DagStore is null && _services.Store is IAiExecutionCreateIfAbsentStore createStore)
            {
                return createStore;
            }

            throw new InvalidOperationException(
                "The configured execution store does not support exact create-if-absent execution creation.");
        }

        /// <summary>
        /// Reloads and validates the authoritative execution after another creator wins the exact-id race.
        /// </summary>
        /// <param name="executionId">The exact durable execution identifier.</param>
        /// <param name="pipeline">The frozen pipeline that this creation attempt expected.</param>
        /// <param name="pipelineDefinitionSnapshot">The immutable pipeline definition descriptor expected by this attempt.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The authoritative existing execution record.</returns>
        private async Task<AiExecutionRecord> LoadAndValidateExistingAsync(
            string executionId,
            ResolvedAiPipeline pipeline,
            AiStoredPayload pipelineDefinitionSnapshot,
            CancellationToken cancellationToken)
        {
            var record = _services.DagStore is not null
                ? await _services.DagStore.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false)
                : await _services.Store.GetRecordAsync(executionId, cancellationToken).ConfigureAwait(false);

            var state = _services.DagStore is not null
                ? await _services.DagStore.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false)
                : await _services.Store.GetStateAsync(executionId, cancellationToken).ConfigureAwait(false);

            if (record is null || state is null)
            {
                throw new InvalidOperationException(
                    $"Execution '{executionId}' won a create-if-absent race but its durable record/state pair could not be reloaded.");
            }

            if (!string.Equals(record.ExecutionId, executionId, StringComparison.Ordinal) ||
                !string.Equals(state.ExecutionId, executionId, StringComparison.Ordinal) ||
                !string.Equals(record.PipelineName, pipeline.Name, StringComparison.Ordinal) ||
                !string.Equals(state.PipelineName, pipeline.Name, StringComparison.Ordinal) ||
                record.ExecutionMode != pipeline.ExecutionMode ||
                !HaveSameContentHash(record.PipelineDefinitionSnapshot, pipelineDefinitionSnapshot))
            {
                throw new InvalidOperationException(
                    $"Execution id '{executionId}' is already bound to incompatible durable execution data.");
            }

            var existingVersion = state.Metadata.TryGetValue("pipeline.definition.version", out var version)
                ? version?.ToString()
                : null;

            if (!string.Equals(existingVersion, pipeline.Version ?? string.Empty, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Execution id '{executionId}' is already bound to pipeline version '{existingVersion ?? string.Empty}', not '{pipeline.Version ?? string.Empty}'.");
            }

            var expectedSteps = pipeline.Steps.Select(step => step.Name).ToArray();
            if (!record.Steps.SequenceEqual(expectedSteps, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Execution id '{executionId}' is already bound to a different DAG step topology.");
            }

            return record;
        }

        /// <summary>
        /// Loads the immutable execution-bound pipeline definition, verifies its durable hash, and ensures the
        /// declarative definition supplied by the run request represents the same canonical content.
        /// </summary>
        /// <param name="definition">The declarative definition resolved from the run request.</param>
        /// <param name="pipelineDefinitionSnapshot">The immutable descriptor that must become authoritative.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The resolved pipeline prepared from the verified immutable definition.</returns>
        private async Task<ResolvedAiPipeline> PreparePinnedPipelineAsync(
            AiPipelineDefinition definition,
            AiStoredPayload pipelineDefinitionSnapshot,
            CancellationToken cancellationToken)
        {
            var reader = new AiImmutableJsonPayloadReader(_services.PayloadStoreResolver);
            var frozenJson = await reader
                .LoadAndVerifyAsync(pipelineDefinitionSnapshot, cancellationToken)
                .ConfigureAwait(false);

            var requestedJson = AiCanonicalJson.Serialize(definition);
            if (!string.Equals(frozenJson, requestedJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Preallocated execution definition '{definition.Name}' does not match its immutable pipeline definition snapshot.");
            }

            var frozenDefinition = AiCanonicalJson.Deserialize<AiPipelineDefinition>(frozenJson);
            if (string.IsNullOrWhiteSpace(frozenDefinition.Name) ||
                string.IsNullOrWhiteSpace(frozenDefinition.Version))
            {
                throw new InvalidOperationException(
                    "Immutable pipeline definition snapshots used for exact execution creation require an explicit name and version.");
            }

            return await _services.PipelineExecutor
                .PrepareAsync(frozenDefinition, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Validates that an execution-bound pipeline definition descriptor carries a durable content hash.
        /// </summary>
        /// <param name="snapshot">The immutable pipeline definition descriptor.</param>
        private static void EnsureValidPipelineDefinitionSnapshot(AiStoredPayload snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.ContentHash))
            {
                throw new InvalidOperationException(
                    "Exact execution creation requires an immutable pipeline definition snapshot with a content hash.");
            }

            if (!snapshot.IsInline && string.IsNullOrWhiteSpace(snapshot.ArtifactId))
            {
                throw new InvalidOperationException(
                    "Artifact-backed immutable pipeline definition snapshots must contain an artifact id.");
            }
        }

        /// <summary>
        /// Determines whether two immutable payload descriptors bind the same canonical content.
        /// </summary>
        /// <param name="existing">The authoritative persisted descriptor.</param>
        /// <param name="candidate">The duplicate creation descriptor.</param>
        /// <returns><c>true</c> when both descriptors carry the same non-empty content hash.</returns>
        private static bool HaveSameContentHash(AiStoredPayload? existing, AiStoredPayload candidate)
        {
            return existing is not null &&
                   !string.IsNullOrWhiteSpace(existing.ContentHash) &&
                   !string.IsNullOrWhiteSpace(candidate.ContentHash) &&
                   string.Equals(existing.ContentHash, candidate.ContentHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Seeds structured execution input using the existing execution state writer.
        /// </summary>
        /// <param name="state">The execution state to seed.</param>
        /// <param name="input">The structured values to persist.</param>
        private void SeedStructuredInput(
            AiExecutionState state,
            IDictionary<string, object?> input)
        {
            foreach (var pair in input)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    throw new ArgumentException(
                        "Structured input contains an empty or whitespace key.",
                        nameof(input));
                }

                _services.StateWriter.SetData(state, pair.Key, pair.Value);
            }
        }
    }
}
