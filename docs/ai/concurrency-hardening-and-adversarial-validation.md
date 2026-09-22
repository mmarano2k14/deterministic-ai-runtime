# Concurrency Hardening and Adversarial Validation

Status: Implemented / validated. The historical HTTP/gRPC process-host P10–P35 crash-recovery campaign remains valid, and the current semantic adversarial matrix is additionally green across HTTP/gRPC × ProcessHostPool/KubernetesPool. P35 remains classified as the experimental edge of the concentrated local test environment.

This document describes how the Deterministic AI Runtime is validated under concentrated concurrency, real process loss, recovery races, Redis and MongoDB pressure, and multi-tenant isolation constraints.

It is intentionally separate from [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md).

```text
distributed-concurrency-throttling.md
    = production admission, leases, throttling, and capacity policy

this document
    = adversarial validation of ownership, lifecycle, recovery, and convergence
```

---

## Purpose

The objective is not to reproduce a comfortable production topology.

The objective is to force rare interleavings to appear before production:

- competing claims;
- lease expiration;
- scale-out duplication;
- runtime readiness races;
- process startup races;
- shared queue dispatch races;
- local-queued recovery races;
- in-flight resume races;
- stale capacity;
- shutdown and disposal races;
- cross-tenant contamination;
- replay, ledger, trace, and forensics gaps.

The core principle is:

> The test is intentionally a poor production topology and an excellent race-condition laboratory.

---

## Why the Local Test Is More Violent Than Production

A production deployment would normally distribute pressure across:

- Kubernetes nodes;
- runtime pools;
- replicated control-plane workers;
- tenant-aware cells;
- Redis Cluster or isolated Redis cells;
- MongoDB replica sets or shards;
- admission control;
- backpressure;
- quotas;
- warm runtime capacity;
- bounded scale-out;
- node autoscaling;
- separate observability storage.

The adversarial local harness deliberately removes many of those advantages.

It concentrates control planes, tenants, runtime processes, shared queues, local queues, heartbeats, claims, leases, scale-out, recovery, ledger, tracing, forensics, Redis, and MongoDB on one machine.

```text
production topology
    -> distribute pressure and preserve service levels

adversarial local topology
    -> concentrate collisions and expose correctness defects
```

A tenant is not intended to map one-to-one to a process or pod in production.

The local scenario creates isolated runtime lifecycles because process creation and process death are the failure mechanisms under test.

---

## Scenario Topology

Each parallel scenario contains three tenants:

```text
Scenario N
├── impacted tenant A
│   ├── one in-flight DAG execution
│   ├── two local-queued runs
│   └── runtime process killed
├── impacted tenant B
│   ├── one in-flight DAG execution
│   ├── two local-queued runs
│   └── runtime process killed
└── safe tenant
    ├── three runs
    └── runtime process remains alive
```

The exact pre-crash inventory is:

```text
TotalWorkCount = 3
InFlightExecutionCount = 1
LocalQueuedRunCount = 2
```

The harness does not accept ambiguous inventory shapes.

For example, this is invalid:

```text
InFlightExecutionCount = 2
LocalQueuedRunCount = 1
```

The exact inventory proves that:

- one DAG execution genuinely exists;
- two assigned runs have not started;
- all three work items belong to the runtime that will be evaluated;
- the local-queued work is genuinely at risk when the process dies.

---

## Durable Identity Model

The validation depends on the runtime identity model:

```text
TenantId
    = ownership and isolation boundary

SharedRunId
    = durable control-plane submission identity

LocalRunId
    = assignment identity inside one runtime instance

ExecutionId
    = durable identity of a started DAG execution

RuntimeInstanceId
    = physical or logical runtime owner
```

The recovery paths are intentionally different.

### In-Flight Execution

```text
SharedRunId
-> LocalRunId
-> ExecutionId
-> process dies
-> replacement RuntimeInstanceId
-> same ExecutionId resumes
```

The proof requires:

- same `TenantId`;
- same `SharedRunId`;
- same `ExecutionId`;
- failed capacity suppressed;
- replacement runtime selected;
- remaining DAG steps completed;
- no committed step replayed as a new execution.

### Local-Queued Run

```text
SharedRunId
-> old LocalRunId
-> process dies before DAG execution starts
-> durable requeue
-> replacement RuntimeInstanceId
-> new LocalRunId
-> new ExecutionId starts
```

The proof requires:

- same `TenantId`;
- same `SharedRunId`;
- old `LocalRunId` associated with the failed runtime;
- no durable `ExecutionId` before the crash;
- durable redispatch;
- new local ownership after recovery.

A generic “the run eventually completed” assertion is not sufficient.

---

## Stable Single-Flight Scale-Out Identity

Concurrent recovery requests representing one logical replacement need must converge on one scale-out request.

The stable deduplication identity is based on:

```text
ControlPlaneId
+ deduplication scope
+ recovery intent
+ SharedRunId
+ failed RuntimeInstanceId
```

Diagnostic metadata must not fragment the identity.

This prevents several recovery observers from creating several replacement runtimes for the same failed work.

---

## Readiness Is a Lifecycle Chain

A process is not ready because it exists.

The validated readiness boundary is:

```text
process created
-> runtime registered
-> compatible capacity published
-> transport endpoint reachable
-> runtime dispatchable
```

The control plane must not treat process creation as usable capacity.

Readiness failures are classified separately from:

- provider selection;
- scale-out persistence;
- host creation;
- registry visibility;
- capacity publication;
- dispatch;
- recovery redispatch.

For HTTP process-host creation, a bounded second attempt is allowed only for the proven compatible-registry-missing condition, with cleanup of the first failed lifecycle.

---

## Scale-Out Watcher Readiness

Submission must not begin before the scale-out watcher is operational.

The watcher readiness boundary requires:

- resolved `ControlPlaneId`;
- successful scale-out store access;
- a functional polling cycle.

This removes a startup race in which work can request scale-out before the component responsible for fulfilling that request is ready.

---

## Durable Crash Gate

A crash boundary based only on elapsed time is not deterministic.

Under pressure, the same delay can represent:

- a DAG with a few committed steps;
- a DAG near completion;
- a completed DAG.

The robust lifecycle is:

```text
gate armed
-> in-flight DAG reaches persisted checkpoint
-> exact assigned-work inventory confirmed
-> impacted process killed
-> gate released
-> replacement runtime resumes
```

The gate must be:

- durable;
- visible across external processes;
- scoped by execution and tenant identity;
- released after the process kill;
- safe for the non-killed control tenant.

A long artificial step delay is not equivalent to a gate. Long delays were rejected because they retain active work, replay the wait after recovery, increase process lifetime, and amplify local Redis and ThreadPool pressure.

---

## Claim and Lease Safety

The campaign protects several ownership races.

### Concurrency Lease Acquired but DAG Claim Lost

```text
worker A acquires distributed capacity
worker B wins the DAG claim
worker A loses the claim race
worker A releases its capacity immediately
```

A lost DAG claim must not leak distributed capacity.

### Shared Queue Claim Abandoned

A shared queue item must not remain permanently claimed by a background pump after:

- worker failure;
- cancellation;
- timeout;
- claim expiration;
- control-plane shutdown.

Expired ownership must converge without double dispatch.

### Failed Runtime Capacity

When a process dies:

```text
failed runtime becomes unsafe
-> capacity stops advertising
-> assigned work is enumerated
-> replacement capacity is selected or created
```

Stale capacity must not accept new work.

---

## Safe Tenant as an Active Control

The safe tenant is not decorative.

It proves that recovery remains tenant-scoped while neighboring runtimes fail.

The safe tenant must:

- remain alive;
- complete its three runs;
- receive no recovered work;
- receive no recovery forensics;
- receive no cross-tenant ledger records;
- retain its own registry and capacity visibility;
- avoid redispatch caused by another tenant’s process loss.

Expected evidence:

```text
SafeTenantRecoveredWork = 0
SafeTenantRecoveryForensics = 0
CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
```

Correct recovery is not sufficient for a SaaS runtime if recovery can cross tenant ownership.

---

## Content-Agnostic Step Execution

The runtime does not interpret the business meaning of a step result.

A step may perform:

- RAG retrieval;
- an LLM call;
- vector search;
- an external MCP invocation;
- an internal network call;
- Python, .NET, Java, Go, Rust, or JavaScript code;
- a database operation;
- human approval;
- another provider- or plugin-backed operation.

The runtime owns execution semantics:

```text
admission
-> policy enforcement
-> durable ownership
-> dispatch
-> completion or failure observation
-> retry
-> retention
-> eviction
-> claims and leases
-> recovery
-> replay
-> observability
```

The engine does not need to judge whether an LLM answer is intelligent or whether retrieved content is relevant.

Those concerns belong to the step implementation, provider, application, or result policy.

The runtime must guarantee what happens to the execution that produced the result.

Real AI workloads can introduce:

- unpredictable latency;
- streaming;
- large payloads;
- provider rate limits;
- network failure;
- non-idempotent side effects;
- model-specific retry rules;
- cancellation constraints;
- cost and token policies.

Those characteristics change policy and capacity configuration. They do not redesign durable ownership and recovery.

> The runtime does not need to understand the answer. It needs to guarantee what happens to the execution that produced it.

---

## Parallel Validation Ladder

The current validation ladder is:

| Level | Scenarios | Tenants | Runs | Logical DAG steps | Processes killed | Affected jobs recovered | Classification |
|---|---:|---:|---:|---:|---:|---:|---|
| P10 | 10 | 30 | 90 | 4,500 | 20 | 60 | Repeatedly green; fast validation |
| P15 | 15 | 45 | 135 | 6,750 | 30 | 90 | Green intermediate validation |
| P20 | 20 | 60 | 180 | 9,000 | 40 | 120 | Heavy-pressure validation |
| P30 | 30 | 90 | 270 | 13,500 | 60 | 180 | Reproducibly stable validated ceiling |
| P35 | 35 | 105 | 315 | 15,750 | 70 | 210 | Successful on HTTP and gRPC; experimental local-machine edge |

P35 is not presented as a universal operating limit.

It is the point where local hardware, process startup, ThreadPool scheduling, sockets, Redis, MongoDB, garbage collection, and operating-system scheduling increasingly influence the result.

---

## Final P35 Evidence

### HTTP Process Host

```text
35 scenarios passed
0 scenarios failed
105 tenants
315 DAG executions
15,750 logical DAG step completions
70 real external process kills
210 affected jobs recovered
```

Measured server-side datastore deltas:

```text
Redis commands                 2,913,328
MongoDB operations             1,278,120
Combined datastore operations  4,191,448
Total datastore traffic        18.29 GiB
```

Observed boundaries:

```text
Redis evicted keys             0
MongoDB rejected connections   0
```

### gRPC Process Host

The same logical workload completed 35/35 through the gRPC provider and process-host path.

An independent raw-log sample contained:

```text
6,170 step-completion events
6,170 distinct ExecutionId/step pairs
0 duplicate step completions
0 contested runtime ownership
131 observed executions reached step 50
```

Eleven complete handovers in the captured sample showed:

```text
last step on runtime-1  = 10
first step on runtime-2 = 11
```

The same durable `ExecutionId` continued.

---

## What the Campaign Proves

The completed validation strongly demonstrates:

- real external processes can die while owning durable work;
- unsafe runtime capacity is suppressed;
- in-flight execution resumes with the same `ExecutionId`;
- committed steps are not replayed as a new execution;
- local-queued work is durably redispatched;
- claims and ownership converge;
- safe tenants remain isolated;
- recovery evidence remains readable through replay, ledger, trace, and forensics;
- the same recovery contract works through HTTP and gRPC process hosts;
- capacity can degrade before execution correctness does.

---

## What the Campaign Does Not Prove

The campaign does not prove:

- one million simultaneous executions;
- universal P35 timing on every machine;
- a universal throughput ceiling;
- multi-region behavior;
- Redis primary failover;
- MongoDB election behavior;
- Kubernetes node loss;
- every possible race interleaving;
- regulated-production SLO readiness.

The exact throughput ceiling remains topology- and hardware-dependent.

---

## Failure Classification

Before changing code, failures are classified.

### Infrastructure Saturation

- Redis timeout;
- ThreadPool queue growth;
- high asynchronous operation count;
- shutdown during pending operations;
- nonlinear process startup.

### Runtime Lifecycle

- runtime registration missing;
- capacity not yet visible;
- endpoint not reachable;
- scale-out observed but not fulfilled;
- readiness not reached.

### Recovery Convergence

- claim not released;
- work still associated with failed runtime;
- redispatch incomplete;
- replacement ownership unresolved.

### Harness Race

- DAG completed before kill;
- inventory disappeared before observation;
- invalid assigned-work shape;
- observer started after the target state passed.

A larger timeout cannot recover an inventory state that has already disappeared.

---

## Production Interpretation

The local harness validates correctness under concentrated pressure.

Production should optimize capacity differently:

```text
shared queue
-> admission and backpressure
-> compatible warm runtime
-> atomic capacity reservation
-> execution
-> return runtime to pool
-> bounded process scale-out
-> runtime-pool pod scale-out
-> Kubernetes node autoscaling
```

A production runtime pool should reuse compatible registered runtimes.

A new process should be created only when:

```text
no compatible runtime has capacity
and no compatible runtime is already starting
and the process-level scale-out limit is not reached
```

A new pool pod should be created only when existing pools cannot accept or create more runtime capacity.

---

## Runtime Pool Manager Direction

The next unit of capacity is a pool pod with several independently registered runtime processes.

```text
One Logical Control Plane
        |
        +---- Runtime Pool Pod A
        |     ├── Runtime Process A1
        |     ├── Runtime Process A2
        |     └── Runtime Process A3
        |
        +---- Runtime Pool Pod B
              ├── Runtime Process B1
              └── Runtime Process B2
```

Every internal runtime remains independently registered with:

- `RuntimeInstanceId`;
- `PoolId`;
- `PodName`;
- `PodUid`;
- `NodeName`;
- `ProcessId`;
- transport endpoint;
- status;
- available capacity;
- tenant ownership;
- isolation mode;
- draining state.

This preserves two explicit failure domains:

- process-level recovery;
- pod-level recovery.

The P35 process-kill campaign validates the process-level recovery semantics that the pooled architecture must preserve.

---

## Current Semantic Adversarial Matrix

The original P10–P35 campaign primarily stresses concurrency, datastore pressure, process loss, ownership races, and convergence. A newer complementary matrix targets *semantic execution boundaries* directly.

It is green across:

```text
                    ProcessHostPool    KubernetesPool
gRPC                     VERIFIED          VERIFIED
HTTP                     VERIFIED          VERIFIED
```

with nine canonical rows per combination:

```text
Baseline
CrashEarly
ChildInvocationBoundary
ContinuationConsume
Depth2RuntimeFailure
Depth3RuntimeFailure
SeedA
SeedB
SeedC
```

This adds 36 bounded deterministic schedule validations without invalidating the earlier P35 evidence.

The continuation-consume row is deliberately strict: the exact continuation `SharedRun` is derived from deterministic child invocation identity, the exact physical runtime is pre-armed, durable `Dispatched` ownership is used as authority, the process is frozen before semantic proof reads, and the same process is physically killed. The production continuation path is not delayed or modified solely for the test.

See [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md) and [Adversarial Runtime Validation Evidence Index](adversarial-runtime-validation-evidence-index.md).

---

## Validation Discipline

Future hardening follows these rules:

1. Analyze complete logs before changing code.
2. Patch a proven first cause.
3. Run targeted compilation and tests before expensive parallel validation.
4. Do not run P20, P30, or P35 after speculative tuning.
5. Separate protocol defects from harness defects.
6. Separate local saturation from architecture limits.
7. Do not increase Redis timeout as the primary fix for pressure.
8. One green high-pressure run does not establish repeatability.
9. Require exact crash preconditions.
10. Prefer durable state transitions over elapsed-time assumptions.
11. Keep safe tenants as active controls.
12. Preserve separate proofs for in-flight resume and local-queued redispatch.

---

## Central Result

The campaign reached the local machine’s saturation curve without first losing:

- execution identity;
- tenant isolation;
- ownership convergence;
- replay evidence;
- ledger evidence;
- trace evidence;
- recovery forensics.

> Capacity degraded before correctness did.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Testing Strategy](testing-strategy.md)
- [Distributed Concurrency and Throttling](distributed-concurrency-throttling.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Provider-Agnostic Process-Host Recovery](provider-agnostic-process-host-recovery.md)
- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
