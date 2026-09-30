# ProcessHostPool snapshot/replay source audit

Date: 2026-09-30

## Scope

The review uses the mounted `implementations(1).zip` source archive, delivered V20/V21 installers and consumers, and `Pasted text(20260930-081059).txt`. Source-file line numbers refer to that archive, not to an assumed current checkout. Local changes outside the supplied materials are not verified. No live Redis/MongoDB queries or .NET compilation were performed.

## Findings

### 1. Confirmed diagnostic installer defect

`apply_processhostpool_bridge.py`, `_inject_child_runtime_trace`, returns immediately when `MULTIPLEXED_INTERACTIVE_SDK_CHILD_TRACE` already appears in the target C# source. The expanded Mongo predicate exists only in the fresh-install template below that return.

The defect was reproduced using the delivered V20 installer and the original uploaded `SystemAiRuntimeProcessPoolChild.cs`. Applying the documented `System.Environment` hotfix and then applying V21 returns byte-identical C# without `MONGO SNAPSHOT STORE` in the predicate. Fresh V21 does contain the Mongo predicate, but still emits the unqualified `Environment.GetEnvironmentVariable` reference.

Consequences:

- A successful overall bridge-install message did not verify the new Mongo filter.
- Existing installations could continue suppressing child Mongo registration and upsert lines.
- Zero visible UPSERT lines in V21 is not evidence of zero database writes.
- The missing trace is a proven diagnostic defect; it does not by itself explain the replay snapshot misses.

### 2. The historical readiness wait exists, but in another runner

`ProcessHostProductionScenarioRunner.cs:36-37` defines a 30-second timeout and 100-millisecond interval. Lines 450-458 call `WaitForReplaySnapshotsAsync` when a snapshot store is available. The implementation at lines 816 onward polls `snapshotStore.GetAsync` and removes an execution from its pending set when the result is non-null. It does not explicitly test snapshot terminal status or equality of the returned embedded ExecutionId.

The selected `ProcessHostPoolProductionScenarioTestsBase` follows another path: durable parent completion, bounded topology/capacity verification, then `RecoveredExecutionReplayProofAssertions.AssertRecoveredExecutionsReplayableThroughMcpAsync`. The inspected pool path does not call that standalone snapshot-readiness helper. Its replay proof uses `McpBackpressureRetryHelper`, which retries HTTP 429 exceptions only, not a successful HTTP response containing `Snapshot not found.`

The prior claim that this exact pool test passed because it already used the 30-second snapshot-readiness barrier was therefore not established and conflated two runners.

### 3. A terminal-record/snapshot visibility race is supported by the source, not yet proven for these failures

In `AiDagBatchExecutionRunner.cs:548-563`, converged record persistence is awaited before `EnsureTerminalLifecycleAsync`. Public `ObserveAsync` and `GetResultAsync` read the DAG execution record/state, not the durable replay snapshot. The external consumers read the result and make one replay call after terminal observation; they do not wait on the snapshot store.

A valid interleaving is therefore:

```text
converged DAG record committed
public observation returns Completed
public replay reads the snapshot before its write completes
terminal lifecycle persists the snapshot afterward
```

This is a code-supported race opportunity. The supplied V21 log cannot prove that it is the specific cause, because child snapshot writes were not reliably forwarded and the two missed IDs were not queried again later in the log. The fact that Python runs third does not grant its own execution an additional post-completion delay.

### 4. Python is not established to be a stale snapshot or a false replay PASS

All three consumers create a fresh UUID/GUID for each submission and use it in the idempotency key. The public boundary uses that key as the run key, and `AiPublicationJson.ExecutionId` hashes the partition and run key. IDs are not simply reused because the publication is unchanged. None of the three current root ExecutionIds was found in the other mounted pasted logs.

The log proves that the Python replay lookup found a matching Completed snapshot. Its creation time/writer is not exposed in the log. That absence is insufficient to classify it as residual state.

`DefaultAiExecutionReplayService.cs` invokes `_replayExecutor.ExecuteAsync` before the compatible-existing-execution branch. The executor invokes the replay validator. The message saying replay was skipped in that branch means restoration was skipped; it does not mean snapshot/consistency validation was bypassed. The Python public replay gates pass. This is not proof of a cold restore or external-provider re-execution.

### 5. Mongo/Redis durable environment composition is present

`ProcessHostPoolProductionScenarioSettingsComposer` copies `ConnectionStrings:Redis`, `ConnectionStrings:Mongo`, and `Mongo:DatabaseName`. `ProcessRuntimePoolBootstrapRegistration.Configure` calls `AddDurableRuntimeEnvironment`, which adds the corresponding child variables and enables both snapshot persistence and its Mongo provider.

No missing configuration key was demonstrated by the inspected composition code. This does not claim that every effective runtime option was observed; the previous negative argument based on missing Mongo log lines is invalid because of the installer defect.

### 6. Snapshot success must be established from persistence acknowledgement, not a generic success message

`DefaultAiExecutionSnapshotService.TryPersistAsync` catches persistence exceptions, attempts a storage-failure ledger event, and does not rethrow the original persistence failure. Its caller, `AiDagExecutionLifecycleHelper`, then logs `SnapshotPersisted` after the method returns. That generic message alone is therefore not proof of a successful Mongo write.

For each exact ExecutionId, the reliable diagnostic sequence is an UPSERT start, an acknowledged UPSERT RESULT, and a lookup of the same store identity. Snapshot skip/failure signals and the storage-failure ledger event are relevant when an acknowledgement is missing. The correction forwards lifecycle and Mongo acknowledgement markers without dumping configuration values.

## Current run verdict

| SDK | Public execution | Replay snapshot lookup | Public replay gates |
|---|---|---|---|
| .NET | Completed | Missing at lookup | Failed |
| TypeScript | Completed | Missing at lookup | Failed |
| Python | Completed | Matching Completed snapshot found | Passed |

The matrix remains failed. No replay gate should be removed, and no fixed sleep or speculative Mongo setting is justified by this audit.

## Delivered correction

The incremental patch fixes the diagnostic upgrade path only. It updates known previously generated predicates, qualifies `System.Environment`, forwards Mongo snapshot and snapshot-lifecycle markers, includes a tee-observation UTC timestamp, rejects ambiguous/unrecognized installations, and preserves the untraced `ReadToEndAsync` path. It adds no SDK dependency on engine contracts, Redis, or MongoDB.

The UTC timestamp denotes tee receipt, not the MongoDB commit time. No timeout, replay engine, finalization order, authorization, queue, OpenAI configuration, or PERF instrumentation behavior is changed.

## Validation performed

- Ten Python regression tests passed, covering fresh installation, upgrades with/without the namespace hotfix, previously installed Mongo tracing, repeated idempotency, malformed/ambiguous/edited source rejection, and forwarded snapshot markers.
- On the actual uploaded C# source, fresh installation and upgrade from generated V20 tracing produced identical corrected source.
- The full bridge installer succeeded on a staged copy of the actual uploaded source, including an already traced child. A second installation left all C# files byte-identical.
- The existing launcher verifier passed on the staged corrected pack.
- No .NET build or live end-to-end validation is claimed. The local environment has no `dotnet` executable.

## Remaining decision

The actual replay failure remains unassigned between a timing race, an omitted terminal lifecycle attempt, and an unsuccessful snapshot persistence attempt. The corrected trace can distinguish these without changing the successful execution path. A bounded server-side readiness check is appropriate only after confirming the relevant visibility contract; external SDK consumers must remain independent of internal snapshot-store interfaces.

Exact copied excerpts and archive hashes are provided in `PROCESSHOSTPOOL_V21_SOURCE_EVIDENCE.txt`.
