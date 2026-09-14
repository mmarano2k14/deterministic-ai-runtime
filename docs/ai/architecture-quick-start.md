# Architecture Quick Start

**Core components, durable truth, failure boundaries, and trade-offs.**

The Deterministic AI Runtime runs workflows across replaceable worker and runtime processes. A workflow is a DAG: operations connected by dependencies, such as calling an LLM, executing code, invoking a tool, or waiting for human approval.

**The core idea: physical capacity is replaceable; durable execution identity and recorded progress are not tied to that capacity.**

Here, *deterministic* describes execution identity, ownership, state transitions, and replay validation. It does not make model answers deterministic or guarantee exactly-once effects in an external system.

## 1. The system in one picture

```text
Client / API / inbound MCP
           |
      RBAC + durable tenant context
           |
      CONTROL PLANE
      shared work -> admission -> capacity reservation
           |
      Local / HTTP / gRPC provider
           |
      selected RUNTIME INSTANCE
      DAG engine -> claim -> execute -> persist transition
           |
      Native plugin / hosted function / outbound MCP tool

Shared state: Redis coordination + MongoDB persistence
Recovery:     observe failure -> claim recovery -> resume / redispatch
Observation:  canonical events -> Ledger / lifecycle / forensics
```

These are responsibility boundaries, not a requirement for a separate service per box.

| Component | Owns | Does not own |
|---|---|---|
| **Control plane** | Shared submissions, tenant-aware admission, runtime selection, capacity, and recovery coordination. | Executing the body of each DAG operation. |
| **Provider and host manager** | Transport to the selected runtime; placement in local, process, or Kubernetes hosting. | Work recovery or silent routing to a different runtime. |
| **Runtime instance and DAG engine** | Dependency readiness, claims, policy checkpoints, retries, durable waits, and finalization. | The business meaning of a plugin's result. |
| **Plugin or hosted function** | Domain work and its result. Hosted functions run Python, TypeScript, or .NET code. | DAG progression; hosted functions receive no runtime-store authority. |

A hosted function process is **not** a trusted runtime instance. A supervisor manages its lifetime; the invocation journal controls its assignment and result. See [hosted execution](hosted-multilanguage-execution.md).

## 2. Where truth lives

The distributed Redis/MongoDB composition separates these authorities:

| Question | Authoritative state |
|---|---|
| What work was accepted and still needs dispatch? | **Shared Run Store and Shared Queue** in Redis, not a process-local queue. |
| Who may advance a live DAG operation? | **Redis DAG state, claims, tokens, and Lua transitions.** Redis is active state, not a disposable cache. |
| What data and definition are retained? | **MongoDB records, payloads, and snapshots.** Published functions additionally use an immutable publication and run pin. |
| Which hosted-function result may be applied? | **Durable Invocation Journal** in MongoDB: frozen inputs, lease/epoch, result, and continuation obligation. |
| What failed, and who may recover it? | **Runtime Pool Failure Journal** records the incident; **Recovery Claim Store** controls mutation. The registry describes current runtime/capacity state. |
| What happened, and why? | **Decision Ledger, Runtime Lifecycle Journal, and Recovery Forensics** retain evidence, not replacement execution authority. |

There is no single cross-store transaction. Atomic transitions protect individual boundaries; continuation and reconciliation bring their states into agreement. **Logs and realtime notifications are not proof of durable application.**

Details: [distributed execution](distributed-execution.md), [failure authority](runtime-pool-failure-authority.md), [event observation](engine-event-observation.md).

## 3. Five invariants to keep in mind

**Identity survives placement.** `SharedRunId` identifies shared work, `ExecutionId` the durable DAG, and `RuntimeInstanceId` replaceable capacity. In-flight recovery preserves `ExecutionId`, not the old physical attempt.

**Tenant ownership survives the request.** The execution-context snapshot carries `TenantId` across queueing, dispatch, and recovery. Background execution restores it rather than depending on the original request.

**Only current authority may mutate.** DAG claims protect transitions; hosted-function leases and epochs protect result acceptance. An obsolete function worker cannot gain DAG authority by returning a result.

**Hosted results: recorded, scheduled, and applied are different.** A result arriving before `Park` remains journaled. Queue acceptance is not application; acknowledgement requires the exact result receipt and terminal parent state.

**A pinned run does not follow `latest`.** Republishing code does not change an unstarted function in an existing pinned run. Missing required artifacts fail explicitly rather than selecting another version.

See [tenant isolation](multi-tenant-control-plane-isolation.md) and [hosted execution contracts](hosted-multilanguage-execution.md).

## 4. What happens when something fails?

Consider one runtime disappearing midway through a DAG:

```text
Execution E on runtime R1
    -> failure recorded; R1 excluded from capacity
    -> exact affected work enumerated; recovery claimed
    -> replacement capacity R2 selected
    -> same execution E resumes from recorded state
```

Work lost from R1's local queue is redispatched from `SharedRunId`; it may not have an `ExecutionId` yet.

| Failure boundary | Recovery scope or limit |
|---|---|
| **One runtime inside a pool** | Recover its affected work; preserve the parent host and healthy siblings. |
| **Entire ProcessHost or Kubernetes Pod** | Recover the exact failed membership; runtimes in other host boundaries remain selectable. |
| **Hosted function process** | Reject stale assignment results. An expired lease alone does not justify repeating a possibly completed external effect. |
| **Interruption after a hosted result is journaled** | Reconciliation can apply the recorded result without launching the function again. Duplicate continuation is not a new logical invocation. |
| **External tool timeout or lost response** | The effect may already have happened. Effect identity is not remote deduplication; durable MCP reconciliation remains outside the current transport. |

Process recovery does not make datastore outages transparent. Redis Cluster failover and distributed multi-control-plane recovery claims remain hardening boundaries, not established guarantees.

Details: [pool failure recovery](runtime-pool-failure-recovery.md). Durable Child DAG waiting uses the same orchestration and releases parent capacity while waiting; see [Child DAG composition](child-dag-composition.md).

## 5. The main trade-offs

| Architectural choice | Benefit | Cost or constraint |
|---|---|---|
| **Redis coordination plus durable MongoDB records** | Conditional ownership changes and retained execution/evidence data have distinct responsibilities. | More than one persistence boundary to operate and reconcile; no global transaction is implied. |
| **Explicit identities and exact routing** | Recovery targets failed work without silently substituting a healthy sibling. | Identity and tenant context must be propagated and validated across every asynchronous boundary. |
| **Bounded reusable runtime pools** | Healthy capacity survives execution cycles and can be reused. | Admission must queue or apply backpressure when available capacity is exhausted. |
| **Immutable publication and supplied dependencies** | A run retains its original implementation and environment selection. | Required artifacts must remain available. Changes require publication, not network package resolution during execution. |
| **Policies decide; the engine transitions** | Behavior is extensible without moving lifecycle authority into plugins. | Each policy family needs its own checkpoint and result contract; hosted custom support currently covers `Concurrency`. |

## 6. What this does not claim

Hosted execution is opt-in server integration, **not yet an external SDK or public publication API**. Exact inline Child DAG definitions can contain published custom Python, TypeScript, or .NET functions: the child keeps the original immutable publication through a `ChildExecutionId` binding and resumes its parent through the existing Child DAG continuation path. This does not create a second scheduler or worker authority.

Process isolation is **not a hostile-code sandbox**. Unsupported isolation/network requirements are rejected rather than downgraded.

Outbound MCP transport and effect metadata do **not** guarantee exactly-once actions or audit replay without re-emission. Validation uses read-only or explicitly idempotent tools.

Evidence is bounded: existing adversarial tests cover selected failure schedules, not all possible interleavings. Native recursive Child DAG validation reaches Depth3, while published custom Child DAG closure explicitly exercises two nested Child DAG levels. Parent replay is covered; dedicated recursive-child replay remains `NOT_EVALUATED`. See the [runtime validation matrix](adversarial-runtime-validation-matrix.md) and [hosted validation](hosted-multilanguage-validation.md).

---

**Continue reading:** [Architecture reference](architecture-overview.md) · [Replay and audit](replay-and-audit.md) · [Complete documentation](../index.md).

**Run it:** [Existing Quick start](../../README.md#quick-start) · [Local Kubernetes setup](kubernetes-local-environment.md).
