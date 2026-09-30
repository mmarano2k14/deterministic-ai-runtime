using Multiplexed.AI.Demo.InteractiveAgent.DotNet;
using Multiplexed.AI.Sdk.Contracts.Replay;

const string Id = "published-test-execution";
AiSdkExecutionReplayResponse Good() => new() { ExecutionId = Id, Succeeded = true, Deterministic = true };
AiSdkExecutionReplayResponse Missing() => new() { ExecutionId = Id, Succeeded = false, Deterministic = false, FailureReason = "Snapshot not found." };
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task<TException> Throws<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException error) { return error; }
    throw new Exception($"Expected {typeof(TException).Name}.");
}
async Task<(ReplayReadinessOutcome Outcome, int Calls)> Sequence(AiSdkExecutionReplayResponse[] values, bool completed = true)
{
    int calls = 0;
    var outcome = await ReplaySnapshotReadiness.ExecuteAsync(
        Id, completed, _ => Task.FromResult(values[Math.Min(calls++, values.Length - 1)]),
        timeout: TimeSpan.FromSeconds(1), pollInterval: TimeSpan.FromMilliseconds(1));
    return (outcome, calls);
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("immediate success unchanged", async () => {
        var item = Good(); var (outcome, calls) = await Sequence([item]);
        Check(ReferenceEquals(outcome.Response, item) && calls == 1 && outcome.SnapshotMissingCount == 0, "Immediate response changed.");
    }),
    ("missing to genuine success", async () => {
        var item = Good(); var (outcome, calls) = await Sequence([Missing(), Missing(), item]);
        Check(ReferenceEquals(outcome.Response, item) && calls == 3 && outcome.SnapshotMissingCount == 2, "Retry sequence incorrect.");
    }),
    ("other failure not retried", async () => {
        var item = Missing() with { FailureReason = "Snapshot invalid." };
        var (outcome, calls) = await Sequence([item, Good()]);
        Check(ReferenceEquals(outcome.Response, item) && calls == 1, "Unexpected retry.");
    }),
    ("determinism failure not retried", async () => {
        var item = Good() with { Deterministic = false, FailureReason = "Fingerprint mismatch." };
        var (outcome, calls) = await Sequence([item, Good()]);
        Check(ReferenceEquals(outcome.Response, item) && calls == 1, "Determinism failure masked.");
    }),
    ("noncompleted execution not retried", async () => {
        var item = Missing(); var (outcome, calls) = await Sequence([item, Good()], false);
        Check(ReferenceEquals(outcome.Response, item) && calls == 1, "Unexpected retry for failed execution.");
    }),
    ("reason must match exactly", async () => {
        var item = Missing() with { FailureReason = "Snapshot not found. Details" };
        var (outcome, calls) = await Sequence([item, Good()]);
        Check(ReferenceEquals(outcome.Response, item) && calls == 1, "Broad retry predicate.");
    }),
    ("wrong execution identity rejected", async () => {
        await Throws<InvalidOperationException>(async () => { await Sequence([Missing() with { ExecutionId = "other" }]); });
    }),
    ("transport error propagated once", async () => {
        var expected = new InvalidOperationException("unauthorized transport"); int calls = 0;
        var actual = await Throws<InvalidOperationException>(async () => {
            await ReplaySnapshotReadiness.ExecuteAsync(Id, true, _ => { calls++; return Task.FromException<AiSdkExecutionReplayResponse>(expected); });
        });
        Check(ReferenceEquals(expected, actual) && calls == 1, "Transport exception swallowed.");
    }),
    ("permanent absence times out", async () => {
        await Throws<TimeoutException>(async () => {
            await ReplaySnapshotReadiness.ExecuteAsync(Id, true, _ => Task.FromResult(Missing()),
                timeout: TimeSpan.FromMilliseconds(30), pollInterval: TimeSpan.FromMilliseconds(5));
        });
    }),
    ("inflight deadline cancels transport", async () => {
        CancellationToken received = default;
        await Throws<TimeoutException>(async () => {
            await ReplaySnapshotReadiness.ExecuteAsync(Id, true, async token => {
                received = token; await Task.Delay(TimeSpan.FromSeconds(5), token); return Good();
            }, timeout: TimeSpan.FromMilliseconds(30));
        });
        Check(received.IsCancellationRequested, "Deadline not passed to transport.");
    }),
    ("uncooperative async call remains bounded", async () => {
        var never = new TaskCompletionSource<AiSdkExecutionReplayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Throws<TimeoutException>(async () => {
            await ReplaySnapshotReadiness.ExecuteAsync(Id, true, _ => never.Task, timeout: TimeSpan.FromMilliseconds(30));
        });
        never.SetResult(Good());
    }),
    ("caller cancellation preserved", async () => {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = ReplaySnapshotReadiness.ExecuteAsync(Id, true, async token => {
            entered.TrySetResult(true); await Task.Delay(TimeSpan.FromSeconds(5), token); return Good();
        }, cancellationToken: cancellation.Token);
        await entered.Task; cancellation.Cancel();
        await Throws<OperationCanceledException>(async () => { await work; });
    }),
    ("already cancelled caller does not invoke transport", async () => {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); int calls = 0;
        await Throws<OperationCanceledException>(async () => {
            await ReplaySnapshotReadiness.ExecuteAsync(Id, true, _ => { calls++; return Task.FromResult(Good()); }, cancellationToken: cancellation.Token);
        });
        Check(calls == 0, "Cancelled call was started.");
    }),
    ("invalid duration rejected", async () => {
        await Throws<ArgumentOutOfRangeException>(async () => {
            await ReplaySnapshotReadiness.ExecuteAsync(Id, true, _ => throw new Exception("must not run"), timeout: TimeSpan.Zero);
        });
    }),
    ("every attempt traced", async () => {
        var traces = new List<string>(); int calls = 0;
        var outcome = await ReplaySnapshotReadiness.ExecuteAsync(Id, true, _ => Task.FromResult(calls++ == 0 ? Missing() : Good()),
            pollInterval: TimeSpan.FromMilliseconds(1), trace: traces.Add);
        Check(outcome.Attempts == 2 && traces.Count == 2 && traces[0].Contains("Outcome=SnapshotPending") && traces[1].Contains("Outcome=Ready"), "Trace missing.");
    })
};
int failures = 0;
foreach (var (name, test) in tests)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"Replay readiness tests: {tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;
