using System.Reflection;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.ExecutionAssistance;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Isolation;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeQueue;
using Multiplexed.Abstractions.AI.Execution;
using Multiplexed.Abstractions.AI.Execution.Control;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.AI.Observability;
using Multiplexed.Abstractions.AI.Pipeline;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeQueue;
using Multiplexed.AI.Runtime.Execution.Engine.Core;
using Multiplexed.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.AI.Runtime.Observability.Logging;
using Multiplexed.Rbac.Core.ExecutionContext;
using ExecutionContext = Multiplexed.Rbac.Core.ExecutionContext.ExecutionContext;

namespace Multiplexed.AI.Tests.Unit.Execution.Instance.Worker
{
    /// <summary>
    /// Unit tests for runtime pipeline background controller resume dispatch.
    /// </summary>
    public sealed class AiRuntimePipelineBackgroundControllerResumeTests
    {
        /// <summary>
        /// Verifies that controlled recovery resume does not create a new execution and
        /// advances the existing durable execution identifier through the runtime worker.
        /// </summary>
        [Fact]
        public async Task EnqueueResumeAsync_Should_Run_Worker_With_Existing_ExecutionId()
        {
            const string existingExecutionId = "execution-existing-1";

            var worker = new CapturingRuntimeInstanceWorker();
            var runExecutionIndex = new InMemoryAiRuntimeRunExecutionIndex();
            var lifecycleHook = new CapturingRunLifecycleHook();

            var controller = CreateController(
                worker,
                runExecutionIndex,
                lifecycleHook);

            await controller.StartAsync();

            var handle = await controller.EnqueueResumeAsync(
                new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = CreateExecutionContextSnapshot(),
                    PipelineDefinition = CreatePipelineDefinition()
                },
                existingExecutionId);

            var queuedIndex =
                await runExecutionIndex
                    .GetAsync(handle.RunId)
                    .ConfigureAwait(false);

            Assert.NotNull(queuedIndex);
            Assert.Equal(existingExecutionId, queuedIndex!.ExecutionId);
            Assert.Equal("queued", queuedIndex.Status);

            var final = await handle.Completion.WaitAsync(
                TimeSpan.FromSeconds(10));

            await controller.StopAsync();

            var indexed = await runExecutionIndex.GetAsync(handle.RunId);

            Assert.Equal(existingExecutionId, handle.ExecutionId);
            Assert.Equal(existingExecutionId, worker.LastExecutionId);
            Assert.Equal(existingExecutionId, final.ExecutionId);
            Assert.Equal(AiExecutionStatus.Completed, final.Status);
            Assert.NotNull(indexed);
            Assert.Equal(existingExecutionId, indexed!.ExecutionId);
            Assert.Equal("completed", indexed.Status);
            Assert.True(lifecycleHook.FinalizedCalled);
            Assert.Equal(existingExecutionId, lifecycleHook.LastExecutionId);
        }

        /// <summary>
        /// Creates a runtime pipeline background controller with test doubles.
        /// </summary>
        private static AiRuntimePipelineBackgroundController CreateController(
            CapturingRuntimeInstanceWorker worker,
            IAiRuntimeRunExecutionIndex runExecutionIndex,
            IAiRuntimePipelineRunLifecycleHook lifecycleHook)
        {
            var engine = new AiDagExecutionEngine(
                NullProxy.Create<IAiDagExecutionEngineServices>(),
                NullProxy.Create<IAiDagExecutionEngineRuntimeServices>());

            return new AiRuntimePipelineBackgroundController(
                engine,
                worker,
                new CapturingRuntimeInstanceWorkerGroup(),
                new CapturingRuntimeInstanceWorkerFactory(worker),
                new StaticPipelineRunDefinitionResolver(),
                new NoopPipelineRunDefinitionPublisher(),
                lifecycleHook,
                NullProxy.Create<IAiExecutionControlService>(),
                new TestRuntimeInstanceIdentity(),
                NullProxy.Create<IAiRuntimeLogger>(),
                NullProxy.Create<IAiRuntimeObservability>(),
                NullProxy.Create<IAiExecutionAssistanceCandidateStore>(),
                runExecutionIndex,
                new TestExecutionContextAccessor(),
                Options.Create(new AiRuntimePipelineBackgroundControllerOptions
                {
                    QueueCapacity = 16,
                    MaxConcurrentRuns = 1,
                    MaxLocalWorkersPerExecution = 1,
                    RejectEnqueueWhenStopped = true
                }));
        }

        /// <summary>
        /// Creates a minimal DAG pipeline definition.
        /// </summary>
        private static AiPipelineDefinition CreatePipelineDefinition()
        {
            return new AiPipelineDefinition
            {
                Name = "pipeline-1",
                ExecutionMode = AiExecutionMode.Dag,
                Version = "unit-test",
                Steps = new[]
                {
                    new AiPipelineStepDefinition
                    {
                        Name = "step-1",
                        StepKey = "noop",
                        Order = 0
                    }
                }
            };
        }

        /// <summary>
        /// Creates a tenant execution context snapshot.
        /// </summary>
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

        /// <summary>
        /// Runtime worker fake that captures the execution id it was asked to run.
        /// </summary>
        private sealed class CapturingRuntimeInstanceWorker : IAiRuntimeInstanceWorker
        {
            public string? LastExecutionId { get; private set; }

            public Task<AiExecutionRecord> RunExecutionAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
                cancellationToken.ThrowIfCancellationRequested();

                LastExecutionId = executionId;

                return Task.FromResult(new AiExecutionRecord
                {
                    ExecutionId = executionId,
                    PipelineName = "pipeline-1",
                    Status = AiExecutionStatus.Completed,
                    CompletedAtUtc = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// Worker group fake used only if distributed mode is accidentally enabled.
        /// </summary>
        private sealed class CapturingRuntimeInstanceWorkerGroup : IAiRuntimeInstanceWorkerGroup
        {
            public Task<AiExecutionRecord> RunExecutionAsync(
                string executionId,
                IReadOnlyCollection<IAiRuntimeInstanceWorker> workers,
                CancellationToken cancellationToken = default)
            {
                return workers.First().RunExecutionAsync(
                    executionId,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Worker factory fake.
        /// </summary>
        private sealed class CapturingRuntimeInstanceWorkerFactory : IAiRuntimeInstanceWorkerFactory
        {
            private readonly IAiRuntimeInstanceWorker worker;

            public CapturingRuntimeInstanceWorkerFactory(
                IAiRuntimeInstanceWorker worker)
            {
                this.worker = worker;
            }

            public IReadOnlyCollection<IAiRuntimeInstanceWorker> CreateWorkers(
                int workerCount)
            {
                return Enumerable
                    .Range(0, Math.Max(1, workerCount))
                    .Select(_ => worker)
                    .ToArray();
            }
        }

        /// <summary>
        /// Static pipeline definition resolver fake.
        /// </summary>
        private sealed class StaticPipelineRunDefinitionResolver : IAiRuntimePipelineRunDefinitionResolver
        {
            public Task<AiPipelineDefinition> ResolveAsync(
                AiRuntimePipelineRunRequest request,
                CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(request);
                cancellationToken.ThrowIfCancellationRequested();

                return Task.FromResult(
                    request.PipelineDefinition ?? CreatePipelineDefinition());
            }
        }

        /// <summary>
        /// Pipeline definition publisher fake.
        /// </summary>
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

        /// <summary>
        /// Run lifecycle hook fake.
        /// </summary>
        private sealed class CapturingRunLifecycleHook : IAiRuntimePipelineRunLifecycleHook
        {
            public bool FinalizedCalled { get; private set; }

            public string? LastExecutionId { get; private set; }

            public Task OnFinalizedAsync(
                AiRuntimePipelineRunFinalizedContext context,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(context);
                cancellationToken.ThrowIfCancellationRequested();

                FinalizedCalled = true;
                LastExecutionId = context.ExecutionId;

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Runtime instance identity fake.
        /// </summary>
        private sealed class TestRuntimeInstanceIdentity : IAiRuntimeInstanceIdentityDescriptor
        {
            public string RuntimeInstanceId { get; } = "runtime-instance-1";

            public string HostName => "unit-test-host";

            public int ProcessId => Environment.ProcessId;

            public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Execution context accessor fake.
        /// </summary>
        private sealed class TestExecutionContextAccessor : IExecutionContextAccessor
        {
            public ExecutionContext? Current { get; private set; }

            public void Set(
                ExecutionContext context)
            {
                Current = context;
            }

            public void Clear()
            {
                Current = null;
            }
        }

        /// <summary>
        /// Dynamic no-op proxy factory for large runtime service interfaces that are not
        /// relevant to this test.
        /// </summary>
        private static class NullProxy
        {
            public static T Create<T>()
                where T : class
            {
                return DispatchProxy.Create<T, NullDispatchProxy>();
            }

            public static object? Create(
                Type type)
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

        /// <summary>
        /// Dynamic no-op dispatch proxy.
        /// </summary>
        private class NullDispatchProxy : DispatchProxy
        {
            protected override object? Invoke(
                MethodInfo? targetMethod,
                object?[]? args)
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

                    return fromResult.Invoke(
                        null,
                        new[] { GetDefaultValue(resultType) });
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

                if (typeof(IDisposable).IsAssignableFrom(returnType))
                {
                    return DisposableScope.Instance;
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

            private static object? GetDefaultValue(
                Type type)
            {
                return type.IsValueType
                    ? Activator.CreateInstance(type)
                    : null;
            }
        }

        /// <summary>
        /// Disposable no-op scope.
        /// </summary>
        private sealed class DisposableScope : IDisposable
        {
            public static readonly IDisposable Instance = new DisposableScope();

            public void Dispose()
            {
            }
        }
    }
}
