using System.Text.Json;
using Multiplexed.AI.Sdk.Contracts.Common;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Watch;
using Multiplexed.AI.Sdk.Errors;
using Multiplexed.AI.Sdk.Transport;

namespace Multiplexed.AI.Sdk.Tests.Client
{
    public sealed class AiSdkExecutionWatchClientTests
    {
        [Fact]
        public async Task Watch_Advances_Stateless_Cursor_And_Stops_After_Terminal_Item()
        {
            var transport = new RecordingWatchTransport(request =>
            {
                var watchRequest = ReadRequest(request);
                return watchRequest.AfterSequence switch
                {
                    null => Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 10,
                        Kind = AiSdkExecutionWatchEventKind.Snapshot,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch,
                        Snapshot = new AiSdkExecutionObservation
                        {
                            ExecutionId = watchRequest.ExecutionId,
                            Status = AiSdkExecutionStatus.Running,
                            CreatedAtUtc = DateTimeOffset.UnixEpoch,
                            UpdatedAtUtc = DateTimeOffset.UnixEpoch
                        }
                    }),
                    10 => Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 11,
                        Kind = AiSdkExecutionWatchEventKind.Event,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                        Channel = AiSdkExecutionWatchChannel.Steps,
                        EventType = "watch.test.progress"
                    }),
                    11 => Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 12,
                        Kind = AiSdkExecutionWatchEventKind.Event,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
                        Channel = AiSdkExecutionWatchChannel.Lifecycle,
                        EventType = "watch.test.terminal",
                        PayloadSchemaVersion = 1,
                        Payload = JsonSerializer.SerializeToElement(new { status = AiSdkExecutionStatus.Completed })
                    }),
                    _ => throw new InvalidOperationException("Watch requested an unexpected cursor.")
                };
            });
            IAiSdkClient client = new AiSdkClient(transport);

            var items = new List<AiSdkExecutionWatchEvent>();
            await foreach (var item in client.WatchExecutionAsync(new AiSdkExecutionWatchRequest
            {
                ExecutionId = "exec-watch",
                Channels = [AiSdkExecutionWatchChannel.Steps]
            }))
            {
                items.Add(item);
            }

            Assert.Equal(3, items.Count);
            Assert.Equal(AiSdkExecutionWatchEventKind.Snapshot, items[0].Kind);
            Assert.Equal(11, items[1].Sequence);
            Assert.Equal(12, items[2].Sequence);
            Assert.Equal(3, transport.Requests.Count);

            var first = ReadRequest(transport.Requests[0]);
            Assert.True(first.IncludeInitialSnapshot);
            Assert.Null(first.AfterSequence);
            Assert.Equal(new[] { AiSdkExecutionWatchChannel.Steps }, first.Channels);

            var second = ReadRequest(transport.Requests[1]);
            Assert.False(second.IncludeInitialSnapshot);
            Assert.Equal(10, second.AfterSequence);

            var third = ReadRequest(transport.Requests[2]);
            Assert.False(third.IncludeInitialSnapshot);
            Assert.Equal(11, third.AfterSequence);
            Assert.All(transport.Requests, request => Assert.Equal(AiSdkOperationNames.WatchExecution, request.Operation));
        }

        [Fact]
        public async Task Watch_Yields_ResyncRequired_Then_Automatically_Reestablishes_Snapshot_Boundary()
        {
            var transport = new RecordingWatchTransport(request =>
            {
                var watchRequest = ReadRequest(request);
                if (watchRequest.AfterSequence == 100)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Kind = AiSdkExecutionWatchEventKind.ResyncRequired,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch,
                        ResyncRequired = new AiSdkExecutionWatchResyncRequired
                        {
                            Reason = AiSdkExecutionWatchResyncReason.HistoryUnavailable,
                            RequestedAfterSequence = 100,
                            EarliestAvailableSequence = 200,
                            LatestSequence = 250
                        }
                    });
                }

                if (watchRequest.AfterSequence is null && watchRequest.IncludeInitialSnapshot)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 250,
                        Kind = AiSdkExecutionWatchEventKind.Snapshot,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                        Snapshot = new AiSdkExecutionObservation
                        {
                            ExecutionId = watchRequest.ExecutionId,
                            Status = AiSdkExecutionStatus.Running,
                            CreatedAtUtc = DateTimeOffset.UnixEpoch,
                            UpdatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1)
                        }
                    });
                }

                if (watchRequest.AfterSequence == 250)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 251,
                        Kind = AiSdkExecutionWatchEventKind.Event,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
                        Channel = AiSdkExecutionWatchChannel.Lifecycle,
                        EventType = "watch.test.terminal",
                        PayloadSchemaVersion = 1,
                        Payload = JsonSerializer.SerializeToElement(new { status = AiSdkExecutionStatus.Completed })
                    });
                }

                throw new InvalidOperationException("Watch requested an unexpected cursor.");
            });
            var client = new AiSdkClient(transport);

            var items = new List<AiSdkExecutionWatchEvent>();
            await foreach (var item in client.WatchExecutionAsync(new AiSdkExecutionWatchRequest
            {
                ExecutionId = "exec-watch",
                AfterSequence = 100,
                IncludeInitialSnapshot = false
            }))
            {
                items.Add(item);
            }

            Assert.Equal(3, items.Count);
            Assert.Equal(AiSdkExecutionWatchEventKind.ResyncRequired, items[0].Kind);
            Assert.Equal(AiSdkExecutionWatchEventKind.Snapshot, items[1].Kind);
            Assert.Equal(250, items[1].Sequence);
            Assert.Equal(251, items[2].Sequence);

            var snapshotRequest = ReadRequest(transport.Requests[1]);
            Assert.Null(snapshotRequest.AfterSequence);
            Assert.True(snapshotRequest.IncludeInitialSnapshot);
        }

        [Fact]
        public async Task Watch_Ignores_Exact_Duplicate_And_Continues_From_The_Same_Cursor()
        {
            var call = 0;
            var transport = new RecordingWatchTransport(request =>
            {
                var watchRequest = ReadRequest(request);
                call++;
                if (call == 1)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = watchRequest.AfterSequence,
                        Kind = AiSdkExecutionWatchEventKind.Event,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch,
                        Channel = AiSdkExecutionWatchChannel.Steps,
                        EventType = "watch.test.duplicate"
                    });
                }

                return Success(new AiSdkExecutionWatchEvent
                {
                    ExecutionId = watchRequest.ExecutionId,
                    Sequence = watchRequest.AfterSequence + 1,
                    Kind = AiSdkExecutionWatchEventKind.Event,
                    OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                    Channel = AiSdkExecutionWatchChannel.Lifecycle,
                    EventType = "watch.test.terminal",
                    PayloadSchemaVersion = 1,
                    Payload = JsonSerializer.SerializeToElement(new { status = AiSdkExecutionStatus.Completed })
                });
            });
            var client = new AiSdkClient(transport);

            var items = new List<AiSdkExecutionWatchEvent>();
            await foreach (var item in client.WatchExecutionAsync(new AiSdkExecutionWatchRequest
            {
                ExecutionId = "exec-watch",
                AfterSequence = 41,
                IncludeInitialSnapshot = false
            }))
            {
                items.Add(item);
            }

            var terminal = Assert.Single(items);
            Assert.Equal(42, terminal.Sequence);
            Assert.Equal(2, transport.Requests.Count);
            Assert.All(transport.Requests, item => Assert.Equal(41, ReadRequest(item).AfterSequence));
        }

        [Fact]
        public async Task Watch_Detects_Unfiltered_Gap_And_Resynchronizes_Before_Delivering_More_Events()
        {
            var transport = new RecordingWatchTransport(request =>
            {
                var watchRequest = ReadRequest(request);
                if (watchRequest.AfterSequence == 40)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 42,
                        Kind = AiSdkExecutionWatchEventKind.Event,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch,
                        Channel = AiSdkExecutionWatchChannel.Steps,
                        EventType = "watch.test.gapped"
                    });
                }

                if (watchRequest.AfterSequence is null && watchRequest.IncludeInitialSnapshot)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 42,
                        Kind = AiSdkExecutionWatchEventKind.Snapshot,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                        Snapshot = new AiSdkExecutionObservation
                        {
                            ExecutionId = watchRequest.ExecutionId,
                            Status = AiSdkExecutionStatus.Running,
                            CreatedAtUtc = DateTimeOffset.UnixEpoch,
                            UpdatedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1)
                        }
                    });
                }

                if (watchRequest.AfterSequence == 42)
                {
                    return Success(new AiSdkExecutionWatchEvent
                    {
                        ExecutionId = watchRequest.ExecutionId,
                        Sequence = 43,
                        Kind = AiSdkExecutionWatchEventKind.Event,
                        OccurredAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(2),
                        Channel = AiSdkExecutionWatchChannel.Lifecycle,
                        EventType = "watch.test.terminal",
                        PayloadSchemaVersion = 1,
                        Payload = JsonSerializer.SerializeToElement(new { status = AiSdkExecutionStatus.Completed })
                    });
                }

                throw new InvalidOperationException("Watch requested an unexpected cursor.");
            });
            var client = new AiSdkClient(transport);

            var items = new List<AiSdkExecutionWatchEvent>();
            await foreach (var item in client.WatchExecutionAsync(new AiSdkExecutionWatchRequest
            {
                ExecutionId = "exec-watch",
                AfterSequence = 40,
                IncludeInitialSnapshot = false
            }))
            {
                items.Add(item);
            }

            Assert.Equal(3, items.Count);
            Assert.Equal(AiSdkExecutionWatchEventKind.ResyncRequired, items[0].Kind);
            Assert.Equal(AiSdkExecutionWatchResyncReason.GapDetected, items[0].ResyncRequired!.Reason);
            Assert.Equal(40, items[0].ResyncRequired.RequestedAfterSequence);
            Assert.Equal(AiSdkExecutionWatchEventKind.Snapshot, items[1].Kind);
            Assert.Equal(42, items[1].Sequence);
            Assert.Equal(43, items[2].Sequence);
        }

        [Fact]
        public async Task Watch_Request_Cancellation_Stops_Only_The_Observation()
        {
            var transport = new BlockingTransport();
            var client = new AiSdkClient(transport);
            using var cancellation = new CancellationTokenSource();

            await using var enumerator = client.WatchExecutionAsync(
                    new AiSdkExecutionWatchRequest { ExecutionId = "exec-watch" },
                    cancellation.Token)
                .GetAsyncEnumerator();

            var moveNext = enumerator.MoveNextAsync().AsTask();
            await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveNext);
            Assert.Single(transport.Requests);
            Assert.Equal(AiSdkOperationNames.WatchExecution, transport.Requests[0].Operation);
            Assert.DoesNotContain(
                transport.Requests,
                request => string.Equals(request.Operation, AiSdkOperationNames.CancelExecution, StringComparison.Ordinal));
        }

        [Fact]
        public void Watch_Rejects_Unsupported_Request_Schema_Before_Transport()
        {
            var transport = new RecordingWatchTransport(_ => throw new InvalidOperationException("Must not be invoked."));
            var client = new AiSdkClient(transport);

            var exception = Assert.Throws<AiSdkException>(() =>
            {
                _ = client.WatchExecutionAsync(new AiSdkExecutionWatchRequest
                {
                    SchemaVersion = AiSdkSchemaVersions.ExecutionWatchRequest + 1,
                    ExecutionId = "exec-watch"
                });
            });

            Assert.Equal(AiSdkErrorKind.UnsupportedSchema, exception.Error.Kind);
            Assert.Empty(transport.Requests);
        }

        [Fact]
        public async Task Watch_Fails_Closed_When_Response_Regresses_Behind_The_Cursor()
        {
            var transport = new RecordingWatchTransport(request =>
            {
                var watchRequest = ReadRequest(request);
                return Success(new AiSdkExecutionWatchEvent
                {
                    ExecutionId = watchRequest.ExecutionId,
                    Sequence = watchRequest.AfterSequence - 1,
                    Kind = AiSdkExecutionWatchEventKind.Event,
                    OccurredAtUtc = DateTimeOffset.UnixEpoch,
                    Channel = AiSdkExecutionWatchChannel.Steps,
                    EventType = "watch.test.regressing"
                });
            });
            var client = new AiSdkClient(transport);

            await using var enumerator = client.WatchExecutionAsync(new AiSdkExecutionWatchRequest
            {
                ExecutionId = "exec-watch",
                AfterSequence = 41,
                IncludeInitialSnapshot = false
            }).GetAsyncEnumerator();

            var exception = await Assert.ThrowsAsync<AiSdkException>(() => enumerator.MoveNextAsync().AsTask());
            Assert.Equal(AiSdkErrorKind.InvalidResponse, exception.Error.Kind);
            Assert.Equal("regressing_watch_sequence", exception.Error.Code);
        }

        private static AiSdkExecutionWatchRequest ReadRequest(AiSdkTransportRequest request)
        {
            Assert.Equal(AiSdkOperationNames.WatchExecution, request.Operation);
            return request.Arguments.GetProperty("request").Deserialize<AiSdkExecutionWatchRequest>()
                ?? throw new JsonException("Could not deserialize Watch request.");
        }

        private static AiSdkTransportResponse Success<T>(T value) =>
            AiSdkTransportResponse.Success(JsonSerializer.SerializeToElement(value));

        private sealed class RecordingWatchTransport : IAiSdkTransport
        {
            private readonly Func<AiSdkTransportRequest, AiSdkTransportResponse> _response;

            public RecordingWatchTransport(Func<AiSdkTransportRequest, AiSdkTransportResponse> response)
            {
                _response = response;
            }

            public List<AiSdkTransportRequest> Requests { get; } = new();

            public ValueTask<AiSdkTransportResponse> InvokeAsync(
                AiSdkTransportRequest request,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(request);
                return ValueTask.FromResult(_response(request));
            }
        }

        private sealed class BlockingTransport : IAiSdkTransport
        {
            public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<AiSdkTransportRequest> Requests { get; } = new();

            public async ValueTask<AiSdkTransportResponse> InvokeAsync(
                AiSdkTransportRequest request,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
        }
    }
}
