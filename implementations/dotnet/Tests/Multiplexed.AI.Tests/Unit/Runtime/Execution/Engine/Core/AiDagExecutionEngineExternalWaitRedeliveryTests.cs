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
using System.Reflection;

namespace Multiplexed.AI.Tests.Unit.Runtime.Execution.Engine.Core
{
    /// <summary>
    /// Validates idempotent external-wait redelivery after the authoritative parent call-site is already terminal.
    /// </summary>
    public sealed class AiDagExecutionEngineExternalWaitRedeliveryTests
    {
        [Theory]
        [InlineData(AiExecutionStatus.Completed, AiStepExecutionStatus.Completed)]
        [InlineData(AiExecutionStatus.Failed, AiStepExecutionStatus.Failed)]
        public async Task ResumeExternalWaitingStepAsync_Should_Accept_Terminal_Consumed_Redelivery_As_Idempotent_NoOp(
            AiExecutionStatus executionStatus,
            AiStepExecutionStatus stepStatus)
        {
            var record = CreateRecord(executionStatus);
            var state = CreateState(stepStatus);
            var dagStore = CreateDagStore(record, state);
            var engine = CreateEngine(dagStore);

            var observed =
                await engine
                    .ResumeExternalWaitingStepAsync(
                        record.ExecutionId,
                        "execute-child-dag")
                    .ConfigureAwait(false);

            Assert.Same(record, observed);
            Assert.Equal(executionStatus, observed.Status);
            Assert.Equal(stepStatus, state.Steps["execute-child-dag"].Status);
        }

        [Theory]
        [InlineData(AiExecutionStatus.Cancelled, AiStepExecutionStatus.Completed)]
        [InlineData(AiExecutionStatus.Completed, AiStepExecutionStatus.Ready)]
        [InlineData(AiExecutionStatus.Failed, AiStepExecutionStatus.WaitingForExternal)]
        public async Task ResumeExternalWaitingStepAsync_Should_Reject_Terminal_Redelivery_Without_Consumed_CallSite_Proof(
            AiExecutionStatus executionStatus,
            AiStepExecutionStatus stepStatus)
        {
            var record = CreateRecord(executionStatus);
            var state = CreateState(stepStatus);
            var dagStore = CreateDagStore(record, state);
            var engine = CreateEngine(dagStore);

            var exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                        () => engine.ResumeExternalWaitingStepAsync(
                            record.ExecutionId,
                            "execute-child-dag"))
                    .ConfigureAwait(false);

            Assert.Contains("is terminal and cannot continue external wait", exception.Message);
            Assert.Contains($"ExecutionStatus='{executionStatus}'", exception.Message);
            Assert.Contains($"StepStatus='{stepStatus}'", exception.Message);
        }

        private static AiDagExecutionEngine CreateEngine(
            IAiDagExecutionStore dagStore)
        {
            return new AiDagExecutionEngine(
                new TestEngineServices(dagStore),
                NullProxy.Create<IAiDagExecutionEngineRuntimeServices>());
        }

        private static AiExecutionRecord CreateRecord(
            AiExecutionStatus status)
        {
            return new AiExecutionRecord
            {
                ExecutionId = "parent-execution-1",
                PipelineName = "parent-pipeline",
                ExecutionMode = AiExecutionMode.Dag,
                Status = status,
                Steps = ["execute-child-dag"],
                CompletedSteps =
                    status is AiExecutionStatus.Completed or AiExecutionStatus.Failed
                        ? ["execute-child-dag"]
                        : []
            };
        }

        private static AiExecutionState CreateState(
            AiStepExecutionStatus stepStatus)
        {
            return new AiExecutionState
            {
                ExecutionId = "parent-execution-1",
                PipelineName = "parent-pipeline",
                Steps =
                    new Dictionary<string, AiStepState>(StringComparer.Ordinal)
                    {
                        ["execute-child-dag"] =
                            new AiStepState
                            {
                                StepName = "execute-child-dag",
                                Status = stepStatus,
                                Version = 4
                            }
                    }
            };
        }

        private static IAiDagExecutionStore CreateDagStore(
            AiExecutionRecord record,
            AiExecutionState state)
        {
            var store =
                DispatchProxy.Create<
                    IAiDagExecutionStore,
                    DagStoreProxy>();

            var proxy = (DagStoreProxy)(object)store;
            proxy.Record = record;
            proxy.State = state;

            return store;
        }

        private class DagStoreProxy : DispatchProxy
        {
            public AiExecutionRecord? Record { get; set; }

            public AiExecutionState? State { get; set; }

            protected override object? Invoke(
                MethodInfo? targetMethod,
                object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);

                return targetMethod.Name switch
                {
                    nameof(IAiDagExecutionStore.GetRecordAsync) =>
                        Task.FromResult(Record),

                    nameof(IAiDagExecutionStore.GetStateAsync) =>
                        Task.FromResult(State),

                    _ => throw new NotSupportedException(
                        $"Unit DAG store proxy does not support '{targetMethod.Name}'.")
                };
            }
        }

        private sealed class TestEngineServices : IAiDagExecutionEngineServices
        {
            public TestEngineServices(
                IAiDagExecutionStore dagStore)
            {
                DagStore = dagStore;
            }

            public IAiExecutionStore Store { get; } =
                NullProxy.Create<IAiExecutionStore>();

            public IContextStore ContextStore { get; } =
                NullProxy.Create<IContextStore>();

            public IExecutionContextAccessor Accessor { get; } =
                NullProxy.Create<IExecutionContextAccessor>();

            public IExecutionContextFactory ContextFactory { get; } =
                NullProxy.Create<IExecutionContextFactory>();

            public IServiceProvider Services { get; } =
                NullProxy.Create<IServiceProvider>();

            public IAiSequentialPipelineExecutor PipelineExecutor { get; } =
                NullProxy.Create<IAiSequentialPipelineExecutor>();

            public IAiRuntimeLogger Logger { get; } =
                NullProxy.Create<IAiRuntimeLogger>();

            public IAiExecutionCleanupService CleanupService { get; } =
                NullProxy.Create<IAiExecutionCleanupService>();

            public IOptions<AiEngineOptions> AiOptions { get; } =
                Options.Create(new AiEngineOptions());

            public IAiRuntimeInstanceIdentityDescriptor RuntimeInstanceIdentity { get; } =
                NullProxy.Create<IAiRuntimeInstanceIdentityDescriptor>();

            public IAiStepResultPayloadCompactor PayloadCompactor { get; } =
                NullProxy.Create<IAiStepResultPayloadCompactor>();

            public IAiPayloadStoreResolver PayloadStoreResolver { get; } =
                NullProxy.Create<IAiPayloadStoreResolver>();

            public IAiExecutionStateReader StateReader { get; } =
                NullProxy.Create<IAiExecutionStateReader>();

            public IAiExecutionStateWriter StateWriter { get; } =
                NullProxy.Create<IAiExecutionStateWriter>();

            public IAiExecutionStepResolver StepResolver { get; } =
                NullProxy.Create<IAiExecutionStepResolver>();

            public IAiDagExecutionStore? DagStore { get; }

            public IAiExecutionSnapshotService<ExecutionContextSnapshot>? SnapshotService =>
                null;

            public IAiRuntimeObservability ObservabilityService { get; } =
                NullProxy.Create<IAiRuntimeObservability>();

            public IAiPolicyEngineFactory PolicyEngineFactory { get; } =
                NullProxy.Create<IAiPolicyEngineFactory>();

            public IAiDagStepExecutionOrchestrator StepExecutionOrchestrator { get; } =
                NullProxy.Create<IAiDagStepExecutionOrchestrator>();

            public IAiConcurrencyGate ConcurrencyGate { get; } =
                NullProxy.Create<IAiConcurrencyGate>();

            public IAiExecutionControlGate ExecutionControlGate { get; } =
                NullProxy.Create<IAiExecutionControlGate>();

            public IAiExecutionControlService ExecutionControlService { get; } =
                NullProxy.Create<IAiExecutionControlService>();

            public IAiExecutionReplayMetadataService ReplayMetadataService { get; } =
                NullProxy.Create<IAiExecutionReplayMetadataService>();
        }

        private static class NullProxy
        {
            public static T Create<T>()
                where T : class
            {
                return DispatchProxy.Create<T, NullDispatchProxy>();
            }
        }

        private class NullDispatchProxy : DispatchProxy
        {
            protected override object? Invoke(
                MethodInfo? targetMethod,
                object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);

                var returnType = targetMethod.ReturnType;

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                if (returnType.IsGenericType &&
                    returnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var resultType = returnType.GenericTypeArguments[0];
                    var result = resultType.IsValueType
                        ? Activator.CreateInstance(resultType)
                        : null;

                    return typeof(Task)
                        .GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(resultType)
                        .Invoke(null, [result]);
                }

                return returnType.IsValueType
                    ? Activator.CreateInstance(returnType)
                    : null;
            }
        }
    }
}
