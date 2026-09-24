using System.Text.Json;
using System.Threading.Channels;
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Contracts.Control;
using Multiplexed.AI.Sdk.Contracts.Executions;
using Multiplexed.AI.Sdk.Contracts.Observation;
using Multiplexed.AI.Sdk.Contracts.Replay;
using Multiplexed.AI.Sdk.Contracts.Watch;

namespace Multiplexed.AI.Demo.InteractiveAgent.DotNet;

internal sealed class InteractiveExecutionConsole
{
    private static readonly TimeSpan ObservationInterval =
        TimeSpan.FromMilliseconds(400);

    private readonly IAiSdkClient _client;
    private readonly string _executionId;
    private readonly string _waitingKey;
    private readonly string _waitingStepName;
    private readonly ConsoleInputPump _input = new();
    private bool _waitingAnnounced;
    private Task<string?>? _pendingInput;

    internal InteractiveExecutionConsole(
        IAiSdkClient client,
        string executionId,
        string waitingKey,
        string waitingStepName)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _executionId = executionId;
        _waitingKey = waitingKey;
        _waitingStepName = waitingStepName;
    }

    internal async Task<AiSdkExecutionResult?> RunAsync()
    {
        _input.Start();

        using var watchCancellation = new CancellationTokenSource(
            TimeSpan.FromMinutes(20));

        var watchTask = WatchAsync(watchCancellation.Token);

        PrintCommands();

        while (true)
        {
            var observation = await _client.ObserveExecutionAsync(_executionId);

            AnnounceInputBoundary(observation);

            if (IsTerminal(observation.Status))
            {
                watchCancellation.Cancel();
                await IgnoreWatchTerminationAsync(watchTask);
                return await _client.GetExecutionResultAsync(_executionId);
            }

            var commandTask = GetPendingInputAsync();
            var delayTask = Task.Delay(ObservationInterval);
            var completed = await Task.WhenAny(commandTask, delayTask);

            if (completed != commandTask)
            {
                continue;
            }

            var command = (await ConsumePendingInputAsync())?.Trim().ToLowerInvariant();

            if (string.Equals(command, "q", StringComparison.Ordinal))
            {
                watchCancellation.Cancel();
                await IgnoreWatchTerminationAsync(watchTask);
                return null;
            }

            await HandleCommandAsync(command, observation);
        }
    }

    internal async Task RunPostTerminalCommandsAsync(
        AiSdkExecutionStatus terminalStatus)
    {
        Console.WriteLine();
        Console.WriteLine("Post-terminal commands:");
        Console.WriteLine("  [x] deterministic replay validation");
        Console.WriteLine("  [q] exit");

        while (true)
        {
            Console.Write("> ");
            var command = (await ConsumePendingInputAsync())?.Trim().ToLowerInvariant();

            switch (command)
            {
                case "x":
                    await ReplayAsync();
                    break;

                case "q":
                case "":
                case null:
                    return;

                default:
                    Console.WriteLine(
                        $"Execution is already {terminalStatus}. Use 'x' or 'q'.");
                    break;
            }
        }
    }

    internal static string FormatJson(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
    }

    private static void PrintCommands()
    {
        Console.WriteLine("Commands while the execution is active:");
        Console.WriteLine("  [p] pause");
        Console.WriteLine("  [r] resume");
        Console.WriteLine("  [i] submit human input");
        Console.WriteLine("  [c] cancel");
        Console.WriteLine("  [s] status");
        Console.WriteLine("  [q] detach local console without cancelling");
        Console.WriteLine();
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _client.WatchExecutionAsync(
                new AiSdkExecutionWatchRequest
                {
                    ExecutionId = _executionId,
                    IncludeInitialSnapshot = true
                },
                cancellationToken))
            {
                switch (item.Kind)
                {
                    case AiSdkExecutionWatchEventKind.Snapshot
                        when item.Snapshot is not null:
                        PrintSnapshot(item.Sequence, item.Snapshot);
                        break;

                    case AiSdkExecutionWatchEventKind.Event:
                        Console.WriteLine(
                            $"[watch #{item.Sequence?.ToString() ?? "-"}] " +
                            $"{item.Channel?.ToString() ?? "event"} " +
                            $"{item.EventType ?? "event"}");
                        break;

                    case AiSdkExecutionWatchEventKind.ResyncRequired:
                        Console.WriteLine(
                            $"[watch] resync required: " +
                            $"{item.ResyncRequired?.Reason.ToString() ?? "unknown"}");
                        break;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[watch] stopped: {ex.Message}");
        }
    }

    private static void PrintSnapshot(
        long? sequence,
        AiSdkExecutionObservation snapshot)
    {
        var steps = string.Join(
            ", ",
            snapshot.Steps.Select(
                step => $"{step.Name}={step.Status}"));

        Console.WriteLine(
            $"[snapshot #{sequence?.ToString() ?? "-"}] " +
            $"execution={snapshot.Status}; {steps}");
    }

    private void AnnounceInputBoundary(
        AiSdkExecutionObservation observation)
    {
        if (_waitingAnnounced)
        {
            return;
        }

        var waitingStep = observation.Steps.FirstOrDefault(
            step => string.Equals(
                step.Name,
                _waitingStepName,
                StringComparison.Ordinal));

        if (waitingStep?.Status != AiSdkExecutionStepStatus.WaitingForExternal)
        {
            return;
        }

        _waitingAnnounced = true;

        Console.WriteLine();
        Console.WriteLine("Human input required.");
        Console.WriteLine(
            $"Step '{_waitingStepName}' is durably parked. " +
            "Enter 'i' to approve/reject and add feedback.");
        Console.WriteLine();
    }

    private async Task HandleCommandAsync(
        string? command,
        AiSdkExecutionObservation observation)
    {
        switch (command)
        {
            case "p":
                await PauseAsync();
                break;

            case "r":
                await ResumeAsync();
                break;

            case "i":
                await SubmitInputAsync(observation);
                break;

            case "c":
                await CancelAsync();
                break;

            case "s":
                PrintSnapshot(sequence: null, observation);
                break;

            case "x":
                Console.WriteLine(
                    "Replay validation is available after terminal convergence.");
                break;

            case "":
            case null:
                break;

            default:
                Console.WriteLine(
                    "Unknown command. Use p, r, i, c, s, or q.");
                break;
        }
    }

    private async Task PauseAsync()
    {
        Console.WriteLine();
        Console.WriteLine(
            $"SDK command: sdk.execution.pause({_executionId})");

        var response = await _client.PauseExecutionAsync(
            _executionId,
            new AiSdkExecutionControlRequest
            {
                Reason = "interactive-agent-console-pause"
            });

        Console.WriteLine(
            $"Pause accepted={response.Accepted}; " +
            $"controlState={response.State?.Status.ToString() ?? "unknown"}");
        Console.WriteLine(
            $"ExecutionId unchanged: {response.ExecutionId}");
        Console.WriteLine();
    }

    private async Task ResumeAsync()
    {
        Console.WriteLine();
        Console.WriteLine(
            $"SDK command: sdk.execution.resume({_executionId})");

        var response = await _client.ResumeExecutionAsync(
            _executionId,
            new AiSdkExecutionControlRequest
            {
                Reason = "interactive-agent-console-resume"
            });

        Console.WriteLine(
            $"Resume accepted={response.Accepted}; " +
            $"controlState={response.State?.Status.ToString() ?? "unknown"}");
        Console.WriteLine(
            $"ExecutionId unchanged: {response.ExecutionId}");
        Console.WriteLine();
    }

    private async Task SubmitInputAsync(
        AiSdkExecutionObservation observation)
    {
        var waitingStep = observation.Steps.FirstOrDefault(
            step => string.Equals(
                step.Name,
                _waitingStepName,
                StringComparison.Ordinal));

        if (waitingStep?.Status != AiSdkExecutionStepStatus.WaitingForExternal)
        {
            Console.WriteLine(
                "The execution is not currently parked at the human-input boundary.");
            return;
        }

        var approved = await ReadApprovalAsync();

        Console.Write("Feedback (optional): ");
        var feedback = (await ConsumePendingInputAsync())?.Trim() ?? string.Empty;

        Console.WriteLine();
        Console.WriteLine(
            $"SDK command: sdk.execution.input.submit({_executionId})");

        var response = await _client.SubmitExecutionInputAsync(
            _executionId,
            new AiSdkExecutionInputSubmissionRequest
            {
                WaitingKey = _waitingKey,
                WaitingStepName = _waitingStepName,
                Reason = "interactive-agent-human-review",
                CorrelationId = $"interactive-agent-input-{Guid.NewGuid():N}",
                Input = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["approved"] = approved,
                        ["feedback"] = feedback
                    })
            });

        Console.WriteLine(
            $"Input accepted={response.Accepted}; " +
            $"controlState={response.State?.Status.ToString() ?? "unknown"}");
        Console.WriteLine(
            $"ExecutionId unchanged: {response.ExecutionId}");
        Console.WriteLine();
    }

    private async Task<bool> ReadApprovalAsync()
    {
        while (true)
        {
            Console.Write("Approve the agent plan? [y/n]: ");
            var answer = (await ConsumePendingInputAsync())?.Trim();

            if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(answer, "n", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Console.WriteLine("Enter 'y' or 'n'.");
        }
    }

    private async Task CancelAsync()
    {
        Console.WriteLine();
        Console.WriteLine(
            $"SDK command: sdk.execution.cancel({_executionId})");

        var response = await _client.CancelExecutionAsync(
            _executionId,
            new AiSdkExecutionCancellationRequest
            {
                Reason = "interactive-agent-console-cancel",
                CorrelationId = $"interactive-agent-cancel-{Guid.NewGuid():N}"
            });

        Console.WriteLine(
            $"Cancellation requested={response.CancellationRequested}; " +
            $"status={response.Status}");
        Console.WriteLine();
    }

    private async Task ReplayAsync()
    {
        Console.WriteLine();
        Console.WriteLine(
            $"SDK command: sdk.execution.replay({_executionId})");

        var replay = await _client.ReplayExecutionAsync(
            _executionId,
            new AiSdkExecutionReplayRequest
            {
                StrictDeterminism = true,
                IncludeDiagnostics = true,
                Reason = "interactive-agent-console-replay",
                CorrelationId = $"interactive-agent-replay-{Guid.NewGuid():N}"
            });

        Console.WriteLine($"Replay succeeded: {replay.Succeeded}");
        Console.WriteLine(
            $"Deterministic: {replay.Deterministic?.ToString() ?? "unknown"}");

        if (!string.IsNullOrWhiteSpace(replay.Message))
        {
            Console.WriteLine($"Message: {replay.Message}");
        }

        if (!string.IsNullOrWhiteSpace(replay.FailureReason))
        {
            Console.WriteLine($"Failure: {replay.FailureReason}");
        }

        foreach (var diagnostic in replay.Diagnostics)
        {
            Console.WriteLine($"  {diagnostic}");
        }

        Console.WriteLine(
            "Replay validates the existing durable execution; it does not create a second execution.");
        Console.WriteLine();
    }


    private Task<string?> GetPendingInputAsync()
    {
        return _pendingInput ??= _input.ReadAsync();
    }

    private async Task<string?> ConsumePendingInputAsync()
    {
        var pending = GetPendingInputAsync();
        var value = await pending;
        _pendingInput = null;
        return value;
    }

    private static bool IsTerminal(AiSdkExecutionStatus status) =>
        status is
            AiSdkExecutionStatus.Completed or
            AiSdkExecutionStatus.Failed or
            AiSdkExecutionStatus.Cancelled;

    private static async Task IgnoreWatchTerminationAsync(Task watchTask)
    {
        try
        {
            await watchTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class ConsoleInputPump
    {
        private readonly Channel<string?> _lines =
            Channel.CreateUnbounded<string?>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true
                });

        private int _started;

        internal void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var line = Console.ReadLine();
                    await _lines.Writer.WriteAsync(line);

                    if (line is null)
                    {
                        _lines.Writer.TryComplete();
                        return;
                    }
                }
            });
        }

        internal async Task<string?> ReadAsync()
        {
            if (await _lines.Reader.WaitToReadAsync())
            {
                return _lines.Reader.TryRead(out var line)
                    ? line
                    : null;
            }

            return null;
        }
    }
}
