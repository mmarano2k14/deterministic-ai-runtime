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
using Multiplexed.AI.Runtime.Execution.Cleanup;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Persistence.Replay;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.AI.Stores;
using Multiplexed.Rbac.Core.ExecutionContext;
using Multiplexed.Rbac.Core.Runtime;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Composition.ChildDag.Support
{
    /// <summary>
    /// Supplies only the DAG engine services required by child composition unit tests.
    /// </summary>
    internal sealed class TestAiDagExecutionEngineServices : IAiDagExecutionEngineServices
    {
        public TestAiDagExecutionEngineServices(
            IAiExecutionStore store,
            IAiRuntimeLogger? logger = null,
            IAiDagExecutionStore? dagStore = null,
            IExecutionContextAccessor? accessor = null,
            IExecutionContextFactory? contextFactory = null)
        {
            Store = store ?? throw new ArgumentNullException(nameof(store));
            Logger = logger ?? new NoopLogger();
            DagStore = dagStore;
            Accessor = accessor ?? new ExecutionContextAccessor();
            ContextFactory = contextFactory ?? new ExecutionContextFactory();
        }

        public IAiExecutionStore Store { get; }
        public IAiDagExecutionStore? DagStore { get; }
        public IAiRuntimeLogger Logger { get; }
        public IContextStore ContextStore => throw new NotSupportedException();
        public IExecutionContextAccessor Accessor { get; }
        public IExecutionContextFactory ContextFactory { get; }
        public IServiceProvider Services => throw new NotSupportedException();
        public IAiSequentialPipelineExecutor PipelineExecutor => throw new NotSupportedException();
        public IAiExecutionCleanupService CleanupService => throw new NotSupportedException();
        public IOptions<AiEngineOptions> AiOptions => throw new NotSupportedException();
        public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity => throw new NotSupportedException();
        public IAiStepResultPayloadCompactor PayloadCompactor => throw new NotSupportedException();
        public IAiPayloadStoreResolver PayloadStoreResolver => throw new NotSupportedException();
        public IAiExecutionStateReader StateReader => throw new NotSupportedException();
        public IAiExecutionStateWriter StateWriter => throw new NotSupportedException();
        public IAiExecutionStepResolver StepResolver => throw new NotSupportedException();
        public IAiExecutionSnapshotService<ExecutionContextSnapshot>? SnapshotService => throw new NotSupportedException();
        public IAiRuntimeObservability ObservabilityService => throw new NotSupportedException();
        public IAiPolicyEngineFactory PolicyEngineFactory => throw new NotSupportedException();
        public IAiConcurrencyGate ConcurrencyGate => throw new NotSupportedException();
        public IAiDagStepExecutionOrchestrator StepExecutionOrchestrator => throw new NotSupportedException();
        public IAiExecutionControlGate ExecutionControlGate => throw new NotSupportedException();
        public IAiExecutionControlService ExecutionControlService => throw new NotSupportedException();
        public IAiExecutionReplayMetadataService ReplayMetadataService => throw new NotSupportedException();
    }
}
