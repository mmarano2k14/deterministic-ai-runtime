# Documentation Index

This directory contains the documentation for **Deterministic AI Runtime**.

The main repository README is intentionally short. It explains the project, its purpose, current capabilities, and roadmap.

The main technical documentation is preserved at the root of this `docs/` directory.

Focused AI runtime documentation is organized under:

- [`ai/`](ai/)

---

## Start Here

| Document | Purpose |
|---|---|
| [`../README.md`](../README.md) | Main repository entry point. Short, professional overview. |
| [`runtime-internals.md`](runtime-internals.md) | Complete technical reference preserved from the original README. |
| [`enterprise-readiness.md`](enterprise-readiness.md) | Matrix of enterprise AI execution questions and runtime answers. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated runtime decision ledger, audit foundations, retention auditability, and replay lifecycle event correlation. |
| [`ai/observability.md`](ai/observability.md) | High-level observability index summarizing ledger, tracing, metrics, logs, correlation, replay diagnostics, and current observability roadmap. |
| [`ai/observability-tracing.md`](ai/observability-tracing.md) | Runtime tracing, trace timelines, correlation, trace storage modes, Mongo trace persistence, MemoryAndMongo mode, and tracing improvements. |
| [`ai/runtime-metrics.md`](ai/runtime-metrics.md) | Runtime metric domains, metric storage modes, worker/retention/storage/resolver/hot-state/policy metrics, and metrics improvements. |
| [`ai/replay-and-audit.md`](ai/replay-and-audit.md) | Deterministic Replay Engine V1, snapshot restore, fingerprint validation, replay metadata, ledger/timeline diagnostics, and replay TODO/improvements. |
| [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md) | Runtime control-plane foundation covering replay control, execution control, runtime queue control, runtime instance registry/control, discovery, capacity, admission decisions, Redis-backed scale-out request lifecycle, fulfilled-run requeue, and Kubernetes-oriented orchestration foundations. |
| [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md) | Runtime discovery, registry, and capacity foundation covering Redis control-plane discovery, ControlPlaneIdResolver, runtime registration, capacity descriptors, pump readiness, local scale-out capacity visibility, cleanup, and HTTP pooled runtime identity. |
| [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md) | MCP server as a runtime control-plane adapter, including host modes, MCP tool groups, runtime role separation, local runtime instance pool behavior, Redis/local scale-out execution, shared queue dispatch flow, and Kubernetes direction. |
| [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md) | Provider-based runtime instance administration model for local, Redis command queue, HTTP, gRPC, and Kubernetes providers, including dispatch/status/control/scale-out capabilities, descriptor metadata, provider routing, and future slot reservations. |
| [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md) | Shared queue pump, queue-first submit mode, direct-dispatch scale-out path, fulfilled-run requeue, manual drain, dispatch-time admission, pump identity separation, runtime worker capacity visibility, and `MaxLocalWorkersPerExecution`. |
| [`comparison-existing-tools.md`](comparison-existing-tools.md) | Ecosystem positioning against agent frameworks, workflow engines, orchestration tools, observability platforms, and distributed infrastructure. |
| [`roadmap.md`](roadmap.md) | Project roadmap organized by phases. |

---

## Recommended Reading Paths

### For CTOs, Engineering Managers, and Recruiters

Start with:

1. [`../README.md`](../README.md)
2. [`enterprise-readiness.md`](enterprise-readiness.md)
3. [`comparison-existing-tools.md`](comparison-existing-tools.md)
4. [`roadmap.md`](roadmap.md)

This path explains what the project is, why it matters, and how it maps to enterprise AI execution problems.

### For Architects and Senior Engineers

Start with:

1. [`../README.md`](../README.md)
2. [`ai/architecture-overview.md`](ai/architecture-overview.md)
3. [`enterprise-readiness.md`](enterprise-readiness.md)
4. [`ai/observability.md`](ai/observability.md)
5. [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md)
6. [`ai/observability-tracing.md`](ai/observability-tracing.md)
7. [`ai/runtime-metrics.md`](ai/runtime-metrics.md)
8. [`ai/replay-and-audit.md`](ai/replay-and-audit.md)
9. [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md)
10. [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md)
11. [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md)
12. [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md)
13. [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md)
14. [`runtime-internals.md`](runtime-internals.md)

This path gives both the strategic positioning and the complete technical depth.

### For Contributors

Start with:

1. [`ai/architecture-overview.md`](ai/architecture-overview.md)
2. [`ai/config-driven-runtime.md`](ai/config-driven-runtime.md)
3. [`ai/policy-driven-execution.md`](ai/policy-driven-execution.md)
4. [`ai/context-resolution-and-helpers.md`](ai/context-resolution-and-helpers.md)
5. [`ai/step-plugins.md`](ai/step-plugins.md)
6. [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md)
7. [`ai/observability-tracing.md`](ai/observability-tracing.md)
8. [`ai/runtime-metrics.md`](ai/runtime-metrics.md)
9. [`ai/replay-and-audit.md`](ai/replay-and-audit.md)
10. [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md)
11. [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md)
12. [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md)
13. [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md)
14. [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md)
15. [`runtime-internals.md`](runtime-internals.md)
16. [`roadmap.md`](roadmap.md)

This path gives the current architecture, configuration model, context resolution layer, extension model, technical reference, and next planned improvements.

---

## Core Documentation

### [`runtime-internals.md`](runtime-internals.md)

The complete technical reference preserved from the original README.

It includes detailed explanations of:

- runtime architecture
- DAG execution
- Redis hot state
- Redis Lua coordination
- distributed workers
- retry and recovery
- retention and compaction
- payload externalization
- rehydration resolver
- distributed concurrency and throttling
- execution control state
- runtime queue control
- observability
- deterministic replay engine and snapshot foundations
- replay metadata, ledger, and timeline diagnostics
- execution-correlated decision ledger
- roadmap and vision

This document intentionally keeps the original depth. It should not be deleted.

### [`enterprise-readiness.md`](enterprise-readiness.md)

A structured matrix answering key enterprise AI runtime questions:

- worker crashes
- duplicate execution prevention
- replay
- auditability
- concurrency limits
- pause/resume/cancel
- human-in-the-loop
- bounded memory/state
- multi-runtime-instance coordination
- deterministic convergence

### [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md)

Execution-correlated runtime decision ledger foundations.

This document explains:

- execution-correlated runtime auditability
- structured runtime decision recording
- execution versus run correlation
- claim and concurrency audit visibility
- retry and recovery audit visibility
- queue and execution control observability
- human-in-the-loop auditability
- retention and compaction auditability
- snapshot persistence audit events
- finalization race visibility
- replay lifecycle event correlation

The document also explains how replay lifecycle events are correlated with the same execution ledger model used by the rest of the runtime.

### [`ai/replay-and-audit.md`](ai/replay-and-audit.md)

Deterministic replay and audit foundations.

This document explains:

- replay-as-validation using persisted snapshots
- audit-only replay
- restore from persisted snapshot
- deterministic replay fingerprint comparison
- replay metadata
- payload reference validation
- replay lifecycle ledger events
- replay timeline diagnostics
- 100-step distributed replay reference tests
- replay log examples
- replay TODO and improvement roadmap

### [`ai/observability.md`](ai/observability.md)

High-level observability index and summary.

This document links the three focused observability areas:

- execution-correlated decision ledger
- observability and tracing
- runtime metrics

It explains how logs, metrics, traces, and ledger entries work together around a shared runtime correlation model.

### [`ai/observability-tracing.md`](ai/observability-tracing.md)

Runtime observability and tracing foundations.

This document explains:

- runtime observability facade
- runtime tracing facade
- in-memory trace recorder
- in-memory trace timeline
- trace correlation context
- trace store abstraction
- MongoDB-backed trace persistence
- trace storage modes: `Disabled`, `Memory`, `Mongo`, and `MemoryAndMongo`
- distributed chaos trace diagnostics
- tracing TODO and improvement roadmap

### [`ai/runtime-metrics.md`](ai/runtime-metrics.md)

Runtime metrics foundations.

This document explains:

- runtime metrics facade
- execution metrics
- worker metrics
- retention metrics
- storage metrics
- resolver metrics
- hot-state metrics
- policy metrics
- metric storage modes: `Disabled`, `Memory`, `Mongo`, and `MemoryAndMongo`
- distributed chaos metrics diagnostics
- metrics TODO and improvement roadmap

### [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md)

Runtime control-plane and orchestration foundations.

This document explains:

- replay control-plane facade
- execution control-plane facade
- local runtime queue control-plane facade
- runtime instance registry
- runtime instance capacity store
- control-plane discovery store
- control-plane id resolver
- runtime instance control-plane facade
- run admission and slot decisioning
- admission reservations
- Redis-backed scale-out request persistence
- scale-out watcher/provider selector lifecycle
- fulfilled scale-out shared run requeue
- RunId versus ExecutionId separation at the control-plane level
- queue pause/resume ledger correlation behavior
- Kubernetes-oriented runtime instance visibility
- shared runtime controller foundations


### [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md)

Runtime discovery, registry, and capacity foundations.

This document explains:

- Redis control-plane discovery store
- MCP-published logical control-plane identity
- ControlPlaneIdResolver
- runtime-only host discovery resolution
- runtime instance registration
- runtime heartbeat
- runtime capacity descriptor publication
- worker and run-slot capacity visibility
- shared queue pump readiness gate
- provider metadata for local, HTTP, and local scale-out dispatch
- scale-out-created runtime capacity visibility
- HTTP pooled runtime identity model
- registry and capacity shutdown cleanup
- TTL and self-healing direction
- validated Redis registry, capacity, discovery, and admission reservation behavior

### [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md)

MCP server as a runtime control-plane adapter.

This document explains:

- MCP server purpose and scope
- `ControlPlaneOnly` mode
- `ControlPlaneWithLocalRuntimeInstances` mode
- `RuntimeInstanceOnly` mode
- runtime role separation between control-plane hosts and executable runtime instances
- control-plane discovery publication
- runtime-only host identity resolution
- local runtime instance pool behavior
- HTTP pooled runtime provider behavior
- Redis/local scale-out execution flow
- shared queue dispatch flow
- MCP tool groups
- RunId versus ExecutionId behavior in MCP tools
- local queue preservation rules
- Kubernetes direction for MCP/control-plane deployment

### [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md)

Provider-based runtime instance administration model.

This document explains:

- why runtime instance providers are needed
- provider discovery through class attributes
- provider capabilities for dispatch, status, control, capacity, and scale-out
- provider router responsibilities
- local provider behavior
- local provider scale-out capability
- Redis command queue provider direction
- HTTP and gRPC provider direction
- Kubernetes provider responsibilities
- admission and provider separation
- Redis admission reservation foundation
- HTTP pooled runtime provider validation
- descriptor metadata keys for provider routing

### [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md)

Shared queue pump and worker capacity model.

This document explains:

- queue-first submit mode
- direct-dispatch scale-out path
- fulfilled scale-out shared run requeue
- manual shared queue drain
- background shared queue pump
- dispatch-time admission
- pump identity versus assigned runtime identity
- local runtime queue preservation
- shared queue no-double-dispatch behavior
- local and HTTP provider dispatch foundations
- HTTP pooled runtime dispatch validation
- Redis admission reservation foundation
- shared queue pump readiness gate
- runtime worker capacity visibility
- worker-aware `CanAcceptRun`
- `MaxLocalWorkersPerExecution`
- future admission capacity reservation
- Kubernetes-oriented runtime hosting direction

### [`comparison-existing-tools.md`](comparison-existing-tools.md)

A high-level ecosystem positioning document comparing the runtime with existing categories such as:

- agent frameworks
- workflow engines
- data orchestration tools
- observability platforms
- distributed compute systems
- infrastructure orchestration

This document does not rank tools. It clarifies where Deterministic AI Runtime fits architecturally.

### [`roadmap.md`](roadmap.md)

The project roadmap organized into phases:

- Completed
- Phase 0 — Documentation Restructure
- Phase 1 — Enterprise Demo
- Phase 2 — Real Enterprise Sample
- Phase 3 — Correlated Observability, Tracing, and Metrics
- Phase 4 — Kubernetes Deployment Demo
- Phase 5 — Public API / SDK Polish
- Phase 6 — Deterministic Replay Engine and Audit Foundations
- Phase 7 — Replay Controller, HTTP APIs, Dashboard, and Operational Tooling
- Phase 8 — Cost and Provider Governance
- Phase 9 — Articles / Public Positioning

---

## Runtime Architecture and Execution

| Document | Purpose |
|---|---|
| [`ai/architecture-overview.md`](ai/architecture-overview.md) | High-level runtime architecture and major runtime layers, including control-plane scale-out, fulfilled-run requeue, and provider-based dispatch. |
| [`ai/distributed-execution.md`](ai/distributed-execution.md) | Distributed workers, Redis coordination, claims, leases, and deterministic convergence. |
| [`ai/execution-control-state.md`](ai/execution-control-state.md) | ExecutionId-level pause, resume, cancel, waiting-for-input, and control-state behavior. |
| [`ai/runtime-queue-control.md`](ai/runtime-queue-control.md) | RunId-level background controller queue control, hot enqueue, and RunId versus ExecutionId separation. |
| [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md) | Runtime control-plane foundation for replay, execution control, runtime queue control, runtime instance registry/control, discovery, capacity, admission, scale-out request lifecycle, and shared orchestration. |
| [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md) | Runtime discovery, Redis registry, Redis capacity descriptors, ControlPlaneIdResolver, pump readiness, local scale-out capacity visibility, cleanup, and HTTP pooled runtime identity. |
| [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md) | MCP server adapter over runtime control-plane foundations, including host modes, tool groups, role separation, local runtime pool behavior, Redis/local scale-out execution, and Kubernetes direction. |
| [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md) | Provider-based runtime instance administration, dispatch, and scale-out model for local, Redis command queue, HTTP, gRPC, and Kubernetes providers. |
| [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md) | Shared queue pump, queue-first submit mode, direct-dispatch scale-out path, fulfilled-run requeue, manual drain, dispatch-time admission, worker capacity visibility, and local worker caps per execution. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated runtime auditability, runtime decision recording, and replay lifecycle event correlation. |
| [`ai/observability.md`](ai/observability.md) | High-level observability index and summary linking ledger, tracing, metrics, and logs. |

---

## Reliability, State, and Recovery

| Document | Purpose |
|---|---|
| [`ai/retry-and-recovery.md`](ai/retry-and-recovery.md) | Retry engine, retry state, WaitingForRetry, Redis Lua transitions, and stale worker recovery. |
| [`ai/retention-and-compaction.md`](ai/retention-and-compaction.md) | Bounded hot state, compaction, eviction, payload externalization, and resolver safety. |
| [`ai/replay-and-audit.md`](ai/replay-and-audit.md) | Deterministic Replay Engine V1, snapshot restore, audit-only replay, fingerprint validation, replay metadata, ledger/timeline diagnostics, and future replay APIs. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated decision ledger, retention auditability, control-state auditability, and replay lifecycle evidence. |

---

## Distributed Governance and Observability

| Document | Purpose |
|---|---|
| [`ai/distributed-concurrency-throttling.md`](ai/distributed-concurrency-throttling.md) | Redis ZSET concurrency gate, provider/model/operation throttling, and admission policies. |
| [`ai/observability.md`](ai/observability.md) | High-level observability index summarizing logs, metrics, traces, ledger, correlation, and roadmap direction. |
| [`ai/observability-tracing.md`](ai/observability-tracing.md) | Runtime tracing, trace timelines, trace records, Mongo trace persistence, Memory/Mongo/MemoryAndMongo modes, and tracing improvements. |
| [`ai/runtime-metrics.md`](ai/runtime-metrics.md) | Runtime metric domains, metric storage modes, worker/retention/storage/resolver/hot-state/policy metrics, and metrics improvements. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated decision ledger, runtime audit visibility, and structured runtime lifecycle evidence. |
| [`ai/testing-strategy.md`](ai/testing-strategy.md) | Integration testing strategy and validation approach for distributed runtime guarantees, including Redis/local scale-out request, requeue, dispatch, and execution evidence. |

---

## Runtime Control Plane and Orchestration

| Document | Purpose |
|---|---|
| [`ai/runtime-control-plane.md`](ai/runtime-control-plane.md) | Runtime control-plane foundation, replay/execution/queue/instance facades, discovery, capacity, admission, Redis-backed scale-out lifecycle, fulfilled-run requeue, and shared controller orchestration. |
| [`ai/runtime-discovery-registry-capacity.md`](ai/runtime-discovery-registry-capacity.md) | Redis discovery, ControlPlaneIdResolver, runtime registry, capacity descriptors, scale-out capacity visibility, readiness gate, cleanup lifecycle, and HTTP pooled identity model. |
| [`ai/mcp-server-control-plane.md`](ai/mcp-server-control-plane.md) | MCP server as control-plane adapter, including host modes, discovery publication, MCP tool groups, Redis/local scale-out execution, shared queue dispatch, local/HTTP pooled runtime behavior, and runtime role separation. |
| [`ai/runtime-instance-provider-model.md`](ai/runtime-instance-provider-model.md) | Runtime instance provider model for provider-based dispatch, HTTP pooled runtime hosting, status, control, capacity, scale-out, descriptor metadata, and provider routing. |
| [`ai/shared-queue-pump-and-worker-capacity.md`](ai/shared-queue-pump-and-worker-capacity.md) | Shared queue pump/manual drain, queue-first dispatch, direct-dispatch scale-out, fulfilled-run requeue, readiness gate, dispatch-time admission, pump identity separation, runtime worker capacity visibility, and `MaxLocalWorkersPerExecution`. |
| [`ai/runtime-queue-control.md`](ai/runtime-queue-control.md) | RunId-level local runtime queue control, hot enqueue, queue pause/resume, and queued/running cancellation behavior. |
| [`ai/execution-control-state.md`](ai/execution-control-state.md) | ExecutionId-level durable pause, resume, cancel, waiting-for-input, and human-input control state. |
| [`ai/execution-correlated-ledger.md`](ai/execution-correlated-ledger.md) | Execution-correlated runtime decision ledger and audit visibility used by control-plane operations. |
| [`ai/observability.md`](ai/observability.md) | Observability index connecting logs, metrics, traces, ledger, replay diagnostics, and control-plane visibility. |

---

## Runtime Extension and Configuration

| Document | Purpose |
|---|---|
| [`ai/config-driven-runtime.md`](ai/config-driven-runtime.md) | How pipeline definitions and structured configuration drive runtime behavior. |
| [`ai/policy-driven-execution.md`](ai/policy-driven-execution.md) | Shared policy model used by retry, retention, concurrency, throttling, and admission control. |
| [`ai/context-resolution-and-helpers.md`](ai/context-resolution-and-helpers.md) | Input resolution, step context building, payload rehydration, provider metadata, policy context, and helper services. |
| [`ai/step-plugins.md`](ai/step-plugins.md) | Step keys, registered executors, class attributes, assembly scanning, provider abstractions, and plugin-style runtime extension. |
| [`ai/rag-pipelines.md`](ai/rag-pipelines.md) | RAG retrieval, merge, compose, provider-oriented workflow execution, auto-registered RAG steps, and deterministic RAG pipelines. |

---

## Documentation Status

Many focused documents started as documentation split placeholders, but several core runtime areas are now fully documented, including execution control state, runtime queue control, runtime control-plane foundations, MCP server control-plane usage, runtime instance provider architecture direction, runtime discovery/registry/capacity, Redis-backed scale-out request lifecycle, fulfilled-run requeue, shared queue pump and worker capacity, distributed concurrency, retention/compaction, deterministic replay and audit foundations, execution-correlated decision ledger foundations, observability/tracing foundations, and runtime metrics foundations.

The complete technical reference remains preserved in:

- [`runtime-internals.md`](runtime-internals.md)

Focused documents should be expanded progressively by extracting, refining, and linking content from `runtime-internals.md`.

---

## Documentation Rule

The original technical depth must be preserved.

New focused documents should be extracted from `runtime-internals.md` gradually.

Do not delete technical content until it has been safely moved, reviewed, and linked from this index.

When adding new documentation:

1. Add core documentation directly under `docs/`.
2. Add focused AI runtime documentation under `docs/ai/`.
3. Link new documents from this index.
4. Keep links relative to this file.
5. Preserve the complete technical reference in `runtime-internals.md`.
6. Clearly distinguish between implemented features, available foundations, and planned work.
7. Keep replay documentation connected to ledger, tracing, and metrics because Replay V1 now exposes replay metadata, replay lifecycle ledger events, and trace timeline diagnostics.
8. Keep observability overview, tracing, runtime metrics, and replay/audit linked together because they describe different layers of the same runtime visibility model.
9. Keep runtime control-plane documentation linked with runtime queue control, execution control state, instance visibility, admission, and future Kubernetes/shared-controller documentation.
10. Keep MCP server control-plane and runtime instance provider documentation linked with runtime control-plane, shared controller, admission, local runtime queues, runtime capacity descriptors, and Kubernetes preparation.
11. Keep shared queue pump and worker capacity documentation linked with shared controller usage, runtime queue control, MCP control-plane, runtime instance provider model, runtime discovery/registry/capacity, and testing strategy.
12. Keep runtime discovery, registry, and capacity documentation linked with runtime control-plane, MCP control-plane, runtime instance provider model, shared queue pump readiness, and testing strategy.
13. Keep Redis/local scale-out documentation linked across runtime control-plane, MCP control-plane, runtime instance provider model, shared queue pump, discovery/registry/capacity, config-driven runtime, and testing strategy.
