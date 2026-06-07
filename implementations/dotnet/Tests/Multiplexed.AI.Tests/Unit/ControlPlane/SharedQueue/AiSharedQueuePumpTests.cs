using Microsoft.Extensions.Options;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Dispatch;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;

namespace Multiplexed.AI.Tests.Unit.ControlPlane.SharedQueue
{
    public sealed class AiSharedQueuePumpTests
    {
        [Fact]
        public async Task PumpOnceAsync_Should_Return_NoItemAvailable_When_Dispatcher_Has_No_Item()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(
                    new[]
                    {
                        new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            NoItemAvailable = true,
                            RuntimeInstanceId = "runtime-1",
                            Message = "No pending shared queue item is available.",
                            StartedAtUtc = DateTimeOffset.UtcNow,
                            CompletedAtUtc = DateTimeOffset.UtcNow
                        }
                    }),
                Options.Create(new AiSharedQueuePumpOptions
                {
                    MaxDispatchesPerCycle = 10
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal(1, result.AttemptedDispatchCount);
            Assert.Equal(0, result.SuccessfulDispatchCount);
            Assert.Equal(0, result.FailedDispatchCount);
            Assert.True(result.StoppedBecauseNoItemAvailable);
            Assert.Single(result.DispatchResults);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Dispatch_Multiple_Items_Until_NoItemAvailable()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(
                    new[]
                    {
                        CreateDispatchSuccess("shared-run-1"),
                        CreateDispatchSuccess("shared-run-2"),
                        new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            NoItemAvailable = true,
                            RuntimeInstanceId = "runtime-1",
                            StartedAtUtc = DateTimeOffset.UtcNow,
                            CompletedAtUtc = DateTimeOffset.UtcNow
                        }
                    }),
                Options.Create(new AiSharedQueuePumpOptions
                {
                    MaxDispatchesPerCycle = 10
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.Equal(3, result.AttemptedDispatchCount);
            Assert.Equal(2, result.SuccessfulDispatchCount);
            Assert.Equal(0, result.FailedDispatchCount);
            Assert.True(result.StoppedBecauseNoItemAvailable);
            Assert.Equal(3, result.DispatchResults.Count);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Respect_Request_MaxDispatches()
        {
            var dispatcher = new FakeSharedQueueDispatcher(
                new[]
                {
                    CreateDispatchSuccess("shared-run-1"),
                    CreateDispatchSuccess("shared-run-2"),
                    CreateDispatchSuccess("shared-run-3")
                });

            var pump = new AiSharedQueuePump(
                dispatcher,
                Options.Create(new AiSharedQueuePumpOptions
                {
                    MaxDispatchesPerCycle = 10
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1",
                MaxDispatches = 2
            });

            Assert.True(result.Success);
            Assert.Equal(2, result.AttemptedDispatchCount);
            Assert.Equal(2, result.SuccessfulDispatchCount);
            Assert.Equal(0, result.FailedDispatchCount);
            Assert.False(result.StoppedBecauseNoItemAvailable);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Use_Options_MaxDispatches_When_Request_MaxDispatches_Is_Null()
        {
            var dispatcher = new FakeSharedQueueDispatcher(
                new[]
                {
                    CreateDispatchSuccess("shared-run-1"),
                    CreateDispatchSuccess("shared-run-2"),
                    CreateDispatchSuccess("shared-run-3")
                });

            var pump = new AiSharedQueuePump(
                dispatcher,
                Options.Create(new AiSharedQueuePumpOptions
                {
                    MaxDispatchesPerCycle = 2
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.Equal(2, result.AttemptedDispatchCount);
            Assert.Equal(2, result.SuccessfulDispatchCount);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Continue_On_Dispatch_Failure_When_Option_Is_Disabled()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(
                    new[]
                    {
                        CreateDispatchFailure("shared-run-1", "dispatch failed"),
                        CreateDispatchSuccess("shared-run-2"),
                        new AiSharedQueueDispatchResult
                        {
                            Success = false,
                            NoItemAvailable = true,
                            RuntimeInstanceId = "runtime-1",
                            StartedAtUtc = DateTimeOffset.UtcNow,
                            CompletedAtUtc = DateTimeOffset.UtcNow
                        }
                    }),
                Options.Create(new AiSharedQueuePumpOptions
                {
                    MaxDispatchesPerCycle = 10,
                    StopCycleOnDispatchFailure = false
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.Equal(3, result.AttemptedDispatchCount);
            Assert.Equal(1, result.SuccessfulDispatchCount);
            Assert.Equal(1, result.FailedDispatchCount);
            Assert.True(result.StoppedBecauseNoItemAvailable);
            Assert.Contains("dispatch failed", result.Diagnostics);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Stop_On_Dispatch_Failure_When_Option_Is_Enabled()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(
                    new[]
                    {
                        CreateDispatchFailure("shared-run-1", "dispatch failed"),
                        CreateDispatchSuccess("shared-run-2")
                    }),
                Options.Create(new AiSharedQueuePumpOptions
                {
                    MaxDispatchesPerCycle = 10,
                    StopCycleOnDispatchFailure = true
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.True(result.Success);
            Assert.Equal(1, result.AttemptedDispatchCount);
            Assert.Equal(0, result.SuccessfulDispatchCount);
            Assert.Equal(1, result.FailedDispatchCount);
            Assert.False(result.StoppedBecauseNoItemAvailable);
            Assert.Contains("dispatch failed", result.Diagnostics);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Return_Failure_When_Disabled()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(Array.Empty<AiSharedQueueDispatchResult>()),
                Options.Create(new AiSharedQueuePumpOptions
                {
                    Enabled = false
                }));

            var result = await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.False(result.Success);
            Assert.Equal("runtime-1", result.RuntimeInstanceId);
            Assert.Equal("Shared queue pump is disabled.", result.FailureReason);
            Assert.Empty(result.DispatchResults);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Pass_Request_Context_To_Dispatcher()
        {
            var dispatcher = new FakeSharedQueueDispatcher(
                new[]
                {
                    CreateDispatchSuccess("shared-run-1")
                });

            var pump = new AiSharedQueuePump(
                dispatcher,
                Options.Create(new AiSharedQueuePumpOptions
                {
                    WorkerId = "option-worker",
                    Source = "option-source"
                }));

            await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1",
                PumpWorkerId = "request-worker",
                TenantId = "tenant-1",
                PipelineKey = "pipeline-1",
                ClaimTtl = TimeSpan.FromSeconds(45),
                CorrelationId = "correlation-1",
                RequestedBy = "tester",
                Source = "request-source",
                Reason = "pump test",
                Metadata = new Dictionary<string, string>
                {
                    ["key"] = "value"
                }
            });

            Assert.NotNull(dispatcher.LastRequest);
            Assert.Equal("runtime-1", dispatcher.LastRequest!.RuntimeInstanceId);
            Assert.Equal("request-worker", dispatcher.LastRequest.WorkerId);
            Assert.Equal("tenant-1", dispatcher.LastRequest.TenantId);
            Assert.Equal("pipeline-1", dispatcher.LastRequest.PipelineKey);
            Assert.Equal(TimeSpan.FromSeconds(45), dispatcher.LastRequest.ClaimTtl);
            Assert.Equal("correlation-1", dispatcher.LastRequest.CorrelationId);
            Assert.Equal("tester", dispatcher.LastRequest.RequestedBy);
            Assert.Equal("request-source", dispatcher.LastRequest.Source);
            Assert.Equal("pump test", dispatcher.LastRequest.Reason);
            Assert.Equal("value", dispatcher.LastRequest.Metadata["key"]);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Use_Option_Worker_And_Source_When_Request_Does_Not_Provide_Them()
        {
            var dispatcher = new FakeSharedQueueDispatcher(
                new[]
                {
                    CreateDispatchSuccess("shared-run-1")
                });

            var pump = new AiSharedQueuePump(
                dispatcher,
                Options.Create(new AiSharedQueuePumpOptions
                {
                    WorkerId = "option-worker",
                    Source = "option-source"
                }));

            await pump.PumpOnceAsync(new AiSharedQueuePumpRequest
            {
                PumpRuntimeInstanceId = "runtime-1"
            });

            Assert.NotNull(dispatcher.LastRequest);
            Assert.Equal("option-worker", dispatcher.LastRequest!.WorkerId);
            Assert.Equal("option-source", dispatcher.LastRequest.Source);
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Throw_When_Request_Is_Null()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(Array.Empty<AiSharedQueueDispatchResult>()),
                Options.Create(new AiSharedQueuePumpOptions()));

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                pump.PumpOnceAsync(null!));
        }

        [Fact]
        public async Task PumpOnceAsync_Should_Throw_When_RuntimeInstanceId_Is_Missing()
        {
            var pump = new AiSharedQueuePump(
                new FakeSharedQueueDispatcher(Array.Empty<AiSharedQueueDispatchResult>()),
                Options.Create(new AiSharedQueuePumpOptions()));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                pump.PumpOnceAsync(new AiSharedQueuePumpRequest
                {
                    PumpRuntimeInstanceId = " "
                }));
        }

        private static AiSharedQueueDispatchResult CreateDispatchSuccess(
            string sharedRunId)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueDispatchResult
            {
                Success = true,
                SharedRunId = sharedRunId,
                RuntimeInstanceId = "runtime-1",
                Message = "Dispatched.",
                StartedAtUtc = now,
                CompletedAtUtc = now
            };
        }

        private static AiSharedQueueDispatchResult CreateDispatchFailure(
            string sharedRunId,
            string failureReason)
        {
            var now = DateTimeOffset.UtcNow;

            return new AiSharedQueueDispatchResult
            {
                Success = false,
                SharedRunId = sharedRunId,
                RuntimeInstanceId = "runtime-1",
                FailureReason = failureReason,
                Message = "Dispatch failed.",
                StartedAtUtc = now,
                CompletedAtUtc = now
            };
        }

        private sealed class FakeSharedQueueDispatcher : IAiSharedQueueDispatcher
        {
            private readonly Queue<AiSharedQueueDispatchResult> _results;

            public FakeSharedQueueDispatcher(
                IEnumerable<AiSharedQueueDispatchResult> results)
            {
                _results = new Queue<AiSharedQueueDispatchResult>(results);
            }

            public AiSharedQueueDispatchRequest? LastRequest { get; private set; }

            public List<AiSharedQueueDispatchRequest> Requests { get; } = new();

            public Task<AiSharedQueueDispatchResult> DispatchNextAsync(
                AiSharedQueueDispatchRequest request,
                CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                Requests.Add(request);

                if (_results.Count == 0)
                {
                    return Task.FromResult(new AiSharedQueueDispatchResult
                    {
                        Success = false,
                        NoItemAvailable = true,
                        RuntimeInstanceId = request.RuntimeInstanceId,
                        Message = "No pending shared queue item is available.",
                        StartedAtUtc = DateTimeOffset.UtcNow,
                        CompletedAtUtc = DateTimeOffset.UtcNow
                    });
                }

                var result = _results.Dequeue();

                return Task.FromResult(new AiSharedQueueDispatchResult
                {
                    Success = result.Success,
                    NoItemAvailable = result.NoItemAvailable,
                    SharedRunId = result.SharedRunId,
                    RuntimeInstanceId = request.RuntimeInstanceId,
                    QueueItem = result.QueueItem,
                    SharedRun = result.SharedRun,
                    DispatchResult = result.DispatchResult,
                    Message = result.Message,
                    FailureReason = result.FailureReason,
                    StartedAtUtc = result.StartedAtUtc,
                    CompletedAtUtc = result.CompletedAtUtc,
                    DurationMs = result.DurationMs,
                    Diagnostics = result.Diagnostics
                });
            }
        }
    }
}