# Deterministic AI Runtime

A deterministic, multi-tenant .NET runtime for **durable AI workflow execution** across local workers, real external processes, reusable Runtime Pools, and Kubernetes-hosted runtime instances.

Most AI tooling starts at prompts, agents, and RAG. This runtime starts one layer down, at **execution**:

> **Who owns the work, what survives a crash, what may execute again, and can the result be replayed and audited afterward?**

It provides durable DAG execution, Redis-backed coordination, provider-based dispatch, bounded reusable capacity, crash recovery, deterministic replay, tenant isolation, and canonical event observation behind one shared control plane. The engine does not judge the answer; it guarantees the lifecycle of the execution that produced it — an LLM call, a RAG step, an MCP tool, a database command, a human approval, or any HTTP/gRPC workload.

[![Version](https://img.shields.io/badge/Version-0.0.8.5-blue)](./CHANGELOG.md)
[![Changelog](https://img.shields.io/badge/Changelog-view-lightgrey)](./CHANGELOG.md)
![AI Runtime](https://img.shields.io/badge/AI-Deterministic%20Execution-purple)
![Runtime](https://img.shields.io/badge/Runtime-distributed-brightgreen)
![Child DAG](https://img.shields.io/badge/Child%20DAG-Validated-brightgreen)
![Matrix](https://img.shields.io/badge/Adversarial%20Matrix-36%2F36-brightgreen)
![Observation](https://img.shields.io/badge/Observation-EventDriven-brightgreen)
![Redis](https://img.shields.io/badge/Redis-required-red?logo=redis)
![MongoDB](https://img.shields.io/badge/MongoDB-required-green?logo=mongodb)
![Kubernetes](https://img.shields.io/badge/Kubernetes-supported-326CE5?logo=kubernetes&logoColor=white)
![HTTP](https://img.shields.io/badge/Transport-HTTP-0A66C2)
![gRPC](https://img.shields.io/badge/Transport-gRPC-244C5A)
![Status](https://img.shields.io/badge/Status-active%20development-orange)
![License](https://img.shields.io/badge/License-BSL%201.1-lightgrey)

📄 **Validation methodology and evidence:** [Adversarial Runtime Validation Matrix](docs/ai/adversarial-runtime-validation-matrix.md) — how correctness here is validated as an invariant, not a single green run, with the full 36-row evidence archive.

## Start here

- **Complete documentation:** [docs/index.md](docs/index.md)
- **Interactive AI Runtime Analysis Demo:** [demo/rbac-aiAnalysis/nextjs/README.md](demo/rbac-aiAnalysis/nextjs/README.md)
- **Installation / local Kubernetes:** [Kubernetes / Minikube installation and recovery guide](docs/ai/kubernetes-local-environment.md)
- **Architecture:** [Architecture overview](docs/ai/architecture-overview.md)
- **Runtime Pools:** [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md)
- **Recovery:** [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)
- **Replay and audit:** [Replay and audit](docs/ai/replay-and-audit.md)

---

## What it proves today

| Area | Evidence |
|---|---|
| Deterministic execution | Dependency-aware DAG execution with durable state, claims, retry, recovery, retention, replay, and deterministic convergence. |
| Durable Child DAGs | `WaitingForExternal` without holding capacity, deterministic child identity, continuation, same-parent recovery, recursive `ChildDepth = 3`. |
| Recursive exactness | Nested child logical-step exactness is verified. Dedicated recursive-child replay remains `NOT_EVALUATED`. |
| Hosting | Local, Process, Kubernetes, ProcessHostPool, KubernetesPool — HTTP and gRPC, exact `RuntimeInstanceId` routing. |
| Runtime Pools | Bounded reusable capacity, warm reuse, child-failure isolation, full-boundary recovery. |
| Deterministic recovery | Same-`ExecutionId` in-flight resume; durable shared-state redispatch for queued work; claim-protected mutation. |
| Durable authority | Failure journal, append-only Lifecycle Journal, Ledger, trace, Recovery Forensics — independent stores correlated by first-class identities. |
| Event-driven lifecycle | Canonical engine facts through one Event Manager and central projection catalog; no second bus. |
| Multi-tenancy | RBAC context survives async dispatch; tenant-scoped admission, capacity, recovery, Ledger, replay, and Forensics. |

Configuration and policy drive retry, retention, concurrency, admission, isolation, hosting, and recovery — without engine rewrites.

---

## Validation evidence

### 36 / 36 deterministic adversarial matrix

Four provider/transport combinations × nine deterministic failure schedules — all green:

| Runtime model | gRPC | HTTP |
|---|---:|---:|
| KubernetesPool | 9 / 9 | 9 / 9 |
| ProcessHostPool | 9 / 9 | 9 / 9 |

Rows: `Baseline`, `CrashEarly`, `ChildInvocationBoundary`, `ContinuationConsume`, `Depth2RuntimeFailure`, `Depth3RuntimeFailure`, `SeedA`, `SeedB`, `SeedC`.

```text
72 execution cycles          1,296 / 1,296 parent runs completed
66,096 parent logical steps  196,992 recursive child logical steps
0 missing child steps         0 unexpected duplicate child steps
0 ownership-transition violations
1,296 / 1,296 parent replay proofs
288 recovered SharedRuns      72 / 72 process-kill identity-continuity proofs
```

Raw xUnit artifacts — one per row, each with a distinct SHA-256 — are archived at `docs/files/adversarial-runtime-validation-logs.zip`:

```text
SHA-256 = a8e252b2b7277c196d594f0da6963b2e39eab3ad0e2a6415306974d2a8497c03
```

Every number above is **independently recomputable** from that archive: each row's invariants can be extracted directly from its raw log — you do not have to trust this summary. The matrix proves the **selected** deterministic schedules; it does not claim exhaustive exploration of every possible interleaving. See the [evidence index](docs/ai/adversarial-runtime-validation-evidence-index.md).

**Recursive proof scope (per row):**

```text
36 parents · 108 recursive child executions · 5,472 child logical steps · depth 3
Root-parent step exactness      VERIFIED
Nested child-step exactness     VERIFIED
Nested Child DAG terminality    VERIFIED
Parent replay                   VERIFIED
Dedicated recursive-child replay  NOT_EVALUATED
```

**Historical P35 stress campaign:** both HTTP and gRPC completed 35 / 35 — 105 tenants, 315 real DAG executions, 70 real process kills, 210 recovered jobs per transport (HTTP batch: ~4.19M datastore ops, 18.29 GiB). P35 is the experimental edge of the tested machine, not a universal throughput guarantee.

---

## Quick start

**Prerequisites:** a compatible .NET SDK, Redis, MongoDB, Docker, and Kubernetes for K8s-backed scenarios.

```powershell
dotnet build implementations/dotnet/Multiplexed.sln
dotnet test  implementations/dotnet/Tests/Multiplexed.AI.Tests/Multiplexed.AI.Tests.csproj
dotnet test  implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Multiplexed.AI.McpServer.Tests.Integration.csproj
```

Long-running ProcessHostPool and KubernetesPool proofs run with targeted filters and reachable Redis/MongoDB/Kubernetes. The local Kubernetes image, namespace, and pull-policy contract are defined in `KubernetesSdkScenarioConstants.cs` (the source of truth). Full bootstrap: **[Kubernetes / Minikube guide](docs/ai/kubernetes-local-environment.md)**.

---

## Deep reference

The architecture, identity model, recovery semantics, replay, observability, and full capability matrix are below — collapsed so this page stays scannable. Expand what you need; each section mirrors a dedicated document under [`docs/`](docs/index.md).

<details>

<summary><b>Architecture at a glance &amp; identity model</b></summary>

## Architecture at a glance

```text
Client / API / MCP
        ↓
RBAC ExecutionContext
        ↓
Durable ExecutionContextSnapshot
        ↓
Shared Runtime Controller
        ↓
Shared Run Store / Shared Queue
        ↓
Tenant-Aware Admission
        ↓
Registry / Capacity / Reservations
        ↓
Provider Selection
        ↓
Local / HTTP / gRPC Provider
        ↓
Runtime Host Manager
        │
        ├── Local
        ├── Process
        ├── Kubernetes
        ├── ProcessHostPool
        └── KubernetesPool
                ↓
        exact RuntimeInstanceId
                ↓
DAG Execution Engine
        ↓
Redis Hot State + Lua Coordination
        ↓
Step Executors / Plugins
        ↓
MongoDB Payloads / Snapshots / History
        ↓
Canonical Engine Event
        ↓
Existing Event Manager
        ↓
Central Projection Catalog
        ├── Decision Ledger
        ├── Recovery Forensics
        ├── Runtime Lifecycle Journal
        ├── Metrics
        ├── Logging
        └── Realtime
                ↓
Replay / Trace / Deterministic Lifecycle Observation
```

The architecture deliberately separates:

```text
logical execution identity
physical execution attempt
runtime capacity identity
transport route
infrastructure failure boundary
durable recovery authority
```

That separation is what makes deterministic recovery possible.

---

## Identity model

Important identities include:

| Identity | Responsibility |
|---|---|
| `TenantId` | Durable tenant-isolation boundary. |
| `TenantGroupId` | Tenant grouping and shared-isolation context. |
| `SharedRunId` | Durable shared work identity. |
| `LocalRunId` | Runtime-local physical attempt. |
| `ExecutionId` | Durable DAG execution identity. |
| `RuntimeInstanceId` | Independently selectable runtime capacity. |
| `WorkerId` | Worker execution identity. |
| `PoolId` | Logical reusable Runtime Pool. |
| `HostId` | Parent hosting-boundary identity. |
| `KubernetesPodUid` | Kubernetes failure-boundary identity. |
| `RouteId` | Exact transport-route incarnation. |
| `FailureId` | Durable failure observation. |
| `ClaimId` | Deterministic recovery-claim identity. |
| `LeaseId` | Active claim generation. |
| `CorrelationId` | Cross-component correlation. |
| `CausationId` | Causal relationship. |

Critical invariant:

```text
ExecutionId is not RuntimeInstanceId.
RuntimeInstanceId is not HostId.
HostId is not Pod UID.
Pod UID is not a transport route.
LocalRunId is not the durable execution.
```

A physical execution attempt may be replaced while the logical execution remains the same.

---

</details>

<details>

<summary><b>Deterministic DAG execution &amp; durable Child DAG composition</b></summary>

## Deterministic DAG execution

The DAG engine provides:

- dependency resolution;
- durable step state;
- atomic claims;
- retries;
- stale-work recovery;
- deterministic convergence;
- pause;
- resume;
- cancellation;
- human input;
- terminal snapshots;
- payload externalization;
- retention and compaction;
- replay evidence;
- execution-correlated decisions.

A failed physical attempt may start work that is retried later.

Correctness is based on durable logical identities and transitions, not simplistic process-level invocation counts.

See [Architecture overview](docs/ai/architecture-overview.md).

---

## Durable Child DAG composition

A parent DAG can delegate to another durable DAG execution, release runtime capacity while waiting, and resume through a deterministic continuation.

```text
Parent ExecutionId
        ↓
ExecuteChildDag
        ↓
durable child relation
        ↓
Child ExecutionId
        ↓
parent step = WaitingForExternal
        ↓
parent runtime capacity released
        ↓
child executes / retries / recovers
        ↓
child terminal result frozen
        ↓
deterministic continuation scheduled
        ↓
same parent ExecutionId resumes
```

The Child DAG path reuses the existing DAG engine, Shared Run Store, Shared Queue, runtime providers, policy boundaries, recovery ownership, Ledger, tracing, lifecycle, and Forensics.

It does not introduce a second orchestration engine, Child-DAG-specific queue, second event bus, or alternate recovery model.

### Deterministic child identity

```text
ParentExecutionId
        ↓
ParentCallSiteId
        ↓
ChildInvocationKey
        ↓
ChildExecutionId
        ↓
ContinuationId
        ↓
Continuation SharedRunId
```

### Waiting without holding runtime capacity

`WaitingForExternal` is durable.

The parent does not keep a physical runtime slot occupied while the child runs.

### Continuation semantics

Call-site terminality alone does not imply that a continuation has been consumed.

A `Completed` call-site can legitimately coexist with a still-`Scheduled` continuation while the parent remains non-terminal.

That distinction matters during crash recovery and deterministic redrive.

### Recursive validation

Current validation reaches:

```text
ChildDepth = 3
```

The proof evidence is explicitly bounded at depth 3.

The runtime may use the same execution contract for deeper nesting, but deeper-depth validation is not claimed by the current proof matrix.

See [Durable Child DAG composition](docs/ai/child-dag-composition.md).

---

</details>

<details>

<summary><b>Runtime hosting models, ProcessHostPool, KubernetesPool &amp; exact routing</b></summary>

## Runtime hosting models

| Mode | Boundary model | Transport | Reusable capacity | Status |
|---|---|---|---:|---|
| Local | In-process runtime | Local | N/A | Implemented |
| Process | One external runtime process | HTTP / gRPC | No | Implemented / validated |
| Kubernetes | One runtime instance per Pod/Service | HTTP / gRPC | No | Implemented / validated |
| ProcessHostPool | Parent ProcessHost containing multiple runtimes | HTTP / gRPC | Yes | Implemented / validated |
| KubernetesPool | Kubernetes Pod containing multiple runtimes | HTTP / gRPC | Yes | Implemented / validated |

The hosting model changes physical placement.

It does not change the logical execution contract.

---

## ProcessHostPool

ProcessHostPool provides bounded reusable capacity across real external parent processes.

```text
Logical ProcessHostPool
    ├── ProcessHost A
    │      ├── Runtime A1
    │      ├── Runtime A2
    │      └── Runtime A3
    │
    ├── ProcessHost B
    │      ├── Runtime B1
    │      ├── Runtime B2
    │      └── Runtime B3
    │
    └── ProcessHost C
           ├── Runtime C1
           ├── Runtime C2
           └── Runtime C3
```

Each child runtime has its own `RuntimeInstanceId`.

The parent ProcessHost is a hosting and failure boundary, not an execution identity.

### Isolated child-runtime failure

```text
Runtime A2 dies
        ↓
ProcessHost A survives
        ↓
A1 and A3 remain valid
        ↓
exact failed runtime becomes unsafe
        ↓
affected work recovered
        ↓
replacement runtime restores membership
```

Healthy siblings are preserved.

### Full ProcessHost failure

```text
ProcessHost B dies
        ↓
B1 / B2 / B3 disappear
        ↓
exact failed membership identified
        ↓
durable failure recorded
        ↓
recovery candidates claimed
        ↓
replacement ProcessHost created
        ↓
replacement runtimes registered
        ↓
affected SharedRuns recovered
```

This creates a genuinely hierarchical failure model:

```text
child runtime failure
    ≠
full parent-boundary failure
```

Recovery scope follows the failure scope.

See [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md).

---

## KubernetesPool

KubernetesPool applies the same reusable Runtime Pool model inside a real Kubernetes failure boundary.

```text
Kubernetes Node
        ↓
Pod = infrastructure failure boundary
        ↓
in-Pod Runtime Pool
        ├── Runtime A1
        ├── Runtime A2
        ├── Runtime A3
        └── ...
```

The Pod does not become the execution identity.

Each in-Pod runtime remains independently registered and selectable by exact `RuntimeInstanceId`.

Validated behavior includes:

- multiple independent runtimes per Pod;
- bounded Pod count;
- bounded runtimes per Pod;
- HTTP and gRPC;
- exact in-Pod child-runtime failure;
- parent Pod survival during isolated child failure;
- healthy sibling preservation;
- exact child replacement;
- distinct fully busy Pod failure;
- external/manual Pod deletion;
- exact failed-Pod work recovery;
- warm Pod reuse;
- deterministic final cleanup.

For local setup, see the [Kubernetes / Minikube guide](docs/ai/kubernetes-local-environment.md).

---

## Exact routing

The control plane selects one exact `RuntimeInstanceId`.

```text
Control Plane
        ↓
selected RuntimeInstanceId
        ↓
provider
        ↓
exact transport route
        ↓
exact runtime
```

Transport routing is not allowed to silently substitute a sibling runtime.

Route identity and execution identity remain separate.

See:

- [HTTP runtime provider](docs/ai/http-runtime-provider.md)
- [gRPC runtime provider](docs/ai/grpc-runtime-provider.md)
- [Runtime discovery, registry, and capacity](docs/ai/runtime-discovery-registry-capacity.md)

---

</details>

<details>

<summary><b>Deterministic recovery, durable failure authority &amp; warm capacity reuse</b></summary>

## Deterministic recovery

### In-flight work

```text
SharedRunId
LocalRunId
ExecutionId
RuntimeInstanceId
        ↓
physical runtime dies
        ↓
durable failure authority
        ↓
exact candidate inventory
        ↓
recovery claim
        ↓
replacement RuntimeInstanceId
replacement LocalRunId
same ExecutionId
```

Core invariant:

```text
ExecutionIdBefore == ExecutionIdAfter
```

The physical attempt changes.

The durable logical execution does not.

### Durable queued work

A dead local queue is not durable recovery authority.

```text
dead process-local queue
    ≠ durable truth
SharedRunId
    ↓
shared durable state
    ↓
redispatch
```

### Claim-protected recovery mutation

Multiple coordinators may observe the same failure.

Only one recovery mutation authority should win.

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
InventoryFingerprint
ClaimId
LeaseId
```

Observation can be concurrent.

Mutation is claim-protected.

See [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md).

---

## Durable failure authority

Physical failure becomes a durable first-class fact.

```text
physical failure
        ↓
FailureId
        ↓
Runtime Pool Failure Journal
        ↓
exact failed membership
        ↓
candidate inventory
        ↓
recovery claim
        ↓
resume / redispatch
```

Failure state is not reconstructed solely from transient process logs, process exit codes, or registry snapshots.

See [Runtime Pool failure authority](docs/ai/runtime-pool-failure-authority.md).

---

## Warm capacity reuse

Healthy Runtime Pool capacity is reused between execution cycles.

```text
cycle 1
    ↓
create bounded pool
    ↓
execute + recover
    ↓
keep healthy converged capacity
cycle 2
    ↓
reuse warm pool
    ↓
execute + recover
    ↓
final deterministic cleanup
```

Replacement capacity is introduced because of actual failure, not merely because new work arrives.

---

</details>

<details>

<summary><b>Replay, evidence, ledger/forensics &amp; event-driven lifecycle observation</b></summary>

## Replay, audit, and durable evidence

The runtime persists enough evidence to reconstruct and validate execution after completion or recovery.

```text
terminal snapshot
    +
deterministic fingerprint
    +
DAG / step state
    +
payload references
    +
Decision Ledger
    +
Runtime Lifecycle Journal
    +
trace
    +
Recovery Forensics
        ↓
post-execution reconstruction and validation
```

Replay foundations include:

- audit-only replay;
- restore replay;
- deterministic fingerprint validation;
- replay metadata;
- Ledger loading;
- trace loading;
- lifecycle reconstruction;
- post-crash recovery replay proof.

Current proof boundary:

```text
Parent replay                     VERIFIED
Dedicated recursive-child replay NOT_EVALUATED
```

See [Replay and audit](docs/ai/replay-and-audit.md).

---

## Ledger, lifecycle, forensics, metrics, and realtime

Observability is intentionally split by responsibility.

```text
Decision Ledger
    → durable execution and decision evidence
Runtime Lifecycle Journal
    → append-only host / Pod / runtime / placement history
Recovery Forensics
    → work-item-level recovery timeline
Metrics
    → quantitative operational projection
Logging
    → operational diagnostics
Realtime
    → live event delivery
```

These surfaces are correlated through first-class identities but remain independent responsibilities.

See:

- [Runtime Lifecycle Journal](docs/ai/runtime-lifecycle-journal.md)
- [Runtime recovery forensics](docs/ai/runtime-recovery-forensics.md)
- [Recovery replay, Ledger, and trace proof](docs/ai/recovery-replay-ledger-trace-proof.md)

---

## Event-driven lifecycle architecture

Canonical engine facts flow through the existing Event Manager.

No parallel event bus is introduced.

```text
Engine semantic fact
        ↓
Canonical Event Namespace
        ↓
Existing Event Manager
        ↓
Central Projection Catalog
        ├── Decision Ledger
        ├── Recovery Forensics
        ├── Runtime Lifecycle Journal
        ├── Metrics
        ├── Logging
        └── Realtime
```

Architectural rule:

```text
ONE ENGINE FACT
=
ONE CANONICAL EVENT
=
ONE CANONICAL DECLARATION
=
ONE CENTRAL DISPATCH PATH
```

The Event Manager centralizes observation ownership without pretending that every projection shares one transactional boundary.

Projection durability can differ:

```text
RequiredDurable
ReplayableDurable
BestEffort
None
```

See [Engine event observation and lifecycle catalog](docs/ai/engine-event-observation.md).

---

## Deterministic EventDriven testing

Reference synchronization:

```text
durable evidence check
        ↓
subscribe to realtime canonical events
        ↓
durable evidence re-check
        ↓
await canonical event if still needed
        ↓
verify final durable state
```

Events are synchronization.

Durable stores remain correctness authority.

Hard watchdogs remain mandatory.

Historical polling paths remain where useful for compatibility and regression coverage.

See [Testing strategy](docs/ai/testing-strategy.md).

---

</details>

<details>

<summary><b>Configuration, policy, queue-first coordination &amp; RBAC security context</b></summary>

## Configuration-driven runtime

Runtime structure is configurable rather than hard-coded into the execution engine.

Configuration areas include:

- provider;
- transport;
- hosting mode;
- Runtime Pool bounds;
- queue behavior;
- retry;
- retention;
- concurrency;
- isolation;
- observability;
- persistence;
- admission;
- runtime-host settings.

The architectural separation is:

```text
Configuration
    defines runtime structure and operating parameters.
Policy
    decides what should happen for this execution.
Plugin
    performs domain work.
```

See [Configuration-driven runtime](docs/ai/config-driven-runtime.md).

---

## Policy-driven execution

Policies govern decisions such as:

- retry;
- retention;
- concurrency;
- admission;
- isolation;
- runtime selection;
- failure handling;
- recovery;
- execution control;
- resource pressure.

The engine remains responsible for deterministic state transitions.

Policies decide behavior within explicit boundaries.

See [Policy-driven execution](docs/ai/policy-driven-execution.md).

---

## Queue-first admission and distributed coordination

Submissions can enter durable shared state before runtime capacity becomes available.

```text
Client
    ↓
Shared Runtime Controller
    ↓
SharedRun
    ↓
Shared Queue
    ↓
Tenant-aware admission
    ↓
Runtime capacity
    ↓
dispatch
```

This allows the runtime to handle:

- bounded capacity;
- transient backpressure;
- runtime loss;
- redispatch;
- recovery;
- warm-pool reuse;

without losing the durable logical submission.

### Redis responsibilities

Redis provides hot coordination for:

- shared queue state;
- registry and capacity;
- claims;
- leases;
- reservations;
- atomic transitions;
- recovery coordination.

Lua scripts are used where atomic conditional transitions are required.

### MongoDB responsibilities

MongoDB provides durable state and evidence for areas such as:

- execution records;
- payloads;
- snapshots;
- lifecycle history;
- failure facts;
- Recovery Forensics.

Redis and MongoDB intentionally serve different responsibilities.

---

## Security, RBAC, and durable execution context

Authorization context is captured before asynchronous execution leaves the original request scope.

```text
API / MCP
        ↓
RBAC ExecutionContext
        ↓
durable ExecutionContextSnapshot
        ↓
SharedRun
        ↓
Shared Queue
        ↓
runtime dispatch
        ↓
background continuation / recovery
        ↓
RBAC context restored
```

This matters because the original request may no longer exist when:

- queued work starts;
- a Child DAG continuation resumes;
- a background reconciler operates;
- a runtime is replaced;
- recovery moves execution to new physical capacity.

Authorization failure and capacity pressure remain separate concerns.

See [Multi-tenant control-plane isolation](docs/ai/multi-tenant-control-plane-isolation.md).

---

</details>

<details>

<summary><b>Multi-tenant isolation &amp; pluggable execution</b></summary>

## Multi-tenant isolation

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

Tenant-aware behavior is validated across:

- registry;
- capacity;
- admission;
- reservations;
- queueing;
- runtime selection;
- scale-out;
- recovery;
- Ledger;
- replay;
- lifecycle;
- Recovery Forensics.

Supported isolation models include:

```text
Shared
Dedicated
Hybrid
```

Tenant ownership is typed, not inferred from names or diagnostic metadata.

---

## Pluggable execution

The runtime is content-agnostic.

A step can represent:

- LLM execution;
- RAG;
- MCP tools;
- database operations;
- human approval;
- HTTP services;
- gRPC services;
- file processing;
- polyglot processes;
- other domain-specific work.

The engine owns orchestration correctness.

Plugins own domain behavior.

See [Step plugins](docs/ai/step-plugins.md).

---

</details>

<details>

<summary><b>Full capability matrix</b></summary>

## Core capabilities

| Capability | Status |
|---|---:|
| Deterministic DAG execution | Implemented |
| Durable Child DAG composition | Implemented / validated |
| Recursive depth-3 validation | Validated |
| Exact recursive child logical-step accounting | Validated |
| Redis hot state and Lua coordination | Implemented |
| MongoDB durable state / evidence | Implemented |
| Distributed workers and step claims | Implemented |
| Retry and stale-work recovery | Implemented |
| Pause / resume / cancel / human input | Implemented |
| Retention / compaction / payload externalization | Implemented |
| Snapshot foundations | Implemented |
| Audit replay | Implemented |
| Restore replay | Implemented |
| Replay Ledger / trace evidence | Implemented / validated |
| Configuration-driven runtime | Implemented foundation |
| Policy-driven execution | Implemented |
| Pluggable execution | Implemented foundation |
| Decision Ledger | Implemented |
| Runtime Lifecycle Journal | Implemented / validated |
| Runtime Pool Failure Journal | Implemented / validated |
| Recovery Forensics | Implemented / validated |
| Metrics / tracing / realtime foundations | Implemented |
| Canonical engine-event observation | Implemented / validated |
| EventDriven lifecycle observer | Implemented / validated |
| RBAC execution-context propagation | Implemented / validated |
| Shared / Dedicated / Hybrid isolation | Implemented / validated |
| Registry / capacity / reservations | Implemented / validated |
| Shared Runtime Controller / shared queue | Implemented / validated |
| Local runtime provider | Implemented / validated |
| HTTP runtime provider | Implemented / validated |
| gRPC runtime provider | Implemented / validated |
| Kubernetes Runtime Host Provider | Implemented / validated |
| ProcessHostPool | Implemented / validated |
| KubernetesPool | Implemented / validated |
| Exact child failure isolation | Implemented / validated |
| Full-boundary recovery | Implemented / validated |
| External/manual boundary recovery | Implemented / validated |
| Warm Runtime Pool reuse | Implemented / validated |
| Claim-protected recovery | Implemented / validated |
| 36-row adversarial matrix | 36 / 36 VERIFIED |
| Redis Cluster failover validation | Further hardening |
| Multi-control-plane claim arbitration | Further hardening |
| Recovery-of-recovery | Not yet validated |
| Dedicated recursive-child replay | NOT_EVALUATED |
| Public API / SDK polish | Planned |

---

</details>

---

## Where it sits

Compared with Temporal, Dapr, Dagster, Prefect, and LangGraph, this is an **execution-authority layer**, not another agent framework. Its recovery model emphasizes durable execution state and exact ownership rather than relying on workflow-history re-execution as its primary recovery mechanism; it adds first-class multi-tenant isolation and atomic distributed ownership that in-process checkpointing alone does not provide. Durable waiting, sub-workflows, and human-in-the-loop are not claimed as novel — mature engines have them. See [ecosystem positioning](docs/comparison-existing-tools.md).

## Current boundaries

Under active development, not a finished commercial platform. Explicitly outside current proof: dedicated recursive-child replay (`NOT_EVALUATED`), recursion beyond depth 3, and recovery-of-recovery. On ownership: the 36-row matrix proves ownership-**transition** correctness (0 violations). Mutation exclusivity relies on the runtime's atomic claim-token and compare-and-set coordination primitives; continuous ownership-**interval** exclusivity is not independently proven by this matrix. Also further hardening: Redis Cluster failover, durable multi-control-plane claim arbitration, and multi-node Kubernetes scale. See the [roadmap](docs/roadmap.md) and the [full documentation index](docs/index.md).

## Interactive AI Runtime Analysis Demo

A focused interactive demo application is included in this repository to show how the runtime can be **consumed and extended through public extension points without modifying its core**.

> **The demo is not the Deterministic AI Runtime itself.**  
> It is a small application built on top of the runtime and uses its real execution primitives.

The demo exercises:

- RBAC and atomic `ContextKey` rotation under in-flight traffic;
- Redis/Lua-backed atomic coordination;
- realtime metrics, logs, and runtime evidence;
- AI-assisted analysis of bounded execution evidence;
- pluggable steps and deterministic policies;
- AI-generated proposals;
- deterministic policy eligibility decisions;
- explicit human approval or rejection;
- durable Child DAG execution;
- recovery-aware execution and deterministic verification.

Its decision boundary is explicit:

```text
AI analyzes and proposes
        ↓
deterministic policy gates
        ↓
human approves / rejects
        ↓
runtime executes durably
        ↓
evidence verifies
```

After verification, the investigation follows one of two modes:

- **Stop when conclusion is strong** — stop when the available evidence is conclusive.
- **Continue with another useful experiment** — the AI must propose a materially different follow-up that passes policy and explicit human approval again before the next durable Child DAG is created.

The demo therefore demonstrates both application-level extensibility and the runtime boundary:

```text
Demo application
  ├── Next.js UI / traffic scenarios
  ├── AI analysis
  ├── pluggable steps
  ├── pluggable policies
  └── human approval UX
            │
            ▼
Deterministic AI Runtime
  ├── durable execution
  ├── lifecycle
  ├── DAG / Child DAG semantics
  ├── recovery
  ├── Redis coordination
  ├── MongoDB durable persistence
  └── verification
```

### Demo locations

```text
demo/
└── rbac-aiAnalysis/
    ├── Multiplexed.Sample.Demo.Rbac.AiAnalysis.csproj
    └── nextjs/
        ├── package.json
        └── README.md
```

- Demo root: [`demo/rbac-aiAnalysis`](demo/rbac-aiAnalysis)
- Demo API: [`Multiplexed.Sample.Demo.Rbac.AiAnalysis`](/demo/rbac-aiAnalysis/Multiplexed.Sample.Demo.Rbac.AiAnalysis)
- Next.js UI: [`demo/rbac-aiAnalysis/nextjs`](demo/rbac-aiAnalysis/nextjs)
- Demo README: [`demo/rbac-aiAnalysis/nextjs/README.md`](demo/rbac-aiAnalysis/nextjs/README.md)

### Run the demo

From the repository root, start the backend:

```powershell
dotnet run --project .\demo/rbac-aiAnalysis/Multiplexed.Sample.Demo.Rbac.AiAnalysis.csproj.csproj
```

In a second terminal, start the Next.js UI:

```powershell
cd .\demo\rbac-aiAnalysis\nextjs
npm install
npm run dev
```

Redis and MongoDB must be reachable according to the runtime/demo configuration. AI analysis additionally requires the provider configuration expected by the demo API.

---

## License

This project is licensed under the **Business Source License 1.1 (BSL 1.1)**.

It is free to use for development, testing, evaluation, and internal purposes. Production use to provide a competing AI orchestration, workflow engine, or distributed runtime platform requires an explicit commercial agreement.

The licensed source automatically converts to the **Apache License 2.0 on January 1, 2029**.

See the repository `LICENSE` file for the complete and authoritative terms.

---

> **The runtime does not need to understand the answer. It needs to guarantee what happens to the execution that produced it.**

>

> Models may be probabilistic. Execution identity, ownership, recovery, replay, audit, and failure accounting are not.
