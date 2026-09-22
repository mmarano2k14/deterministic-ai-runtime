# Replay and Audit

Status: Replay engine V1 implemented and validated. Documentation still evolving for controller, HTTP API, dashboard, and operational tooling.

This document describes the replay, snapshot, deterministic validation, decision ledger, trace timeline, and audit foundations of the Deterministic AI Runtime.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

Production AI execution must be inspectable, recoverable, and explainable.

When an AI workflow completes, fails, is cancelled, or needs to be investigated, the runtime should be able to answer:

- what executed?
- which steps completed?
- which steps failed?
- what was retried?
- what was recovered?
- what payloads were externalized?
- what state was persisted?
- can the execution be restored?
- can the restored execution be compared with the original?
- which replay lifecycle events were recorded?
- which ledger events belong to the same execution?
- which trace timeline events explain runtime behavior?
- can a future system audit decisions and runtime transitions?

The replay and audit foundations exist to support these questions.

---

## Current Scope

The runtime currently provides a validated Replay Engine V1 and audit foundations.

Current validated capabilities include:

- terminal snapshots
- snapshot persistence
- replay restore from snapshot
- replay idempotency when live execution already exists
- audit-only replay without restoring live state
- deterministic replay validation
- execution fingerprints
- persisted replay metadata
- original fingerprint vs reconstructed fingerprint comparison
- dependency graph validation
- step state validation
- payload reference validation
- archived / compacted / evicted payload reference validation
- restored execution comparison
- retention-compatible resolver reconstruction foundations
- terminal snapshot and replay compatibility with bounded hot state
- execution-correlated replay ledger events
- optional replay report enrichment with decision ledger events
- optional replay report enrichment with trace timeline events
- replay diagnostic output for ledger and timeline inspection
- reference 100-step distributed replay integration tests

The following are planned future capabilities:

- runtime-level replay controller abstraction
- HTTP Replay API
- replay summary endpoint
- replay ledger endpoint
- replay timeline endpoint
- replay dashboard / UI
- exportable replay reports
- richer decision lineage tooling
- compliance-oriented decision inspection

This document intentionally separates the implemented Replay Engine V1 from future replay operations, API, dashboard, and audit platform capabilities.

---

## Hosted Invocation Restoration and External Effects

Hosted custom functions add a durable result-reuse path alongside the existing replay engine. Once the journal has accepted a result, reconstructed services can apply that exact result through the existing DAG continuation without launching the function again. A receipt binds the operation to the accepted result hash; acknowledgement also requires the terminal parent.

This recovery/application path is not a new replay mode. It does not turn arbitrary function side effects into exactly-once operations, and controlled restoration from serialized stores is distinct from a host kill or database restart.

Outbound MCP can now opt into a separate durable effect-evidence journal. It freezes one logical effect intent, commits `Dispatching` before the physical `tools/call`, persists confirmed `Completed` responses before they return through the normal DAG path, and reuses those confirmed responses locally on a later invocation without a second business call. Classified pre-call non-emission can be stored as `NotSent`; ambiguous post-boundary outcomes remain fail-closed `Uncertain` or stale `Dispatching` until explicit reconciliation.

This is not a new Replay Engine mode and does not make arbitrary remote providers exactly-once. The existing audit-only snapshot/replay path still does not deliberately re-execute external tools. Durable MCP effect replay means reuse of already-confirmed stored effect evidence, not re-running the business action. See [Durable MCP Effect Evidence](durable-mcp-effect-evidence.md) and [Durable MCP Effect Evidence Validation](durable-mcp-effect-evidence-validation.md).

The hosted custom `Concurrency` policy path likewise does not introduce a durable policy-result replay store. See [Hosted Multilanguage Execution](hosted-multilanguage-execution.md) and [Hosted Multilanguage Validation](hosted-multilanguage-validation.md) for these exact boundaries.

---

## Replay Engine V1

Replay Engine V1 is implemented as replay-as-validation and replay-as-restoration.

It does not re-run external providers.
It does not call LLMs again.
It does not replay side effects.
It reconstructs and validates persisted execution state.

Replay Engine V1 can answer:

- can the snapshot be loaded?
- can execution state be reconstructed?
- can dependency ordering be validated?
- can final step states be validated?
- can payload references be validated?
- can compacted or evicted payload references still be resolved?
- does the reconstructed fingerprint match the original persisted fingerprint?
- can the runtime restore execution state from durable snapshot data?
- can an audit-only report be produced without restoring state?
- can ledger events and trace timeline events be attached to the replay report?

Implemented replay modes include:

- `AuditOnly`
- `ResumeIncomplete` / restore from persisted snapshot

The current Replay Engine V1 is library-level runtime functionality.
A future controller and HTTP API will expose this capability externally.

---

## Replay Report Model

The replay report exposes a deterministic audit view of an execution.

The report includes:

- execution id
- replay mode
- pipeline name
- pipeline key
- execution status
- execution found flag
- snapshot found flag
- fingerprint found flag
- original fingerprint
- reconstructed fingerprint
- fingerprint match result
- dependency graph validation result
- step state validation result
- payload reference validation result
- replay validity
- failure reason
- total step count
- completed step count
- failed step count
- waiting-for-retry step count
- running step count
- retry count
- recovery count
- replay issues
- step-level replay details
- optional decision ledger events
- optional trace timeline events
- persisted replay metadata

This makes replay useful both for automated validation and for human diagnostics.

---

## Why Replay Matters

Replay is important because production AI workflows are not simple request/response calls.

They may involve:

- multiple DAG steps
- distributed workers
- retries
- recovery after crashes
- retention and compaction
- externalized payloads
- human-in-the-loop control
- cancellation races
- provider/model/operation throttling

Without replay foundations, debugging and proving correctness becomes difficult.

Replay gives the runtime a way to restore and inspect a previous execution state.

---

## Persistence Layer

Replay depends on durable persistence.

Redis is optimized for:

- speed
- active execution
- coordination between workers
- distributed claiming
- retry and recovery metadata

Redis is not the long-term audit store.

The persistence layer stores durable execution data such as:

- terminal snapshots
- large payload references
- archived step data
- execution metadata
- replay-relevant state

MongoDB or a dedicated payload store is used for durable execution data.

---

## Snapshot Foundations

A snapshot is a durable representation of an execution at a point in time.

Terminal snapshots are especially important.

A terminal snapshot may include:

- execution record
- execution status
- pipeline name
- step states
- completed step information
- retry state
- payload references
- metadata required for replay
- data required for deterministic validation

Snapshots are stored outside hot Redis state.

Redis is the active coordination layer.

Durable storage is responsible for preserving replayable execution data.

---

## Terminal Snapshot

A terminal snapshot is created when execution reaches a terminal state such as:

```text
Completed
Failed
Cancelled
```

The snapshot preserves the final execution state.

This is required because hot state may later be cleaned up, compacted, evicted, or deleted.

A terminal snapshot provides a durable basis for replay and inspection.

---

## Snapshot Normalization

Before persistence, execution data should be normalized.

Snapshot normalization removes or avoids transient runtime fields such as:

- claim tokens
- temporary lease ids
- worker-specific ownership fields
- non-replay-safe running ownership metadata
- process-specific runtime values

Snapshot normalization helps ensure that snapshots are:

- consistent
- portable
- replayable
- safe to restore
- suitable for deterministic comparison

This is important because replay should restore meaningful execution state, not stale runtime ownership.

---

## Payload Externalization and Snapshots

Large payloads should not be stored directly inside hot execution state.

Instead:

```text
large payload
        ↓
payload store / MongoDB
        ↓
snapshot keeps payload reference
```

This allows:

- smaller snapshots
- reduced Redis memory usage
- better performance
- replay compatibility with retention
- resolver-backed reconstruction

Snapshots and payload references work together.

---

## Replay When Live State Exists

Replay must be idempotent.

If the live DAG state still exists, replay should not duplicate or overwrite it.

The expected behavior is:

```text
Execution still exists in DAG store
        ↓
ReplayAsync(ExecutionId)
        ↓
AlreadyExists = true
Restored = false
```

This prevents replay from corrupting active or already restored state.

---

## Replay After Live State Deletion

Replay should restore state when the live DAG record has been deleted but a terminal snapshot exists.

The expected behavior is:

```text
Execution completed
        ↓
Terminal snapshot persisted
        ↓
Live DAG record/state deleted
        ↓
ReplayAsync(ExecutionId)
        ↓
Restored = true
AlreadyExists = false
        ↓
DAG record/state restored
```

This validates that the runtime can reconstruct a completed execution from durable snapshot data.

---

## Replay Behavior

When replay restores execution state, it must not restore unsafe transient ownership.

Replay should preserve stable execution facts such as:

- completed steps remain completed
- failed terminal state remains failed
- cancelled terminal state remains cancelled
- retry counts are preserved
- payload references are preserved
- deterministic step outcomes are preserved

Replay should not preserve invalid transient state such as:

- stale claim tokens
- expired leases
- dead worker ownership
- process-local state

For broader non-terminal replay scenarios, running work should not be restored as if the old worker still owns it.

A safe replay model should reset or normalize non-terminal running ownership before execution can continue.

The currently validated replay behavior focuses on terminal snapshots, live-state idempotency, restore after live state deletion, and deterministic fingerprint comparison.

---

## Deterministic Replay Validation

Replay should restore the same terminal execution result, not just return a success flag.

The runtime validates deterministic replay by comparing a stable fingerprint before and after restoration.

The fingerprint may include:

- `ExecutionId`
- pipeline name
- terminal status
- completed steps
- required resolved step statuses
- retry counts
- relevant replay-safe metadata

The expected result is:

```text
original completed execution fingerprint
=
restored execution fingerprint after replay
```

This proves that replay restored the same execution outcome.

---

## Execution Fingerprints

An execution fingerprint is a stable representation of important replay-relevant state.

It should avoid volatile values that naturally change between runs or restores.

A useful fingerprint focuses on:

- execution identity
- terminal status
- deterministic step outcomes
- completed step set
- retry counts
- required resolved state
- replay-safe result markers

A fingerprint should not rely on unstable fields such as:

- machine-specific worker identity
- transient claim tokens
- temporary lease ids
- volatile timestamps not required for deterministic validation

The goal is to compare meaningful execution outcome, not incidental runtime noise.

---

## Replay and Retention

Replay must work with retention and compaction.

A completed execution may have:

- compacted payloads
- evicted hot state
- archived step data
- payload references
- resolver-backed reconstruction

Replay foundations must preserve enough information to restore and resolve required completed data.

This means retention cannot destroy replay-critical data.

The relationship is:

```text
Retention keeps hot state bounded.
Snapshots and payload storage preserve durable replay data.
Resolver reconstructs required archived data.
```

---

## Replay and Resolver Reconstruction

The resolver is important for replay because not all data remains inline in Redis.

Replay may need to restore or inspect state whose payloads were externalized.

Resolver reconstruction may use:

- Redis hot state
- payload references
- archive indexes
- MongoDB payload store
- terminal snapshot metadata

This enables replay even after retention has reduced hot state.

---

## Replay and Retry State

Retry state is part of deterministic execution behavior.

Replay validation should preserve or compare retry-related outcomes such as:

- retry count
- final failed status
- completed-after-retry state
- step state after retry exhaustion
- retry-safe terminal metadata

This is important because retry behavior affects the execution outcome.

---

## Replay and Cancellation

Cancellation must be represented correctly in terminal state.

A cancellation request may race with natural DAG completion.

The runtime must ensure that cancellation override is reflected in finalization.

Example:

```text
Cancellation requested
        +
last claimed step completes successfully
        +
DAG convergence appears completed
        ↓
terminal execution status = Cancelled
```

Replay should preserve the final terminal status.

If the execution finalized as `Cancelled`, replay should restore it as `Cancelled`.

---

## Replay and Human-in-the-Loop

Human-in-the-loop foundations may affect replay and audit.

If an execution waited for input and then continued, future audit capabilities should be able to answer:

- what waiting key was used?
- which step requested input?
- what input was submitted?
- when was it submitted?
- who submitted it?

The current runtime has human-input control foundations.

A richer audit trail for human decisions belongs to future durable decision ledger work.

---

## Audit Foundations

Auditability is broader than replay.

Replay answers:

```text
Can I restore and inspect execution state?
```

Audit answers:

```text
Can I explain what happened and why?
```

Current audit foundations include:

- explicit execution state
- explicit step states
- retry state
- control state
- terminal snapshots
- structured configuration
- policy-driven decisions
- observability and trace foundations
- deterministic replay validation

These foundations make future audit and decision ledger capabilities possible.

---

## Execution-Correlated Decision Ledger

The runtime now includes an execution-correlated decision ledger foundation.

It records important runtime decisions such as:

- execution lifecycle decisions
- run lifecycle decisions
- queue decisions
- step claim decisions
- retry decisions
- recovery decisions
- retention decisions
- concurrency admission decisions
- payload externalization and rehydration events
- snapshot persistence events
- replay lifecycle events
- terminal finalization decisions

Replay currently records lifecycle events such as:

- `replay.requested`
- `replay.started`
- `replay.comparison_completed`
- `replay.completed`
- `replay.failed`

The decision ledger is not the same as simple logs.

A decision ledger provides structured evidence of runtime decisions, correlated by execution context and suitable for diagnostics, replay reports, and future audit workflows.

---

## Audit vs Logs

Logs are useful, but they are not enough for audit.

Logs can be:

- incomplete
- unstructured
- noisy
- difficult to correlate
- transient depending on infrastructure

The execution-correlated decision ledger should continue to be:

- structured
- persisted
- correlated by `ExecutionId`
- connected to runtime decisions
- replay-aware
- queryable

The current runtime already has the foundations required for this because decisions are centralized, state-driven, and correlated by execution context.

---

## Replay and Policy-Driven Execution

Policy-driven execution improves auditability.

Policies can affect:

- retry behavior
- retention behavior
- concurrency admission
- throttling behavior

Audit foundations should eventually record:

- which policy ran
- what decision it returned
- what configuration was used
- what runtime context was evaluated
- how the decision affected execution

This is why policy-driven execution and replay/audit are connected.

---

## Replay and Config-Driven Runtime

The runtime is config-driven.

Replay and audit should preserve enough context to understand which configuration shaped execution.

Relevant configuration may include:

- pipeline name
- pipeline version
- step definitions
- step keys
- `config.retry`
- `config.retention`
- `config.concurrency`
- provider/model/operation metadata
- policy definitions

Without configuration context, replay may restore state but fail to explain behavior.

---

## Replay Safety Rules

Replay must follow safety rules:

1. Do not duplicate live execution state.
2. Do not overwrite active executions.
3. Restore only from trusted durable snapshots.
4. Preserve terminal status.
5. Preserve replay-relevant retry state.
6. Preserve payload references required for rehydration.
7. Preserve cancellation finalization outcome.
8. Avoid relying on volatile claim or lease metadata.
9. Normalize or clear non-replay-safe ownership fields.
10. Validate restored state with deterministic fingerprints.
11. Keep replay compatible with retention and compaction.

---

## Replay Flow

A simplified replay flow is:

```text
ReplayAsync(ExecutionId)
        ↓
Record replay.requested
        ↓
Load terminal snapshot
        ↓
If snapshot missing:
    return invalid replay report
        ↓
Record replay.started
        ↓
Validate snapshot data
        ↓
Execute replay validation
        ↓
Validate dependency graph
Validate step states
Validate payload references
Compare fingerprints
        ↓
Optionally load ledger events
Optionally load trace timeline events
        ↓
Record replay.comparison_completed
        ↓
If live DAG state already exists:
    return report without restoring
        ↓
If AuditOnly:
    return report without restoring
        ↓
Normalize replay state
        ↓
Restore DAG record/state
        ↓
Persist replay-compatible snapshot state
        ↓
Record replay.completed
        ↓
Return enriched replay report
```

---

## Deterministic Validation Flow

A simplified deterministic validation flow is:

```text
Completed execution exists
        ↓
Build original execution fingerprint
        ↓
Persist terminal snapshot
        ↓
Delete live DAG state
        ↓
Replay from snapshot
        ↓
Build reconstructed execution fingerprint
        ↓
Compare original and reconstructed fingerprints
        ↓
Original == Restored
```

This validates that replay restores the same terminal execution outcome.

---

## Validated Behavior

The current replay, snapshot, ledger, and timeline foundations are validated through integration tests covering:

- terminal snapshots are created
- replay reports compatible existing state when live state still exists
- replay restores from snapshot after live DAG state deletion
- audit-only replay validates without restoring live DAG state
- restored execution fingerprints match original execution fingerprints
- persisted replay metadata is included in replay reports
- retry counts are preserved in replay validation
- retention does not break required completed step resolution
- archive-backed resolver reconstruction remains compatible with replay foundations
- payload references remain valid after compaction and eviction
- terminal lifecycle remains compatible with snapshot creation
- cancellation finalization outcome is preserved in terminal state
- missing snapshots fail safely with diagnostics
- ledger events are included only when requested
- timeline events are included only when requested
- replay lifecycle events are recorded in the decision ledger

A reference replay integration test validates a real distributed 100-step chaos execution with:

- distributed multi-worker execution
- retry behavior
- retention compaction and eviction
- terminal snapshot persistence
- live execution bundle deletion
- restore from persisted snapshot
- deterministic fingerprint validation
- replay metadata propagation
- ledger event loading
- timeline event loading

The diagnostic replay test prints:

- replay summary
- replay metadata
- ledger summary by category / event / outcome
- replay lifecycle ledger events
- trace summary by category / name
- trace timeline samples

These tests prove that replay restores and validates the same terminal execution result, not only a successful replay flag.


## Replay Diagnostic Log Example

The following example is a shortened diagnostic output from the reference replay integration test:

```text
Multiplexed.AI.Tests.Integration.Runtime.Execution.Persistence.Replay
.AiExecutionReplayReferenceIntegrationTests
.Replay_Should_Print_Ledger_And_Timeline_Report

============================================================
REPLAY DIAGNOSTIC REPORT
============================================================
ExecutionId:            e9c1438329b24d6398a79df1b2907115
Mode:                   ResumeIncomplete
PipelineName:           replay-reference-chaos-100-1f4294573dc14e3a9c1be54d2c13ed74
Status:                 Completed
ReplayValid:            True
FingerprintFound:       True
FingerprintMatches:     True
DependencyGraphValid:   True
StepStateValid:         True
PayloadReferencesValid: True
TotalSteps:             100
CompletedSteps:         100
FailedSteps:            0
WaitingForRetrySteps:   0
RunningSteps:           0
RetryCount:             11
RecoveryCount:          0
Issues:                 0
StepReports:            100
LedgerEvents:           2046
TimelineEvents:         1382
```

This proves that the replay report can validate a real distributed 100-step execution and expose both decision-ledger and trace-timeline evidence.

### Replay Metadata Example

```text
REPLAY METADATA
------------------------------------------------------------
Metadata.ExecutionId:        e9c1438329b24d6398a79df1b2907115
Metadata.FingerprintVersion: v1
Metadata.GeneratedAtUtc:     2026-05-29T10:09:39.6257486Z
Metadata.Fingerprint:        CB25C2C5185D89C55D83032FA15DD113E173C532710E983A9B33EE38CAA8B622
```

The metadata shows the persisted deterministic fingerprint used to compare the original execution state with the reconstructed replay state.

### Ledger Summary Example

```text
LEDGER SUMMARY BY CATEGORY / EVENT / OUTCOME
------------------------------------------------------------
Claim            | claim.acquired                           | Allowed      | Count=111
Claim            | claim.attempted                          | Started      | Count=212
Claim            | claim.denied                             | Denied       | Count=184
Concurrency      | concurrency.lease_acquired               | Allowed      | Count=194
Concurrency      | concurrency.lease_released               | Released     | Count=194
Execution        | execution.completed                      | Completed    | Count=1
Execution        | execution.created                        | Persisted    | Count=1
Execution        | execution.finalized                      | Completed    | Count=1
Finalization     | finalization.completed                   | Completed    | Count=1
Finalization     | finalization.race_lost                   | Denied       | Count=10
Finalization     | finalization.started                     | Started      | Count=11
Payload          | payload.externalized                     | Persisted    | Count=13
Payload          | payload.rehydrated                       | Applied      | Count=100
Policy           | policy.allowed                           | Allowed      | Count=224
Policy           | policy.evaluated                         | Started      | Count=224
Replay           | replay.requested                         | Started      | Count=1
Replay           | replay.started                           | Started      | Count=1
Retention        | retention.compacted                      | Applied      | Count=13
Retention        | retention.evaluated                      | Started      | Count=112
Retention        | retention.evicted                        | Applied      | Count=73
Retention        | retention.skipped                        | Skipped      | Count=26
Retention        | retention.triggered                      | Triggered    | Count=86
Retry            | retry.evaluated                          | Started      | Count=11
Retry            | retry.scheduled                          | Applied      | Count=7
Run              | run.completed                            | Completed    | Count=1
Run              | run.started                              | Started      | Count=1
Snapshot         | snapshot.created                         | Persisted    | Count=11
Step             | step.completed                           | Completed    | Count=100
Step             | step.failed                              | Failed       | Count=11
Step             | step.started                             | Started      | Count=111
```

The ledger summary shows that replay is not isolated from runtime observability. It can return the same execution-correlated decision stream covering claims, concurrency, policy evaluation, retention, retry, snapshots, payload handling, and step lifecycle events.

### Replay Lifecycle Events Example

```text
REPLAY LEDGER EVENTS
------------------------------------------------------------
2026-05-29T10:09:39.9181440+00:00 | replay.requested | Started | Worker=replay-service | StepId=_replay | StepKey=_replay | Reason=Replay request received.
2026-05-29T10:09:39.9599897+00:00 | replay.started   | Started | Worker=replay-service | StepId=_replay | StepKey=_replay | Reason=Replay snapshot loaded.
```

Replay emits its own lifecycle events into the same execution-correlated ledger, making replay itself auditable.

### Trace Summary Example

```text
TRACE SUMMARY BY CATEGORY / NAME
------------------------------------------------------------
dag-store        | RecoverTimedOutSteps.succeeded           | Count=212
dag-store        | TryAcquireConcurrencyLease.succeeded     | Count=194
dag-store        | TryClaimStep.succeeded                   | Count=194
dag-store        | TryFinalizeExecution.succeeded           | Count=11
execution        | execution.succeeded                      | Count=224
retention        | retention.succeeded                      | Count=112
step             | execute.completed                        | Count=224
step             | execute.failed                           | Count=11
step             | execute.succeeded                        | Count=200
```

The trace summary provides a timeline-level view of runtime execution behavior. This complements the decision ledger by showing traced runtime operations grouped by category and operation name.

### Trace Timeline Sample

```text
TRACE TIMELINE SAMPLE
------------------------------------------------------------
2026-05-29T10:09:31.9676676Z | step | execute.succeeded | ExecutionId=e9c1438329b24d6398a79df1b2907115 | RunId=f06bde6ea6994181bcbfd162e3bef380 | Worker=pipeline-background-controller | StepId=chaos-step-001 | StepKey=hello-world | Tags=[category=step, durationMs=5.7675, operation=step.execute, stepType=HelloWorldStep, succeeded=True]
2026-05-29T10:09:31.9950073Z | step | execute.succeeded | ExecutionId=e9c1438329b24d6398a79df1b2907115 | RunId=f06bde6ea6994181bcbfd162e3bef380 | Worker=pipeline-background-controller | StepId=chaos-step-009 | StepKey=distributed.chaos.flaky-provider | Tags=[category=step, durationMs=24.0392, operation=step.execute, stepType=DistributedChaosFlakyProviderStep, succeeded=True]
```

The full test output contains many more timeline records. The example above is intentionally shortened so the documentation remains readable while still showing how replay diagnostics expose execution id, run id, worker id, step id, step key, trace category, trace name, and structured tags.


## Failure Scenarios Covered

| Scenario | Runtime Behavior |
|---|---|
| Replay requested while live state exists | Replay reports already exists and does not duplicate state. |
| Live state deleted after terminal snapshot | Replay restores from snapshot. |
| Retained/compacted data needed after replay | Resolver reconstructs from payload/archive storage. |
| Retry occurred before terminal state | Replay fingerprint includes retry-relevant outcome. |
| Cancelled execution replayed | Restored terminal status remains `Cancelled`. |
| Snapshot missing | Replay should fail safely with diagnostics. |
| Payload reference missing | Resolver should report failure; replay state is incomplete. |
| Volatile claim metadata differs | Fingerprint should ignore non-deterministic transient fields. |
| Stale ownership exists in snapshot | Replay should normalize or clear unsafe ownership state. |

---

## Current Status

| Capability | Status |
|---|---|
| Terminal snapshots | Implemented / validated |
| Snapshot persistence | Implemented / validated |
| Snapshot normalization | Implemented / validated foundations |
| Replay when live state exists | Implemented / validated |
| Audit-only replay | Implemented / validated |
| Replay from snapshot after live state deletion | Implemented / validated |
| Deterministic replay validation | Implemented / validated |
| Execution fingerprint comparison | Implemented / validated |
| Persisted replay metadata | Implemented / validated |
| Dependency graph validation | Implemented / validated |
| Step state validation | Implemented / validated |
| Payload reference validation | Implemented / validated |
| Retry-count preservation in replay validation | Implemented / validated |
| Retention-compatible resolver reconstruction | Implemented / validated foundations |
| Replay-safe terminal status restoration | Implemented / validated foundations |
| Execution-correlated replay ledger events | Implemented / validated |
| Replay ledger event loading | Implemented / validated |
| Replay trace timeline loading | Implemented / validated |
| Replay diagnostic report output | Implemented / validated |
| Replay Engine V1 | Completed |
| Runtime replay controller abstraction | Planned |
| HTTP Replay API | Planned |
| Replay UI / dashboard | Planned |
| Replay export tooling | Planned |
| Compliance-oriented decision inspection | Planned |

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| Snapshot store | Persists terminal execution snapshots. |
| Replay service | Restores execution state from durable snapshots. |
| DAG store | Holds live execution state and restored state. |
| Payload store | Preserves externalized payloads. |
| Resolver | Rehydrates compacted or evicted data. |
| Retention system | Keeps hot state bounded without destroying replay data. |
| Finalization service | Produces terminal state and triggers snapshot lifecycle. |
| Observability layer | Provides execution events and diagnostics. |
| Decision ledger | Persists structured execution-correlated runtime decisions for diagnostics and audit foundations. |
| Trace timeline | Provides execution-correlated trace events for replay diagnostics. |
| Replay validator | Validates fingerprints, dependency graph, step states, and payload references. |

---

## TODO / Improvements

The Replay Engine V1 is implemented and validated, but the operational replay platform still has planned improvements.

### Runtime Abstractions

- Add `IAiExecutionReplayController` as a runtime-level controller abstraction.
- Add controller request/result models for summary, audit, restore, ledger, and timeline use cases.
- Keep the replay controller independent from ASP.NET so CLI, HTTP APIs, dashboards, and Kubernetes tooling can reuse it.

### HTTP API

- Add an ASP.NET integration package or host project for replay endpoints.
- Add endpoint for replay summary by `ExecutionId`.
- Add endpoint for replay audit-only validation.
- Add endpoint for replay restore from snapshot.
- Add endpoint for replay decision ledger events.
- Add endpoint for replay trace timeline events.

### Report Shape

- Add compact summary DTOs for external API consumers.
- Add optional ledger and timeline summaries in addition to raw event lists.
- Add counts for retention actions, concurrency denials, retries, payload rehydrations, and finalization race outcomes.
- Add safer redaction rules for replay reports that may contain sensitive metadata.

### Diagnostics and Dashboard

- Add replay dashboard support for:
  - fingerprint comparison
  - replay validity
  - dependency graph validation
  - step state validation
  - payload validation
  - ledger timeline
  - trace timeline
  - retry and retention summaries
- Add timeline grouping by step, worker, category, event type, provider, model, and operation.

### Persistence and Querying

- Add durable replay report persistence if replay reports need to be saved after generation.
- Add replay search by execution id, pipeline, status, fingerprint, date range, and failure reason.
- Add long-term audit storage strategy for large ledger and timeline histories.

### Testing

- Add targeted tests for fingerprint mismatch once a safe corruption hook or test store helper is available.
- Add tests for replay report redaction rules.
- Add tests for replay API DTO mapping when HTTP APIs are introduced.
- Keep the 100-step replay reference test as the canonical replay proof.

### Operational Concerns

- Decide whether verbose diagnostic output should stay in normal CI or move behind an explicit diagnostic trait.
- Add replay export to JSON and Markdown.
- Add replay export to PDF only from tooling or documentation layers, not from the runtime core.
- Add Kubernetes-aware replay access so any node can inspect an execution from shared durable state.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Observability](observability.md)
- [Observability Tracing](observability-tracing.md)
- [Execution-Correlated Ledger](execution-correlated-ledger.md)
- [Runtime Metrics](runtime-metrics.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction from the complete technical reference.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
