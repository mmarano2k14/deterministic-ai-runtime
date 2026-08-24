# Observability

Status: Documentation split in progress / validated for durable process-boundary observability, MCP replay / ledger / trace queries, runtime crash recovery forensics, tenant-scoped ledger isolation, and control-plane causal chain evidence. This page is the high-level observability index for the Deterministic AI Runtime.

This document summarizes the focused observability documents:

- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md) — canonical event namespace, Event Manager projection architecture, deterministic lifecycle observer, event catalog, and EventDriven testing contract.
- [Durable Child DAG Composition](child-dag-composition.md) — implemented / validated through recursive Depth3 production scenarios.


---

## Purpose

Distributed AI execution cannot be operated safely as a black box.

In production, an AI runtime may involve:

- controller queue lifecycle
- durable DAG execution lifecycle
- multiple runtime instances
- multiple workers
- distributed step claims
- retry and step-level recovery
- runtime instance crash recovery
- recovery forensics
- retention and compaction
- externalized payloads
- payload rehydration
- concurrency admission
- provider/model/operation throttling
- pause, resume, cancel, and human input control
- snapshot and replay foundations
- replay / ledger / trace proof after recovery
- tenant-scoped observability isolation
- terminal finalization races

Observability exists to make this behavior visible, measurable, traceable, and auditable.

The runtime should be able to answer:

```text
What happened?
When did it happen?
Which execution did it belong to?
Which run created it?
Which worker did it?
Which runtime instance observed it?
Which step was affected?
Which claim token owned it?
Which provider/model/operation was involved?
Which runtime decision was made?
Which runtime failed?
Which work item was recovered?
Was recovery scoped to the impacted tenant only?
Did unrelated tenants remain absent from the recovery surface?
Which metrics changed?
Can this be inspected later?
```

---

## Centralized Engine Event Observation — Implemented

The runtime aligns semantic engine lifecycle observation through the **existing Event Manager** rather than introducing another event system. Canonical engine events are declared under one dedicated namespace and projected centrally to the existing Ledger, Recovery Forensics, Execution Forensics, Runtime Lifecycle Journal, Metrics, Logging, and Realtime implementations.

```text
Engine semantic fact
    ↓
Canonical event
    ↓
Existing Event Manager
    ↓
Central Projection Catalog
    ├── Ledger
    ├── Recovery / Execution Forensics
    ├── Runtime Lifecycle Journal
    ├── Metrics
    ├── Logging
    └── Realtime
```

The centralized architecture is also the synchronization surface for EventDriven production tests. Durable facts use the missed-event-safe pattern:

```text
check durable evidence
→ subscribe realtime
→ re-check durable evidence
→ await canonical event
→ verify final durable state
```

This lifecycle coverage is now validated with recursive Child DAG execution through Depth3, real child-runtime failure, distinct parent ProcessHost/Pod failure, same-`ExecutionId` resume, warm reuse, replay, Ledger, Runtime Lifecycle Journal, trace, and Recovery Forensics proof. Child DAG composition is therefore documented as **Implemented / validated**.

See [Engine Event Observation and Lifecycle Catalog](engine-event-observation.md) and [Durable Child DAG Composition](child-dag-composition.md).

---

## Observability Model

The runtime observability model is composed of five complementary layers plus replay/audit validation.

| Layer | Purpose | Main Document |
|---|---|---|
| Decision ledger | Durable structured runtime decisions and lifecycle facts. | [Execution-Correlated Decision Ledger](execution-correlated-ledger.md) |
| Tracing | Runtime timeline and operation flow diagnostics. | [Observability, Metrics, and Tracing](observability-tracing.md) |
| Metrics | Aggregated counters, totals, durations, and grouped runtime signals. | [Runtime Metrics](runtime-metrics.md) |
| Runtime recovery forensics | Durable per-work-item recovery timelines and failure incident evidence. | [Runtime Recovery Forensics](runtime-recovery-forensics.md) |
| Logs | Human-readable runtime messages for operators and developers. | This overview and runtime internals |

These layers are intentionally separate.

A log explains something to a human.

A metric summarizes behavior.

A trace shows how runtime activity flowed over time.

A ledger entry records a structured decision or lifecycle fact.

Together, they make distributed AI execution explainable. After runtime process crash recovery, replay / ledger / trace / forensics are validated together as one proof surface rather than as unrelated diagnostics.

---

## Correlation Model

The current observability foundation aligns metrics, tracing, and the decision ledger around shared runtime correlation.

Important correlation fields include:

- `CorrelationId`
- `RunId`
- `SharedRunId`
- `LocalRunId`
- `ExecutionId`
- `TenantId`
- `TenantGroupId`
- `PipelineName`
- `PipelineVersion`
- `PipelineKey`
- `RuntimeInstanceId`
- `WorkerId`
- `StepId`
- `StepKey`
- `ClaimToken`
- provider
- model
- operation
- payload references
- human input references
- trace scope identifiers
- `ForensicsId`
- `RuntimeFailureIncidentId`

This shared model allows a future dashboard, replay API, or audit API to connect runtime behavior across different observability layers.

---

## RunId and ExecutionId Separation

The runtime separates controller lifecycle identity from durable DAG execution identity.

```text
RunId
= controller / queue / background run lifecycle id

ExecutionId
= authoritative durable DAG execution id
```

This separation is important.

A run can be queued before a DAG execution exists.

Once the execution is created, later runtime activity should be correlated with both the `RunId` and the durable `ExecutionId` when possible.

This prevents queue/controller lifecycle events from being confused with authoritative execution state.

---

## Layer 1: Execution-Correlated Decision Ledger

The decision ledger records structured runtime decisions and lifecycle transitions.

It is the audit-oriented layer of observability.

It helps answer:

- when was an execution created?
- when was a run queued, started, completed, failed, or cancelled?
- which worker claimed a step?
- which claim token owned the step?
- why was a claim denied?
- which concurrency lease was acquired or released?
- was retry evaluated, scheduled, denied, or exhausted?
- was recovery detected and applied?
- which policy allowed, denied, or failed?
- when did pause, resume, cancel, or human input happen?
- was a terminal snapshot persisted?
- did storage persistence fail?
- which finalization worker won or lost the race?

Current ledger coverage includes:

- execution lifecycle
- run lifecycle
- queue control
- distributed claim lifecycle
- step execution lifecycle
- retry decisions
- recovery decisions
- policy decisions
- concurrency and throttling decisions
- execution control state
- human-in-the-loop state
- retention and compaction auditability
- payload lifecycle foundations
- snapshot persistence
- storage persistence failure
- finalization lifecycle and race outcomes

See:

- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)

---

## Control-Plane Causal Chain Ledger

The ledger now also records control-plane infrastructure decisions that are not only step-execution events.

This is important for process-host recovery because the proof must explain not only that a DAG completed, but how the control plane reacted when runtime capacity disappeared.

Validated control-plane ledger domains include:

- scale-out request persistence;
- scale-out watcher observation;
- provider selection;
- runtime host manager invocation;
- process runtime host creation;
- runtime registration and capacity visibility;
- registry / capacity lookup;
- execution recovery reconciliation;
- recovered work redispatch.

The control-plane causal chain is separate from the execution ledger. The execution ledger explains what happened to an execution. The control-plane causal chain explains why the infrastructure selected, created, replaced, or refused runtime capacity.

See:

- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)

---

## Layer 2: Tracing and Timeline Diagnostics

Tracing records runtime operation flow.

It is the timeline-oriented layer of observability.

It helps answer:

- what happened first?
- which runtime operation succeeded or failed?
- how did execution progress over time?
- which worker claimed which step?
- when was concurrency admission evaluated?
- when was a claim acquired?
- when did retention run?
- when did recovery scan?
- when did a step execute?
- what tags and correlation fields were attached?

Current tracing foundations include:

- runtime tracing facade
- in-memory trace recorder
- in-memory trace timeline
- trace correlation context
- trace store abstraction
- MongoDB-backed trace persistence
- memory trace store
- no-op trace store
- composite trace store
- store-only trace recorder
- `Disabled`, `Memory`, `Mongo`, and `MemoryAndMongo` trace modes
- distributed chaos trace diagnostics
- grouped trace output by category and operation name

Important trace categories include:

- `execution`
- `step`
- `dag-store`
- `retention`
- `resolver`
- `payload`
- `runtime`

Example trace names include:

```text
dag-store / TryClaimStep.succeeded
dag-store / TryAcquireConcurrencyLease.succeeded
dag-store / RecoverTimedOutSteps.succeeded
step / execute.succeeded
retention / retention.succeeded
execution / execution.succeeded
resolver / resolve.succeeded
```

See:

- [Observability, Metrics, and Tracing](observability-tracing.md)

---

## Layer 3: Runtime Metrics

Metrics provide aggregate runtime signals.

They are the measurement-oriented layer of observability.

They help answer:

- how many steps were claimed?
- how many claim misses happened?
- how many workers participated?
- how many worker cycles were recorded?
- how many retries were scheduled?
- how many steps were recovered?
- how many finalization attempts occurred?
- how many resolver misses occurred?
- how many payloads were stored or loaded?
- how many bytes were compacted?
- how many steps were evicted?
- how many retention plans were created?
- how often did policy evaluation fail?
- which terminal statuses were observed?

Current metric domains include:

- execution metrics
- runtime instance worker metrics
- retention metrics
- retention trigger metrics
- retention decision metrics
- retention plan metrics
- retention execution metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics

Metric persistence supports:

```text
Disabled
Memory
Mongo
MemoryAndMongo
```

See:

- [Runtime Metrics](runtime-metrics.md)

---

## Layer 4: Runtime Recovery Forensics

Runtime recovery forensics records the causal recovery timeline for each work item assigned to an unsafe runtime instance.

This layer is intentionally different from logs, metrics, traces, and execution ledger entries. It answers a narrower audit question:

```text
A runtime process died.
Which assigned work items were impacted?
How was each one recovered?
Which tenant did each recovered item belong to?
Which tenants were not impacted?
```

The runtime currently validates two recovery timeline shapes.

In-flight DAG execution recovery uses an existing durable `ExecutionId` and records a resume timeline:

```text
execution.recovery.candidate.detected
→ shared.run.requeued.for.resume
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
→ dag.resume.started
→ dag.resume.completed
→ execution.recovery.completed
```

Local-queued recovery uses the durable `SharedRunId` because no `ExecutionId` exists yet:

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

The safe-tenant invariant is part of the forensics model. A tenant whose runtime was not killed must have zero recovery forensics.

See:

- [Runtime Recovery Forensics](runtime-recovery-forensics.md)

---

## Layer 5: Logging

Logging remains the human-readable operational layer.

Logs are useful for:

- local development
- console diagnostics
- runtime demo output
- explaining operator-facing behavior
- quick debugging
- exception visibility

Logs should include stable identifiers such as:

- `ExecutionId`
- `RunId`
- `PipelineName`
- `PipelineKey`
- `StepId`
- `StepKey`
- `WorkerId`
- `RuntimeInstanceId`
- `ClaimToken` where applicable

Logs are useful, but audit-grade runtime decisions should be captured by the decision ledger, not only by log lines.

---

## Storage Modes

Metrics and tracing now support configurable persistence modes.

| Mode | Meaning |
|---|---|
| `Disabled` | Persistence is disabled. |
| `Memory` | Records are kept in memory for local diagnostics and tests. |
| `Mongo` | Records are persisted to MongoDB. |
| `MemoryAndMongo` | Records are available in memory and persisted to MongoDB. |

This allows the runtime to support both local diagnostics and durable inspection.

Typical usage:

| Scenario | Suggested Mode |
|---|---|
| Unit tests | `Memory` |
| Local diagnostics | `Memory` or `MemoryAndMongo` |
| Durable local integration tests | `MemoryAndMongo` |
| Production-like durable diagnostics | `Mongo` or exporter-backed mode in the future |
| Minimal runtime mode | `Disabled` |

---

## MemoryAndMongo Mode

`MemoryAndMongo` mode is important because it validates two different requirements at the same time:

```text
Memory
= immediate process-local diagnostics

Mongo
= durable post-execution inspection
```

For tracing, this means the runtime can inspect a live timeline while also persisting trace records.

For metrics, this means the runtime can keep fast in-memory counters while also preparing durable metric diagnostics.

This mode is especially useful for distributed chaos tests and future dashboard foundations.

---

## Recovery Observability Proof Model

Runtime process crash recovery is not considered proven only because recovered runs eventually complete.

A recovered execution must remain observable after convergence through the same durable surfaces as normal execution:

- execution ledger evidence;
- execution trace evidence;
- completion evidence;
- step completion evidence;
- replay report;
- replay ledger;
- replay trace;
- strict replay validation;
- recovery forensics timeline where the work was actually impacted.

The production crash recovery scenarios validate this across impacted tenants and a safe tenant.

Important invariants include:

```text
ReplayValidatedExecutions = 9/9
LedgerEvidence = 9/9
TraceEvidence = 9/9
CompletionEvidence = 9/9
StepCompletionEvidence = 9/9
SafeTenantRecoveredWork = 0
SafeTenantRecoveryForensics = 0
CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryLeakDetected = false
```

This means observability is part of the recovery contract, not an optional debug layer.

See:

- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)

---

## Tenant-Scoped Observability Isolation

Tenant isolation applies to observability queries as well as dispatch and runtime capacity.

A tenant-scoped query must not be able to read another tenant's ledger, trace, replay, or recovery forensics records.

The multi-tenant process crash recovery scenario validates this by killing real runtime processes for tenant A and tenant B while tenant C remains safe. After recovery, MCP observability queries prove:

```text
TenantBEntriesVisibleFromTenantA = 0
TenantAEntriesVisibleFromTenantB = 0
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
CrossTenantLedgerLeakDetected = false
```

This protects the audit surface from becoming a cross-tenant side channel.

---

## Decision Ledger vs Traces vs Metrics

The three focused documents should be read together.

| Question | Best Layer |
|---|---|
| Which decision was made? | Decision ledger |
| Why was a claim denied? | Decision ledger + trace tags |
| Which worker claimed a step? | Ledger + trace |
| How many retries happened? | Metrics + ledger |
| What happened over time? | Tracing |
| How many workers participated? | Metrics |
| Did Mongo receive trace records? | Tracing store diagnostics |
| Did retention evict hot state? | Ledger + metrics + traces |
| Can replay tooling reconstruct behavior? | Ledger + traces + snapshots |
| Which provider/model was throttled? | Traces + ledger + future metrics |
| Which runtime process failed? | Forensics + control-plane ledger + traces |
| Which work items recovered? | Forensics + ledger + shared run store evidence |
| Did replay remain valid after recovery? | Replay report + replay ledger + replay trace |
| Did a safe tenant remain untouched? | Tenant-scoped ledger + forensics + replay proof |

No single layer is enough.

The strength of the runtime is that these layers can share the same correlation model.

---

## Current Validated Behavior

Current tests and diagnostics validate:

- execution-correlated decision ledger events
- run lifecycle ledger events
- queue pause/resume ledger events
- execution control ledger events
- human input ledger events
- claim ledger events
- step ledger events
- retry ledger events
- recovery ledger events
- policy ledger events
- concurrency ledger events
- snapshot ledger events
- finalization ledger events
- in-memory runtime metrics
- runtime instance worker metrics
- retention metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics
- MemoryAndMongo metric configuration
- in-memory trace timeline
- Mongo-backed trace persistence
- MemoryAndMongo trace configuration
- distributed chaos trace output
- distributed chaos metrics output
- trace lookup by execution id and run id
- correlation projection into timeline events
- trace grouping by category and operation
- MCP replay report / ledger / trace queries across process boundaries
- runtime crash recovery forensics for in-flight executions
- runtime crash recovery forensics for local-queued shared runs
- control-plane causal chain ledger for scale-out / provider / host-manager / recovery flow
- tenant-scoped ledger isolation with no cross-tenant visibility
- safe tenant non-impact proof with zero recovery forensics
- replay / ledger / trace validation after real process-host crash recovery

---

## Current Status

| Capability | Status |
|---|---|
| Runtime observability facade | Implemented |
| Runtime logging | Implemented |
| Runtime metrics facade | Implemented |
| Runtime tracing facade | Implemented |
| Execution-correlated decision ledger | Implemented / validated |
| Shared runtime correlation context | Implemented foundation |
| In-memory metrics | Implemented |
| Mongo metric mode | Foundation implemented |
| Metrics MemoryAndMongo mode | Foundation implemented |
| In-memory trace timeline | Implemented |
| Mongo trace persistence | Implemented |
| Trace MemoryAndMongo mode | Implemented |
| Distributed chaos observability diagnostics | Implemented |
| Replay-specific observability | Implemented / validated for MCP replay report, replay ledger, and replay trace scenarios |
| Runtime recovery forensics | Implemented / validated |
| Control-plane causal chain ledger | Implemented / validated |
| Tenant-scoped observability isolation | Implemented / validated |
| Recovery replay / ledger / trace proof | Implemented / validated |
| Policy-specific tracing | Planned |
| OpenTelemetry exporters | Planned |
| Prometheus/Grafana integration | Planned |
| Observability dashboard | Planned |
| Cost governance dashboard | Planned |

---

## Documentation Map

Read these documents depending on the question.

| Document | Use it for |
|---|---|
| [Execution-Correlated Decision Ledger](execution-correlated-ledger.md) | Runtime audit events, ledger categories, event types, outcomes, reasons, metadata, and replay audit foundations. |
| [Observability, Metrics, and Tracing](observability-tracing.md) | Trace records, trace timeline, trace storage modes, Mongo trace persistence, correlation, and tracing TODOs. |
| [Runtime Metrics](runtime-metrics.md) | Metric domains, metric storage modes, worker metrics, retention/storage/resolver/hot-state/policy metrics, and metric TODOs. |
| [Runtime Recovery Forensics](runtime-recovery-forensics.md) | Per-work-item runtime recovery timelines, failure incident ids, in-flight resume proof, local-queued redispatch proof, and safe-tenant non-impact evidence. |
| [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md) | Infrastructure-level ledger events for scale-out, provider selection, host-manager creation, capacity visibility, and recovery reconciliation. |
| [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md) | How replay, ledger, trace, completion, and forensics evidence are validated together after recovery. |
| [runtime-internals.md](../runtime-internals.md) | Complete original technical reference. |

---

## TODO / Improvements Summary

The focused documents contain detailed TODO sections.

High-level observability follow-up items include:

### 1. Policy-Specific Tracing

Policy resolution should be separated from physical step execution.

Planned:

- `AiPolicyTraceContext`
- `TracePolicyAsync`
- policy trace categories such as:

```text
policy / retry.definition.succeeded
policy / concurrency.policy.succeeded
policy / retention.policy.succeeded
```

### 2. WorkerId vs RuntimeInstanceId Normalization

The runtime should consistently distinguish:

```text
RuntimeInstanceId
= process / host / pod identity

WorkerId
= logical runtime worker identity
```

### 3. PipelineKey Propagation

`PipelineKey` should be propagated consistently across:

- controller enqueue
- queue lifecycle
- execution creation
- worker execution
- step tracing
- storage tracing
- metrics
- ledger entries

### 4. LeaseId vs ClaimToken Separation

Concurrency lease id should be represented separately from DAG claim token.

Planned:

- add `LeaseId` to trace correlation context
- reserve `ClaimToken` for DAG step ownership
- reserve `LeaseId` for concurrency capacity ownership

### 5. Trace Enrichment Refactor

Trace enrichment should be centralized.

Planned precedence:

1. explicit trace context
2. ambient runtime correlation
3. operation tags
4. fallback values

### 6. Stronger Metric and Trace Assertions

Current distributed chaos tests are useful diagnostics.

Future tests should verify stricter values per category:

- step traces have step id and step key
- claimed step traces have claim token
- concurrency traces have lease id
- worker id is logical worker identity
- memory and Mongo trace stores contain equivalent key categories
- metric records are persisted and queryable by execution id/run id
- no policy resolution is emitted as physical step execution

### 7. Exporters and Dashboards

Planned external observability integrations:

- OpenTelemetry traces
- OpenTelemetry metrics
- Prometheus endpoint
- Grafana dashboard
- Jaeger-compatible trace view
- execution timeline UI
- provider/model cost dashboard

### 8. Replay-Aware Observability

MCP replay observability is already validated for replay report, replay ledger, and replay trace retrieval. Future replay-aware UI and dashboard work should build on that surface to show:

- original execution timeline
- replay execution timeline
- divergence points
- fingerprint comparison
- resolver reconstruction
- missing payload references
- replay validation metrics
- recovery forensics timeline links
- control-plane causal chain links

---

## Design Principles

The observability layer follows these principles:

1. Runtime execution safety comes first.
2. Observability should not break workflow execution in best-effort mode.
3. Logs, metrics, traces, and ledger entries should share correlation identifiers.
4. `RunId` and `ExecutionId` must remain semantically separate.
5. Runtime instance identity and worker identity must remain distinguishable.
6. Decision facts belong in the ledger, not only in logs.
7. Runtime flow belongs in traces.
8. Aggregated behavior belongs in metrics.
9. Sensitive payloads should not be logged or traced blindly.
10. Replay, audit, and dashboard tooling should reuse the same correlation model.
11. Recovery observability must prove both impacted work recovery and safe tenant non-impact.
12. Forensics should record per-work-item recovery timelines, not only aggregate incident logs.

---

## Summary

The Deterministic AI Runtime observability foundation now includes:

- execution-correlated decision ledger
- runtime metrics facade
- runtime tracing facade
- in-memory trace timeline
- MongoDB trace persistence
- metric storage mode foundations
- trace storage modes
- MemoryAndMongo support
- distributed chaos diagnostics
- shared correlation model across runtime observability layers
- MCP replay / ledger / trace validation across process boundaries
- runtime recovery forensics for real process-host crashes
- control-plane causal chain ledger for scale-out and recovery
- tenant-scoped observability isolation
- recovery proof across replay, ledger, trace, and forensics

This makes the runtime observable not only as a workflow executor, but as a distributed AI execution system.

---

## Related Documents

- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Replay and Audit](replay-and-audit.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a high-level index and summary for the observability documentation split.

