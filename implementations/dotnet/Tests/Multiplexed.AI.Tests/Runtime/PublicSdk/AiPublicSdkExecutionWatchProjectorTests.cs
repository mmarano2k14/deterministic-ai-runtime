using Multiplexed.Abstractions.AI.Observability.Context;
using Multiplexed.Abstractions.AI.Observability.Events;
using Multiplexed.Abstractions.AI.Observability.Ledger;
using Multiplexed.AI.McpServer.PublicSdk;
using Multiplexed.AI.Observability.Ledger;
using Multiplexed.AI.Sdk.Contracts.Watch;

namespace Multiplexed.AI.Tests.Runtime.PublicSdk
{
    public sealed class AiPublicSdkExecutionWatchProjectorTests
    {
        [Fact]
        public async Task Public_Sequence_Ignores_Internal_Only_Ledger_Entries()
        {
            const string executionId = "execution-watch-sequence";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Claim.Acquired, stepId: "step-a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Step.Started, stepId: "step-a", stepKey: "step.a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Claim.LeaseRenewed, stepId: "step-a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Policy.Evaluated, stepId: "step-a", policyKey: "risk"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Claim.Released, stepId: "step-a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Completed));

            var first = await projector.WaitForNextAsync(executionId, Array.Empty<AiSdkExecutionWatchChannel>(), 0);
            var second = await projector.WaitForNextAsync(executionId, Array.Empty<AiSdkExecutionWatchChannel>(), first.Sequence!.Value);
            var third = await projector.WaitForNextAsync(executionId, Array.Empty<AiSdkExecutionWatchChannel>(), second.Sequence!.Value);

            Assert.Equal(1, first.Sequence);
            Assert.Equal(AiEngineEvents.Step.Started, first.EventType);
            Assert.Equal(2, second.Sequence);
            Assert.Equal(AiEngineEvents.Policy.Evaluated, second.EventType);
            Assert.Equal(3, third.Sequence);
            Assert.Equal(AiEngineEvents.Execution.Completed, third.EventType);
            Assert.Equal(3, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Channel_Filter_Does_Not_Expose_Unselected_Or_Internal_Payloads()
        {
            const string executionId = "execution-watch-filter";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Step.Started,
                stepId: "step-a",
                stepKey: "step.a",
                runtimeInstanceId: "runtime-secret",
                workerId: "worker-secret",
                claimToken: "claim-secret"));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Policy.Allowed,
                stepId: "step-a",
                policyKey: "risk",
                runtimeInstanceId: "runtime-secret",
                workerId: "worker-secret",
                claimToken: "claim-secret"));

            var item = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Policies },
                afterSequence: 0);

            Assert.Equal(2, item.Sequence);
            Assert.Equal(AiSdkExecutionWatchChannel.Policies, item.Channel);
            Assert.Equal(AiEngineEvents.Policy.Allowed, item.EventType);

            Assert.True(item.Payload.HasValue);
            var payload = item.Payload.Value.GetRawText();
            Assert.DoesNotContain("runtime-secret", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("worker-secret", payload, StringComparison.Ordinal);
            Assert.DoesNotContain("claim-secret", payload, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Policy_Skipped_Does_Not_Consume_Public_Sequence()
        {
            const string executionId = "execution-watch-policy-skip";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Policy.Skipped,
                stepId: "step-a",
                policyKey: "optional-policy"));

            Assert.Equal(0, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Durable_Recovery_Ledger_Facts_Project_To_Public_Recovery_Semantics()
        {
            const string executionId = "execution-watch-recovery";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.Detected,
                category: AiDecisionLedgerCategory.Recovery));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.Applied,
                category: AiDecisionLedgerCategory.Recovery));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.StepRecovered,
                stepId: "step-a",
                stepKey: "step.a",
                category: AiDecisionLedgerCategory.Recovery));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.ExecutionRecovered,
                category: AiDecisionLedgerCategory.Recovery));

            var first = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Recovery },
                afterSequence: 0);
            var second = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Recovery },
                afterSequence: first.Sequence!.Value);
            var third = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Recovery },
                afterSequence: second.Sequence!.Value);
            var fourth = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Recovery },
                afterSequence: third.Sequence!.Value);

            Assert.Equal(1, first.Sequence);
            Assert.Equal(AiSdkExecutionWatchChannel.Recovery, first.Channel);
            Assert.Equal("recovery.started", first.EventType);

            Assert.Equal(2, second.Sequence);
            Assert.Equal(AiSdkExecutionWatchChannel.Recovery, second.Channel);
            Assert.Equal("recovery.resumed", second.EventType);

            Assert.Equal(3, third.Sequence);
            Assert.Equal(AiSdkExecutionWatchChannel.Recovery, third.Channel);
            Assert.Equal("recovery.resumed", third.EventType);

            Assert.Equal(4, fourth.Sequence);
            Assert.Equal(AiSdkExecutionWatchChannel.Recovery, fourth.Channel);
            Assert.Equal("recovery.completed", fourth.EventType);
            Assert.Equal(4, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Recovery_Forensics_Owned_Events_Do_Not_Enter_Decision_Ledger_Watch_Sequence()
        {
            const string executionId = "execution-watch-recovery-forensics";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.ExecutionRecoveryCandidateDetected,
                category: AiDecisionLedgerCategory.Recovery));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.DagResumeStarted,
                category: AiDecisionLedgerCategory.Recovery));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.ExecutionRecoveryCompleted,
                category: AiDecisionLedgerCategory.Recovery));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Recovery.ExecutionRecoveryFailed,
                category: AiDecisionLedgerCategory.Recovery));

            Assert.Equal(0, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Human_Input_Ledger_Facts_Remain_Internal_Until_A_Public_Watch_Channel_Is_Versioned()
        {
            const string executionId = "execution-watch-human-input";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.HumanInput.Requested,
                stepId: "approval",
                category: AiDecisionLedgerCategory.HumanInput));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.HumanInput.Waiting,
                stepId: "approval",
                category: AiDecisionLedgerCategory.HumanInput));
            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.HumanInput.Submitted,
                stepId: "approval",
                category: AiDecisionLedgerCategory.HumanInput));

            Assert.Equal(0, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Effect_Like_Ledger_Facts_Are_Not_Synthesized_Without_A_Proven_Durable_Public_Source()
        {
            const string executionId = "execution-watch-effect-source";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                "effect.completed",
                category: AiDecisionLedgerCategory.Control));

            Assert.Equal(0, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Parked_Step_Remains_The_Public_Generic_Waiting_For_External_Semantic()
        {
            const string executionId = "execution-watch-parked-external";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(
                executionId,
                AiEngineEvents.Step.Parked,
                stepId: "approval",
                stepKey: "approval",
                category: AiDecisionLedgerCategory.Step));

            var item = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Steps },
                afterSequence: 0);

            Assert.Equal(AiSdkExecutionWatchChannel.Steps, item.Channel);
            Assert.Equal(AiEngineEvents.Step.Parked, item.EventType);
            Assert.True(item.Payload.HasValue);
            Assert.Equal(
                nameof(Multiplexed.AI.Sdk.Contracts.Observation.AiSdkExecutionStepStatus.WaitingForExternal),
                item.Payload.Value.GetProperty("status").GetString());
        }

        [Fact]
        public async Task Cursor_Beyond_Public_High_Water_Returns_Resync_Required()
        {
            const string executionId = "execution-watch-invalid-cursor";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Created));

            var item = await projector.WaitForNextAsync(
                executionId,
                Array.Empty<AiSdkExecutionWatchChannel>(),
                afterSequence: 2);

            Assert.Equal(AiSdkExecutionWatchEventKind.ResyncRequired, item.Kind);
            Assert.NotNull(item.ResyncRequired);
            Assert.Equal(AiSdkExecutionWatchResyncReason.InvalidCursor, item.ResyncRequired!.Reason);
            Assert.Equal(2, item.ResyncRequired.RequestedAfterSequence);
            Assert.Equal(1, item.ResyncRequired.EarliestAvailableSequence);
            Assert.Equal(1, item.ResyncRequired.LatestSequence);
        }


        [Fact]
        public async Task Retention_Window_Rejects_Expired_Public_Cursor()
        {
            const string executionId = "execution-watch-retention-expired";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(
                ledger,
                new AiPublicSdkExecutionWatchOptions { RetainedPublicEventLimit = 3 });

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Created));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Step.Started, stepId: "step-a", stepKey: "step.a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Policy.Evaluated, stepId: "step-a", policyKey: "risk"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Completed));

            var item = await projector.WaitForNextAsync(
                executionId,
                Array.Empty<AiSdkExecutionWatchChannel>(),
                afterSequence: 1);

            Assert.Equal(AiSdkExecutionWatchEventKind.ResyncRequired, item.Kind);
            Assert.NotNull(item.ResyncRequired);
            Assert.Equal(AiSdkExecutionWatchResyncReason.HistoryUnavailable, item.ResyncRequired!.Reason);
            Assert.Equal(1, item.ResyncRequired.RequestedAfterSequence);
            Assert.Equal(3, item.ResyncRequired.EarliestAvailableSequence);
            Assert.Equal(5, item.ResyncRequired.LatestSequence);
            Assert.Equal(5, await projector.GetLatestSequenceAsync(executionId));
        }

        [Fact]
        public async Task Retention_Window_Allows_Cursor_Immediately_Before_Earliest_Available_Event()
        {
            const string executionId = "execution-watch-retention-boundary";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(
                ledger,
                new AiPublicSdkExecutionWatchOptions { RetainedPublicEventLimit = 3 });

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Created));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Step.Started, stepId: "step-a", stepKey: "step.a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Policy.Evaluated, stepId: "step-a", policyKey: "risk"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Completed));

            var item = await projector.WaitForNextAsync(
                executionId,
                Array.Empty<AiSdkExecutionWatchChannel>(),
                afterSequence: 2);

            Assert.Equal(AiSdkExecutionWatchEventKind.Event, item.Kind);
            Assert.Equal(3, item.Sequence);
            Assert.Equal(AiEngineEvents.Step.Started, item.EventType);
        }

        [Fact]
        public async Task Terminal_Lifecycle_Event_Converges_Narrow_Channel_Watch()
        {
            const string executionId = "execution-watch-terminal";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Completed));

            var item = await projector.WaitForNextAsync(
                executionId,
                new[] { AiSdkExecutionWatchChannel.Policies },
                afterSequence: 0);

            Assert.Equal(AiSdkExecutionWatchEventKind.Event, item.Kind);
            Assert.Equal(AiSdkExecutionWatchChannel.Lifecycle, item.Channel);
            Assert.Equal(AiEngineEvents.Execution.Completed, item.EventType);
            Assert.Equal(1, item.Sequence);
        }

        [Fact]
        public async Task Historical_Scan_Uses_Bounded_Query_Pages_Without_GetByExecution()
        {
            const string executionId = "execution-watch-bounded-read";
            var ledger = new RecordingDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(
                ledger,
                new AiPublicSdkExecutionWatchOptions
                {
                    RetainedPublicEventLimit = 32,
                    LedgerReadBatchSize = 2,
                    PollInterval = TimeSpan.Zero
                });

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Created));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Claim.Acquired, stepId: "step-a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Step.Started, stepId: "step-a", stepKey: "step.a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Policy.Evaluated, stepId: "step-a", policyKey: "risk"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Completed));

            Assert.Equal(4, await projector.GetLatestSequenceAsync(executionId));

            var item = await projector.WaitForNextAsync(
                executionId,
                Array.Empty<AiSdkExecutionWatchChannel>(),
                afterSequence: 2);

            Assert.Equal(AiSdkExecutionWatchEventKind.Event, item.Kind);
            Assert.Equal(3, item.Sequence);
            Assert.Equal(AiEngineEvents.Policy.Evaluated, item.EventType);
            Assert.Equal(0, ledger.GetByExecutionCallCount);
            Assert.NotEmpty(ledger.Queries);
            Assert.All(ledger.Queries, query => Assert.Equal(2, query.Limit));
        }

        [Fact]
        public async Task Slow_Consumer_Outside_Public_Window_Resynchronizes_Without_Server_Buffering()
        {
            const string executionId = "execution-watch-slow-consumer";
            var ledger = new RecordingDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(
                ledger,
                new AiPublicSdkExecutionWatchOptions
                {
                    RetainedPublicEventLimit = 3,
                    LedgerReadBatchSize = 2,
                    PollInterval = TimeSpan.Zero
                });

            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Created));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Started));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Step.Started, stepId: "step-a", stepKey: "step.a"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Policy.Evaluated, stepId: "step-a", policyKey: "risk"));
            await ledger.AppendAsync(CreateEntry(executionId, AiEngineEvents.Execution.Completed));

            var item = await projector.WaitForNextAsync(
                executionId,
                Array.Empty<AiSdkExecutionWatchChannel>(),
                afterSequence: 1);

            Assert.Equal(AiSdkExecutionWatchEventKind.ResyncRequired, item.Kind);
            Assert.NotNull(item.ResyncRequired);
            Assert.Equal(AiSdkExecutionWatchResyncReason.HistoryUnavailable, item.ResyncRequired!.Reason);
            Assert.Equal(3, item.ResyncRequired.EarliestAvailableSequence);
            Assert.Equal(5, item.ResyncRequired.LatestSequence);
            Assert.Equal(0, ledger.GetByExecutionCallCount);
            Assert.All(ledger.Queries, query => Assert.Equal(2, query.Limit));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4097)]
        public void Invalid_Ledger_Read_Batch_Size_Fails_Fast(int batchSize)
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AiPublicSdkExecutionWatchProjector(
                    new InMemoryAiDecisionLedger(),
                    new AiPublicSdkExecutionWatchOptions
                    {
                        LedgerReadBatchSize = batchSize
                    }));

            Assert.Contains("LedgerReadBatchSize", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Negative_Poll_Interval_Fails_Fast()
        {
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new AiPublicSdkExecutionWatchProjector(
                    new InMemoryAiDecisionLedger(),
                    new AiPublicSdkExecutionWatchOptions
                    {
                        PollInterval = TimeSpan.FromMilliseconds(-1)
                    }));

            Assert.Contains("PollInterval", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Cancelled_Wait_Stops_Only_The_Watch_Request()
        {
            const string executionId = "execution-watch-cancel";
            var ledger = new InMemoryAiDecisionLedger();
            var projector = new AiPublicSdkExecutionWatchProjector(ledger);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                projector.WaitForNextAsync(
                    executionId,
                    Array.Empty<AiSdkExecutionWatchChannel>(),
                    afterSequence: 0,
                    cancellation.Token));

            Assert.Empty(await ledger.GetByExecutionAsync(executionId));
        }

        private sealed class RecordingDecisionLedger : IAiDecisionLedger
        {
            private readonly InMemoryAiDecisionLedger _inner = new();

            public int GetByExecutionCallCount { get; private set; }

            public List<AiDecisionLedgerQuery> Queries { get; } = new();

            public Task AppendAsync(
                AiDecisionLedgerEntry entry,
                CancellationToken cancellationToken = default) =>
                _inner.AppendAsync(entry, cancellationToken);

            public Task<IReadOnlyList<AiDecisionLedgerEntry>> GetByExecutionAsync(
                string executionId,
                CancellationToken cancellationToken = default)
            {
                GetByExecutionCallCount++;
                return _inner.GetByExecutionAsync(executionId, cancellationToken);
            }

            public Task<IReadOnlyList<AiDecisionLedgerEntry>> QueryAsync(
                AiDecisionLedgerQuery query,
                CancellationToken cancellationToken = default)
            {
                Queries.Add(query);
                return _inner.QueryAsync(query, cancellationToken);
            }
        }

        private static AiDecisionLedgerEntry CreateEntry(
            string executionId,
            string eventType,
            string? stepId = null,
            string? stepKey = null,
            string? policyKey = null,
            string? runtimeInstanceId = null,
            string? workerId = null,
            string? claimToken = null,
            AiDecisionLedgerCategory category = AiDecisionLedgerCategory.Execution)
        {
            return new AiDecisionLedgerEntry
            {
                EntryId = Guid.NewGuid().ToString("N"),
                CorrelationContext = new AiRuntimeLedgerEventCorrelationContext
                {
                    ExecutionId = executionId,
                    StepId = stepId,
                    StepKey = stepKey,
                    PolicyKey = policyKey,
                    RuntimeInstanceId = runtimeInstanceId,
                    WorkerId = workerId,
                    ClaimToken = claimToken
                },
                Category = category,
                EventType = eventType,
                Outcome = AiDecisionLedgerOutcome.None,
                TimestampUtc = DateTimeOffset.UtcNow
            };
        }
    }
}
