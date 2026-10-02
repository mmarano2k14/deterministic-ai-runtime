# ProcessHostPool replay snapshot visibility correction

Date: 2026-09-30

## Evidence and provenance

Observed run: `Pasted text(20260930-100115).txt`, SHA-256 `633ad41d2d5d7897f481d756f41ed2678e40be2f401b4a2799f942092f5874fb`.
Source inspected: `implementations(1).zip`.
Consumer baseline: delivered V21 demo with the V21.1 trace installer upgrade.
The uploaded run confirms the diagnostic predicate upgrade at line 13.
This audit does not claim access to the user's live Git checkout or a live database.

## Observed failure

All three public root executions complete. Their replay calls return
`Snapshot not found.` while each root snapshot upsert is in flight.
Each write then returns an UPSERT RESULT with a nonempty UpsertedId for the
same root ExecutionId. The writes target `multiplexed-ai-mcp-http-tests /
ai_execution_snapshots`, also named in the replay lookups.

| SDK | Root prefix | UPSERT start (UTC, received) | UPSERT result (UTC, received) | Observed interval |
|---|---|---|---|---|
| .NET | published-3ff2012d | 09:58:16.688044 | 09:58:28.688279 | 12.000 s |
| TypeScript | published-114d20fd | 09:58:43.000103 | 09:58:51.987489 | 8.987 s |
| Python | published-3e848361 | 09:59:08.925145 | 09:59:18.879232 | 9.954 s |

Exact relevant lines:
- .NET: UPSERT 7801; GET 8005; missing result 9198; write result 9199.
- TypeScript: UPSERT 13369; GET 13552; missing result 14459; write result 14461.
- Python: UPSERT 19964; GET 20250; missing result 21237; write result 21238.
- Failure gates: 22068–22069, 22478–22479, 23067–23068.
- Test result: failed, one test, 2.0929 minutes, lines 25014–25018.

The UTC fields are parent tee reception timestamps, not database commit timestamps.
The merged stdout/stderr ordering is not a cross-process causal clock. In particular,
no millisecond-precise GET-versus-commit ordering is claimed. The overlap between the
public replay read and the unfinished snapshot write is supported by these traces
and by the inspected source ordering. No subsequent successful GET for the same ID
is present in this run. The write acknowledgements are observed, not inferred from
the generic `SnapshotPersisted` message.

This disproves the earlier missing-writer interpretation for this run. It does not
explain the underlying reason for the approximately 9–12 second observed intervals.

## Source contract

`AiDagBatchExecutionRunner` persists the converged DAG record before awaiting
terminal lifecycle/snapshot persistence. Public observation reads the DAG record,
not the replay snapshot; Completed is therefore not a snapshot-readiness barrier.

The public SDK Replay method invokes `AiReplayOperation.Replay`.
`AiReplayControlPlane.MapReplayMode` maps this operation to `AuditOnly`.
Repeating this public audit request validates the existing execution; it does not
submit a new run, restore state, or rerun OpenAI steps. Audit/ledger entries may be
emitted for each genuine replay attempt.

The historical standalone ProcessHost readiness helper is not treated as proof that
the ProcessHostPool test already used the same barrier. This correction is explicitly
limited to the interactive validation consumers.

## Implemented policy

The .NET, TypeScript, and Python demo consumers retain one immutable-in-practice
replay request/correlation identifier and the same ExecutionId across attempts.

Retry only when:
1. Both the final public observation and public result report Completed.
2. The replay response identifies the requested ExecutionId.
3. The response reports failed replay, does not report deterministic success, and
   its failureReason is exactly `Snapshot not found.`.

The stage has a 30-second budget, including network calls and delays. The delay
between eligible attempts is 250 milliseconds. Neither value claims a MongoDB
latency guarantee. Normal cooperative asynchronous cancellation is required;
blocking synchronous code is outside the cancellation contract.

A different failure, determinism mismatch, transport/authentication error, or
changed identity is not retried. Permanent absence and an over-budget call fail
validation. No synthetic successful replay result is constructed. The final
server response object is returned unchanged, then the original eleven gates run.

Additional evidence diagnostics record replayAttempts, snapshotMissingResponses,
and replayReadinessElapsedMs. The existing evidence schema already allows these
diagnostics; the schema and validator are unchanged.

## Scope and integrity

Changed: three automated demo consumers.
Added: one bounded readiness helper per language, offline regression suites,
and verification documentation.
Unchanged: SDK packages/contracts, production runtime, snapshot writer, replay
engine, auth, topology, queue/pump, OpenAI configuration, PERF2 attribution,
launcher, V21.1 trace installer, evidence schema, evidence validator, and all eleven
existing gate predicates. Exact gate sections were compared to the V21 files.

The retry discriminator is deliberately the current exact public failure message.
No broad exception retry or substring matching is introduced. A future change to
that message requires an explicit contract update and currently fails closed.

## Verification performed

- Python readiness helper: 15/15 offline behavioral tests passed.
- Python demo source compilation: passed.
- Full TypeScript demo compilation with the uploaded public SDK declarations and
  Node typings: passed with TypeScript 5.8.3.
- TypeScript readiness helper: 15/15 offline behavioral tests passed with Node 22.16.0.
- Existing trace upgrade suite: 10/10 tests passed.
- Existing launcher/bridge verifier: passed.
- All original gate sections and validation/installer files noted above: unchanged.
- A 15-case .NET offline suite is included. It was NOT compiled or executed here
  because no dotnet SDK is installed in the working environment.
- No live OpenAI/Redis/MongoDB test, new E2E PASS, or full matrix regression is claimed.

The supplied run remains failed evidence. The next run must independently produce
a successful strict replay response and pass the unchanged gates.
