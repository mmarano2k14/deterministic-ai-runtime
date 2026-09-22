# Observability, Metrics, and Tracing

Status: Implemented foundation / validated for runtime metrics, trace storage, Mongo-backed trace persistence, process-boundary observability, MCP replay / ledger / trace inspection, runtime crash recovery evidence, and tenant-scoped recovery observability. Production-grade dashboarding, OpenTelemetry exporters, policy-specific tracing, and stricter trace-value validation remain planned improvements.

This document describes the observability, metrics, and tracing foundations of the Deterministic AI Runtime.

The runtime observability layer records structured runtime signals correlated by `CorrelationId`, `SharedRunId`, `RunId`, `LocalRunId`, `ExecutionId`, tenant identity, pipeline identity, step identity, worker/runtime instance, claim token, provider/model/operation metadata, storage operations, replay diagnostics, and runtime recovery evidence.


---

## Purpose

Production AI execution must be visible, diagnosable, and explainable.

A distributed AI runtime must answer questions such as:

- which execution is currently running?
- which controller run produced which DAG execution?
- which worker claimed a step?
- which runtime instance executed work?
- which step failed, retried, recovered, or completed?
- which provider/model/operation caused throttling?
- which storage operation failed or slowed down?
- which payload was externalized or rehydrated?
- when was hot state compacted or evicted?
- why did retention trigger?
- how many worker cycles were needed?
- which runtime instance observed terminal completion?
- where did a trace record get stored?
- can memory diagnostics and durable MongoDB diagnostics be enabled at the same time?
- can replay, ledger, and trace evidence be queried after execution or recovery?
- can runtime crash recovery be explained per work item?
- can a safe tenant prove that it received no recovery evidence after another tenant crashed?

The observability layer exists to make runtime behavior inspectable without mixing operational concerns into the execution logic.

It is not a replacement for the decision ledger.

It complements the ledger by providing metrics, timelines, traces, counters, and diagnostic records.

---

## Current Scope

The current observability implementation provides foundations across four layers:

- runtime observability facade
- runtime metrics
- runtime tracing
- runtime logging
- execution-correlated decision ledger integration

Implemented observability areas include:

- execution lifecycle metrics
- worker metrics
- runtime instance worker metrics
- retention metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics
- trace recording
- in-memory trace timeline
- MongoDB-backed trace persistence
- memory and MongoDB storage modes
- correlated trace records
- correlated timeline events
- trace output diagnostics for distributed chaos tests
- metrics diagnostics for distributed worker execution
- shared runtime correlation context
- correlation-aware observability composition
- MCP replay report / replay ledger / replay trace inspection
- runtime crash recovery trace and ledger evidence
- runtime recovery forensics integration points
- control-plane causal chain observability
- tenant-scoped ledger / trace / replay proof after recovery
- safe tenant non-impact observability proof

The current implementation is intentionally foundation-first.

OpenTelemetry-style exporters, production dashboards, UI timelines, and cost dashboards remain planned work. MCP replay / ledger / trace inspection and recovery proof validation are implemented and validated in production recovery scenarios; richer public replay/audit APIs and UI experiences remain planned work.

---

## Why Observability Matters

Distributed AI workflows are not simple synchronous method calls.

A production AI workflow may involve:

- controller queues
- run lifecycle state
- durable DAG execution state
- multiple workers
- multiple runtime instances
- Redis Lua atomic state transitions
- distributed concurrency leases
- retry windows
- stale worker recovery
- runtime instance crash recovery
- local queued work redispatch after runtime loss
- in-flight DAG resume after process death
- payload compaction
- payload rehydration
- retention and hot-state eviction
- snapshot persistence
- external provider throttling
- human-in-the-loop blocking
- finalization races

Without runtime observability, failures become difficult to explain:

```text
The workflow did not finish.
```

is not enough.

The runtime must make it possible to understand:

```text
Which worker claimed the step?
Which claim token owned it?
Which runtime instance executed it?
Was the step throttled?
Was a retry scheduled?
Was the payload externalized?
Did retention evict hot state?
Did the resolver reconstruct the step correctly?
Which finalization worker won the race?
Which runtime process died?
Which work was recovered from that runtime?
Which work was not touched because its tenant was safe?
```

Observability turns the runtime from a black box into a diagnosable execution system.

---

## Observability vs Logs vs Metrics vs Traces vs Ledger

The runtime uses multiple complementary visibility layers.

| Layer | Purpose |
|---|---|
| Logs | Human-readable operational messages. |
| Metrics | Aggregated counters, durations, totals, grouped counts, and runtime health signals. |
| Traces | Timeline and flow diagnostics for runtime operations. |
| Trace records | Structured trace data that can be stored in memory and/or MongoDB. |
| Decision ledger | Structured runtime decisions and lifecycle facts correlated to execution state. |
| Recovery forensics | Durable per-work-item recovery timelines linked to runtime failure incidents. |

Each layer answers a different question.

A log may say:

```text
[AI DAG] Step claimed.
```

A metric may show:

```text
StepClaimedCount = 100
WorkerCyclesByRuntimeInstance[worker-1] = 20
```

A trace may show:

```text
dag-store / TryClaimStep.succeeded / StepId=chaos-step-001 / ClaimToken=...
```

A decision ledger entry may record:

```text
Category=Claim
EventType=claim.acquired
Outcome=Allowed
ExecutionId=...
StepKey=...
WorkerId=...
ClaimToken=...
Reason=Step claim acquired.
```

Together, these layers provide both operational visibility and future audit/replay foundations.

---

## Correlation Model

Observability is correlated through shared runtime correlation objects.

The runtime-level correlation context is represented by `AiRuntimeExecutionCorrelationContext`.

It can carry:

- `CorrelationId`
- `SharedRunId`
- `RunId`
- `LocalRunId`
- `ExecutionId`
- `TenantId`
- `TenantGroupId`
- `PipelineName`
- `PipelineVersion`
- `PipelineKey`
- `RuntimeInstanceId`
- `WorkerId`

The trace-level correlation context is represented by `AiRuntimeTraceCorrelationContext`.

It can additionally carry:

- `StepId`
- `StepKey`
- `ClaimToken`
- `PolicyKey`
- `Provider`
- `Model`
- `Operation`
- `InputPayloadRef`
- `OutputPayloadRef`
- `HumanInputRef`
- `PromptRef`
- `TraceId`
- `TraceScopeId`
- `ForensicsId`
- `RuntimeFailureIncidentId`
- `RecoveryMode`
- `ParentTraceScopeId`
- `Source`

The goal is to ensure that logs, metrics, traces, ledger events, replay diagnostics, and runtime recovery forensics can all be connected to the same execution story.

---

## RunId and ExecutionId Separation

The runtime separates controller lifecycle identity from durable DAG execution identity.

```text
RunId
= controller / queue / background run lifecycle id

ExecutionId
= authoritative durable DAG execution id
```

Observability must respect this separation.

Some events exist before a durable `ExecutionId` is known.

Examples:

```text
run.queued
run.dequeued
```

may be correlated by `RunId` and `CorrelationId`.

Once execution creation succeeds, later traces and metrics should include the durable `ExecutionId`.

This separation prevents queue/controller activity from being confused with authoritative DAG execution state.


### SharedRunId, LocalRunId, and Recovery Correlation

Runtime crash recovery adds another identity layer that observability must preserve.

```text
SharedRunId
= durable shared submission identity used by the control plane and shared queue

LocalRunId
= runtime-local execution attempt identity on a specific runtime instance

ExecutionId
= durable DAG execution identity, once the DAG exists
```

For in-flight recovery, `ExecutionId` must remain stable across the failed runtime and the replacement runtime. Observability must therefore show the same durable execution before and after recovery.

For local-queued recovery, an `ExecutionId` may not exist yet. Observability must correlate the recovery through `SharedRunId` and failed `LocalRunId` until a replacement runtime creates the durable `ExecutionId`.

This distinction is required to prove that:

- in-flight DAG work resumed the same execution;
- local queued work was redispatched without duplicate submission;
- recovery did not confuse queue identity with durable execution identity;
- tenant-scoped observability can distinguish impacted and safe tenants.

---

## Observability Facade

The runtime composes observability through a central facade.

The runtime observability facade exposes:

- metrics
- tracing
- logging
- decision ledger recorder
- runtime correlation accessor

Typical responsibility:

| Component | Responsibility |
|---|---|
| `IAiRuntimeObservability` | Central facade for runtime observability services. |
| `IAiRuntimeMetrics` | Runtime metrics facade. |
| `IAiRuntimeTracer` | Runtime tracing facade. |
| `IAiRuntimeLogger` | Runtime logging facade. |
| `IAiDecisionLedgerRecorder` | Runtime decision ledger recorder. |
| `IAiRuntimeCorrelationAccessor` | Ambient runtime correlation accessor. |

This keeps runtime code from directly depending on many isolated observability services.

---

## Runtime Crash Recovery Observability

Runtime crash recovery is observable across multiple layers:

```text
runtime health signal
    ↓
unsafe runtime capacity
    ↓
execution recovery reconciliation
    ↓
assigned work enumeration
    ↓
in-flight resume or local-queued redispatch
    ↓
replacement runtime selection
    ↓
replay / ledger / trace / forensics proof
```

The HTTP provider may report transport failures such as `http-circuit-open` or `http-provider-unavailable`, but it does not own recovery. Runtime health reconciliation and execution recovery reconciliation remain separate responsibilities.

The observability boundary is:

```text
HTTP provider
= transport signal / dispatch failure reason

RuntimeInstanceHealthReconciler
= unsafe capacity and routing safety

ExecutionRecoveryReconciler
= assigned work recovery

Runtime recovery forensics
= per-work-item recovery timeline

Ledger / trace / replay
= post-recovery proof surface
```

This separation matters because a runtime may become unsafe for routing before all assigned work has been recovered. A replacement runtime becoming visible is also not the same as recovery completion.

Recovery is observable only when the assigned work has been reconciled and proof has been written.

## Recovery Forensics and Trace Relationship

Runtime recovery forensics are not normal trace records.

They are durable per-work-item recovery records linked to a `RuntimeFailureIncidentId`.

For an in-flight execution, the recovery timeline includes DAG resume evidence:

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

For local queued work, the timeline is shorter because no DAG execution exists yet:

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

Trace records show runtime flow.

Ledger entries show structured decisions.

Forensics records show the recovery story for one assigned work item.

Replay validates that the recovered execution remains audit-ready after convergence.

## Tenant-Scoped Recovery Observability

Recovery observability must preserve tenant isolation.

A tenant-scoped query must not expose another tenant's ledger, trace, replay, or forensics evidence.

The validated multi-tenant crash scenario proves this shape:

```text
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
```

Expected observability result:

```text
Tenant A = recovery evidence exists
Tenant B = recovery evidence exists
Tenant C = no recovery evidence
```

The safe tenant still produces normal execution observability: ledger, trace, replay, completion evidence, and step completion evidence.

But it must not receive recovery evidence.

Important invariants:

```text
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
```

This is the difference between proving that recovery worked and proving that recovery stayed tenant-scoped.

## Control-Plane Causal Chain Observability

Recovery is not only a runtime-side event.

The control plane also records causal evidence for infrastructure decisions.

Validated causal domains include:

```text
scale-out request persisted
scale-out watcher observed request
provider selected
runtime host manager created host
process runtime host started
runtime capacity became visible
runtime registry/capacity lookup succeeded
execution recovery reconciled assigned work
recovered work redispatched
```

These records explain how replacement capacity appeared and how recovered work moved back into the normal dispatch path.

The causal chain must remain distinct from execution ledger entries. Execution ledger entries explain what happened inside an execution. Control-plane causal chain entries explain what the control plane decided and did around runtime capacity, scale-out, and recovery.

## Metrics Scope

Runtime metrics are grouped by domain.

Current metric domains include:

- execution metrics
- worker metrics
- retention metrics
- storage metrics
- hot-state metrics
- resolver metrics
- policy metrics

The metrics layer is intentionally lightweight and test-friendly.

It is useful for:

- runtime health checks
- integration test validation
- worker participation validation
- execution convergence diagnostics
- storage hit/miss visibility
- retention behavior validation
- resolver miss/failure visibility
- policy success/failure visibility

---

## Execution Metrics

Execution metrics record runtime execution behavior.

Examples include:

- step retry counts
- recovered step counts
- step claim counts
- claim miss counts
- finalization attempt counts
- finalization success counts
- execution lifecycle counters

Execution metrics are important for understanding whether the runtime is making forward progress.

They also help validate distributed behavior in integration tests.

---

## Worker Metrics

Worker metrics record runtime worker behavior.

Examples include:

- cycles by runtime instance
- terminal observations by status
- worker participation
- completed terminal observations

These metrics are especially useful for distributed execution tests.

A distributed chaos execution can prove that multiple workers participated and that terminal completion was eventually observed.

Example diagnostic output:

```text
WORKER CYCLES
------------------------------------------------------------
WorkerId=MSI:...:worker:1:... | Cycles=19
WorkerId=MSI:...:worker:2:... | Cycles=20
WorkerId=MSI:...:worker:3:... | Cycles=19

TERMINAL STATUS METRICS
------------------------------------------------------------
Status=Completed | Count=11
```

Multiple terminal observations are acceptable in a distributed runtime because several workers may observe terminal completion.

The important invariant is:

```text
CompletedCount >= 1
No unexpected Failed terminal status
Expected workers participated
```

---

## Retention Metrics

Retention metrics record hot-state lifecycle behavior.

Retention metric domains include:

- trigger metrics
- decision metrics
- plan metrics
- execution metrics

They help answer:

- was retention triggered?
- was retention skipped?
- was compaction required?
- was eviction required?
- how many steps were compacted?
- how many steps were evicted?
- how many bytes were saved?
- did retention fail?

Retention metrics are essential because retention affects both memory safety and replay readiness.

---

## Storage Metrics

Storage metrics record payload and storage behavior.

They can include:

- payload stored count
- payload loaded count
- payload store hit count
- payload store miss count
- payload store failure count
- stored byte totals
- operations grouped by storage kind
- failures grouped by exception type

Storage metrics are useful for validating MongoDB payload persistence, Redis cache behavior, and payload rehydration flows.

---

## Resolver Metrics

Resolver metrics record context resolution behavior.

They can include:

- resolve started count
- resolve success count
- resolve miss count
- resolve failure count
- operations grouped by path
- failures grouped by exception type

Resolver metrics are important because DAG steps often depend on previous step outputs, state paths, externalized payloads, or replay-safe state reconstruction.

Resolver misses and failures are often the first sign of broken context reconstruction.

---

## Hot-State Metrics

Hot-state metrics record Redis execution state size and compaction effects.

They can include:

- state step added count
- state step removed count
- state compacted count
- last compaction before/after step count
- total steps removed by compaction
- observed step count
- estimated byte size

Hot-state metrics help prove that long-running workflows do not grow unbounded in Redis.

---

## Policy Metrics

Policy metrics record policy execution and decision behavior.

They are useful for:

- retry policy evaluation
- retention policy evaluation
- concurrency policy evaluation
- policy failures
- policy duration diagnostics
- policy result grouping

Policy metrics complement the decision ledger by providing counters and health signals around policy execution.

---

## Metric Storage Modes

Runtime metric persistence supports configurable storage modes.

Supported modes include:

```text
Disabled
Memory
Mongo
MemoryAndMongo
```

### Disabled

Metric persistence is disabled.

This mode is useful when metrics are intentionally turned off.

### Memory

Metrics are stored in memory.

This is the default development and test-friendly mode.

### Mongo

Metric records are persisted to MongoDB.

This mode is useful for durable metric diagnostics.

### MemoryAndMongo

Metrics are available in memory and persisted to MongoDB.

This mode is useful for tests, local diagnostics, and future dashboard foundations.

---

## Metric Store Options

Metric storage is configured through `AiRuntimeMetricStoreOptions`.

The options include:

- mode
- MongoDB connection string
- MongoDB database name
- MongoDB collection name

When explicit metric MongoDB options are not provided, the runtime can reuse the existing runtime MongoDB configuration.

This avoids requiring duplicate connection configuration for metrics, traces, payloads, snapshots, and ledger storage.

---

## Tracing Scope

Runtime tracing records structured execution flow diagnostics.

Current trace areas include:

- execution traces
- step traces
- storage traces
- retention traces
- resolver traces
- runtime timeline events
- correlated trace records
- MongoDB-backed trace records

Tracing is intended to show runtime flow.

It is more detailed than metrics and less decision-oriented than the ledger.

---

## Trace Records

Trace records represent completed trace scopes.

A trace record can contain:

- trace id
- operation
- execution id
- step id
- started timestamp
- completed timestamp
- duration
- success/failure state
- error type
- error message
- tags
- correlation context

Trace records can be kept in memory, persisted to MongoDB, or both.

---

## Trace Timeline

The in-memory trace recorder projects trace records into a timeline.

A trace timeline event can include:

- execution id
- timestamp
- category
- name
- step id
- tags
- correlation context

Examples:

```text
dag-store / TryClaimStep.succeeded
dag-store / TryAcquireConcurrencyLease.succeeded
step / execute.succeeded
retention / retention.succeeded
execution / execution.succeeded
resolver / resolve.succeeded
```

The timeline is useful for local diagnostics, integration tests, and future UI work.

---

## Trace Categories

The runtime currently uses normalized trace categories.

Common categories include:

- `execution`
- `step`
- `dag-store`
- `retention`
- `resolver`
- `payload`
- `runtime`

The category identifies the runtime component.

The event name identifies the normalized operation result.

Examples:

```text
dag-store / TryClaimStep.succeeded
dag-store / RecoverTimedOutSteps.succeeded
step / execute.succeeded
retention / retention.succeeded
execution / execution.succeeded
```

---

## Trace Storage Modes

Runtime trace persistence supports configurable storage modes.

Supported modes include:

```text
Disabled
Memory
Mongo
MemoryAndMongo
```

### Disabled

Trace persistence is disabled.

### Memory

Trace records are stored in memory.

This is suitable for tests, diagnostics, local runtime inspection, and early UI work.

### Mongo

Trace records are persisted to MongoDB.

This is suitable for durable trace diagnostics.

### MemoryAndMongo

Trace records are stored in memory and persisted to MongoDB.

This is useful when both live diagnostics and durable trace persistence are needed.

---

## Trace Store Abstractions

The tracing layer includes a trace store abstraction.

Implemented trace stores include:

- `IAiRuntimeTraceStore`
- `NoOpAiRuntimeTraceStore`
- `InMemoryAiRuntimeTraceStore`
- `MongoAiRuntimeTraceStore`
- `CompositeAiRuntimeTraceStore`

The composite store allows `MemoryAndMongo` mode.

In that mode, the memory store provides live/local diagnostics, while MongoDB provides durable trace persistence.

---

## Trace Recorders

The tracing layer includes trace recorders.

Current recorders include:

- `NoOpAiTraceRecorder`
- `InMemoryAiTraceRecorder`
- `StoreOnlyAiTraceRecorder`

`InMemoryAiTraceRecorder` records traces in memory and can optionally persist completed records to a configured trace store.

`StoreOnlyAiTraceRecorder` is used when in-memory recording is disabled but durable trace persistence is enabled, for example Mongo-only tracing.

Trace store writes are observational and should not break runtime execution in best-effort configurations.

---

## MongoDB Trace Persistence

MongoDB-backed trace persistence stores completed trace records.

Mongo trace persistence supports lookup by:

- execution id
- run id
- correlation id
- operation

Mongo indexes are created for trace lookup and diagnostic usage.

Mongo index creation uses runtime resilience helpers so transient local Docker or socket errors do not unnecessarily break tests.

---

## Correlated Distributed Chaos Trace Output

The distributed chaos tests can produce trace output grouped by category and operation.

Full trace output is available here:

- [Download full ledger logs](../files/tracing-logs-example.txt)

Example output shape:

```text
TRACE SUMMARY BY CATEGORY / NAME
------------------------------------------------------------
dag-store        | RecoverTimedOutSteps.succeeded      | Count=227
dag-store        | TryAcquireConcurrencyLease.succeeded | Count=210
dag-store        | TryClaimStep.succeeded              | Count=211
dag-store        | TryFinalizeExecution.succeeded      | Count=10
execution        | execution.succeeded                 | Count=241
retention        | retention.succeeded                 | Count=112
step             | execute.completed                   | Count=224
step             | execute.failed                      | Count=11
step             | execute.succeeded                   | Count=200

TRACE TIMELINE
------------------------------------------------------------

2026-05-28T05:16:49.0659155Z | dag-store        | TryClaimStep.succeeded              | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId= | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:9:f1d6aa2bf3cf4fc2b58e1507740036bf | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryClaimStep | Tags=[backend=Redis, category=dag-store, claimAcquired=False, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=00f21c5346524ab19ec33d2823bb4b6e, durationMs=0.9973, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryClaimStep.succeeded, operation=TryClaimStep, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=00f21c5346524ab19ec33d2823bb4b6e, traceScopeId=00f21c5346524ab19ec33d2823bb4b6e, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:9:f1d6aa2bf3cf4fc2b58e1507740036bf]
2026-05-28T05:16:49.0720696Z | step             | execute.succeeded                   | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId=chaos-step-001 | StepKey=hello-world | ClaimToken=dbfc8114f1754b4991734c91eef867bc | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=step.execute | Tags=[category=step, claimToken=dbfc8114f1754b4991734c91eef867bc, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=9daf03c774f74bd288390c97b48cfab5, durationMs=7.6755, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, name=execute.succeeded, operation=step.execute, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=step, recoveryCount=0, retryCount=0, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, status=Running, stepId=chaos-step-001, stepKey=hello-world, stepType=HelloWorldStep, succeeded=True, traceId=9daf03c774f74bd288390c97b48cfab5, traceScopeId=9daf03c774f74bd288390c97b48cfab5, traceSource=step, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1]
2026-05-28T05:16:49.0721178Z | dag-store        | TryAcquireConcurrencyLease.succeeded | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId=chaos-step-001 | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:5:d8925268af254c73b790612514ec0b62 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryAcquireConcurrencyLease | Tags=[backend=Redis, category=dag-store, completedAtUtc=5/28/2026 5:16:49 AM, concurrency.allowed=True, concurrency.gate.allowed=True, concurrency.leaseId=7e9a4f971ec7472096aa4814f4e56895:chaos-step-001:MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, concurrency.model=gpt-4.1, concurrency.operation=llm.chat, concurrency.pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957:1.0.0, concurrency.policy.allowed=True, concurrency.provider=openai, concurrency.stepKey=hello-world, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=7d7ed96c474b4c7cac23611d1a34c5d7, durationMs=7.1537, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryAcquireConcurrencyLease.succeeded, operation=TryAcquireConcurrencyLease, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=7d7ed96c474b4c7cac23611d1a34c5d7, traceScopeId=7d7ed96c474b4c7cac23611d1a34c5d7, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:5:d8925268af254c73b790612514ec0b62]
2026-05-28T05:16:49.0721393Z | dag-store        | TryAcquireConcurrencyLease.succeeded | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId=chaos-step-001 | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:3:f2ca4cb923da400eb0c68f6674ede1b3 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryAcquireConcurrencyLease | Tags=[backend=Redis, category=dag-store, completedAtUtc=5/28/2026 5:16:49 AM, concurrency.allowed=True, concurrency.gate.allowed=True, concurrency.leaseId=7e9a4f971ec7472096aa4814f4e56895:chaos-step-001:MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, concurrency.model=gpt-4.1, concurrency.operation=llm.chat, concurrency.pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957:1.0.0, concurrency.policy.allowed=True, concurrency.provider=openai, concurrency.stepKey=hello-world, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=49b47e64e0574be4a8d77fe3656c0974, durationMs=7.2567, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryAcquireConcurrencyLease.succeeded, operation=TryAcquireConcurrencyLease, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=49b47e64e0574be4a8d77fe3656c0974, traceScopeId=49b47e64e0574be4a8d77fe3656c0974, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:3:f2ca4cb923da400eb0c68f6674ede1b3]
2026-05-28T05:16:49.0721819Z | dag-store        | TryAcquireConcurrencyLease.succeeded | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId=chaos-step-001 | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:7:d3d5ad07aaf04085aceeaae048fc0b0d | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryAcquireConcurrencyLease | Tags=[backend=Redis, category=dag-store, completedAtUtc=5/28/2026 5:16:49 AM, concurrency.allowed=True, concurrency.gate.allowed=True, concurrency.leaseId=7e9a4f971ec7472096aa4814f4e56895:chaos-step-001:MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, concurrency.model=gpt-4.1, concurrency.operation=llm.chat, concurrency.pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957:1.0.0, concurrency.policy.allowed=True, concurrency.provider=openai, concurrency.stepKey=hello-world, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=a0042c2a55af4918b39c8119600a06f6, durationMs=6.4698, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryAcquireConcurrencyLease.succeeded, operation=TryAcquireConcurrencyLease, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=a0042c2a55af4918b39c8119600a06f6, traceScopeId=a0042c2a55af4918b39c8119600a06f6, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:7:d3d5ad07aaf04085aceeaae048fc0b0d]
2026-05-28T05:16:49.0723247Z | dag-store        | TryClaimStep.succeeded              | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId= | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:10:653a9ad6890044b4afb7227592433983 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryClaimStep | Tags=[backend=Redis, category=dag-store, claimAcquired=False, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=5bf1e78bbc6142dfa3857ca26b1d314d, durationMs=6.6685, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryClaimStep.succeeded, operation=TryClaimStep, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=5bf1e78bbc6142dfa3857ca26b1d314d, traceScopeId=5bf1e78bbc6142dfa3857ca26b1d314d, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:10:653a9ad6890044b4afb7227592433983]
2026-05-28T05:16:49.0728282Z | dag-store        | TryClaimStep.succeeded              | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId= | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:7:d3d5ad07aaf04085aceeaae048fc0b0d | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryClaimStep | Tags=[backend=Redis, category=dag-store, claimAcquired=False, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=c99f1bf68171404691cfd2acd4418cdd, durationMs=0.626, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryClaimStep.succeeded, operation=TryClaimStep, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=c99f1bf68171404691cfd2acd4418cdd, traceScopeId=c99f1bf68171404691cfd2acd4418cdd, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:7:d3d5ad07aaf04085aceeaae048fc0b0d]
2026-05-28T05:16:49.0728403Z | dag-store        | TryClaimStep.succeeded              | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId= | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:8:1da1c1538f184707b77b23deefe5dda6 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryClaimStep | Tags=[backend=Redis, category=dag-store, claimAcquired=False, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=fb4e181310f545c9bb40b1e858f6fc62, durationMs=7.1633, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryClaimStep.succeeded, operation=TryClaimStep, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=fb4e181310f545c9bb40b1e858f6fc62, traceScopeId=fb4e181310f545c9bb40b1e858f6fc62, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:8:1da1c1538f184707b77b23deefe5dda6]
2026-05-28T05:16:49.0728702Z | dag-store        | TryClaimStep.succeeded              | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId= | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:5:d8925268af254c73b790612514ec0b62 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryClaimStep | Tags=[backend=Redis, category=dag-store, claimAcquired=False, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=90c40ddc17654be19e5e875dfd646e47, durationMs=0.719, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryClaimStep.succeeded, operation=TryClaimStep, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=90c40ddc17654be19e5e875dfd646e47, traceScopeId=90c40ddc17654be19e5e875dfd646e47, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:5:d8925268af254c73b790612514ec0b62]
2026-05-28T05:16:49.0734214Z | dag-store        | TryClaimStep.succeeded              | ExecutionId=7e9a4f971ec7472096aa4814f4e56895 | RunId=a8f02527095948cfb8105229ffe8a9de | CorrelationId=a8f02527095948cfb8105229ffe8a9de | Pipeline=distributed-chaos-100-cc300b6680f54363b63742121881c957 | PipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957 | StepId= | StepKey= | ClaimToken= | Worker=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:3:f2ca4cb923da400eb0c68f6674ede1b3 | RuntimeInstance=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1 | Provider= | Model= | Operation=TryClaimStep | Tags=[backend=Redis, category=dag-store, claimAcquired=False, completedAtUtc=5/28/2026 5:16:49 AM, correlationId=a8f02527095948cfb8105229ffe8a9de, distributedTraceId=8bbf0b48a3d0414888fe3f7cbef117f1, durationMs=0.6425, executionId=7e9a4f971ec7472096aa4814f4e56895, failed=False, hit=, name=TryClaimStep.succeeded, operation=TryClaimStep, pipelineKey=distributed-chaos-100-cc300b6680f54363b63742121881c957, pipelineName=distributed-chaos-100-cc300b6680f54363b63742121881c957, rawOperation=storage, runId=a8f02527095948cfb8105229ffe8a9de, runtimeInstanceId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1, startedAtUtc=5/28/2026 5:16:49 AM, stepId=chaos-step-001, succeeded=True, traceId=8bbf0b48a3d0414888fe3f7cbef117f1, traceScopeId=8bbf0b48a3d0414888fe3f7cbef117f1, traceSource=Redis, workerId=MSI:34196:56e90c5f65a44a1f8329373ab272b8e1:worker:3:f2ca4cb923da400eb0c68f6674ede1b3]
```

Timeline output can include:

```text
Timestamp
Category
Name
ExecutionId
RunId
CorrelationId
Pipeline
PipelineKey
StepId
StepKey
ClaimToken
Worker
RuntimeInstance
Provider
Model
Operation
Tags
```

This makes distributed runtime behavior inspectable during tests.

---

## Distributed Claim Tracing

Distributed claim tracing records claim-related storage operations.

Important claim trace operations include:

```text
TryClaimStep
```

Useful trace fields include:

- execution id
- pipeline key
- step id
- step key
- worker id
- claim acquired flag
- claim token

Claim token visibility is important because it proves which worker owned the step claim.

The current implementation has improved tag coverage for claim tracing.

Further normalization remains planned.

---

## Distributed Concurrency Tracing

Distributed concurrency tracing records concurrency admission and lease behavior.

Important concurrency trace operations include:

```text
TryAcquireConcurrencyLease
```

Useful trace fields include:

- pipeline key
- step key
- worker id
- lease id
- provider
- model
- operation
- allowed/denied decision
- denial reason

Concurrency tracing helps explain provider/model/operation throttling behavior.

It is especially important for distributed throttling and chaos scenarios.

---

## Recovery Tracing

Recovery tracing records stale running-step recovery scans and participates in the wider runtime crash recovery proof surface.

Important recovery trace operations include:

```text
RecoverTimedOutSteps
```

Useful trace fields include:

- execution id
- pipeline key
- worker id
- recovered count
- recovered step names

Recovery tracing helps prove that crashed or stale workers do not permanently block progress.

Runtime instance crash recovery is broader than stale step recovery. A runtime process can die while it owns both in-flight DAG executions and local queued work. In that case, trace evidence must be interpreted together with registry/capacity state, recovery forensics, ledger entries, shared run state, and replay proof.

Important distinction:

```text
Stale running-step recovery
= a worker claim expired inside an execution

Runtime instance crash recovery
= an entire runtime process became unsafe and all assigned work must be reconciled
```

Both are recovery, but they are not the same recovery scope.

---

## Retention Tracing

Retention tracing records retention and hot-state lifecycle behavior.

Useful trace fields include:

- compacted count
- evicted count
- removed steps
- skipped flag
- duration
- success/failure state

Retention tracing helps explain when and why hot state was compacted or evicted.

It complements retention metrics and retention ledger events.

---

## Storage Tracing

Storage tracing records runtime storage operations.

Storage operations may include:

- Redis DAG store operations
- MongoDB payload storage operations
- payload cache operations
- snapshot persistence
- trace persistence
- metric persistence

Storage tracing is useful when diagnosing runtime persistence behavior.

---

## Testing Coverage

Observability, metrics, and tracing are validated through tests covering:

- in-memory metrics
- execution metrics
- worker metrics
- retention metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics
- runtime metrics facade composition
- trace recorder behavior
- in-memory trace timeline
- MongoDB trace persistence
- MemoryAndMongo trace mode
- MemoryAndMongo metrics mode
- distributed chaos trace output
- distributed chaos metrics output
- execution id trace lookup
- run id trace lookup
- trace correlation validation
- runtime worker participation metrics
- MCP replay report / ledger / trace retrieval
- process-boundary observability from child RuntimeInstanceOnly processes
- runtime crash recovery ledger / trace proof
- runtime recovery forensics proof
- safe tenant no-recovery-evidence proof
- control-plane causal chain evidence

Recent diagnostic tests validate that:

- distributed chaos tracing can write to both in-memory timeline and MongoDB
- runtime metrics remain available in `MemoryAndMongo` mode
- trace output can be grouped by category and operation
- memory timeline and Mongo trace store both receive correlated records
- traces can be inspected by execution id and run id

---

## Current Status

| Capability | Status |
|---|---|
| Runtime observability facade | Implemented |
| Runtime logging facade | Implemented |
| Runtime metrics facade | Implemented |
| Execution metrics | Implemented |
| Worker metrics | Implemented |
| Retention metrics | Implemented |
| Storage metrics | Implemented |
| Resolver metrics | Implemented |
| Hot-state metrics | Implemented |
| Policy metrics | Implemented |
| Metric store modes | Implemented |
| Metric Memory mode | Implemented |
| Metric Mongo mode | Foundation implemented |
| Metric MemoryAndMongo mode | Foundation implemented |
| Runtime tracing facade | Implemented |
| In-memory trace recorder | Implemented |
| In-memory trace timeline | Implemented |
| Trace correlation context | Implemented |
| Trace store modes | Implemented |
| Trace Memory mode | Implemented |
| Trace Mongo mode | Implemented |
| Trace MemoryAndMongo mode | Implemented |
| Mongo trace persistence | Implemented |
| Store-only trace recorder | Implemented |
| Distributed chaos trace diagnostics | Implemented |
| Distributed chaos metrics diagnostics | Implemented |
| MCP replay report / ledger / trace inspection | Implemented / validated |
| Process-boundary observability | Implemented / validated |
| Runtime crash recovery observability | Implemented / validated |
| Runtime recovery forensics integration | Implemented / validated |
| Tenant-scoped recovery observability | Implemented / validated |
| Control-plane causal chain observability | Implemented / validated |
| OpenTelemetry exporter | Planned |
| Production dashboard | Planned |
| Trace UI | Planned |
| Cost dashboard | Planned |
| Replay/audit API integration | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| `AiRuntimeObservability` | Composes metrics, tracing, logging, ledger recorder, and correlation access. |
| `IAiRuntimeMetrics` | Exposes runtime metrics domains. |
| `AiExecutionMetrics` | Records execution, retry, recovery, claim, and finalization metrics. |
| `AiRuntimeInstanceWorkerMetrics` | Records worker cycles and terminal observations. |
| `AiRetentionMetrics` | Aggregates trigger, decision, plan, and execution retention metrics. |
| `AiStorageMetrics` | Records payload and storage metrics. |
| `AiResolverMetrics` | Records context resolver metrics. |
| `AiHotStateMetrics` | Records hot-state size and compaction metrics. |
| `AiPolicyMetrics` | Records policy execution and decisions. |
| `IAiRuntimeTracer` | Starts trace scopes and records trace lifecycle. |
| `InMemoryAiRuntimeTracer` | Captures trace records and runtime correlation. |
| `IAiTraceRecorder` | Records completed trace records. |
| `InMemoryAiTraceRecorder` | Stores trace records in memory, projects timeline events, and can write to a trace store. |
| `StoreOnlyAiTraceRecorder` | Writes trace records only to the configured store. |
| `IAiRuntimeTraceStore` | Stores completed trace records. |
| `MongoAiRuntimeTraceStore` | Persists trace records to MongoDB. |
| `CompositeAiRuntimeTraceStore` | Writes traces to memory and MongoDB. |
| `IAiTraceTimeline` | Provides process-local timeline diagnostics. |
| `IAiRuntimeCorrelationAccessor` | Provides ambient runtime correlation for async execution flow. |
| DAG claim service | Emits storage traces for claim, concurrency lease acquisition, and stale step recovery. |
| Runtime health reconciler | Produces unsafe runtime capacity signals used by routing and recovery. |
| Execution recovery reconciler | Reconciles assigned work on unsafe runtime instances and emits recovery evidence. |
| Runtime recovery forensics store/API | Persists and exposes per-work-item recovery timelines. |
| DAG step executor | Emits step traces for claimed step execution. |
| Retention coordinator | Emits retention traces. |
| Resolver layer | Emits resolver traces. |
| Future exporters | Will expose traces/metrics to external observability systems. |

---

## TODO / Improvements

The current observability foundation is functional and test-validated, but several improvements are planned.

### 1. Separate Policy Tracing from Step Tracing

Some policy-related work is currently visible through step-style tracing.

For example, retry policy resolution during execution creation may appear as:

```text
step / execute.succeeded
```

This is misleading because policy resolution is not physical step execution.

Planned improvement:

- add `AiPolicyTraceContext`
- add `TracePolicyAsync`
- emit policy traces as:

```text
policy / retry.definition.succeeded
policy / concurrency.policy.succeeded
policy / retention.policy.succeeded
```

### 2. Normalize WorkerId and RuntimeInstanceId

Some trace paths still need clearer separation between:

```text
RuntimeInstanceId
= process / host / pod identity

WorkerId
= logical runtime worker identity
```

Planned improvement:

- ensure all worker loops pass the logical worker id
- ensure all runtime host/process traces pass runtime instance id
- never silently substitute runtime instance id as worker id unless the operation is runtime-instance-scoped

### 3. Propagate PipelineKey Everywhere

Some trace paths currently expose pipeline key through tags while others may not have it in the main correlation context.

Planned improvement:

- set `PipelineKey` in ambient runtime correlation at controller enqueue/start boundaries
- preserve it through execution creation
- propagate it through worker execution
- ensure trace records, timeline events, metrics, and ledger entries all expose the same pipeline key

### 4. Add Dedicated LeaseId Field

Concurrency lease id is currently visible through tags and sometimes claim-token-oriented fields.

Planned improvement:

- add `LeaseId` to trace correlation context
- keep `ClaimToken` for DAG claim ownership only
- keep `LeaseId` for distributed concurrency capacity only
- update trace output to show both fields independently

### 5. Normalize Trace Enrichment

Trace records are currently enriched from a combination of context objects, tags, ambient correlation, and operation-specific metadata.

Planned improvement:

- centralize trace enrichment in one helper
- define precedence rules:
  - explicit trace context
  - ambient runtime correlation
  - tags
  - fallback values
- avoid duplicated enrichment logic across tracer, recorder, and timeline projection

### 6. Improve Storage Trace Context

`AiStorageTraceContext` should become richer.

Planned fields:

- pipeline key
- step key
- worker id
- claim token
- lease id
- provider
- model
- operation kind
- storage operation kind

This will reduce reliance on tags for important correlation fields.

### 7. Add Stronger Trace Assertions

Current distributed chaos tracing tests are diagnostic and intentionally flexible.

Planned stricter assertions:

- every step trace has `ExecutionId`
- every physical step trace has `StepId`
- every physical step trace has `StepKey`
- every distributed claimed step trace has a claim token
- every claim trace includes worker id
- concurrency traces include lease id
- memory and Mongo trace stores contain equivalent key categories
- timeline events are ordered by timestamp
- no policy resolution traces are emitted as physical step execution

### 8. Add OpenTelemetry Exporters

The current tracer is runtime-local.

Planned improvement:

- OpenTelemetry-compatible trace exporter
- metrics exporter
- span hierarchy support
- trace id propagation
- external distributed tracing compatibility

### 9. Add Observability Dashboard

Future dashboard views should include:

- execution timeline
- worker participation
- retry timeline
- recovery timeline
- retention and compaction timeline
- provider/model throttling
- payload externalization/rehydration
- finalization race visibility
- replay comparison view

### 10. Add Cost and Provider Governance Metrics

Future provider governance should include:

- token usage
- provider cost
- model cost
- call counts by provider/model/operation
- throttling impact
- retry cost impact
- wasted call detection
- RAG redundancy metrics

### 11. Add Replay-Aware Trace Views

MCP replay report, replay ledger, and replay trace retrieval are validated.

The remaining work is to turn this proof surface into richer public API and UI views.

Planned improvement:

- show original execution trace
- show replay execution trace
- compare operation ordering
- compare fingerprints
- compare step outputs
- show divergence points
- show missing payload or resolver failures
- show recovery replay proof next to runtime recovery forensics

### 12. Add Retention-Aware Trace Views

Retention and compaction affect inspection.

Planned improvement:

- show when payload was compacted
- show when hot-state step data was evicted
- show archive index usage
- show payload rehydration
- show resolver reconstruction after eviction
- expose whether a step result came from hot state or rehydrated payload

### 13. Add Failure Classification

Trace failures should eventually be classified by source.

Planned categories:

- step failure
- provider failure
- policy failure
- resolver failure
- storage failure
- concurrency denial
- retry budget exhausted
- cancellation
- control-state block
- human-input wait
- retention failure
- replay validation failure

### 14. Add Sampling and Retention for Observability Data

Trace and metric data can grow quickly.

Planned improvement:

- trace sampling policies
- metric aggregation windows
- TTL for local diagnostic records
- MongoDB retention policies
- execution-scoped cleanup tools
- export-before-delete workflows

### 15. Add Production Configuration Guide

Observability configuration should be documented for production-like setups.

Planned documentation:

- memory-only local mode
- Mongo-only durable mode
- MemoryAndMongo development mode
- disabling observability
- strict vs best-effort behavior
- collection naming
- indexing
- Docker local troubleshooting
- Kubernetes observability wiring

---

## Design Principles

The observability layer follows these principles:

1. Runtime execution safety comes first.
2. Observability should not break workflow execution in best-effort mode.
3. Metrics, traces, logs, and ledger entries should share correlation fields.
4. `RunId` and `ExecutionId` must remain semantically separate.
5. Runtime instance identity and worker identity must remain distinguishable.
6. Trace records should be useful locally and durable when configured.
7. Memory and MongoDB modes should be independently selectable.
8. Diagnostic tests should reveal observability quality issues without over-constraining early foundations.
9. Important runtime decisions should be recorded in the ledger, not only in traces.
10. Future replay, audit, and dashboard tooling should be able to consume the same correlation model.
11. Tenant-scoped observability must not leak ledger, trace, replay, or forensics records across tenants.
12. Safe tenants should produce normal execution observability but no recovery observability when they were not impacted.

---

## Relationship to Replay and Audit

Observability is a foundation for replay and audit capabilities. MCP replay report, replay ledger, and replay trace retrieval are already validated; richer public replay/audit APIs and UI workflows remain future work.

Replay answers:

```text
Can I restore and compare execution state?
```

The decision ledger answers:

```text
Which decisions were made?
```

Tracing answers:

```text
What happened over time?
```

Metrics answer:

```text
How often and how much?
```

Together they support:

- replay diagnostics
- audit trails
- decision history
- timeline inspection
- worker participation analysis
- retry/recovery explanation
- provider throttling explanation
- retention and compaction inspection
- finalization race explanation
- future dashboarding
- runtime crash recovery audit
- safe tenant non-impact proof

Replay-specific UI and public audit workflows should be expanded later by the official Replay API, but replay/ledger/trace evidence is already part of the validated MCP recovery proof surface.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Execution-Correlated Ledger](execution-correlated-ledger.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Replay and Audit](replay-and-audit.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction and update based on the current observability, metrics, and tracing implementation.

