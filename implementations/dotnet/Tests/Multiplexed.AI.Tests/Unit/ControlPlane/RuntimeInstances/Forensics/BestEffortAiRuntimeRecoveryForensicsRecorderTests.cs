using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.RuntimeInstances.Forensics;
using Multiplexed.AI.Runtime.ControlPlane.RuntimeInstances.Forensics;
using Xunit;
using Multiplexed.Abstractions.AI.Observability.Events;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.RuntimeInstances.Forensics
{
    /// <summary>
    /// Tests the best-effort runtime recovery forensics recorder.
    /// </summary>
    public sealed class BestEffortAiRuntimeRecoveryForensicsRecorderTests
    {
        /// <summary>
        /// Verifies that the recorder stores records when enabled.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Store_Record_When_Enabled()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: true, strictPersistence: false);

            await recorder.RecordAsync(CreateRecord("forensics-recorder-001", "execution-recorder-001"));

            var loaded = await store.GetByForensicsIdAsync("forensics-recorder-001");

            loaded.Should().NotBeNull();
            loaded!.Identity.ExecutionId.Should().Be("execution-recorder-001");
        }

        /// <summary>
        /// Verifies that the recorder ignores records when disabled.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Not_Store_Record_When_Disabled()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: false, strictPersistence: false);

            await recorder.RecordAsync(CreateRecord("forensics-recorder-002", "execution-recorder-002"));

            var loaded = await store.GetByForensicsIdAsync("forensics-recorder-002");

            loaded.Should().BeNull();
        }

        /// <summary>
        /// Verifies that the recorder stores events when enabled.
        /// </summary>
        [Fact]
        public async Task RecordEventAsync_Should_Append_Event_When_Enabled()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: true, strictPersistence: false);

            await recorder.RecordAsync(CreateRecord("forensics-recorder-003", "execution-recorder-003"));
            await recorder.RecordEventAsync(CreateEvent("event-recorder-003", "forensics-recorder-003", "execution-recorder-003"));

            var loaded = await store.GetByForensicsIdAsync("forensics-recorder-003");

            loaded.Should().NotBeNull();
            loaded!.Events.Should().ContainSingle(x => x.EventId == "event-recorder-003");
        }

        /// <summary>
        /// Verifies that event recording is ignored when disabled.
        /// </summary>
        [Fact]
        public async Task RecordEventAsync_Should_Not_Append_Event_When_Disabled()
        {
            var store = new InMemoryAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: false, strictPersistence: false);

            await recorder.RecordAsync(CreateRecord("forensics-recorder-004", "execution-recorder-004"));
            await recorder.RecordEventAsync(CreateEvent("event-recorder-004", "forensics-recorder-004", "execution-recorder-004"));

            var loaded = await store.GetByForensicsIdAsync("forensics-recorder-004");

            loaded.Should().BeNull();
        }

        /// <summary>
        /// Verifies that persistence failures are swallowed when strict persistence is disabled.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Not_Throw_When_Store_Fails_And_StrictPersistence_Is_Disabled()
        {
            var store = new ThrowingAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: true, strictPersistence: false);

            var act = async () => await recorder.RecordAsync(CreateRecord("forensics-recorder-005", "execution-recorder-005"));

            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Verifies that persistence failures are thrown when strict persistence is enabled.
        /// </summary>
        [Fact]
        public async Task RecordAsync_Should_Throw_When_Store_Fails_And_StrictPersistence_Is_Enabled()
        {
            var store = new ThrowingAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: true, strictPersistence: true);

            var act = async () => await recorder.RecordAsync(CreateRecord("forensics-recorder-006", "execution-recorder-006"));

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        /// <summary>
        /// Verifies that event persistence failures are swallowed when strict persistence is disabled.
        /// </summary>
        [Fact]
        public async Task RecordEventAsync_Should_Not_Throw_When_Store_Fails_And_StrictPersistence_Is_Disabled()
        {
            var store = new ThrowingAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: true, strictPersistence: false);

            var act = async () => await recorder.RecordEventAsync(CreateEvent("event-recorder-007", "forensics-recorder-007", "execution-recorder-007"));

            await act.Should().NotThrowAsync();
        }

        /// <summary>
        /// Verifies that event persistence failures are thrown when strict persistence is enabled.
        /// </summary>
        [Fact]
        public async Task RecordEventAsync_Should_Throw_When_Store_Fails_And_StrictPersistence_Is_Enabled()
        {
            var store = new ThrowingAiRuntimeRecoveryForensicsStore();
            var recorder = CreateRecorder(store, enabled: true, strictPersistence: true);

            var act = async () => await recorder.RecordEventAsync(CreateEvent("event-recorder-008", "forensics-recorder-008", "execution-recorder-008"));

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        /// <summary>
        /// Creates a best-effort recorder for tests.
        /// </summary>
        /// <param name="store">The forensics store.</param>
        /// <param name="enabled">A value indicating whether forensics is enabled.</param>
        /// <param name="strictPersistence">A value indicating whether persistence is strict.</param>
        /// <returns>The recorder.</returns>
        private static BestEffortAiRuntimeRecoveryForensicsRecorder CreateRecorder(
            IAiRuntimeRecoveryForensicsStore store,
            bool enabled,
            bool strictPersistence)
        {
            return new BestEffortAiRuntimeRecoveryForensicsRecorder(
                store,
                Options.Create(new AiRuntimeRecoveryForensicsOptions
                {
                    Enabled = enabled,
                    StrictPersistence = strictPersistence,
                    MaxEventsPerRecord = 500
                }),
                NullLogger<BestEffortAiRuntimeRecoveryForensicsRecorder>.Instance);
        }

        /// <summary>
        /// Creates a recovery forensics record for tests.
        /// </summary>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <returns>The recovery forensics record.</returns>
        private static AiRuntimeRecoveryForensicsRecord CreateRecord(string forensicsId, string executionId)
        {
            return new AiRuntimeRecoveryForensicsRecord
            {
                Identity = new AiRuntimeRecoveryForensicsIdentity
                {
                    ForensicsId = forensicsId,
                    ExecutionId = executionId,
                    SharedRunId = $"shared-{executionId}",
                    PipelineName = "pipeline-recorder-test",
                    TenantId = "tenant-a",
                    TenantGroupId = "tenant-group-a",
                    ControlPlaneId = "control-plane-test"
                },
                Recovery = new AiRuntimeRecoveryInfo
                {
                    RecoveryMode = "resume-existing-execution",
                    RecoveryKind = "in-flight-execution-resume",
                    Outcome = "started",
                    Reason = "test"
                },
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Creates a recovery forensics event for tests.
        /// </summary>
        /// <param name="eventId">The event identifier.</param>
        /// <param name="forensicsId">The forensics identifier.</param>
        /// <param name="executionId">The durable execution identifier.</param>
        /// <returns>The recovery forensics event.</returns>
        private static AiRuntimeRecoveryForensicsEvent CreateEvent(string eventId, string forensicsId, string executionId)
        {
            return new AiRuntimeRecoveryForensicsEvent
            {
                EventId = eventId,
                ForensicsId = forensicsId,
                TimestampUtc = DateTimeOffset.UtcNow,
                EventType = AiEngineEvents.Recovery.ExecutionRecoveryCandidateDetected,
                Outcome = "ok",
                Reason = "test",
                ExecutionId = executionId,
                SharedRunId = $"shared-{executionId}",
                RuntimeInstanceId = "runtime-1"
            };
        }

        /// <summary>
        /// Store implementation that always fails.
        /// </summary>
        private sealed class ThrowingAiRuntimeRecoveryForensicsStore : IAiRuntimeRecoveryForensicsStore
        {
            /// <inheritdoc />
            public Task UpsertAsync(AiRuntimeRecoveryForensicsRecord record, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task AppendEventAsync(string forensicsId, AiRuntimeRecoveryForensicsEvent evt, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task<AiRuntimeRecoveryForensicsRecord?> GetByForensicsIdAsync(string forensicsId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByExecutionIdAsync(string executionId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListBySharedRunIdAsync(string sharedRunId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeInstanceIdAsync(string runtimeInstanceId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListByRuntimeFailureIncidentIdAsync(string runtimeFailureIncidentId, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }

            /// <inheritdoc />
            public Task<IReadOnlyList<AiRuntimeRecoveryForensicsRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("Store failure.");
            }
        }
    }
}