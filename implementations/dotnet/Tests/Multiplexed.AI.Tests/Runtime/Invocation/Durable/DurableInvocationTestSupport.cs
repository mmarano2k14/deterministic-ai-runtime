using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Multiplexed.Abstractions.AI.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable;
using Multiplexed.AI.Runtime.Invocation.Durable.Mongo;

namespace Multiplexed.AI.Tests.Runtime.Invocation.Durable
{
    /// <summary>Test-only snapshots and atomic memory storage, not a production durable provider.</summary>
    internal static class DurableInvocationTestSupport
    {
        internal static readonly AiDurableInvocationScope Scope = new("tenant-a", "group-a", "control-a");
        internal static readonly AiDurableInvocationIdentity Identity = new("tenant-a", "execution-a", "analyze");
        internal static string Digest(char value = 'a') => new(value, 64);
        internal static AiDurableInvocationDefinition Definition() => new(Identity, Scope,
            new AiDurableInvocationTarget("analysis", "1", Digest('a'), "publication-1", Digest('b'),
                "analyze-1", Digest('c'), "python", "python-locked", Digest('d')), "{\"amount\":1}");
        internal static AiDurableInvocationResult Result(bool success = true, string json = "{\"value\":42}") => new(success, json);
        internal static AiDurableInvocationContinuationAcknowledgement Ack(AiDurableInvocationRecord record,
            AiDurableInvocationContinuationStatus status = AiDurableInvocationContinuationStatus.Applied) =>
            new(record.OperationId, record.ResultSha256!, status, status == AiDurableInvocationContinuationStatus.Applied
                ? "Runtime observed the exact result applied." : "Runtime observed the parent already terminal.");

        internal static (AiDurableInvocationJournal Journal, MemoryStore Store, Clock Clock) Create()
        {
            var clock = new Clock();
            var store = new MemoryStore(clock);
            return (new AiDurableInvocationJournal(store, clock), store, clock);
        }

        internal static async Task<AiDurableInvocationRecord> LeaseAsync(AiDurableInvocationJournal journal,
            string worker = "worker-a", TimeSpan? duration = null)
        {
            await journal.PrepareAsync(Definition());
            return (await journal.TryAcquireLeaseAsync(Scope, Identity, worker, duration ?? TimeSpan.FromSeconds(30)))!;
        }

        internal static async Task<AiDurableInvocationRecord> CompleteAsync(AiDurableInvocationJournal journal, bool success = true)
        {
            var leased = await LeaseAsync(journal);
            Assert.Equal(AiDurableInvocationCompletionStatus.Accepted,
                await journal.CompleteAsync(Scope, Identity, leased.Lease!, Result(success)));
            return (await journal.GetAsync(Scope, Identity))!;
        }

        internal static T Codec<T>(string method, params object[] arguments)
        {
            var type = typeof(MongoAiDurableInvocationStore).Assembly.GetType(
                "Multiplexed.AI.Runtime.Invocation.Durable.Mongo.AiDurableInvocationMongoCodec", true)!;
            try { return (T)type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments)!; }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        internal sealed class Clock : TimeProvider
        {
            private long _milliseconds = DateTimeOffset.Parse("2026-09-11T10:00:00+00:00").ToUnixTimeMilliseconds();
            public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));
            internal void Advance(TimeSpan duration) => Interlocked.Add(ref _milliseconds, (long)duration.TotalMilliseconds);
            internal void Set(DateTimeOffset value) => Interlocked.Exchange(ref _milliseconds, value.ToUnixTimeMilliseconds());
        }

        internal sealed class MemoryStore : IAiDurableInvocationStore
        {
            private readonly object _gate = new();
            private readonly Dictionary<AiDurableInvocationIdentity, AiDurableInvocationRecord> _records = new();
            private readonly TimeProvider _clock;
            internal int RejectCasCount;
            internal int CasCalls;
            internal int GetCalls;
            internal bool ThrowAfterNextWrite;
            internal Action? BeforeNextCas;
            internal MemoryStore(TimeProvider clock) => _clock = clock;
            internal string Export() { lock (_gate) return JsonSerializer.Serialize(_records.Values.ToArray()); }
            internal void Restore(string json)
            {
                lock (_gate)
                {
                    _records.Clear();
                    foreach (var record in JsonSerializer.Deserialize<AiDurableInvocationRecord[]>(json)!) _records.Add(record.Definition.Identity, record);
                }
            }
            internal void Corrupt(Func<AiDurableInvocationRecord, AiDurableInvocationRecord> change)
            {
                lock (_gate) _records[Identity] = change(_records[Identity]);
            }

            public async Task<AiDurableInvocationRecord?> GetAsync(AiDurableInvocationScope scope,
                AiDurableInvocationIdentity identity, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref GetCalls);
                AiDurableInvocationRecord? result;
                lock (_gate) result = _records.TryGetValue(identity, out var current) && current.Definition.Scope == scope ? current : null;
                await Task.Yield(); // Allow competing callers to read the same revision.
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }

            public Task<AiDurableInvocationRecord> GetOrCreateAsync(AiDurableInvocationRecord prepared,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (_records.TryGetValue(prepared.Definition.Identity, out var current))
                    {
                        if (prepared.Definition != current.Definition) throw new InvalidOperationException("Conflicting preparation.");
                        return Task.FromResult(current);
                    }
                    _records.Add(prepared.Definition.Identity, prepared);
                    MaybeThrowAfterWrite();
                    return Task.FromResult(prepared);
                }
            }

            public async Task<bool> TryReplaceAsync(AiDurableInvocationRecord expected, AiDurableInvocationRecord replacement,
                CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    CasCalls++;
                    var action = BeforeNextCas; BeforeNextCas = null; action?.Invoke();
                    if (RejectCasCount > 0) { RejectCasCount--; return false; }
                    if (!_records.TryGetValue(expected.Definition.Identity, out var current) || current != expected) return false;
                    var now = _clock.GetUtcNow();
                    if (expected.Status == AiDurableInvocationStatus.Leased)
                    {
                        var replaces = replacement.Status == AiDurableInvocationStatus.Leased && replacement.Lease!.Epoch != expected.Lease!.Epoch;
                        if (replaces ? expected.Lease!.ExpiresAtUtc > now : expected.Lease!.ExpiresAtUtc <= now) return false;
                    }
                    if (replacement.Status == AiDurableInvocationStatus.Leased &&
                        (replacement.Lease!.ExpiresAtUtc <= now || replacement.Lease.ExpiresAtUtc > now.AddMinutes(5))) return false;
                    _records[expected.Definition.Identity] = replacement;
                    MaybeThrowAfterWrite();
                    return true;
                }
            }

            public Task<IReadOnlyList<AiDurableInvocationRecord>> ListDispatchCandidatesAsync(AiDurableInvocationScope scope,
                string executionLanguage, DateTimeOffset nowUtc, int maxCount, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                    return Task.FromResult<IReadOnlyList<AiDurableInvocationRecord>>(_records.Values.Where(record =>
                        record.Definition.Scope == scope && record.Definition.Target.ExecutionLanguage == executionLanguage &&
                        (record.Status == AiDurableInvocationStatus.Prepared ||
                        record.Status == AiDurableInvocationStatus.Leased && record.Lease!.ExpiresAtUtc <= nowUtc))
                        .OrderBy(record => record.UpdatedAtUtc).ThenBy(record => record.OperationId, StringComparer.Ordinal).Take(maxCount).ToArray());
            }

            public Task<IReadOnlyList<AiDurableInvocationRecord>> ListContinuationCandidatesAsync(AiDurableInvocationScope scope,
                int maxCount, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                    return Task.FromResult<IReadOnlyList<AiDurableInvocationRecord>>(_records.Values.Where(record =>
                        record.Definition.Scope == scope && record.ContinuationStatus is
                            AiDurableInvocationContinuationStatus.Pending or AiDurableInvocationContinuationStatus.Scheduled)
                        .OrderBy(record => record.UpdatedAtUtc).ThenBy(record => record.OperationId, StringComparer.Ordinal).Take(maxCount).ToArray());
            }

            private void MaybeThrowAfterWrite()
            {
                if (!ThrowAfterNextWrite) return;
                ThrowAfterNextWrite = false;
                throw new IOException("Injected lost acknowledgement after storage accepted the write.");
            }
        }
    }
}
