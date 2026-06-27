using Microsoft.Extensions.Logging.Abstractions;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Capacity;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Providers;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Registry;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.SharedInstance;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController.Store;
using Multiplexed.Abstractions.AI.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;
using Multiplexed.AI.Runtime.ControlPlane.SharedController.Dispatch;
using Multiplexed.AI.Tests.Fixtures;
using Xunit;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedController
{
    /// <summary>
    /// Tests recovery forensics emitted by the remote shared run dispatcher.
    /// </summary>
    public sealed class RemoteAiSharedRunDispatcherForensicsTests
    {
        /// <summary>
        /// Verifies that remote recovery redispatch records replacement local run and resume context forensics.
        /// </summary>
        [Fact]
        public async Task DispatchAsync_Should_Record_Remote_Local_Run_Registered_And_Resume_Context_Seeded_Forensics_When_Provider_Dispatch_Succeeds()
        {
            var metadata = CreateRecoveryMetadata();
            var snapshot = CreateSnapshot();

            var sharedRun = CreateSharedRun(
                snapshot,
                metadata);

            var provider = new FakeRuntimeInstanceDispatchProvider
            {
                Result = new AiSharedRuntimeInstanceDispatchResult
                {
                    Success = true,
                    RuntimeInstanceId = "runtime-replacement-1",
                    SharedRunId = "shared-run-1",
                    LocalRunId = "local-run-replacement-1",
                    ExecutionId = "execution-1",
                    ClaimToken = "claim-token-1",
                    Message = "remote dispatched",
                    FailureReason = null,
                    StartedAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(-10),
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = 10,
                    Metadata = new Dictionary<string, string>
                    {
                        ["provider.result"] = "ok"
                    }
                }
            };

            var descriptor = new AiRuntimeInstanceCapacityDescriptor
            {
                RuntimeInstanceId = "runtime-replacement-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                Status = AiRuntimeInstanceStatus.Ready,
                WorkerCount = 1,
                ActiveWorkerCount = 0,
                AvailableWorkerCount = 1,
                QueuedRunCount = 0,
                RunningRunCount = 0,
                ActiveRunCount = 0,
                MaxConcurrentRuns = 3,
                MaxRunSlots = 3,
                AvailableRunSlots = 3,
                EffectiveAvailableRunSlots = 3,
                IsQueuePaused = false,
                CanAcceptRun = true,
                LastHeartbeatAtUtc = DateTimeOffset.UtcNow,
                ControlPlaneId = "control-plane-1",
                Metadata = new Dictionary<string, string>()
            };

            var providerResolver = new FakeRuntimeInstanceProviderCapabilityResolver
            {
                Provider = provider,
                Descriptor = descriptor
            };

            var registry = new FakeRuntimeInstanceRegistry();

            await registry.RegisterAsync(
                    new AiRuntimeInstanceRegistration
                    {
                        RuntimeInstanceId = "runtime-replacement-1",
                        TenantId = "tenant-1",
                        TenantGroupId = "tenant-group-1",
                        HostName = "localhost",
                        ProcessId = Environment.ProcessId,
                        WorkerCount = 1,
                        MaxConcurrentRuns = 3,
                        QueueCapacity = 10,
                        RuntimeVersion = "test",
                        Metadata = new Dictionary<string, string>(),
                        RegisteredAtUtc = DateTimeOffset.UtcNow,
                        Role = AiRuntimeInstanceRole.Runtime,
                        HostId = "host-1",
                        RuntimeId = "runtime-1",
                        ControlPlaneHostId = "control-plane-host-1",
                        ControlPlaneId = "control-plane-1"
                    })
                .ConfigureAwait(false);

            var forensicsRecorder = new FakeRuntimeRecoveryForensicsRecorder();

            var dispatcher = new RemoteAiSharedRunDispatcher(
                providerResolver,
                registry,
                NullLogger<RemoteAiSharedRunDispatcher>.Instance,
                forensicsRecorder);

            var result = await dispatcher
                .DispatchAsync(
                    new AiSharedRunDispatchRequest
                    {
                        SharedRun = sharedRun,
                        RuntimeInstanceId = "runtime-replacement-1",
                        ClaimToken = "claim-token-1",
                        CorrelationId = "correlation-1",
                        RequestedBy = "test",
                        Source = "unit-test",
                        Reason = "remote recovery redispatch",
                        Metadata = metadata
                    })
                .ConfigureAwait(false);

            Assert.True(result.Success);
            Assert.Equal("shared-run-1", result.SharedRunId);
            Assert.Equal("runtime-replacement-1", result.RuntimeInstanceId);
            Assert.Equal("local-run-replacement-1", result.LocalRunId);
            Assert.Equal("execution-1", result.ExecutionId);
            Assert.Equal(1, provider.DispatchCalls);
            Assert.Equal(1, providerResolver.ResolveCalls);

            Assert.Equal(2, forensicsRecorder.Events.Count);

            var localRunEvent = forensicsRecorder.Events[0];
            var resumeContextEvent = forensicsRecorder.Events[1];

            Assert.Equal(AiRuntimeRecoveryForensicsEventType.ReplacementLocalRunRegistered, localRunEvent.EventType);
            Assert.Equal(AiRuntimeRecoveryForensicsEventType.ResumeContextSeeded, resumeContextEvent.EventType);

            Assert.Equal("runtime-recovery:execution-1:shared-run-1:local-run-failed-1", localRunEvent.ForensicsId);
            Assert.Equal(localRunEvent.ForensicsId, resumeContextEvent.ForensicsId);

            Assert.Equal("registered", localRunEvent.Outcome);
            Assert.Equal("seeded", resumeContextEvent.Outcome);

            Assert.Equal("execution-1", localRunEvent.ExecutionId);
            Assert.Equal("execution-1", resumeContextEvent.ExecutionId);

            Assert.Equal("shared-run-1", localRunEvent.SharedRunId);
            Assert.Equal("shared-run-1", resumeContextEvent.SharedRunId);

            Assert.Equal("local-run-replacement-1", localRunEvent.LocalRunId);
            Assert.Equal("local-run-replacement-1", resumeContextEvent.LocalRunId);

            Assert.Equal("runtime-replacement-1", localRunEvent.RuntimeInstanceId);
            Assert.Equal("runtime-replacement-1", resumeContextEvent.RuntimeInstanceId);

            Assert.Equal("true", localRunEvent.Metadata["remote.dispatch"]);
            Assert.Equal("runtime-replacement-1", localRunEvent.Metadata["replacement.runtimeInstanceId"]);
            Assert.Equal("local-run-replacement-1", localRunEvent.Metadata["replacement.localRunId"]);
            Assert.Equal("runtime-failed-1", localRunEvent.Metadata["failed.runtimeInstanceId"]);
            Assert.Equal("local-run-failed-1", localRunEvent.Metadata["failed.localRunId"]);
            Assert.Equal("ctx-tenant-1", localRunEvent.Metadata["resume.contextKey"]);
            Assert.Equal("shared-run.execution-context-snapshot", localRunEvent.Metadata["resume.source"]);

            Assert.Equal("true", resumeContextEvent.Metadata["remote.dispatch"]);
            Assert.Equal("runtime-replacement-1", resumeContextEvent.Metadata["replacement.runtimeInstanceId"]);
            Assert.Equal("local-run-replacement-1", resumeContextEvent.Metadata["replacement.localRunId"]);
            Assert.Equal("runtime-failed-1", resumeContextEvent.Metadata["failed.runtimeInstanceId"]);
            Assert.Equal("local-run-failed-1", resumeContextEvent.Metadata["failed.localRunId"]);
            Assert.Equal("ctx-tenant-1", resumeContextEvent.Metadata["resume.contextKey"]);
            Assert.Equal("shared-run.execution-context-snapshot", resumeContextEvent.Metadata["resume.source"]);
        }

        /// <summary>
        /// Creates recovery metadata used by recovery redispatch tests.
        /// </summary>
        /// <returns>The recovery metadata.</returns>
        private static IReadOnlyDictionary<string, string> CreateRecoveryMetadata()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["controlPlaneId"] = "control-plane-1",
                ["recovery.forensicsId"] = "runtime-recovery:execution-1:shared-run-1:local-run-failed-1",
                ["recovery.failedExecutionId"] = "execution-1",
                ["recovery.failedRuntimeInstanceId"] = "runtime-failed-1",
                ["recovery.failedLocalRunId"] = "local-run-failed-1"
            };
        }

        /// <summary>
        /// Creates a shared run record.
        /// </summary>
        /// <param name="snapshot">The execution context snapshot.</param>
        /// <param name="metadata">The shared run metadata.</param>
        /// <returns>The shared run record.</returns>
        private static AiSharedRunRecord CreateSharedRun(
            ExecutionContextSnapshot snapshot,
            IReadOnlyDictionary<string, string> metadata)
        {
            return new AiSharedRunRecord
            {
                SharedRunId = "shared-run-1",
                Status = AiSharedRunStatus.QueuedGlobally,
                RunRequest = new AiRuntimePipelineRunRequest
                {
                    PipelineName = "pipeline-1",
                    ExecutionContextSnapshot = snapshot,
                    Input = new Dictionary<string, object?>
                    {
                        ["value"] = 42
                    }
                },
                ExecutionContextSnapshot = snapshot,
                LocalRunId = "local-run-failed-1",
                ExecutionId = "execution-1",
                AssignedRuntimeInstanceId = "runtime-failed-1",
                AdmissionDecision = null,
                PipelineKey = "pipeline-1",
                CorrelationId = "correlation-1",
                RequestedBy = "test",
                Source = "unit-test",
                Reason = "remote recovery redispatch",
                FailureReason = null,
                SubmittedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                Metadata = metadata,
                ControlPlaneId = "control-plane-1"
            };
        }

        /// <summary>
        /// Creates an execution context snapshot.
        /// </summary>
        /// <returns>The execution context snapshot.</returns>
        private static ExecutionContextSnapshot CreateSnapshot()
        {
            return new ExecutionContextSnapshot
            {
                ContextKey = "ctx-tenant-1",
                Project = "project-1",
                UserId = "user-1",
                TenantId = "tenant-1",
                TenantGroupId = "tenant-group-1",
                CurrentNamespace = "tenant-1",
                Namespaces = [],
                InFlightCount = 0,
                TtlSeconds = 30,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Fake runtime instance provider capability resolver.
        /// </summary>
        private sealed class FakeRuntimeInstanceProviderCapabilityResolver : IAiRuntimeInstanceProviderCapabilityResolver
        {
            /// <summary>
            /// Gets or sets the dispatch provider.
            /// </summary>
            public required FakeRuntimeInstanceDispatchProvider Provider { get; set; }

            /// <summary>
            /// Gets or sets the capacity descriptor.
            /// </summary>
            public required AiRuntimeInstanceCapacityDescriptor Descriptor { get; set; }

            /// <summary>
            /// Gets the number of resolve calls.
            /// </summary>
            public int ResolveCalls { get; private set; }

            /// <inheritdoc />
            public Task<AiRuntimeInstanceProviderCapabilityResolution<TProvider>> ResolveAsync<TProvider>(
                string runtimeInstanceId,
                CancellationToken cancellationToken = default)
                where TProvider : IAiRuntimeInstanceProvider
            {
                ResolveCalls++;

                return Task.FromResult(
                    AiRuntimeInstanceProviderCapabilityResolution<TProvider>.Succeeded(
                        runtimeInstanceId,
                        Descriptor,
                        (TProvider)(object)Provider));
            }
        }

        /// <summary>
        /// Fake runtime instance dispatch provider.
        /// </summary>
        private sealed class FakeRuntimeInstanceDispatchProvider : IAiRuntimeInstanceDispatchProvider
        {
            /// <summary>
            /// Gets or sets the provider dispatch result.
            /// </summary>
            public required AiSharedRuntimeInstanceDispatchResult Result { get; set; }

            /// <summary>
            /// Gets the number of dispatch calls.
            /// </summary>
            public int DispatchCalls { get; private set; }

            /// <inheritdoc />
            public bool CanHandle(
                AiRuntimeInstanceCapacityDescriptor descriptor)
            {
                return true;
            }

            /// <inheritdoc />
            public Task<AiSharedRuntimeInstanceDispatchResult> DispatchAsync(
                AiRuntimeInstanceCapacityDescriptor descriptor,
                AiSharedRuntimeInstanceDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                DispatchCalls++;

                return Task.FromResult(Result);
            }
        }

        /// <summary>
        /// Fake runtime recovery forensics recorder.
        /// </summary>
        private sealed class FakeRuntimeRecoveryForensicsRecorder : IAiRuntimeRecoveryForensicsRecorder
        {
            /// <summary>
            /// Gets recorded recovery forensics events.
            /// </summary>
            public List<AiRuntimeRecoveryForensicsEvent> Events { get; } = [];

            /// <inheritdoc />
            public Task RecordAsync(
                AiRuntimeRecoveryForensicsRecord record,
                CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public Task RecordEventAsync(
                AiRuntimeRecoveryForensicsEvent recoveryEvent,
                CancellationToken cancellationToken = default)
            {
                Events.Add(recoveryEvent);

                return Task.CompletedTask;
            }
        }
    }
}