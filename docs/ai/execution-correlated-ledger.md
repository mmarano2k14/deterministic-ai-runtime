# Execution-Correlated Decision Ledger

Status: Implemented and validated for execution-correlated runtime decisions, replay ledger evidence, control-plane causal chain tracing, runtime recovery decisions, tenant-scoped ledger queries, and process-host crash recovery proof.

This document describes the execution-correlated decision ledger foundations of the Deterministic AI Runtime, including the newer control-plane ledger and recovery proof surfaces validated by HTTP process-host crash recovery scenarios.

The ledger records structured runtime decisions and lifecycle transitions that are correlated by `ExecutionId`, `RunId`, step identity, worker/runtime instance, claim token, and optional concurrency context.

---

## Purpose

Production AI execution must be observable, auditable, and explainable.

Logs are useful for operators, but logs alone are not enough to explain distributed runtime decisions.

The decision ledger exists to answer questions such as:

- when was the execution created?
- when was a run queued, dequeued, started, completed, failed, or cancelled?
- why was a step claimed or denied?
- which worker claimed a step?
- which claim token owned the step?
- which concurrency lease was acquired or released?
- was a retry evaluated, scheduled, denied, or exhausted?
- was recovery detected and applied?
- which policy allowed, denied, or failed?
- when was the queue paused or resumed?
- when was execution control state changed?
- when did human input get requested or submitted?
- was a terminal snapshot persisted?
- did storage persistence fail?
- how can future replay/audit tooling reconstruct what happened?

The ledger is designed as structured evidence of runtime behavior.

It is not a replacement for logs, metrics, or traces. It complements them.

---

## Current Scope

The current implementation provides execution-correlated ledger foundations across the runtime.

Implemented ledger areas include:

- execution lifecycle
- controller run lifecycle
- queue control
- distributed claim lifecycle
- step execution lifecycle
- retry decisions
- recovery decisions
- policy evaluation decisions
- concurrency and throttling decisions
- execution control state
- human input control
- retention and compaction auditability
- payload lifecycle foundations
- snapshot persistence
- storage persistence failure
- finalization lifecycle and race outcomes

Replay-specific ledger evidence is now validated through the replay / MCP observability path.

Replay, ledger, trace, and replay-report readability are part of the production recovery proof. The low-level runtime still avoids emitting replay intent prematurely from places that do not own replay orchestration, but replay evidence itself is no longer only a future concept.

---

## Runtime Ledger vs Control-Plane Ledger

The ledger now covers two complementary evidence surfaces.

```text
Execution-correlated ledger
= facts and decisions about a specific DAG execution, run, step, worker, retry, recovery, policy, concurrency lease, snapshot, replay, or trace.

Control-plane ledger
= facts and decisions about infrastructure orchestration: scale-out request persistence, watcher observation, provider selection, runtime host creation, capacity visibility, registry/capacity lookup, recovery reconciliation, redispatch, and tenant isolation.
```

Both surfaces are required for production recovery proof.

Execution-correlated evidence can show that an execution resumed and completed.

Control-plane evidence can show why a replacement runtime was created, which provider was selected, when capacity became visible, which assigned work was reconciled, and whether any tenant-scoped query leaked across tenants.

The production process-host crash recovery scenarios validate both surfaces together.

---

## Runtime Crash Recovery Ledger Scope

Runtime instance crash recovery is different from stale step recovery.

Step recovery records that a worker abandoned a claimed step and that the step became eligible again.

Runtime instance crash recovery records that an entire runtime process became unsafe and that all work assigned to it had to be reconciled.

Assigned work can include:

```text
InFlightExecution
    Durable ExecutionId already exists.
    Recovery must resume the same ExecutionId on replacement capacity.

LocalQueued
    SharedRunId exists.
    LocalRunId exists.
    ExecutionId may not exist yet.
    Recovery must redispatch the durable SharedRunId without duplicate submission.
```

The ledger must preserve the distinction between these cases.

In-flight recovery is execution-correlated by `ExecutionId`.

Local-queued recovery is primarily correlated by `SharedRunId` and failed `LocalRunId` until the replacement runtime creates the durable `ExecutionId`.

This is why the ledger keeps `RunId`, `LocalRunId`, `SharedRunId`, and `ExecutionId` separate instead of collapsing them into one id.

---

## Validated Control-Plane Causal Chain

The process-host recovery scenario validates a control-plane causal chain across infrastructure and recovery decisions.

Validated domains include:

```text
scale-out request persisted
scale-out watcher observed request
provider selected
runtime host manager created host
process runtime host started
runtime capacity became visible
runtime instance visible through registry/capacity lookup
execution recovery reconciled assigned work
recovered work redispatched
```

Important boundary:

```text
Health reconciliation detects unsafe capacity.
Execution recovery reconciliation recovers assigned work.
HTTP provider reports transport and endpoint failure reasons.
HTTP provider does not own runtime recovery.
Runtime lifecycle owners create or attach replacement capacity.
```

The ledger should never imply that the HTTP provider directly kills, restarts, or replaces runtime instances.

The HTTP provider can emit or return endpoint failure signals such as `http-circuit-open`, but the control plane health and recovery layers decide what that means for routing and assigned work.

---

## Tenant-Scoped Ledger Proof

The ledger is not only an internal diagnostics stream.

It is queried through the MCP observability surface under a tenant-scoped `ExecutionContext`.

Validated crash recovery proof includes direct tenant-scoped ledger queries showing:

```text
TenantBEntriesVisibleFromTenantA = 0
TenantAEntriesVisibleFromTenantB = 0
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
CrossTenantLedgerLeakDetected = false
```

This matters because multi-tenant recovery is not proven only by completing impacted work.

It is also proven by showing that unrelated tenants do not receive recovery entries, recovery forensics, or ledger contamination.

The safe tenant in the production recovery scenario completes normal work, exposes replay / ledger / trace evidence, and remains absent from the recovery ledger surface.

---

## Why a Decision Ledger Matters

Distributed AI workflows are not simple method calls.

A production AI workflow may involve:

- multiple runtime instances
- multiple workers
- DAG dependencies
- Redis Lua atomic claims
- retry scheduling
- stale-worker recovery
- hot-state retention
- payload compaction
- externalized payloads
- provider/model throttling
- queue control
- execution pause/resume/cancel
- human-in-the-loop waiting
- terminal snapshot persistence
- finalization races

When something happens in such a system, the important question is not only:

```text
What log line was printed?
```

The important question is:

```text
Which runtime decision was made, why, by whom, against which execution, and with which state context?
```

The decision ledger provides a durable and queryable foundation for that answer.

---

## Ledger vs Logs vs Metrics vs Traces

The runtime uses multiple observability layers.

| Layer | Purpose |
|---|---|
| Logs | Human-readable operational messages. |
| Metrics | Aggregated counters, durations, and runtime health signals. |
| Traces | Timeline and distributed flow diagnostics. |
| Decision ledger | Structured runtime decisions and lifecycle facts correlated to execution state. |

The decision ledger is more structured than logs and more decision-oriented than metrics.

A log may say:

```text
[AI DAG] Step claimed.
```

A ledger entry should record:

```text
ExecutionId
PipelineKey
StepName
StepKey
WorkerId
ClaimToken
Category
EventType
Outcome
Reason
Metadata
TimestampUtc
```

This makes the event queryable, auditable, and usable by future replay/audit tooling.

---

## Correlation Model

Ledger entries are correlated through `AiRuntimeCorrelationContext`.

The correlation context may include:

- `ExecutionId`
- `CorrelationId`
- `PipelineKey`
- `StepName`
- `StepKey`
- `WorkerId`
- `ClaimToken`
- concurrency scope information
- provider/model/operation information
- payload/input/human input references where applicable

The goal is to make every important event explainable in relation to the execution it belongs to.

---

## RunId and ExecutionId Separation

The runtime separates controller lifecycle identity from durable DAG execution identity.

```text
RunId
= controller / queue / background run lifecycle id

ExecutionId
= authoritative durable DAG execution id
```

The decision ledger respects this separation.

Examples:

```text
run.queued
run.dequeued
```

may exist before the authoritative `ExecutionId` is available.

Once execution is created, later run events become execution-correlated.

This avoids confusing queue/controller lifecycle with durable DAG execution state.

---

## Ledger Entry Shape

A decision ledger entry contains structured information such as:

```text
EntryId
Sequence
TimestampUtc
Category
EventType
Outcome
Reason
CorrelationContext
Metadata
```

The runtime currently uses stable string event constants instead of a very large enum.

This keeps the ledger extensible while still avoiding magic strings across the runtime.

---

## Event Categories

The current ledger event constants are grouped by runtime domain.

Implemented or foundation-ready categories include:

- `Execution`
- `Run`
- `Queue`
- `Dag`
- `Claim`
- `Step`
- `Recovery`
- `Retry`
- `Policy`
- `Concurrency`
- `Control`
- `HumanInput`
- `Retention`
- `Payload`
- `Snapshot`
- `Storage`
- `Finalization`
- `Replay`

Replay constants are used by the replay / MCP observability path where replay intent, replay validation, comparison, and lineage are known.

---

## Execution Events

Execution events describe durable execution lifecycle transitions.

Current execution events include:

```text
execution.created
execution.started
execution.completed
execution.failed
execution.cancelled
execution.finalized
```

These events are used to understand the lifecycle of the authoritative execution.

Execution events are correlated by `ExecutionId`.

---

## Run Events

Run events describe controller-level run lifecycle transitions.

Current run events include:

```text
run.queued
run.dequeued
run.started
run.completed
run.failed
run.cancelled
```

Run events are important because the controller may know about a run before durable DAG execution state exists.

They provide visibility into:

- queue acceptance
- dequeue behavior
- controller start
- controller completion
- controller failure
- cancellation flow

---

## Queue Events

Queue events describe runtime queue control.

Current queue events include:

```text
queue.paused
queue.resumed
```

These events make queue-level operational control auditable.

Queue events are especially useful for explaining why runs were delayed or why a controller was not dequeuing work.

---

## Claim Events

Claim events describe distributed DAG claim decisions.

Current claim events include:

```text
claim.attempted
claim.acquired
claim.denied
claim.expired
claim.released
claim.lease_renewed
claim.lease_expired
```

The currently integrated runtime flow records the core claim lifecycle:

- claim attempted
- claim acquired
- claim denied

Claim events are essential for proving duplicate execution prevention.

They show which worker attempted to claim work, which claim succeeded, and why a claim was denied.

---

## Step Events

Step events describe physical step execution.

Current step events include:

```text
step.started
step.completed
step.failed
step.timed_out
```

The current runtime records:

- step started
- step completed
- step failed

These events are emitted from the claimed-step executor and are correlated with:

- `ExecutionId`
- step name
- step key
- worker id
- claim token
- optional concurrency context

Step events are runtime-side execution evidence.

They do not replace persisted step state.

---

## Retry Events

Retry events describe retry decision outcomes after a failed step transition has been persisted.

Current retry events include:

```text
retry.evaluated
retry.scheduled
retry.denied
retry.attempt_started
retry.attempt_completed
retry.budget_exhausted
```

The current runtime records:

- `retry.evaluated`
- `retry.scheduled`
- `retry.denied`
- `retry.budget_exhausted`

These are recorded after the failed step transition is persisted and the step state is reloaded.

This is important because retry ledger events must reflect the durable result, not only an in-memory decision.

Typical metadata includes:

- step name
- step key
- failure source
- error
- step status
- retry count
- max retries
- next retry timestamp

`retry.attempt_started` and `retry.attempt_completed` remain future candidates for the point where a waiting-for-retry step is actually reclaimed and executed again.

---

## Recovery Events

Recovery events describe both stale running-step recovery and runtime instance crash recovery.

Current recovery events include:

```text
recovery.detected
recovery.applied
recovery.step_recovered
recovery.execution_recovered
execution.recovery.candidate.detected
shared.run.requeued.for.resume
failed.local.run.marked.requeued.for.recovery
replacement.runtime.selected
replacement.local.run.registered
resume.context.seeded
dag.resume.started
dag.resume.completed
execution.recovery.completed
SharedRunRequeuedForLocalQueuedRecovery
```

The runtime records step-level recovery events when timed-out running steps are recovered through the distributed DAG store.

The process-host recovery path also validates execution-level and assigned-work recovery evidence:

- in-flight execution recovery candidates are detected;
- the shared run is requeued for resume;
- the failed local run is marked requeued for recovery;
- replacement runtime capacity is selected;
- replacement local run is registered;
- resume context is seeded;
- DAG resume starts and completes;
- execution recovery is completed.

For local-queued work, the ledger/forensics path records durable shared-run redispatch without pretending that the dead runtime local queue survived.

---

## Policy Events

Policy events describe step-scoped policy evaluation.

Current policy events include:

```text
policy.evaluated
policy.allowed
policy.denied
policy.skipped
policy.failed
```

The current runtime records:

- `policy.evaluated`
- `policy.allowed`
- `policy.denied`
- `policy.failed`

Policy decisions are recorded by the shared policy engine base class.

This keeps policy audit consistent across retry, retention, concurrency, and future policy domains.

`policy.skipped` is intentionally not emitted for an empty policy list.

It should only be used when a policy was deliberately skipped by runtime logic.

---

## Concurrency Events

Concurrency events describe distributed admission and throttling behavior.

Current concurrency events include:

```text
concurrency.evaluated
concurrency.allowed
concurrency.denied
concurrency.throttle_applied
concurrency.lease_acquired
concurrency.lease_released
concurrency.lease_expired
```

The current runtime records the most important distributed concurrency events:

- `concurrency.denied`
- `concurrency.lease_acquired`
- `concurrency.lease_released`

These events explain why a step was admitted, denied, or released.

They are especially important for provider/model/operation throttling.

Metadata may include:

- pipeline key
- step name
- step key
- worker id
- lease id
- claim mode

---

## Control Events

Control events describe durable execution control state.

Current control events include:

```text
control.pause_requested
control.paused
control.resume_requested
control.resumed
control.cancel_requested
control.cancel_observed
control.state_changed
```

These events make pause, resume, and cancellation auditable.

The control service records operator/system intent.

The DAG claim service can record when control state blocks advancement.

This separation is important:

```text
control.cancel_requested
= cancellation was requested

control.cancel_observed
= runtime observed cancellation while attempting to advance
```

---

## Human Input Events

Human input events describe human-in-the-loop state.

Current human input events include:

```text
human_input.requested
human_input.submitted
human_input.rejected
human_input.expired
human_input.waiting
```

The current runtime records:

- `human_input.requested`
- `human_input.waiting`
- `human_input.submitted`

These events explain when execution entered a waiting state and when external input was submitted.

Metadata may include:

- waiting key
- waiting step name
- requested by
- submitted by
- input key count
- control status
- pending action

---

## Retention Events

Retention events describe hot-state lifecycle decisions.

Current retention events include:

```text
retention.evaluated
retention.triggered
retention.skipped
retention.compacted
retention.evicted
```

Retention and compaction are critical because the runtime must keep Redis hot state bounded without destroying replay-critical data.

The decision ledger foundations support auditability for:

- retention evaluation
- retention trigger decisions
- payload compaction
- hot-state eviction
- atomic retention patch application
- resolver-safe evicted step reconstruction

Retention events are related to but separate from snapshot and replay events.

Retention controls hot state.

Snapshots and payload storage preserve durable replay data.

---

## Payload Events

Payload events describe payload lifecycle transitions.

Current payload events include:

```text
payload.externalized
payload.rehydrated
payload.resolution_failed
```

Payload event coverage is important because large result payloads may be compacted or externalized.

When payloads move out of hot state, the runtime must still be able to resolve them through payload references and archive indexes.

Payload events are useful for future audit tooling and replay diagnostics.

---

## Snapshot Events

Snapshot events describe durable terminal snapshot lifecycle.

Current snapshot events include:

```text
snapshot.created
snapshot.loaded
snapshot.restore_requested
snapshot.restore_completed
```

The current runtime records:

```text
snapshot.created
```

after terminal snapshot persistence succeeds.

This provides audit evidence that a replay-critical artifact was persisted.

Replay-related snapshot restore events are deferred until the official Replay API.

---

## Storage Events

Storage events describe durable state persistence outcomes.

Current storage events include:

```text
storage.state_persisted
storage.state_persistence_failed
```

The current runtime records:

```text
storage.state_persistence_failed
```

when best-effort snapshot persistence fails.

Snapshot persistence remains best-effort and must not affect execution reliability.

The ledger makes the failure visible without changing execution outcome.

---

## Finalization Events

Finalization events describe terminal convergence and distributed finalization behavior.

Current finalization events include:

```text
finalization.started
finalization.completed
finalization.failed
finalization.cancellation_override_applied
finalization.race_lost
```

These events are important in a distributed runtime because multiple workers may observe terminal convergence at nearly the same time.

Only one worker should win finalization.

`finalization.race_lost` is not a system failure.

It is an expected distributed coordination outcome.

---

## Replay Events

Replay events are emitted or surfaced by the replay / MCP observability path where replay intent, replay validation, comparison, and lineage are known.

Replay event constants include:

```text
replay.requested
replay.started
replay.completed
replay.failed
replay.comparison_completed
replay.convergence_proof_started
replay.convergence_proof_completed
replay.convergence_proof_failed
```

The core runtime still must not emit replay intent prematurely from low-level execution code.

The validated boundary is:

```text
runtime execution produces durable state, ledger, trace, and snapshots
    ↓
replay / MCP observability owns replay request and validation
    ↓
replay report, replay ledger, and replay trace become queryable proof
```

Production crash recovery scenarios validate replay evidence after recovery for both impacted tenants and the safe tenant.

---

## Recovery Replay / Ledger / Trace Proof

A recovered execution is not considered fully proven only because the DAG reaches `Completed`.

The recovery proof must also show:

```text
execution ledger evidence
execution trace evidence
completion evidence
step completion evidence
replay report readable
replay ledger readable
replay trace readable
strict replay validation
Synthetic = false
```

The process-host crash recovery scenario validates this for all executions in the scenario, including recovered executions and safe-tenant executions.

This is the difference between operational recovery and audit recovery:

```text
Operational recovery
= work completed after failure.

Audit recovery
= work completed after failure and remains explainable through replay, ledger, trace, and forensics.
```

---

## Forensics Relationship

The decision ledger and runtime recovery forensics are related but not identical.

```text
Ledger
= structured decision and lifecycle evidence across runtime and control-plane domains.

Forensics
= per-incident / per-work-item recovery timeline tied to a RuntimeFailureIncidentId and ForensicsId.
```

Forensics gives a compact causal timeline for each recovered work item.

The ledger gives the broader runtime and control-plane evidence surface around those decisions.

A production recovery proof should be able to query both.

---

## Atomic Retention and Compaction Auditability

The ledger work also supports the retention and compaction improvements.

The runtime validates that aggressive retention can:

- compact payload data
- evict hot-state step payloads
- preserve completed step shells
- keep resolver reconstruction working
- preserve retry counts
- reconstruct fingerprint steps
- keep terminal execution consistent

The ledger foundations make these flows auditable.

This is important because retention is not only a memory optimization.

Retention affects long-running execution correctness, replay readiness, and post-execution inspection.

---

## Execution-Correlated Runtime Domains

The current execution-correlated ledger covers these runtime domains:

| Domain | Purpose |
|---|---|
| Execution | Durable execution lifecycle. |
| Run | Controller run lifecycle. |
| Queue | Queue pause/resume control. |
| Claim | Distributed claim decisions. |
| Step | Physical step execution. |
| Retry | Retry evaluation and scheduling. |
| Recovery | Stale running-step recovery. |
| Policy | Policy evaluation and results. |
| Concurrency | Distributed throttling and lease lifecycle. |
| Control | Pause, resume, cancel, and control state. |
| HumanInput | Human-in-the-loop waiting and submission. |
| Retention | Hot-state lifecycle and compaction decisions. |
| Payload | Payload externalization and rehydration foundations. |
| Snapshot | Terminal snapshot persistence. |
| Storage | Persistence failure visibility. |
| Finalization | Terminal convergence and race outcomes. |

---

## Ledger Write Behavior

The ledger recorder supports configurable write behavior.

Typical write modes include:

- disabled
- best-effort
- strict or fail-fast behavior where configured

Best-effort mode is important for production execution reliability.

Ledger failure should not necessarily break workflow execution unless explicitly configured to do so.

This allows the runtime to preserve execution safety while still producing audit evidence whenever possible.

---

## Testing Coverage

The ledger integration is validated through integration and runtime tests covering:

- run lifecycle events
- queue pause/resume events
- execution control events
- human input events
- claim events
- retry events
- recovery events
- policy events
- concurrency events
- snapshot events
- storage failure events
- finalization events
- aggressive retention and compaction
- evicted-step resolver reconstruction
- 100-step distributed chaos execution
- 500-step aggressive retention execution

The tests also validated regressions around:

- queue ledger correlation
- RunId vs ExecutionId semantics
- handle usage in controller tests
- retention policy non-terminal filtering
- aggressive retention hot-state behavior
- resolver consistency after eviction
- premature DAG ready-step event emission

---

The following output is produced by the integration test:

Full ledger output is available here:

- [Download full ledger logs](../files/ledger-logs-example.txt)

```text

============================================================
EXECUTION-CORRELATED DECISION LEDGER
============================================================
ExecutionId: 6b5c904620f4492e9114abea71a60bb0
Pipeline:    distributed-chaos-100-1da54d410606417885e95afc812a9854
Steps:       100
Workers:     10
Events:      2116

SUMMARY BY CATEGORY / EVENT / OUTCOME
------------------------------------------------------------
Claim            | claim.acquired                           | Allowed      | Count=111
Claim            | claim.attempted                          | Started      | Count=224
Claim            | claim.denied                             | Denied       | Count=210
Concurrency      | concurrency.lease_acquired               | Allowed      | Count=209
Concurrency      | concurrency.lease_released               | Released     | Count=209
Execution        | execution.completed                      | Completed    | Count=1
Execution        | execution.created                        | Persisted    | Count=1
Execution        | execution.finalized                      | Completed    | Count=1
Finalization     | finalization.completed                   | Completed    | Count=1
Finalization     | finalization.race_lost                   | Denied       | Count=8
Finalization     | finalization.started                     | Started      | Count=9
Payload          | payload.externalized                     | Persisted    | Count=15
Payload          | payload.rehydrated                       | Applied      | Count=101
Policy           | policy.allowed                           | Allowed      | Count=224
Policy           | policy.evaluated                         | Started      | Count=224
Retention        | retention.compacted                      | Applied      | Count=15
Retention        | retention.evaluated                      | Started      | Count=112
Retention        | retention.evicted                        | Applied      | Count=77
Retention        | retention.skipped                        | Skipped      | Count=20
Retention        | retention.triggered                      | Triggered    | Count=92
Retry            | retry.evaluated                          | Started      | Count=11
Retry            | retry.scheduled                          | Applied      | Count=6
Run              | run.completed                            | Completed    | Count=1
Run              | run.started                              | Started      | Count=1
Snapshot         | snapshot.created                         | Persisted    | Count=11
Step             | step.completed                           | Completed    | Count=100
Step             | step.failed                              | Failed       | Count=11
Step             | step.started                             | Started      | Count=111

TIMELINE
------------------------------------------------------------
2026-05-28T05:14:45.7581746+00:00 | Execution        | execution.created                        | Persisted    | StepId=_execution | StepKey=_execution | Worker=pipeline-background-controller | Reason=DAG execution created and persisted. | Metadata=[context.key=03c31e87d76c443392ae198a24314dfa, execution.mode=Dag, pipeline.name=distributed-chaos-100-1da54d410606417885e95afc812a9854, pipeline.version=1.0.0, step.count=100]
2026-05-28T05:14:45.7582034+00:00 | Run              | run.started                              | Started      | StepId=pipeline-run | StepKey=pipeline-run | Worker=pipeline-background-controller | Reason=Pipeline run started execution processing. | Metadata=[distributed.enabled=True, distributed.worker.count=10, execution.id=6b5c904620f4492e9114abea71a60bb0, pipeline.name=distributed-chaos-100-1da54d410606417885e95afc812a9854, run.id=8e56705dcdd34bbe8968a525300749da]
2026-05-28T05:14:45.9217992+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:4:4db91ebc24e849d7889483eaadcef703 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9217994+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:5:8f66c4bf16cc40ba8643297c438f6885 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9224809+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:7:77a638c87d1e46619402255ecb3fa963 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9225415+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:8:8f5e195a48124c35bbc088eda052c878 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9225420+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:3:167d09d6c67541fe9ebd8711242d2d6c | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9225447+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:1:05fc4870fd5640c4b4e43f3f4f0183c0 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9225596+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:9:8e9240dbc86e4135ac75cf56b110d7db | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9231293+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:10:4760bf8466a74a10a6bbe63d81abb8f1 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9231391+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:2:97053d2b53a84c2ca05813cc7c7b0e37 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:45.9231622+00:00 | Claim            | claim.attempted                          | Started      | StepId=_claim | StepKey=_claim | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:6:254fb521139c452a9ab491cb5732c597 | Reason=Batch claim attempt started. | Metadata=[claim.mode=batch, max.steps=1, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:46.4746474+00:00 | Concurrency      | concurrency.lease_acquired               | Allowed      | StepId=chaos-step-001 | StepKey=hello-world | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:10:4760bf8466a74a10a6bbe63d81abb8f1 | Reason=Concurrency lease acquired before batch step claim. | Metadata=[claim.mode=batch, lease.id=6b5c904620f4492e9114abea71a60bb0:chaos-step-001:MSI:34196:5578191c9d0b435bb817fba324b2d34b, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, step.key=hello-world, step.name=chaos-step-001, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:46.4746475+00:00 | Concurrency      | concurrency.lease_acquired               | Allowed      | StepId=chaos-step-001 | StepKey=hello-world | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:5:8f66c4bf16cc40ba8643297c438f6885 | Reason=Concurrency lease acquired before batch step claim. | Metadata=[claim.mode=batch, lease.id=6b5c904620f4492e9114abea71a60bb0:chaos-step-001:MSI:34196:5578191c9d0b435bb817fba324b2d34b, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, step.key=hello-world, step.name=chaos-step-001, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:46.4758400+00:00 | Claim            | claim.denied                             | Denied       | StepId=chaos-step-001 | StepKey=hello-world | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:5:8f66c4bf16cc40ba8643297c438f6885 | Reason=Batch step claim failed after concurrency lease was acquired. | Metadata=[claim.mode=batch, lease.id=6b5c904620f4492e9114abea71a60bb0:chaos-step-001:MSI:34196:5578191c9d0b435bb817fba324b2d34b, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, step.key=hello-world, step.name=chaos-step-001, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:46.4758434+00:00 | Claim            | claim.acquired                           | Allowed      | StepId=chaos-step-001 | StepKey=hello-world | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:10:4760bf8466a74a10a6bbe63d81abb8f1 | Reason=Step claim acquired. | Metadata=[claim.mode=batch, claim.token=344254933d644e8394a7c8f5f16d5ed2, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, step.key=hello-world, step.name=chaos-step-001, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:46.4758809+00:00 | Step             | step.started                             | Started      | StepId=chaos-step-001 | StepKey=hello-world | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:10:4760bf8466a74a10a6bbe63d81abb8f1 | Reason=Step execution started. | Metadata=[claim.token=344254933d644e8394a7c8f5f16d5ed2, pipeline.name=distributed-chaos-100-1da54d410606417885e95afc812a9854, pipeline.version=1.0.0, step.key=hello-world, step.name=chaos-step-001, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]
2026-05-28T05:14:46.4776522+00:00 | Concurrency      | concurrency.lease_released               | Released     | StepId=chaos-step-001 | StepKey=hello-world | Worker=MSI:34196:5578191c9d0b435bb817fba324b2d34b:worker:5:8f66c4bf16cc40ba8643297c438f6885 | Reason=Concurrency lease released after failed batch step claim. | Metadata=[claim.mode=batch, lease.id=6b5c904620f4492e9114abea71a60bb0:chaos-step-001:MSI:34196:5578191c9d0b435bb817fba324b2d34b, pipeline.key=distributed-chaos-100-1da54d410606417885e95afc812a9854:1.0.0, step.key=hello-world, step.name=chaos-step-001, worker.id=MSI:34196:5578191c9d0b435bb817fba324b2d34b]

```

---

## Events Intentionally Deferred

Some event types exist but are intentionally not fully emitted yet.

Deferred or placement-sensitive events include:

- `dag.step_became_ready`
- `dag.step_blocked`
- `dag.step_unblocked`
- `dag.step_skipped`
- `retry.attempt_started`
- `retry.attempt_completed`

Execution-level recovery and replay evidence are no longer treated as globally deferred concepts. They are emitted or surfaced at the layers that own the corresponding decision: execution recovery reconciliation, replay orchestration, MCP observability, and recovery forensics.

These are deferred because they require precise runtime placement.

For example, `dag.step_became_ready` must be emitted only after persisted DAG completion state is stable.

Adding it too early can observe stale state and interfere with retention-heavy tests.

---

## Design Principles

The ledger follows these principles:

1. Record structured runtime decisions, not noisy log lines.
2. Correlate by `ExecutionId` wherever possible.
3. Preserve `RunId` vs `ExecutionId` separation.
4. Emit replay evidence only from the replay / MCP observability boundary that owns replay intent.
5. Do not put orchestration decisions inside low-level stores unless necessary.
6. Prefer runtime-layer ledger emission for runtime decisions.
7. Keep store-level code focused on atomic persistence.
8. Do not break execution reliability if best-effort ledger persistence fails.
9. Avoid duplicate or misleading events.
10. Emit events only after durable state has reached the decision being recorded.

---

## Relationship to Replay and Audit

The ledger is a foundation for future replay and audit capabilities.

Replay answers:

```text
Can I restore and compare execution state?
```

The ledger helps answer:

```text
What happened before, during, and after that execution?
```

Together, snapshots, replay, resolver reconstruction, and decision ledger entries will allow future tooling to provide:

- replay timelines
- audit trails
- decision history
- runtime control history
- retry/recovery explanation
- provider throttling explanation
- retention and compaction inspection
- finalization race explanation

Replay-specific ledger evidence is produced by the replay / MCP observability boundary, while low-level runtime code still avoids emitting replay intent prematurely.

---

## Current Status

| Capability | Status |
|---|---|
| Execution lifecycle ledger | Implemented |
| Run lifecycle ledger | Implemented |
| Queue control ledger | Implemented |
| Claim ledger | Implemented |
| Step execution ledger | Implemented |
| Retry decision ledger | Implemented |
| Recovery decision ledger | Implemented / validated for stale step and runtime crash recovery evidence |
| Policy decision ledger | Implemented |
| Concurrency ledger | Implemented |
| Execution control ledger | Implemented |
| Human input ledger | Implemented |
| Retention and compaction auditability | Foundation implemented |
| Snapshot created ledger | Implemented |
| Storage persistence failure ledger | Implemented |
| Finalization ledger | Implemented |
| Control-plane causal chain ledger | Implemented / validated |
| Runtime recovery causal evidence | Implemented / validated |
| Safe tenant ledger non-contamination proof | Implemented / validated |
| Replay ledger / replay evidence | Implemented / validated through replay and MCP observability path |
| Tenant-scoped MCP ledger query API | Implemented / validated |
| Replay decision lineage | Foundation implemented / validated where replay evidence is produced |
| UI / dashboard | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| `AiRuntimeObservability` | Exposes metrics, tracing, logging, and decision ledger recorder as a runtime facade. |
| `IAiDecisionLedgerRecorder` | Records ledger entries according to configured write mode. |
| `AiRuntimeCorrelationContextHelper` | Creates stable execution correlation context. |
| Runtime controller | Records run and queue lifecycle events. |
| Execution control service | Records control and human-input lifecycle events. |
| DAG claim service | Records claim, recovery, control-blocking, waiting, and concurrency decisions. |
| Claimed step executor | Records step execution and concurrency lease release events. |
| Policy engine | Records policy evaluation and decision events. |
| Retry ledger helper | Records retry decision outcomes after persisted failure transitions. |
| Snapshot service | Records snapshot persistence and storage failure events. |
| Finalization service | Records terminal finalization events and race outcomes. |
| Replay / MCP observability boundary | Records or exposes replay-specific events, replay evidence, replay ledger, replay trace, and decision lineage where replay intent is known. |
| Runtime health reconciler | Produces unsafe runtime capacity decisions that influence routing and recovery, without owning work redispatch. |
| Execution recovery reconciler | Records assigned-work recovery decisions for unsafe runtime instances and separates in-flight resume from local-queued redispatch. |

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Replay and Audit](replay-and-audit.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Observability](observability.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction and update based on the current execution-correlated ledger implementation.

The original technical depth remains preserved in:

