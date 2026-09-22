# Runtime Metrics

Status: Implemented foundation. MongoDB-backed metric persistence, MemoryAndMongo mode, stricter value assertions, aggregation windows, exporters, and production dashboards remain active improvement areas.

This document describes the runtime metrics foundations of the Deterministic AI Runtime.

Runtime metrics provide counters, grouped values, durations, totals, and diagnostic snapshots for execution behavior, workers, retention, storage, resolver activity, hot-state size, policy decisions, and future operational dashboards.

The complete technical reference is preserved in:

- [runtime-internals.md](../runtime-internals.md)

---

## Purpose

Production AI execution needs measurable runtime signals.

Logs and traces explain what happened in detail.

Metrics answer questions such as:

- how many steps were claimed?
- how many workers participated?
- how many worker cycles were needed?
- how many retries were scheduled?
- how many steps were recovered?
- how many finalization attempts occurred?
- how often did resolver paths miss?
- how many payloads were stored, loaded, missed, or failed?
- how many bytes were stored or compacted?
- how many steps were evicted from hot state?
- how many retention plans were created?
- how often did policy evaluation succeed or fail?
- how often did an execution reach terminal completion?
- which runtime instance did work?
- which storage backend was used?
- which exception types occurred?

Metrics exist to provide runtime health, regression visibility, operational summaries, and future dashboard inputs.

They are not intended to replace logs, traces, or the execution-correlated decision ledger.

---

## Current Scope

The current implementation provides in-memory runtime metrics and metric storage foundations.

Implemented metric areas include:

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
- runtime metrics facade composition
- metric store mode configuration
- MongoDB metric persistence foundations
- MemoryAndMongo mode foundations
- diagnostic distributed chaos metric output

The metric layer is intentionally simple, explicit, and test-friendly.

It is designed to validate runtime correctness before evolving into richer production telemetry.

---

## Why Metrics Matter

Distributed AI execution is not only about whether a workflow completed.

A workflow may complete while still hiding important behavior:

- too many retries
- too many claim misses
- too many resolver misses
- too much hot-state growth
- too many retention operations
- unexpected finalization races
- uneven worker participation
- excessive storage misses
- repeated policy failures
- excessive payload externalization
- too many terminal observations

Metrics provide aggregate visibility into those patterns.

A single log line may explain one event.

A metric can show systemic behavior across the execution.

---

## Metrics vs Logs vs Traces vs Ledger

The runtime uses multiple observability layers.

| Layer | Purpose |
|---|---|
| Logs | Human-readable runtime messages. |
| Metrics | Aggregated counters, grouped values, durations, and totals. |
| Traces | Timeline and operation flow diagnostics. |
| Decision ledger | Structured runtime decisions and lifecycle facts. |

Example:

A log may say:

```text
[AI DAG] Step recovered.
```

A trace may show:

```text
dag-store / RecoverTimedOutSteps.succeeded
```

A ledger entry may record:

```text
recovery.step_recovered
```

A metric may record:

```text
RecoveredStepsByExecution[execution-id] = 3
```

Each layer has a different role.

Metrics are best for counts, health, trends, and dashboards.

---

## Metrics Facade

Runtime metrics are exposed through `IAiRuntimeMetrics`.

The metrics facade groups specialized metric domains.

Current metric domains include:

- execution
- retention
- storage
- hot state
- resolver
- policy
- worker

The design intentionally avoids a single flat metric interface.

A structured facade makes the runtime easier to evolve as more metric domains are added.

---

## Metric Domains

| Domain | Purpose |
|---|---|
| Execution | Step retry, recovery, claim, and finalization counters. |
| Worker | Runtime instance worker cycles and terminal observations. |
| Retention | Trigger, decision, plan, and execution retention counters. |
| Storage | Payload store/load/cache/failure counters and byte totals. |
| Resolver | Context resolution started/success/miss/failure counters. |
| HotState | Hot-state size, step add/remove, and compaction counters. |
| Policy | Policy execution, decisions, durations, and failures. |

---

## Correlation Model

Metrics are aligned with the same runtime correlation model used by tracing and the decision ledger.

The broader observability model can include:

- `CorrelationId`
- `RunId`
- `ExecutionId`
- `PipelineName`
- `PipelineVersion`
- `PipelineKey`
- `RuntimeInstanceId`
- `WorkerId`
- `StepId`
- `StepKey`
- `Provider`
- `Model`
- `Operation`

Current in-memory metrics primarily aggregate by explicit method arguments such as:

- execution id
- step name
- runtime instance id
- storage kind
- resolver path
- exception type
- policy key
- status

Future metric persistence and dashboards should normalize these values into a shared metric record schema.

---

## RunId and ExecutionId Separation

The runtime separates controller lifecycle identity from durable DAG execution identity.

```text
RunId
= controller / queue / background run lifecycle id

ExecutionId
= authoritative durable DAG execution id
```

Metrics should preserve this separation.

Execution metrics should usually be grouped by `ExecutionId`.

Controller or queue metrics may be grouped by `RunId`.

Where both are available, metric records should preserve both.

This avoids mixing controller lifecycle behavior with authoritative DAG execution state.

---

## Execution Metrics

Execution metrics record core DAG runtime behavior.

Typical execution metrics include:

- retry counts by step
- recovered step counts by execution
- finalization attempt count
- finalization success count
- step claim success counts by step
- step claim miss count
- execution lifecycle counters

These metrics answer questions such as:

- which steps retried the most?
- which executions required recovery?
- did finalization happen once or many times?
- did workers frequently miss claims?
- which steps were claimed most often?

Execution metrics are especially useful in tests for deterministic convergence and retry/recovery behavior.

---

## Retry Metrics

Retry-related execution metrics can include:

- retry count by step
- retry attempts per execution
- retry budget usage
- retry success/failure behavior
- waiting-for-retry observation

Retry metrics complement retry ledger events.

The ledger records decision facts.

Metrics show aggregate retry pressure.

---

## Recovery Metrics

Recovery metrics can include:

- recovered step count by execution
- total recovered steps
- recovery scan outcomes
- stale running-step recovery count

Recovery metrics help validate that crashed or timed-out workers do not permanently block progress.

They also help detect unstable executions where recovery occurs frequently.

---

## Claim Metrics

Claim metrics can include:

- step claim success count by step
- claim miss count
- claim contention indicators
- claims by worker or runtime instance in future extensions

Claim metrics are important because distributed execution depends on safe atomic claim ownership.

Frequent claim misses may be normal under high worker pressure, but they should be visible.

---

## Finalization Metrics

Finalization metrics can include:

- finalization attempt count
- finalization success count
- race-lost count in future extensions
- terminal observation counts

Distributed runtimes may allow several workers to observe terminal convergence.

Only one worker should win durable finalization.

Metrics should reflect the distinction between observation and durable finalization.

---

## Worker Metrics

Worker metrics record runtime instance worker behavior.

Current worker metrics include:

- cycles by runtime instance
- terminal observations by status

These metrics help validate distributed execution.

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

In a distributed runtime, multiple workers may observe terminal completion.

Therefore, the correct assertion is usually:

```text
CompletedCount >= 1
```

not:

```text
CompletedCount == 1
```

---

## Retention Metrics

Retention metrics are grouped into specialized sub-domains.

Current retention metric sub-domains include:

- trigger metrics
- decision metrics
- plan metrics
- execution metrics

Retention metrics are essential because retention affects memory safety, hot-state size, resolver reconstruction, and replay readiness.

---

## Retention Trigger Metrics

Retention trigger metrics record whether retention was invoked or skipped.

Examples include:

- triggered count
- skipped count
- reasons for triggered/skipped decisions in future extensions

These metrics answer:

```text
How often is retention evaluated?
How often does it actually run?
```

---

## Retention Decision Metrics

Retention decision metrics record whether compaction or eviction was required.

Examples include:

- compaction required count
- eviction required count
- no action required count

These metrics help validate that retention policies are making expected decisions.

---

## Retention Plan Metrics

Retention plan metrics record the shape of retention plans.

Examples include:

- plan created count
- total compacted steps
- total evicted steps
- total kept steps

These metrics help verify that retention planning behaves correctly under aggressive retention scenarios.

---

## Retention Execution Metrics

Retention execution metrics record physical retention effects.

Examples include:

- step evicted count
- step marked archived count
- payload compacted count
- total before bytes
- total after bytes
- total bytes saved
- retention completed count
- retention failed count
- failures by exception type

These metrics are important for proving that retention reduces hot-state size without breaking resolver consistency.

---

## Hot-State Metrics

Hot-state metrics record Redis execution state size and compaction effects.

Examples include:

- state step added count
- state step removed count
- state compacted count
- last compaction before step count
- last compaction after step count
- total steps removed by compaction
- state size observed count
- last observed step count
- last estimated bytes

Hot-state metrics help validate that long-running or large DAG executions do not grow unbounded in Redis.

---

## Storage Metrics

Storage metrics record payload storage behavior.

Examples include:

- payload stored count
- payload loaded count
- payload store hit count
- payload store miss count
- payload store failure count
- total payload stored bytes
- operations by storage kind
- failures by exception type

Storage metrics are useful for MongoDB payload storage, Redis cache behavior, payload rehydration, and future storage health dashboards.

---

## Resolver Metrics

Resolver metrics record context resolution behavior.

Examples include:

- resolve started count
- resolve success count
- resolve miss count
- resolve failed count
- operations by path
- failures by exception type

Resolver metrics help diagnose input binding, previous step output, externalized payload, and replay-state reconstruction problems.

Resolver misses are especially important when retention and compaction are enabled.

---

## Policy Metrics

Policy metrics record policy execution and decision behavior.

Examples include:

- policy execution count
- policy execution duration
- policy success/failure count
- policy decision count
- policy failures by key or type

Policy metrics are useful for retry, retention, concurrency, timeout, circuit breaker, validation, and future policy domains.

They complement policy ledger events by providing aggregate behavior.

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

This mode is useful when observability is intentionally turned off.

### Memory

Metrics are stored in memory.

This is useful for tests, local development, and runtime diagnostics.

### Mongo

Metric records are persisted to MongoDB.

This is useful for durable metric diagnostics and future dashboards.

### MemoryAndMongo

Metrics are stored in memory and persisted to MongoDB.

This is useful when tests or local diagnostics need immediate access while also validating durable persistence.

---

## Metric Store Options

Metric persistence is configured through `AiRuntimeMetricStoreOptions`.

Options include:

- mode
- MongoDB connection string
- MongoDB database name
- MongoDB collection name

When explicit metric MongoDB options are not supplied, the runtime can fall back to existing runtime MongoDB configuration.

This avoids duplicating connection strings across:

- payload storage
- snapshots
- metrics
- traces
- decision ledger

---

## MongoDB Metric Persistence

MongoDB metric persistence is a foundation for durable operational diagnostics.

Metric records should eventually support lookup by:

- execution id
- run id
- correlation id
- metric domain
- metric name
- runtime instance id
- worker id
- timestamp
- tags

MongoDB metric persistence should remain observational.

It should not break runtime execution unless explicitly configured to do so.

---

## MemoryAndMongo Metrics

`MemoryAndMongo` mode allows the runtime to keep fast in-memory metrics while also persisting metric records to MongoDB.

This is useful for:

- integration tests
- local debugging
- future dashboards
- validating metric persistence
- comparing memory metrics and durable metric records

The current diagnostic tests validate that the runtime metrics facade remains usable when `MemoryAndMongo` mode is enabled.

Further tests should validate persisted MongoDB metric record values in detail.

---

## Distributed Chaos Metrics Output

Distributed chaos metrics tests can print worker participation and terminal status summaries.

Example output shape:

```text
============================================================
RUNTIME METRICS - MEMORY + MONGO CONFIGURATION
============================================================
RunId:       ...
ExecutionId: ...
Pipeline:    distributed-chaos-100-...
Steps:       100
Workers:     10

WORKER CYCLES
------------------------------------------------------------
WorkerId=...:worker:1:... | Cycles=19
WorkerId=...:worker:2:... | Cycles=20

TERMINAL STATUS METRICS
------------------------------------------------------------
Status=Completed | Count=11
```

This output proves that:

- workers participated
- cycles were recorded
- terminal completion was observed
- metrics remain available under MemoryAndMongo configuration

---

## Metric Value Validation

Metric value assertions should distinguish between strict invariants and distributed-runtime observations.

Good strict assertions:

```text
workerCycles.Count == expectedWorkerCount
all worker cycle counts > 0
CompletedCount >= 1
no unexpected Failed terminal status
```

Avoid overly strict assertions such as:

```text
CompletedCount == 1
```

because multiple workers may observe terminal completion.

Metric tests should validate runtime correctness without over-constraining legitimate distributed behavior.

---

## Testing Coverage

Runtime metrics are validated through tests covering:

- execution metrics
- retry count aggregation
- recovery count aggregation
- claim counters
- finalization counters
- invalid key handling
- retention trigger metrics
- retention decision metrics
- retention plan metrics
- retention execution metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics
- runtime metrics facade composition
- worker cycle metrics
- terminal status metrics
- MemoryAndMongo metric configuration
- distributed chaos metrics output

Recent diagnostics validate that:

- distributed chaos executions produce worker cycle metrics
- runtime workers record terminal status observations
- metrics remain available when MemoryAndMongo mode is configured
- metric values can be inspected by runtime instance worker id

---

## Current Status

| Capability | Status |
|---|---|
| Runtime metrics facade | Implemented |
| Execution metrics | Implemented |
| Worker metrics | Implemented |
| Runtime instance worker metrics | Implemented |
| Retention metrics facade | Implemented |
| Retention trigger metrics | Implemented |
| Retention decision metrics | Implemented |
| Retention plan metrics | Implemented |
| Retention execution metrics | Implemented |
| Storage metrics | Implemented |
| Resolver metrics | Implemented |
| Hot-state metrics | Implemented |
| Policy metrics | Implemented |
| Metric storage options | Implemented |
| Metric Disabled mode | Implemented |
| Metric Memory mode | Implemented |
| Metric Mongo mode | Foundation implemented |
| Metric MemoryAndMongo mode | Foundation implemented |
| MongoDB metric persistence | Foundation implemented |
| Distributed chaos metrics diagnostics | Implemented |
| Strict Mongo metric value validation | Planned |
| Metrics exporter | Planned |
| Metrics dashboard | Planned |
| Cost/provider governance metrics | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| `IAiRuntimeMetrics` | Central runtime metrics facade. |
| `AiRuntimeMetrics` | Composes execution, retention, storage, hot-state, resolver, policy, and worker metrics. |
| `AiExecutionMetrics` | Records execution, retry, recovery, claim, and finalization counters. |
| `AiRuntimeInstanceWorkerMetrics` | Records worker cycles and terminal status observations. |
| `AiRetentionMetrics` | Aggregates retention trigger, decision, plan, and execution metrics. |
| `AiRetentionTriggerMetrics` | Records retention triggered/skipped counts. |
| `AiRetentionDecisionMetrics` | Records compaction, eviction, and no-action decisions. |
| `AiRetentionPlanMetrics` | Records retention plan counts and totals. |
| `AiRetentionExecutionMetrics` | Records compaction bytes, evictions, archive marking, completion, and failures. |
| `AiStorageMetrics` | Records payload storage/load/hit/miss/failure counters. |
| `AiResolverMetrics` | Records context resolver started/success/miss/failure counters. |
| `AiHotStateMetrics` | Records hot-state size and compaction counters. |
| `AiPolicyMetrics` | Records policy execution and decision metrics. |
| `AiRuntimeMetricStoreOptions` | Configures metric persistence mode and MongoDB target. |
| Future metric store | Persists normalized metric records. |
| Future exporters | Expose metrics to external observability systems. |

---

## TODO / Improvements

The metric foundation is functional and test-validated, but several improvements are planned.

### 1. Normalize Metric Record Shape

Current in-memory metrics are domain-specific counters.

Future durable metric records should use a normalized shape.

Planned fields:

- metric id
- timestamp
- domain
- name
- value
- unit
- execution id
- run id
- correlation id
- pipeline name
- pipeline version
- pipeline key
- runtime instance id
- worker id
- step id
- step key
- provider
- model
- operation
- tags

### 2. Strengthen Mongo Metric Persistence

Metric Mongo mode and MemoryAndMongo mode should be validated more strictly.

Planned improvements:

- verify persisted metric records by execution id
- verify persisted metric records by run id
- verify persisted metric records by domain
- verify persisted metric records by runtime instance id
- compare memory metrics with Mongo metric records
- add Mongo indexes for common metric queries
- add best-effort persistence behavior tests

### 3. Add Metric Aggregation Windows

Raw metric records can grow quickly.

Planned aggregation support:

- per execution
- per run
- per pipeline
- per worker
- per runtime instance
- per provider
- per model
- per operation
- per time window

### 4. Add Provider and Cost Metrics

Provider governance should include cost and usage metrics.

Planned metrics:

- provider call count
- model call count
- operation call count
- token input count
- token output count
- estimated cost
- retry cost impact
- throttling delay impact
- wasted failed-call cost
- RAG retrieval cost
- redundant call detection

### 5. Add Distributed Concurrency Metrics

Distributed throttling should expose aggregate metrics.

Planned metrics:

- concurrency allowed count
- concurrency denied count
- lease acquired count
- lease released count
- lease expired count
- denied by scope
- wait time by provider/model/operation
- current active lease count
- throttling pressure by scope

### 6. Add Replay Metrics

Replay API implementation should add metrics.

Planned metrics:

- replay requested count
- replay started count
- replay completed count
- replay failed count
- replay comparison count
- fingerprint match count
- fingerprint mismatch count
- replay divergence count
- replay payload miss count
- replay resolver failure count

### 7. Add Control Plane Metrics

Execution control and queue control should expose metrics.

Planned metrics:

- run queued count
- run dequeued count
- queue paused count
- queue resumed count
- execution pause requested count
- execution resume requested count
- execution cancel requested count
- waiting for input count
- human input submitted count
- control-state blocked claim count

### 8. Add Failure Classification Metrics

Failures should be grouped by source.

Planned categories:

- provider failure
- step failure
- policy failure
- resolver failure
- storage failure
- retry budget exhausted
- cancellation
- concurrency denial
- human input timeout
- retention failure
- replay validation failure

### 9. Add Exporters

The current metrics layer is runtime-local.

Planned exporters:

- OpenTelemetry metrics exporter
- Prometheus endpoint
- JSON metric export
- MongoDB metric export viewer
- dashboard-friendly metric snapshots

### 10. Add Dashboard Views

Future dashboard views should include:

- execution health
- worker participation
- retry pressure
- recovery pressure
- provider/model usage
- throttling pressure
- storage hit/miss rates
- resolver misses
- retention/compaction impact
- cost overview
- replay validation results

### 11. Add Metric Retention Policies

Metric records need lifecycle management.

Planned support:

- MongoDB TTL indexes
- execution-scoped cleanup
- aggregate-before-delete
- export-before-delete
- per-domain retention windows
- local in-memory maximum record limits

### 12. Clarify Counter Semantics

Some distributed counters need explicit semantics.

Examples:

```text
TerminalCompletedCount
```

may mean:

- durable finalization completed once
- terminal state observed by a worker
- worker group returned completed

Planned improvement:

- document counter meaning
- split ambiguous counters
- use separate names for observation vs durable transition
- add tests that encode the intended semantics

### 13. Add Metric Units

Metrics should consistently expose units.

Examples:

- count
- milliseconds
- bytes
- steps
- executions
- workers
- tokens
- currency
- percentage

### 14. Add Metric Correlation Helper

Metric recording should use a shared helper to enrich correlation fields consistently.

Planned improvement:

- centralize metric correlation enrichment
- reuse ambient runtime correlation
- allow explicit override from metric call sites
- ensure RunId/ExecutionId separation is preserved

### 15. Add Production Configuration Guide

Production metric configuration should be documented.

Planned documentation:

- memory-only local mode
- Mongo-only durable mode
- MemoryAndMongo development mode
- disabling metrics
- collection naming
- Mongo indexing
- Docker troubleshooting
- Kubernetes metrics wiring
- exporter configuration

---

## Design Principles

The metrics layer follows these principles:

1. Runtime execution safety comes first.
2. Metric recording should not break workflow execution in best-effort mode.
3. Metrics should expose aggregate behavior, not duplicate every log line.
4. Metrics should align with runtime correlation fields.
5. RunId and ExecutionId must remain semantically separate.
6. Runtime instance identity and worker identity must remain distinguishable.
7. Distributed counters must avoid misleading single-worker assumptions.
8. Memory and MongoDB modes should be independently selectable.
9. Metrics should be simple enough for tests and structured enough for future dashboards.
10. Future exporters should reuse the same metric domain model.

---

## Relationship to Replay, Audit, and Tracing

Metrics are one part of the runtime visibility model.

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
How often, how much, and where?
```

Together they support:

- execution diagnostics
- performance analysis
- retry pressure analysis
- recovery analysis
- provider governance
- cost governance
- replay confidence
- audit trails
- dashboarding
- production runtime health

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Distributed Execution](distributed-execution.md)
- [Execution Control State](execution-control-state.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Execution-Correlated Ledger](execution-correlated-ledger.md)
- [Observability and Tracing](observability-tracing.md)
- [Redis Performance Diagnostics](redis-performance-diagnostics.md)
- [MongoDB Performance Diagnostics](mongodb-performance-diagnostics.md)
- [Retention and Compaction](retention-and-compaction.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Policy-Driven Execution](policy-driven-execution.md)
- [Config-Driven Runtime](config-driven-runtime.md)
- [Replay and Audit](replay-and-audit.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document is a focused extraction and update based on the current runtime metrics implementation.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
