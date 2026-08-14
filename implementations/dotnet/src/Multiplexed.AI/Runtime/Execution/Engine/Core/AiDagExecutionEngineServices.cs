using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.Concurrency;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Cleanup;
using Multiplexed.Abstractions.AI.Execution.Context;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Payloads;
using Multiplexed.Abstractions.AI.Execution.Payloads.Resolvers;
using Multiplexed.Abstractions.AI.Execution.Persistence.Replay.Metadata;
using Multiplexed.Abstractions.AI.Execution.Persistence.Snapshot;
using Multiplexed.Abstractions.AI.Execution.Scheduling;
using Multiplexed.Abstractions.AI.Execution.State;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.Abstractions.Runtime;
using Multiplexed.AI.Configuration;
using Multiplexed.AI.Runtime.AI.Policies;
using Multiplexed.AI.Runtime.Configuration;
using Multiplexed.AI.Runtime.Execution.Cleanup;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;

namespace Multiplexed.AI.Runtime.Execution.Engine.Core
{
    /// <summary>
    /// Default implementation of <see cref="IAiDagExecutionEngineServices"/>.
    /// </summary>
    public sealed class AiDagExecutionEngineServices : IAiDagExecutionEngineServices
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AiDagExecutionEngineServices"/> class.
        /// </summary>
        public AiDagExecutionEngineServices(
            IAiExecutionStore store,
            IContextStore contextStore,
            IExecutionContextAccessor accessor,
            IExecutionContextFactory contextFactory,
            IServiceProvider services,
            IAiSequentialPipelineExecutor pipelineExecutor,
            IAiRuntimeLogger logger,
            IAiExecutionCleanupService cleanupService,
            IOptions<AiEngineOptions> aiOptions,
            IAiRuntimeObservability observabilityService,
            IAiStepResultPayloadCompactor payloadCompactor,
            IAiPayloadStoreResolver payloadStoreResolver,
            IAiExecutionStateReader stateReader,
            IAiExecutionStateWriter stateWriter,
            IAiExecutionStepResolver stepResolver,
            IAiRuntimeInstanceIdentityDescriptor runtimeInstanceIdentity,
            IAiPolicyEngineFactory policyEngineFactory,
            IAiConcurrencyGate concurrencyGate,
            IAiDagStepExecutionOrchestrator stepExecutionOrchestrator,
            IAiExecutionControlGate executionControlGate,
            IAiExecutionControlService executionControlService,
            IAiExecutionReplayMetadataService replayMetadataService,
            IAiDagExecutionStore? dagStore = null,
            IAiExecutionSnapshotService<ExecutionContextSnapshot>? snapshotService = null)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            ContextStore = contextStore ?? throw new ArgumentNullException(nameof(contextStore));
            Accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            ContextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            Services = services ?? throw new ArgumentNullException(nameof(services));
            PipelineExecutor = pipelineExecutor ?? throw new ArgumentNullException(nameof(pipelineExecutor));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            CleanupService = cleanupService ?? throw new ArgumentNullException(nameof(cleanupService));
            AiOptions = aiOptions ?? throw new ArgumentNullException(nameof(aiOptions));
            PayloadCompactor = payloadCompactor ?? throw new ArgumentNullException(nameof(payloadCompactor));
            PayloadStoreResolver = payloadStoreResolver ?? throw new ArgumentNullException(nameof(payloadStoreResolver));
            StateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            StateWriter = stateWriter ?? throw new ArgumentNullException(nameof(stateWriter));
            StepResolver = stepResolver ?? throw new ArgumentNullException(nameof(stepResolver));
            RuntimeInstanceIdentity = runtimeInstanceIdentity ?? throw new ArgumentNullException(nameof(runtimeInstanceIdentity));
            ObservabilityService = observabilityService ?? throw new ArgumentNullException(nameof(observabilityService));
            PolicyEngineFactory = policyEngineFactory ?? throw new ArgumentNullException(nameof(policyEngineFactory));
            ConcurrencyGate = concurrencyGate ?? throw new ArgumentNullException(nameof(concurrencyGate));
            StepExecutionOrchestrator = stepExecutionOrchestrator ?? throw new ArgumentNullException(nameof(stepExecutionOrchestrator));
            ExecutionControlGate = executionControlGate ?? throw new ArgumentNullException(nameof(executionControlGate));
            ExecutionControlService = executionControlService ?? throw new ArgumentNullException(nameof(executionControlService));
            ReplayMetadataService = replayMetadataService ?? throw new ArgumentNullException(nameof(replayMetadataService));

            DagStore = dagStore;
            SnapshotService = snapshotService;
        }

        /// <inheritdoc />
        public IAiExecutionStore Store { get; }

        /// <inheritdoc />
        public IContextStore ContextStore { get; }

        /// <inheritdoc />
        public IExecutionContextAccessor Accessor { get; }

        /// <inheritdoc />
        public IExecutionContextFactory ContextFactory { get; }

        /// <inheritdoc />
        public IServiceProvider Services { get; }

        /// <inheritdoc />
        public IAiSequentialPipelineExecutor PipelineExecutor { get; }

        /// <inheritdoc />
        public IAiRuntimeLogger Logger { get; }

        /// <inheritdoc />
        public IAiExecutionCleanupService CleanupService { get; }

        /// <inheritdoc />
        public IOptions<AiEngineOptions> AiOptions { get; }

        /// <inheritdoc />
        public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; }

        /// <inheritdoc />
        public IAiStepResultPayloadCompactor PayloadCompactor { get; }

        /// <inheritdoc />
        public IAiPayloadStoreResolver PayloadStoreResolver { get; }

        /// <inheritdoc />
        public IAiExecutionStateReader StateReader { get; }

        /// <inheritdoc />
        public IAiExecutionStateWriter StateWriter { get; }

        /// <inheritdoc />
        public IAiExecutionStepResolver StepResolver { get; }

        /// <inheritdoc />
        public IAiDagExecutionStore? DagStore { get; }

        /// <inheritdoc />
        public IAiExecutionSnapshotService<ExecutionContextSnapshot>? SnapshotService { get; }

        /// <inheritdoc />
        public IAiRuntimeObservability ObservabilityService { get; }

        /// <inheritdoc />
        public IAiPolicyEngineFactory PolicyEngineFactory { get; }

        /// <inheritdoc />
        public IAiConcurrencyGate ConcurrencyGate { get; }

        /// <inheritdoc />
        public IAiDagStepExecutionOrchestrator StepExecutionOrchestrator { get; }

        /// <inheritdoc />
        public IAiExecutionControlGate ExecutionControlGate { get; }

        /// <inheritdoc />
        public IAiExecutionControlService ExecutionControlService { get; }

        /// <summary>
        /// Gets the replay metadata service responsible for generating
        /// and persisting deterministic replay fingerprints.
        /// </summary>
        public IAiExecutionReplayMetadataService ReplayMetadataService { get; }
    }
}