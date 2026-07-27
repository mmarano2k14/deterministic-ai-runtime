# Deterministic AI Runtime

A deterministic, multi-tenant .NET runtime for durable AI workflow execution across local workers, real child processes, reusable Runtime Pools, and Kubernetes-hosted runtime instances.

Deterministic AI Runtime treats AI orchestration as a distributed-systems problem. It provides durable DAG execution, Redis-backed coordination, provider-based dispatch, crash recovery, deterministic replay, audit, observability, execution control, tenant isolation, configuration-driven runtime behavior, policy-driven execution, a pluggable step model, and deterministic convergence behind one shared control plane.

The runtime is content-agnostic. A step can execute an LLM call, RAG operation, MCP tool, database command, human approval, or polyglot service. The engine does not judge the answer; it guarantees the lifecycle of the execution that produced it.

Most AI tooling starts at prompts, agents, and RAG. This runtime starts one layer down, at execution: who owns the work, what survives a crash, and whether the same run can be replayed and audited afterward. See [ecosystem positioning](docs/comparison-existing-tools.md) for how that compares to Temporal, Dapr, Dagster, Prefect, and LangGraph.

[![Version](https://img.shields.io/badge/Version-1.0.7.8-blue)](./CHANGELOG.md)
[![Changelog](https://img.shields.io/badge/Changelog-view-lightgrey)](./CHANGELOG.md)
![AI Runtime](https://img.shields.io/badge/AI-Deterministic%20Execution-purple)
![Runtime](https://img.shields.io/badge/Runtime-distributed-brightgreen)
![Redis](https://img.shields.io/badge/Redis-required-red?logo=redis)
![MongoDB](https://img.shields.io/badge/MongoDB-required-green?logo=mongodb)
![Kubernetes](https://img.shields.io/badge/Kubernetes-supported-326CE5?logo=kubernetes&logoColor=white)
![Status](https://img.shields.io/badge/Status-active%20development-orange)

## Start Here

- [Architecture overview](docs/ai/architecture-overview.md)
- [Configuration-driven runtime](docs/ai/config-driven-runtime.md)
- [Policy-driven execution](docs/ai/policy-driven-execution.md)
- [Step plugins](docs/ai/step-plugins.md)
- [Replay and audit](docs/ai/replay-and-audit.md)
- [Ecosystem positioning and comparison](docs/comparison-existing-tools.md)
- [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md)
- [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)
- [Concurrency hardening and adversarial validation](docs/ai/concurrency-hardening-and-adversarial-validation.md)
- [Kubernetes Runtime Host Provider](docs/ai/kubernetes-runtime-host-provider.md)
- [Multi-tenant control-plane isolation](docs/ai/multi-tenant-control-plane-isolation.md)
- [Enterprise readiness](docs/enterprise-readiness.md)
- [Complete documentation index](docs/index.md)

## Quick Start

### Prerequisites

- A .NET SDK compatible with the solution
- Redis
- MongoDB
- Docker 
- Kubernetes

### Build

```powershell
dotnet build implementations/dotnet/Multiplexed.sln
```

### Run the Core Runtime Tests

```powershell
dotnet test implementations/dotnet/Tests/Multiplexed.AI.Tests/Multiplexed.AI.Tests.csproj
```

### Run the MCP Production Integration Tests

The real process-host, transport, crash-recovery, multi-tenant, replay, ledger, trace, and targeted Kubernetes/Process scenarios live in the MCP Server integration test project.

```powershell
dotnet test implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Multiplexed.AI.McpServer.Tests.Integration.csproj
```

Use targeted test filters for the long-running Process, Runtime Pool, concurrency, and Kubernetes scenarios. Some gates require Redis, MongoDB, the built `RuntimeInstanceOnly` host, Docker, or a reachable Kubernetes environment.

---

## What the Runtime Proves Today

| Area | Current evidence |
|---|---|
| Deterministic execution | Dependency-aware DAG execution with durable state, atomic claims, retry, recovery, retention, replay, and deterministic convergence. |
| Deterministic replay and audit | Persisted snapshots, fingerprints, ledger events, and trace timelines support audit-only replay, restore replay, and post-recovery validation without re-running external side effects. |
| Configuration-driven runtime | Pipeline, provider, transport, retry, retention, concurrency, observability, and hosting behavior are resolved from explicit configuration rather than hard-coded execution paths. |
| Policy-driven execution | Retry, retention, concurrency, admission, isolation, recovery, and governance decisions are evaluated through dedicated policy boundaries. |
| Pluggable step execution | Step implementations remain external to the orchestration engine and can execute LLM, RAG, MCP, database, human, or polyglot service operations. |
| Real process boundaries | HTTP and gRPC providers dispatch into real external `RuntimeInstanceOnly` child processes. |
| Reusable Runtime Pools | One opt-in process host manages several independently registered runtime instances behind stable HTTP and gRPC endpoints. |
| Exact routing | The control plane targets one `RuntimeInstanceId`; the pool router forwards only to that exact child or returns an explicit failure. |
| Targeted failure isolation | Killing A1 suppresses and removes A1 only; A2 and A3 remain routable and A4 restores capacity with fresh identities. |
| Exact recovery authority | `FailureId`, `PoolId`, `HostId`, `RuntimeInstanceId`, `RouteId`, inventory fingerprint, `ClaimId`, and `LeaseId` form an explicit recovery boundary. |
| Durable recovery semantics | In-flight work resumes the same `ExecutionId`; local-queued work is redispatched from its durable `SharedRunId`. |
| Duplicate-coordinator prevention | Concurrent recovery coordinators produce one active claim lease; stale or released lease generations cannot authorize mutation. |
| Multi-tenant isolation | Registry, capacity, admission, scale-out, recovery, ledger, replay, trace, and forensics remain tenant-scoped. |
| Kubernetes hosting | Existing HTTP and gRPC Kubernetes modes create `RuntimeInstanceOnly` Pods and Services through the Kubernetes .NET SDK with layered readiness and crash recovery. |
| Adversarial concurrency | P10–P35 campaigns validate convergence under real process kills, datastore pressure, lifecycle collisions, and local-machine saturation. |

---

## v1.0.7.8 — Exact Runtime Pool Failure Recovery

The process-host Runtime Pool is now implemented and validated.

```text
Process Pool Host
    PoolId = pool-01
    HostId = host-incarnation-01

    stable HTTP endpoint
    stable gRPC endpoint

    RuntimeInstanceId A1 -> RouteId R1
    RuntimeInstanceId A2 -> RouteId R2
    RuntimeInstanceId A3 -> RouteId R3
```

The control plane selects exact capacity:

```text
select RuntimeInstanceId A2
    ↓
stable pool endpoint
    ↓
exact RouteId lookup
    ↓
forwarding lease
    ↓
A2 child endpoint
```

The transport router is not a scheduler. It does not silently fall back to A1, A3, or A4.

When A1 exits unexpectedly:

```text
record exact A1 failure
    ↓
suppress exact A1 capacity
    ↓
remove exact A1 route
    ↓
publish completion to the pool manager
    ↓
start replacement A4
    ↓
enumerate A1 work only
    ↓
acquire one deterministic recovery claim
    ↓
invoke the existing validated recovery transition boundary
for A1's exact claimed inventory
    ↓
release the claim explicitly
```

The safety boundary is precise:

```text
unsafe capacity = { A1 }
safe capacity   = { A2, A3, A4 }
```

A2 and A3 preserve their original `RuntimeInstanceId` and `RouteId`. A4 receives fresh runtime and route identities.

The Runtime Pool proof establishes exact failure authority, sibling isolation, claim ownership, and invocation of the existing recovery boundary. The process-host recovery suite separately validates the durable transition semantics reused by that boundary: in-flight work resumes the same `ExecutionId`, while local-queued work is redispatched from its durable `SharedRunId`.

### Runtime Pool Identities

| Identity | Responsibility |
|---|---|
| `PoolId` | Logical reusable capacity group. |
| `HostId` | Immutable process-host incarnation; future Kubernetes Pod UID boundary. |
| `RuntimeInstanceId` | Independently selectable execution capacity. |
| `RouteId` | Immutable transport-route incarnation. |
| `FailureId` | One exact failure observation. |
| `ClaimId` | Deterministic recovery claim over exact authority and inventory. |
| `LeaseId` | Unique active claim-acquisition generation. |

Correctness uses typed fields. Diagnostic metadata is never the routing or recovery authority.

---

## Validation Evidence

### Runtime Pool and Regression Gates

```text
Runtime Pool process lifecycle             green
Stable Runtime Pool HTTP routing           green
Stable Runtime Pool gRPC routing           green
Real A1 kill and targeted A4 replacement   green
Exact failure journal and suppression      green
Deterministic recovery claim               green
Claimed recovery execution                 green

Historical Process HTTP                    P10 green
Historical Process gRPC                    P10 green
Existing Kubernetes HTTP                   P5 green
Existing Kubernetes gRPC                   P5 green
```

The Kubernetes gates prove compatibility with the existing one-runtime-per-Pod modes. They do not claim that Kubernetes Runtime Pool Pods are already implemented.

### Adversarial P35 Evidence

Both HTTP and gRPC process-host campaigns completed 35/35.

Per transport:

```text
parallel scenarios             35
tenants                        105
real DAG executions            315
real external process kills    70
affected jobs recovered        210
logical DAG step completions   15,750
```

The measured HTTP batch generated:

```text
Redis commands                 2,913,328
MongoDB operations             1,278,120
combined datastore operations  4,191,448
measured datastore traffic     18.29 GiB
```

The machine slowed down before correctness broke.

The campaign preserved:

- exact pre-crash inventory;
- durable crash checkpoints;
- the same `ExecutionId` for in-flight resume;
- durable redispatch for work that had not started;
- no duplicate step completion;
- no contested runtime ownership;
- no safe-tenant recovery contamination;
- consistent ledger, trace, replay, and recovery forensics.

P35 represents the experimental edge of the local machine, not a universal production throughput guarantee.

---

## Architecture at a Glance

```text
Client / API / MCP
        ↓
RBAC ExecutionContext
        ↓
durable ExecutionContextSnapshot
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
        ├── Fixture
        ├── Process
        ├── Attach
        ├── Kubernetes
        └── opt-in Process Runtime Pool
                ↓
        stable HTTP / gRPC pool endpoint
                ↓
        exact RuntimeInstanceId route
                ↓
        RuntimeInstanceOnly child
                ↓
Local Runtime Queue
        ↓
DAG Execution Engine
        ↓
Redis Hot State + Lua Coordination
        ↓
Stateless Workers / Step Executors
        ↓
MongoDB Payloads / Snapshots
        ↓
Ledger / Metrics / Trace / Replay / Forensics
```

### Responsibility Boundaries

- The control plane owns admission, capacity selection, scale-out, dispatch, and recovery coordination.
- Providers own command transport.
- Runtime Host Manager strategies own host lifecycle.
- The Runtime Pool Manager owns child lifecycle and replacement.
- The route registry owns exact transport reachability.
- The pool router owns exact forwarding, not scheduling.
- Health reconciliation suppresses unsafe capacity.
- Recovery enumeration identifies work assigned to the failed runtime.
- The existing ownership resolver and recovery transition service own durable recovery semantics.
- Configuration defines runtime structure and operating parameters.
- Policies decide how execution, admission, retry, retention, concurrency, isolation, and recovery should behave.
- Step plugins implement domain operations without owning durable orchestration semantics.
- Replay reconstructs and validates persisted execution evidence independently from live external side effects.

---

## Execution Model

The runtime is designed around four complementary extension and governance models.

### Replay-Driven Evidence

Replay is not treated as a log dump or a best-effort reconstruction.

The runtime persists the evidence required to validate an execution after completion or recovery:

```text
terminal snapshot
    + deterministic fingerprint
    + dependency graph
    + step state
    + payload references
    + decision ledger
    + trace timeline
    = replayable execution evidence
```

Supported foundations include:

- audit-only replay;
- restore replay;
- deterministic fingerprint validation;
- replay metadata;
- replay ledger loading;
- replay trace loading;
- post-crash recovery replay proof.

External model or tool side effects do not need to be invoked again to inspect the durable execution history.

### Configuration-Driven Runtime

Runtime behavior is resolved from explicit configuration rather than embedded into step implementations.

Configuration can describe:

- pipelines and DAG structure;
- providers and transports;
- runtime hosting mode;
- retry and recovery parameters;
- retention, compaction, and eviction;
- concurrency limits;
- observability persistence;
- tenant runtime settings;
- queue and scale-out behavior.

This keeps infrastructure decisions outside business-step code and allows the same execution engine to operate across local, Process, Runtime Pool, and Kubernetes boundaries.

### Policy-Driven Execution

Policies define how the runtime should behave when execution conditions change.

Policy boundaries cover:

- retry eligibility and timing;
- retention and eviction;
- distributed concurrency and throttling;
- run admission;
- tenant isolation and shared fallback;
- provider and runtime selection;
- recovery eligibility;
- operational governance.

The orchestration engine applies policy decisions while preserving deterministic state transitions and audit evidence.

### Pluggable Step Model

Step implementations are independent from the durable orchestration engine.

A step can be implemented as:

- an LLM or model invocation;
- a RAG retrieval or composition stage;
- an MCP tool call;
- an HTTP or gRPC service;
- a database operation;
- a human approval;
- .NET, Python, Java, Go, Rust, JavaScript, or another polyglot execution boundary.

Plugins own domain behavior. The runtime owns identity, claims, retries, recovery, policy, persistence, replay, and observability.

---

## Core Capabilities

| Capability | Status |
|---|---:|
| Deterministic DAG execution | Implemented |
| Redis hot state and Lua atomic coordination | Implemented |
| Distributed workers and step claims | Implemented |
| Retry, stale-work recovery, and deterministic convergence | Implemented |
| Execution pause, resume, cancel, and human input | Implemented |
| Run-level queue control | Implemented |
| Retention, compaction, eviction, and payload externalization | Implemented |
| Snapshot and Replay API foundations | Implemented |
| Audit-only and restore replay | Implemented |
| Replay ledger and trace evidence | Implemented / validated |
| Configuration-driven pipeline and runtime behavior | Implemented foundation |
| Policy-driven retry, retention, concurrency, and admission | Implemented |
| Pluggable external step model | Implemented foundation |
| Execution-correlated decision ledger | Implemented |
| Metrics, tracing, and realtime event foundations | Implemented |
| RBAC execution-context propagation | Implemented / validated |
| Shared, Dedicated, and Hybrid tenant isolation | Implemented / validated |
| Redis registry, capacity, discovery, and admission reservations | Implemented / validated |
| Shared Runtime Controller and shared queue pump | Implemented / validated |
| Local runtime provider and scale-out | Implemented / validated |
| HTTP runtime provider and process-host scale-out | Implemented / validated |
| gRPC runtime provider and process-host scale-out | Implemented / validated |
| Kubernetes Runtime Host Provider | Implemented / validated foundation |
| Provider-agnostic HTTP/gRPC crash recovery | Implemented / validated |
| Process-host Runtime Pool Manager | Implemented / validated |
| Stable Runtime Pool HTTP and gRPC routing | Implemented / validated |
| Exact Runtime Pool failure isolation | Implemented / validated |
| Claim-protected deterministic Runtime Pool recovery | Implemented / validated |
| Kubernetes Runtime Pool Pod | Planned |
| Hierarchical runtime/Pod/node capacity selection | Planned |
| Redis Cluster compatibility and distributed claim durability | Planned |
| Public API / SDK polish | Planned |

---

## Deterministic Recovery Semantics

### In-Flight Work

An in-flight candidate already has a durable `ExecutionId`.

```text
RuntimeInstanceId = A1
LocalRunId        = local-a1-flight
ExecutionId       = execution-a1
```

Recovery preserves the same `ExecutionId`.

### Local-Queued Work

A local-queued candidate has a durable `SharedRunId` but no `ExecutionId`.

```text
RuntimeInstanceId = A1
LocalRunId        = local-a1-queued
SharedRunId       = shared-run-01
ExecutionId       = absent
```

The dead process-local queue is not treated as durable truth. Recovery redispatches from shared durable state.

### Deterministic Claim

The exact ordered inventory is fingerprinted before mutation authority is granted.

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

Concurrent coordinators may read the same inventory, but only one active lease can authorize transitions.

A transition exception does not silently release the claim.

---

## Multi-Tenant Control-Plane Isolation

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

RBAC context is captured once and persisted before the request leaves its original API or MCP scope.

```text
MCP / API
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
SharedRunRecord
    ↓
Shared Queue
    ↓
Tenant-Aware Admission
    ↓
Runtime Local Queue
    ↓
DAG Execution
```

The runtime validates:

- tenant-aware registry and capacity visibility;
- Shared, Dedicated, and Hybrid isolation;
- explicit shared fallback policy;
- tenant-scoped scale-out settings;
- tenant-scoped runtime prefixes;
- safe-tenant non-impact during crash recovery;
- no cross-tenant ledger or forensics leakage.

`ContextKey` remains useful for RBAC lookup, correlation, and diagnostics. It is not the durable tenant-isolation boundary.

---

## Kubernetes Runtime Hosting

Kubernetes is a Runtime Host Manager lifecycle strategy, not a replacement transport provider.

```text
HTTP or gRPC scale-out provider
    ↓
Runtime Host Manager
    ↓
KubernetesAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly Pod + Service
    ↓
resource readiness
    ↓
runtime registration and capacity readiness
    ↓
transport endpoint readiness
    ↓
normal HTTP or gRPC dispatch
```

Kubernetes currently supports:

- deterministic Pod and Service lifecycle through the Kubernetes .NET SDK;
- fake and real SDK clients;
- HTTP and gRPC transport preservation;
- Service DNS;
- NodePort;
- per-runtime `kubectl port-forward`;
- Gateway API routing through `HTTPRoute` or `GRPCRoute`;
- layered readiness;
- ownership-safe cleanup;
- Pod crash recovery through the shared health and recovery boundaries.

The existing Kubernetes mode remains one runtime per Pod/Service.

The planned Kubernetes Runtime Pool mode will place several independently registered runtimes inside one Pod and map `HostId` to the Pod UID.

---

## Why This Exists

Prototype AI systems focus on prompts, agents, models, and RAG.

Production AI systems must also answer:

- Who owns the work?
- What happens if a worker crashes?
- What happens if the whole runtime process dies?
- Can an in-flight execution resume without receiving a new identity?
- Can queued work be recovered without trusting a dead local queue?
- Can two recovery coordinators mutate the same work?
- Can one failed child be removed without losing healthy sibling capacity?
- Can unrelated tenants remain provably untouched?
- Can the execution be replayed and audited?
- Can concurrency, retry, retention, and provider pressure be governed?
- Can the same execution protocol survive local, process-host, Runtime Pool, and Kubernetes boundaries?
- Can runtime behavior be changed through configuration instead of rewriting the engine?
- Can retry, retention, concurrency, admission, and recovery remain policy-driven and auditable?
- Can domain steps remain pluggable without weakening execution guarantees?
- Can a completed or recovered run be replay-validated without invoking external side effects again?

Deterministic AI Runtime exists to make those guarantees explicit and testable.

> The runtime does not need to understand the answer. It needs to guarantee what happens to the execution that produced it.

---

## Current Boundaries

This repository is an advanced, test-driven execution infrastructure project under active development. It is not presented as a finished commercial platform.

Implemented now:

- deterministic execution and durable coordination;
- HTTP/gRPC process-host execution;
- existing Kubernetes hosting modes;
- opt-in process-host Runtime Pools;
- exact pool routing;
- targeted child replacement;
- exact failure isolation;
- deterministic claimed recovery;
- deterministic replay, replay ledger, replay trace, and audit foundations;
- configuration-driven runtime behavior;
- policy-driven retry, retention, concurrency, admission, and recovery boundaries;
- pluggable external step execution;
- tenant-aware control-plane isolation.

Still planned:

- Kubernetes Runtime Pool Pods;
- Pod-wide suppression by Kubernetes Pod UID;
- durable distributed pool route, failure, safety, and claim stores;
- multi-control-plane recovery-claim arbitration;
- hierarchical warm-runtime / existing-Pod / new-Pod / new-node selection;
- Redis Cluster key-slot and failover validation;
- production dashboarding and managed-hosting packaging;
- public API and SDK polish.

See the [Runtime Pool roadmap](docs/product-roadmap/runtime-pool-roadmap.md) and the [project roadmap](docs/roadmap.md).

---

## Documentation

### Architecture and Runtime

- [Architecture overview](docs/ai/architecture-overview.md)
- [Ecosystem positioning and comparison](docs/comparison-existing-tools.md)
- [Runtime Pool architecture](docs/ai/runtime-pool-architecture.md)
- [Runtime Pool failure recovery](docs/ai/runtime-pool-failure-recovery.md)
- [Runtime control plane](docs/ai/runtime-control-plane.md)
- [Runtime discovery, registry, and capacity](docs/ai/runtime-discovery-registry-capacity.md)
- [Runtime instance provider model](docs/ai/runtime-instance-provider-model.md)
- [HTTP runtime provider](docs/ai/http-runtime-provider.md)
- [gRPC runtime provider](docs/ai/grpc-runtime-provider.md)
- [Kubernetes Runtime Host Provider](docs/ai/kubernetes-runtime-host-provider.md)

### Recovery, Concurrency, and Evidence

- [Concurrency hardening and adversarial validation](docs/ai/concurrency-hardening-and-adversarial-validation.md)
- [Provider-agnostic process-host recovery](docs/ai/provider-agnostic-process-host-recovery.md)
- [Runtime process crash recovery](docs/ai/runtime-process-crash-recovery.md)
- [Runtime recovery forensics](docs/ai/runtime-recovery-forensics.md)
- [Multi-tenant runtime crash isolation](docs/ai/multi-tenant-runtime-crash-isolation.md)
- [Recovery replay, ledger, and trace proof](docs/ai/recovery-replay-ledger-trace-proof.md)
- [Testing strategy](docs/ai/testing-strategy.md)

### Product Direction

- [Enterprise readiness](docs/enterprise-readiness.md)
- [Project roadmap](docs/roadmap.md)
- [Product roadmap index](docs/product-roadmap/index.md)
- [Runtime Pool roadmap](docs/product-roadmap/runtime-pool-roadmap.md)
- [Current product foundation](docs/product-roadmap/current-foundation.md)
- [Managed hosting model](docs/product-roadmap/managed-hosting-model.md)

The complete documentation map is available at [docs/index.md](docs/index.md).

---

## License

This project is licensed under the **Business Source License 1.1 (BSL)**.

- Free for development, testing, and internal use
- Commercial production use requires a license
- Automatically converts to Apache 2.0 on 2029-01-01

See the repository license file for full terms.