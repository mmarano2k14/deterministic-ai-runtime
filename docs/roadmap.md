# Roadmap

This roadmap describes the planned evolution of **Deterministic AI Runtime**.

The project is under active development. Some capabilities are implemented and validated, some are available as foundations, and some remain planned.

The roadmap should be read as a progression:

```text
runtime engine
    ↓
distributed AI execution infrastructure
    ↓
operational AI control plane
    ↓
MLOps-oriented runtime platform
```

The project is not positioned as a finished commercial platform. It is a serious execution-infrastructure foundation that is being hardened through tests, documentation, and production-like runtime scenarios.

---

## Status Legend

| Status | Meaning |
|---|---|
| Completed / Implemented | Already built in the runtime and covered by tests or validated behavior. |
| Completed (V1) | First complete version delivered; future refinements may continue. |
| Current | Current active documentation or engineering phase. |
| Planned | Identified future work. |
| Foundation available | Core building blocks exist, but the public API, external integration, or production polish is not complete. |
| Platform direction | Long-term evolution path beyond the current runtime foundation. |

---

## Completed and Validated Foundation

The following capabilities are already implemented or available as validated runtime foundations.

| Area | Status |
|---|---|
| Deterministic DAG execution | Implemented |
| Redis hot state | Implemented |
| Redis Lua atomic coordination | Implemented |
| Distributed workers | Implemented |
| Multi-runtime-instance execution foundations | Implemented |
| Deterministic convergence | Implemented |
| Context resolution and helper layer | Foundation available |
| Input binding resolution | Foundation available |
| Previous step output resolution | Foundation available |
| Payload resolver and rehydration | Implemented |
| Provider/model/operation context | Implemented |
| Policy-driven retry | Implemented |
| Stale step recovery | Implemented |
| Runtime process crash recovery | Implemented / validated |
| Real `RuntimeInstanceOnly` process-host recovery | Implemented / validated |
| Runtime health to execution recovery boundary | Implemented / validated |
| Retry vs recovery separation | Implemented / validated |
| Retention and compaction | Implemented |
| Payload externalization | Implemented |
| Rehydration resolver | Implemented |
| Distributed concurrency and throttling | Implemented |
| Policy-driven concurrency admission | Implemented |
| Redis ZSET lease-based concurrency gate | Implemented |
| Execution control state | Implemented |
| Pause / resume / cancel | Implemented |
| Waiting for human input | Implemented |
| Submit human input | Implemented |
| Runtime queue control | Implemented |
| Queue pause / resume | Implemented |
| Queued run cancellation | Implemented |
| Running run cancellation bridge | Implemented |
| Hot enqueue | Implemented |
| SharedRunId / RunId / ExecutionId separation | Implemented / validated |
| Terminal snapshots | Foundation available |
| Replay restoration | Completed (V1) |
| Replay validation and fingerprint verification | Completed (V1) |
| Replay metadata, ledger and timeline diagnostics | Completed (V1) |
| Replay / ledger / trace proof across process boundaries | Implemented / validated |
| Runtime recovery forensics | Implemented / validated |
| Control-plane causal-chain ledger proof | Implemented / validated |
| Safe tenant non-impact validation | Implemented / validated |
| Stable recovery scale-out single-flight deduplication | Implemented / validated |
| Durable crash-gate process-host validation | Implemented / validated |
| Parallel HTTP/gRPC process-host concurrency campaign through P35 | Implemented / validated; P35 is the experimental local-machine edge |
| Content-agnostic step execution boundary | Implemented architecture foundation |
| Runtime metrics and tracing foundations | Foundation available |
| Enterprise runtime demo scenarios | Completed (V1) |
| MCP production runtime scenario framework | Implemented / validated |
| HTTP process-host production scenarios | Implemented / validated |
| Road to MLOps direction | Platform direction |

The most important recent hardening is the transition from simple runtime execution validation to process-boundary recovery validation.

The runtime now proves that real external runtime processes can die while the control plane preserves durable execution truth, restores assigned work, avoids cross-tenant leakage, and produces replayable evidence after recovery.

---

## Phase 0 — Documentation Restructure

**Status:** Completed (V1)

Goal: make the repository readable, credible, and easy to navigate without losing technical depth.

Completed V1 work:

- preserved the original technical README as `docs/runtime-internals.md`
- replaced the root `README.md` with a shorter project entry page
- created `docs/index.md`
- created `docs/enterprise-readiness.md`
- created `docs/roadmap.md`
- created `docs/road-to-mlops.md`
- created focused AI runtime documentation under `docs/ai/`
- added architecture documentation centered around control-plane/runtime separation, context resolution, durable identity, observability, and recovery
- added a documentation map linking strategic, technical, validation, and roadmap documents
- preserved the original technical depth while making the repository easier to navigate

Focused AI runtime documentation created or expanded in V1:

- `docs/ai/architecture-overview.md`
- `docs/ai/distributed-execution.md`
- `docs/ai/execution-control-state.md`
- `docs/ai/runtime-queue-control.md`
- `docs/ai/retry-and-recovery.md`
- `docs/ai/retention-and-compaction.md`
- `docs/ai/distributed-concurrency-throttling.md`
- `docs/ai/replay-and-audit.md`
- `docs/ai/observability.md`
- `docs/ai/observability-tracing.md`
- `docs/ai/runtime-metrics.md`
- `docs/ai/testing-strategy.md`
- `docs/ai/config-driven-runtime.md`
- `docs/ai/policy-driven-execution.md`
- `docs/ai/context-resolution-and-helpers.md`
- `docs/ai/step-plugins.md`
- `docs/ai/rag-pipelines.md`
- `docs/ai/runtime-control-plane.md`
- `docs/ai/runtime-discovery-registry-capacity.md`
- `docs/ai/http-runtime-provider.md`
- `docs/ai/mcp-production-runtime-scenario-framework.md`
- `docs/ai/multi-tenant-control-plane-isolation.md`
- `docs/ai/multi-tenant-runtime-flow.md`
- `docs/ai/runtime-process-crash-recovery.md`
- `docs/ai/runtime-recovery-forensics.md`
- `docs/ai/multi-tenant-runtime-crash-isolation.md`
- `docs/ai/control-plane-ledger-causal-chain.md`
- `docs/ai/recovery-replay-ledger-trace-proof.md`

Future documentation refinement may continue, but the first documentation restructure is complete.

---

## Phase 1 — Enterprise Demo

**Status:** Completed (V1)

Goal: build a demo that clearly answers enterprise AI execution questions.

Implemented demo capabilities include:

- deterministic DAG execution
- distributed workers
- retry and recovery
- retention and compaction pressure
- replay validation
- distributed throttling
- readable runtime logs
- pause/resume/cancel controls
- interactive console execution
- runtime progress monitoring
- deterministic convergence scenarios

Implemented executable scenarios include:

```text
json
chaos-100
chaos-500
throttling-100
```

The demo validates:

- distributed execution behavior
- runtime coordination
- retry and stale step recovery
- retention pressure
- replay restoration
- distributed provider throttling
- deterministic convergence
- execution control state

Future refinements may continue, but the first enterprise demo phase is complete.

---

## Phase 1B — Control-Plane and Process-Host Runtime Validation

**Status:** Completed / Implemented

Goal: prove that the runtime can operate through a real control-plane and provider-hosting model, not only through in-process execution.

Validated capabilities include:

- MCP control-plane host mode
- local runtime pool mode
- HTTP runtime provider mode
- real `RuntimeInstanceOnly` process-host scale-out
- Redis-backed shared run store
- Redis-backed shared queue
- Redis-backed runtime registry
- Redis-backed runtime capacity store
- Redis-backed admission reservation store
- Redis-backed scale-out request store
- scale-out watcher lifecycle
- fulfilled scale-out run requeue
- dispatch-time admission
- tenant-aware Shared / Dedicated / Hybrid runtime visibility
- process-boundary retention, ledger, trace, and replay validation

This phase is important because it moves the runtime from an engine proof into a control-plane/runtime-infrastructure proof.

The validated flow is:

```text
submit run
    ↓
admission sees no safe capacity
    ↓
scale-out request is persisted
    ↓
watcher observes request
    ↓
provider is selected
    ↓
Runtime Host Manager starts a real runtime process
    ↓
runtime registers and publishes capacity
    ↓
shared run is requeued
    ↓
shared queue pump performs normal dispatch
    ↓
runtime executes DAG
    ↓
ledger / trace / replay proof remains available
```

The key architectural boundary is that capacity creation does not complete recovery by itself.

Execution recovery is complete only when assigned work has been reconciled, redispatched or resumed, and observable evidence has been written.

---

## Phase 1C — Runtime Process Crash Recovery

**Status:** Completed / Implemented

Goal: prove that the runtime can recover work after real runtime process failure without relying on volatile local queue state.

Validated crash recovery behavior includes:

- real external runtime process kill
- runtime endpoint failure signal
- runtime health reconciliation
- unsafe capacity suppression
- execution recovery reconciliation
- recovery of in-flight DAG executions
- redispatch of local queued shared runs
- replacement runtime selection
- preserved durable execution identity for in-flight work
- replay / ledger / trace validation after recovery
- runtime recovery forensics
- tenant-scoped recovery evidence
- safe tenant non-impact validation

The core identity model is:

```text
ExecutionId
    = durable DAG execution identity

SharedRunId
    = durable shared submission identity

LocalRunId
    = runtime-local queue attempt identity
```

An in-flight execution is recovered by resuming the same `ExecutionId`.

A local queued run that never created an `ExecutionId` is recovered by redispatching the durable `SharedRunId`.

The local runtime queue remains intentionally volatile. Durable truth is held by:

- shared run store
- shared queue
- runtime run execution index
- DAG store
- registry/capacity state
- ledger, trace, forensics, and replay evidence

The validated multi-tenant crash scenario proves that two impacted tenants can recover while a safe tenant remains untouched.

---


## Phase 1D — Concurrency Hardening and Adversarial Recovery Validation

**Status:** Completed / validated

Goal: prove that process-host recovery remains correct when control planes, tenants, external runtime processes, Redis coordination, MongoDB evidence, claims, leases, scale-out, redispatch, and process kills are concentrated under parallel pressure.

Validated capabilities include:

- stable single-flight recovery scale-out identity;
- readiness as registration, capacity publication, endpoint reachability, and dispatchability;
- exact pre-crash assigned-work inventory;
- durable crash-gate state instead of elapsed-time process termination;
- in-flight resume with the same `ExecutionId`;
- local-queued redispatch through the same `SharedRunId`;
- safe-tenant non-impact;
- failure classification between infrastructure saturation, runtime lifecycle, recovery convergence, and harness races;
- HTTP and gRPC P35 completion;
- datastore pressure measurement for the HTTP P35 run;
- production interpretation based on warm runtime pools rather than one tenant per process or pod.

The detailed technical reference is:

- [`docs/ai/concurrency-hardening-and-adversarial-validation.md`](ai/concurrency-hardening-and-adversarial-validation.md)

---

## Phase 1E — Runtime Pool Architecture and Exact Failure Recovery

Status: **Completed for ProcessHostPool and KubernetesPool correctness foundations**

Delivered:

- first-class `PoolId`, `HostId`, membership, draining, and independent `RuntimeInstanceId` identities;
- process-host Runtime Pool Manager with several real `RuntimeInstanceOnly` child processes;
- stable protocol-neutral route registry with immutable `RouteId`;
- forwarding leases and graceful route draining;
- stable HTTP pool endpoint reusing existing command DTOs;
- stable gRPC pool endpoint reusing the existing generated service and envelopes;
- exact routing with no sibling fallback;
- real A1 failure and A4 targeted replacement;
- first-class `FailureId` journal;
- exact capacity suppression;
- A1-only assigned-work enumeration;
- deterministic inventory fingerprints;
- atomic `ClaimId` recovery authority;
- unique active `LeaseId` generations;
- stale-lease rejection;
- claimed recovery through existing ownership and transition services.

Validated regressions:

```text
Process HTTP P10
Process gRPC P10
Kubernetes HTTP P5
Kubernetes gRPC P5
```

The Kubernetes P5 regression validates compatibility with the historical one-runtime-per-Pod mode. Separate HTTP/gRPC KubernetesPool production proofs validate multi-runtime Pods, hierarchical child/Pod recovery, warm reuse, and bounded capacity.

See:

- [`ai/runtime-pool-architecture.md`](ai/runtime-pool-architecture.md)
- [`ai/runtime-pool-failure-recovery.md`](ai/runtime-pool-failure-recovery.md)
- [`product-roadmap/runtime-pool-roadmap.md`](product-roadmap/runtime-pool-roadmap.md)

---

## Phase 2 — Real Enterprise Sample

**Status:** Planned

Goal: create a realistic enterprise workflow sample using the runtime.

Possible scenarios:

- candidate/job matching workflow
- document review and approval workflow
- compliance decision pipeline
- multi-provider RAG workflow
- human approval workflow with audit trail
- policy-driven provider/model governance workflow
- multi-tenant execution sample with isolated runtime capacity
- operational incident sample showing recovery proof after runtime failure

The sample should show how the runtime applies to real business processes, not only synthetic tests.

The stronger sample will likely combine:

```text
long-running AI workflow
    +
human control
    +
provider governance
    +
tenant isolation
    +
runtime failure recovery
    +
replayable audit evidence
```

---

## Phase 3 — Observability Dashboard

**Status:** Foundations available / Planned polish

Goal: expose runtime behavior visually.

Current observability foundations already include:

- runtime metrics
- trace recording
- runtime events
- retry diagnostics
- stale step recovery diagnostics
- runtime crash recovery diagnostics
- runtime recovery forensics
- control-plane causal-chain proof
- retention diagnostics
- concurrency admission diagnostics
- replay diagnostics
- ledger diagnostics
- process-boundary trace validation
- execution progress monitoring

Future dashboard capabilities may include:

- execution list
- DAG visualization
- step status inspection
- retry timeline
- stale step recovery timeline
- runtime crash recovery timeline
- recovery incident view
- safe tenant non-impact view
- control-plane causal-chain view
- runtime fleet and capacity view
- retention and compaction events
- resolver and context-resolution diagnostics
- concurrency admission decisions
- provider/model throttling visibility
- replay and snapshot visibility
- execution control actions
- tenant-scoped operational views

This phase is partially implemented through runtime observability foundations, but visual operational tooling remains planned.

---

## Phase 4 — Kubernetes Deployment and Runtime Operations

**Status:** Planned

Goal: provide a local or demo-ready distributed deployment.

Expected infrastructure:

- Redis
- MongoDB
- optional RabbitMQ or command queue infrastructure
- optional logging stack
- optional dashboard stack
- control-plane process/pod
- runtime worker instances or runtime-only hosts
- sample API or controller

This phase should prove that the runtime can run as distributed infrastructure, not only as local integration tests.

The important point is that Kubernetes should not replace the runtime recovery model.

Kubernetes may restart or schedule containers, but the runtime still owns:

```text
execution state
assigned work recovery
shared run lifecycle
DAG resume
tenant-aware capacity selection
replay / ledger / trace proof
```

Broader Kubernetes deployment and operations work must preserve the same provider scale-out and recovery boundaries already validated through real Kubernetes and KubernetesPool scenarios.

---

## Phase 4B — Kubernetes Runtime Pool and Hierarchical Capacity

Status: **Implemented / validated for bounded Runtime Pool correctness; broader multi-node scaling remains ongoing**

Delivered capabilities:

- explicit additive `KubernetesPool` host mode;
- one in-Pod pool manager with several independent runtime processes;
- Pod UID / host-incarnation failure-boundary identity;
- independent `RuntimeInstanceId` children;
- HTTP and gRPC transport preservation;
- child runtime kill and replacement while the Pod survives;
- exact sibling identity preservation;
- full distinct Pod deletion and replacement;
- exact five-member Pod recovery in the validated topology;
- bounded 3-Pod × 5-runtime capacity;
- warm-runtime and existing-Pod reuse before replacement capacity;
- two complete warm cycles without intermediate cleanup;
- replay, ledger, lifecycle, trace, and forensics proof;
- final deterministic Pod cleanup.

Remaining infrastructure hardening:

- durable recovery-claim ownership and completion across multiple control planes;
- Redis Cluster key-slot and failover validation;
- multi-node and fault-domain stress;
- cluster autoscaler integration;
- production deployment packaging and managed operational profiles.

See:

- [`ai/runtime-pool-production-validation.md`](ai/runtime-pool-production-validation.md)
- [`ai/runtime-pool-failure-authority.md`](ai/runtime-pool-failure-authority.md)
- [`ai/runtime-lifecycle-journal.md`](ai/runtime-lifecycle-journal.md)

---

## Phase 5 — Public API / SDK Polish

**Status:** Planned

Goal: make the runtime easier to consume from external applications.

Possible work:

- cleaner execution API
- stable request/response contracts
- SDK-friendly abstractions
- clearer controller APIs
- replay controller contracts
- recovery incident query contracts
- forensics query contracts
- control-plane causal-chain query contracts
- better examples
- public helper/context resolver documentation
- CLI or developer utilities

The runtime internals should remain powerful, but the external entry points should become simpler.

---

## Phase 6 — Deterministic Replay Engine and Audit Foundations

**Status:** Completed (V1)

Goal: provide a deterministic replay engine that can validate, inspect, and restore persisted AI executions without re-running external providers, LLM calls, tools, or side effects.

Completed V1 capabilities include:

- replay by `ExecutionId`
- audit-only replay validation
- snapshot-based replay restoration
- deterministic fingerprint validation
- original fingerprint versus reconstructed fingerprint comparison
- replay metadata exposure
- replay validation reports
- replay issue reporting
- step-level replay reports
- dependency graph validation
- final step-state validation
- payload reference validation
- archived / compacted / evicted payload reference validation
- replay ledger events
- replay timeline diagnostics
- replay tracing
- exception-safe replay failure recording
- compatible existing execution detection
- restore into the authoritative runtime store
- DAG-store-aware replay restore support
- distributed replay integration testing
- 100-step replay reference scenario with ledger and timeline diagnostics
- process-boundary replay report validation through MCP scenarios
- replay / ledger / trace proof after runtime process crash recovery

Replay V1 proves that the runtime can reconstruct and validate a completed distributed execution from durable state while preserving deterministic convergence guarantees.

Future refinements may continue, but the replay engine and audit foundations are complete as a first version.

---

## Phase 7 — Replay Controller, Recovery APIs, HTTP APIs, Dashboard, and Operational Tooling

**Status:** Planned

Goal: expose the completed replay, recovery, and forensics foundations through operational entry points that can be used by APIs, CLIs, dashboards, Kubernetes operators, and future audit tooling.

Possible features:

- `IAiExecutionReplayController`
- `IAiRuntimeRecoveryForensicsController`
- `IAiControlPlaneLedgerController`
- replay controller request/response contracts
- runtime recovery incident contracts
- recovery forensics search by `ExecutionId`, `SharedRunId`, `LocalRunId`, tenant, runtime instance, or incident id
- HTTP replay API
- HTTP recovery incident API
- replay summary endpoints
- replay audit endpoints
- replay restore endpoints
- replay ledger endpoints
- replay timeline endpoints
- control-plane causal-chain endpoints
- replay dashboard
- recovery dashboard
- runtime incident dashboard
- replay search by `ExecutionId`
- replay search by pipeline/date/fingerprint
- replay export to JSON or Markdown
- recovery export to JSON or Markdown
- replay operational tooling for support and incident investigation
- recovery operational tooling for support and incident investigation
- replay and recovery access control
- replay-safe context resolution documentation
- integration with future control plane and Kubernetes runtime operations

This phase should avoid coupling the core runtime library directly to ASP.NET.

Controller abstractions should be created first, then HTTP/API hosting can be added around them.

---

## Phase 8 — Cost and Provider Governance

**Status:** Planned

Goal: add governance around AI provider usage and cost.

Possible capabilities:

- provider budgets
- model budgets
- token or request accounting
- cost-aware throttling
- provider fallback policies
- per-tenant limits
- tenant cost attribution
- execution cost visibility
- provider/model/operation context-based cost reporting
- policy-driven cost controls
- retry cost impact
- recovery cost impact
- failed provider call accounting

This extends the existing distributed throttling, context resolution, and policy model into AI cost governance.

---

## Phase 9 — Articles / Public Positioning

**Status:** Current / Planned

Goal: explain the architectural ideas publicly without overstating maturity.

Potential topics:

- AI orchestration as a distributed systems problem
- deterministic convergence for AI workflows
- when the process dies, the execution does not
- context resolution as the connective tissue of AI execution runtimes
- Redis Lua coordination for distributed AI execution
- retry and recovery without hidden local loops
- policy-driven throttling for AI providers
- bounded memory for long-running AI pipelines
- pause/resume/cancel for production AI workflows
- why AI runtimes need replay and auditability
- why local queues can be volatile when durable truth lives elsewhere
- why runtime crash recovery needs evidence, not only restart logic
- how multi-tenant runtime isolation changes recovery expectations
- why P20/P30/P35 are intentionally more violent than production
- why capacity can degrade before correctness breaks
- why the runtime owns execution semantics rather than model semantics

The goal is to position the project seriously and clearly.

The strongest message is not that the runtime replaces existing tools.

The stronger message is that production AI execution needs explicit runtime guarantees:

```text
durable state
safe ownership
recoverable execution
bounded memory
tenant-aware control
explainable evidence
```

---

## Long-Term Platform Direction

The roadmap above tracks the current runtime foundation and enterprise demo evolution.

The project should not be interpreted as a finished product. The deterministic runtime core is a foundation for a broader AI execution and MLOps-oriented platform.

The long-term direction is to evolve from:

```text
runtime engine
```

toward:

```text
AI execution infrastructure
AI operations control plane
MLOps-oriented runtime platform
enterprise AI governance layer
```

This broader direction includes areas such as:

- AI execution infrastructure
- enterprise AI orchestration
- runtime governance
- replay and audit systems
- recovery and incident forensics
- distributed AI operations
- multi-agent coordination
- execution observability
- AI memory and decision systems
- provider governance and cost control
- tenant-aware runtime controls
- runtime fleet operations
- MLOps-oriented runtime infrastructure

See:

- [`docs/road-to-mlops.md`](road-to-mlops.md)

This keeps the current roadmap focused while documenting the larger platform ambition.

---

## Guiding Principles

All roadmap work should respect these principles:

- do not break deterministic guarantees
- do not hide critical execution behavior in local loops
- keep state explicit
- keep workers stateless
- preserve Redis atomic coordination for distributed safety
- separate hot state from durable payloads
- treat local runtime queues as volatile
- keep durable execution truth outside the runtime process
- preserve replayability
- preserve recovery evidence
- keep context resolution explicit and testable
- avoid scattering context-building logic across the engine
- separate health reconciliation from execution recovery
- keep providers responsible for transport, not durable recovery ownership
- maintain clear documentation
- avoid overclaiming maturity
- distinguish implemented features from foundations and planned work
- do not map tenants one-to-one to processes or pods in the production capacity model
- distinguish local saturation from protocol correctness
- prefer durable crash preconditions over elapsed-time test assumptions

---

## Runtime Pool Delivery Status

| Workstream | Status |
|---|---|
| Process-host pool identity | Completed |
| Process-host pool lifecycle | Completed |
| Stable HTTP exact routing | Completed |
| Stable gRPC exact routing | Completed |
| Targeted child replacement | Completed |
| Exact failure journal and suppression | Completed |
| Exact assigned-work enumeration | Completed |
| Deterministic recovery claim | Completed |
| Claimed recovery transitions | Completed |
| Real process-host final proof | Completed |
| Existing Process/Kubernetes regression | Completed |
| Kubernetes Runtime Pool Pod | Completed / validated over HTTP and gRPC |
| Pod-wide failure proof | Completed / validated |
| Bounded runtime/host hierarchical capacity selection | Completed / validated; multi-node expansion ongoing |
| Redis Cluster compatibility | Planned |

---

## Current Priority

The current priorities are:

```text
Enterprise demo polish
Runtime Pool production-proof documentation
Recovery / replay / ledger / lifecycle / trace documentation
Observability polish
Replay and recovery controller/API design
Road to MLOps platform direction
Kubernetes deployment demo
Articles and public positioning
```

The next capacity-hardening priorities are:

```text
Runtime Pool Manager design
Warm runtime process reuse inside pool pods
Tenant-aware cell and catalog direction
Bounded process-level scale-out
Kubernetes runtime-pool continuity
```

Phase 0 documentation restructure is complete as V1.

The runtime foundations are already implemented and validated through distributed integration scenarios, MCP control-plane scenarios, HTTP process-host scenarios, replay/ledger/trace scenarios, and runtime crash recovery scenarios.

The focus is now shifting toward operational polish, clearer API/controller surfaces, recovery and replay tooling, MLOps-oriented platform direction, Kubernetes continuity, and public positioning.

The dedicated long-term platform direction is documented in [`docs/road-to-mlops.md`](road-to-mlops.md).
