# Runtime Process Crash Recovery

Status: Implemented / validated for HTTP and gRPC process-host runtime crash recovery with real `RuntimeInstanceOnly` processes, tenant-scoped runtime replacement, in-flight DAG resume, local-queued shared-run redispatch, runtime recovery forensics, replay / ledger / trace validation, and multi-tenant safe-tenant non-impact proof.

This document describes the runtime process crash recovery model used by the Deterministic AI Runtime control plane.

It focuses on the production recovery path where an entire runtime process disappears, not only where a single worker or step becomes stale.

Related documents:

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Testing Strategy](testing-strategy.md)

---

## Purpose

Runtime process crash recovery exists to make runtime-host failure survivable without losing work, duplicating work, or contaminating unrelated tenants.

A runtime process can die for many reasons:

- operating-system process kill;
- host crash;
- container restart;
- out-of-memory termination;
- runtime exception before graceful shutdown;
- network or endpoint loss that eventually makes heartbeat unsafe;
- supervisor-driven termination;
- future Kubernetes pod eviction or replacement.

The recovery contract is not simply:

```text
Something failed, then later it completed.
```

The recovery contract is stronger:

```text
The control plane must identify exactly which work was assigned to the unsafe runtime, recover only that work, preserve durable execution identity where it already exists, redispatch queued work through durable shared-run state, write recovery forensics, and prove through replay, ledger, trace, and tenant-scoped queries that unrelated tenants were not impacted.
```

This document defines that contract.

---

## Exact Runtime Pool Failure Chain

The process-host Runtime Pool adds a first-class exact failure chain around one child runtime.

```text
A1 exits unexpectedly
    -> record FailureId A1
    -> suppress RuntimeInstanceId A1
    -> remove RouteId A1
    -> preserve A2/A3
    -> start A4
    -> enumerate A1 work only
    -> acquire one claim
    -> execute existing recovery transitions
```

This chain complements the historical runtime health and execution recovery reconcilers.

The Runtime Pool supplies exact local authority and duplicate-coordinator protection. The existing ownership resolver and transition service continue to own durable recovery semantics.

The complete identity boundary is:

```text
FailureId
PoolId
HostId
RuntimeInstanceId
RouteId
LocalRunId
ExecutionId / SharedRunId
ClaimId
LeaseId
```

See [Runtime Pool Failure Recovery](runtime-pool-failure-recovery.md).

---

## Recovery Is Not Retry

Retry and runtime crash recovery solve different problems.

| Concept | Meaning | Retry Budget Consumed? |
|---|---|---|
| Retry | Step logic executed and returned an error or exception. | Yes, if retry is allowed. |
| Stale step recovery | A worker claimed a step but disappeared or abandoned ownership. | No. |
| Runtime process crash recovery | An entire runtime instance became unsafe and all work assigned to it must be reconciled. | No. |

A process crash is infrastructure failure.

It must not consume business retry budget.

A killed runtime process does not mean the business operation failed. It means the runtime owner disappeared.

---

## Runtime Health vs Execution Recovery

Runtime health reconciliation and execution recovery are intentionally separate responsibilities.

```text
RuntimeInstanceHealthReconciler
    detects unsafe / stale / unhealthy runtime capacity
    prevents unsafe capacity from receiving new work
    marks or suppresses unsafe runtime visibility

ExecutionRecoveryReconciler
    enumerates work already assigned to the unsafe runtime
    recovers in-flight executions
    redispatches local-queued shared runs
    writes recovery evidence

HTTP / gRPC Providers
    report transport and endpoint failure signals
    dispatch through their provider transport when capacity is safe
    participate in provider scale-out
    do not own recovery
    do not kill, restart, or replace runtimes directly
```

This boundary is critical.

Transport providers may return failure reasons such as:

```text
http-provider-unavailable
http-dispatch-timeout
http-circuit-open
http-command-failed
grpc-provider-unavailable
grpc-dispatch-timeout
grpc-circuit-open
grpc-command-failed
```

Those are transport or endpoint health signals.

They are not recovery commands.

The lifecycle owner creates or attaches runtime capacity. The health reconciler determines whether capacity is unsafe. The execution recovery reconciler recovers assigned work.

---

## Validated Transport-Health-to-Recovery Boundary

The validated boundary is:

```text
HTTP / gRPC circuit open or process heartbeat loss
    ↓
failure reason / endpoint health signal emitted
    ↓
health reconciler may mark runtime unhealthy, draining, or unsafe
    ↓
dispatcher stops selecting unsafe runtime capacity
    ↓
execution recovery reconciler recovers assigned work if the runtime becomes unsafe
    ↓
replacement capacity requested if required
    ↓
lifecycle owner creates or attaches replacement runtime capacity
```

This is not a future-only design note.

The current process-host recovery scenarios validate the core boundary for real runtime process death:

```text
real RuntimeInstanceOnly process killed
    ↓
heartbeat becomes stale / runtime marked unsafe
    ↓
assigned work is reconciled
    ↓
replacement runtime capacity is created through the provider / Host Manager path
    ↓
in-flight execution resumes
    ↓
local queued work is redispatched
    ↓
replay / ledger / trace / forensics proof is validated
```

---

## Source of Truth

The local runtime queue is intentionally volatile.

It is useful for throughput, local ordering, queue pressure, and worker coordination, but it is not durable truth.

If the runtime process dies, anything only present in that runtime process is gone.

The durable recovery truth is:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
RuntimeInstanceRegistry
RuntimeCapacityStore
Decision ledger
Trace store
Runtime recovery forensics store
Replay / snapshot artifacts
```

A dead local queue is not recovered from the local queue.

It is reconstructed from durable shared-run, execution-index, DAG-store, registry/capacity, ledger, trace, and forensics state.

---

## Identity Model

Runtime crash recovery depends on keeping identity boundaries strict.

### SharedRunId

`SharedRunId` is the durable identity of a submitted shared run.

It exists before a local runtime accepts the run.

It is the durable handle that allows a run dispatched to a dead runtime local queue to be redispatched without creating a duplicate submission.

### LocalRunId

`LocalRunId` is the local runtime queue identity for one runtime attempt.

It belongs to one runtime instance.

When work is recovered to a replacement runtime, the replacement runtime may create a new `LocalRunId`.

The original `LocalRunId` remains useful as evidence of the failed assignment.

### ExecutionId

`ExecutionId` is the durable DAG execution identity.

It exists only after the runtime has started creating or executing the DAG.

For in-flight execution recovery, the `ExecutionId` must remain identical before and after recovery.

```text
ExecutionIdBefore == ExecutionIdAfter
```

This proves the recovered execution is the same durable execution, not a new execution that happens to run the same pipeline.

### RuntimeInstanceId

`RuntimeInstanceId` identifies the runtime capacity that owns the local queue and workers.

A recovered execution can move from one runtime instance to another.

```text
Original RuntimeInstanceId = tenant-a-runtime-1
Replacement RuntimeInstanceId = tenant-a-runtime-2
ExecutionId = unchanged
```

Runtime identity can change.

Durable execution identity must not change.

---

## Assigned Work Categories

When a runtime process becomes unsafe, assigned work is classified into recovery categories.

### InFlightExecution

An `InFlightExecution` is a run that already created a durable DAG execution.

It has:

```text
SharedRunId
LocalRunId
ExecutionId
AssignedRuntimeInstanceId
DAG state
step progress
```

Recovery action:

```text
resume the existing ExecutionId on replacement runtime capacity
```

The replacement runtime receives enough resume context to continue the same DAG execution.

### LocalQueued

A `LocalQueued` run was dispatched to the runtime local queue but had not yet created a durable DAG execution.

It has:

```text
SharedRunId
LocalRunId
AssignedRuntimeInstanceId
no ExecutionId yet
```

Recovery action:

```text
redispatch the durable SharedRunId to replacement runtime capacity
```

There is no DAG to resume because no durable `ExecutionId` exists yet.

The correct behavior is redispatch, not replay and not resume.

---

## Recovery Flow: In-Flight Execution

The validated in-flight recovery flow is:

```text
Runtime process stops heartbeating / becomes unsafe
    ↓
Health reconciler suppresses unsafe runtime capacity
    ↓
Execution recovery reconciler enumerates work assigned to unsafe runtime
    ↓
In-flight execution candidate detected
    ↓
Recovery forensics record opened
    ↓
SharedRun requeued for resume-existing-execution
    ↓
Failed local run marked requeued for recovery
    ↓
Replacement runtime selected through tenant-aware admission / capacity
    ↓
Replacement local run registered
    ↓
Resume context seeded with original ExecutionId and DAG state
    ↓
DAG resume starts on replacement runtime
    ↓
DAG resume completes
    ↓
Execution recovery marked complete
    ↓
Replay / ledger / trace / forensics proof validated
```

Important invariant:

```text
Recovered in-flight work must keep the same durable ExecutionId.
```

This is the difference between true resume and restart.

---

## Recovery Flow: Local-Queued Shared Run

The validated local-queued recovery flow is:

```text
Runtime process stops heartbeating / becomes unsafe
    ↓
Health reconciler suppresses unsafe runtime capacity
    ↓
Execution recovery reconciler enumerates work assigned to unsafe runtime
    ↓
Local queued work candidate detected
    ↓
Recovery forensics record opened
    ↓
SharedRun requeued for local-queued recovery
    ↓
Failed local run marked requeued for recovery
    ↓
Replacement runtime selected through tenant-aware admission / capacity
    ↓
Replacement local run registered
    ↓
Runtime receives original SharedRunId and ExecutionContextSnapshot
    ↓
New local attempt creates a durable ExecutionId normally
    ↓
DAG execution completes
    ↓
Replay / ledger / trace / forensics proof validated
```

Important invariant:

```text
Recovered local-queued work must be redispatched through the existing SharedRunId.
```

It must not create a duplicate shared submission.

---

## Replacement Runtime Selection

Recovery does not bypass admission.

Replacement capacity is selected through the same tenant-aware registry, capacity, provider, and admission model used by normal dispatch.

```text
Recovery needs replacement capacity
    ↓
Tenant context restored from durable shared run / execution context snapshot
    ↓
Tenant-aware runtime visibility filters registry and capacity
    ↓
Existing safe tenant-visible capacity may be used when valid
    ↓
Scale-out request may be persisted when no capacity exists
    ↓
Provider selector resolves the scale-out-capable provider
    ↓
HTTP or gRPC provider delegates host lifecycle to Runtime Host Manager
    ↓
Process host creation starts RuntimeInstanceOnly process
    ↓
Runtime self-registers and publishes capacity
    ↓
Readiness observed
    ↓
Dispatch / recovery continues through normal path
```

The replacement runtime must be tenant-visible to the recovered work.

Dedicated tenant work must not be recovered onto another tenant's runtime.

Hybrid tenant work may use shared capacity only when tenant policy allows shared fallback and the capacity is visible.

---

## Runtime Host Manager Boundary

In HTTP and gRPC process-host scenarios, replacement capacity can be created through:

```text
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
HttpAiRuntimeInstanceProvider or GrpcAiRuntimeInstanceProvider
    ↓
HTTP or gRPC runtime scale-out provisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process
```

The HTTP and gRPC providers participate in scale-out and dispatch through their respective transports.

It does not directly own process lifecycle policy.

The Runtime Host Manager owns host creation or attachment mechanics.

The runtime process self-registers and publishes capacity before it is considered usable.

The recovery model is provider-agnostic. HTTP and gRPC use the same durable recovery contract:

```text
provider-specific dispatch transport
    ↓
real RuntimeInstanceOnly process
    ↓
process kill / unsafe runtime detection
    ↓
assigned work inventory
    ↓
replacement process-host capacity
    ↓
same durable recovery path
```

The validated gRPC path proves that crash recovery does not depend on HTTP command transport. gRPC dispatch, gRPC scale-out provider routing, Runtime Host Manager process creation, replacement runtime selection, strict DAG resume, replay, ledger, trace, and safe-tenant non-impact all converge through the same recovery model.

Scale-out fulfillment is not recovery completion.

Runtime replacement is not recovery completion.

Recovery is complete only when assigned work has been reconciled, resumed or redispatched, and observable proof has been written.

---

## Tenant Isolation During Recovery

Recovery is tenant-scoped.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

Recovery must restore or carry this context before querying registry, capacity, ledger, traces, replay, or forensics.

Tenant isolation applies to:

```text
runtime registry visibility
runtime capacity visibility
admission candidate selection
scale-out request fields
replacement runtime identity
shared run redispatch
DAG resume context
ledger queries
trace queries
forensics queries
replay queries
```

A recovery incident in tenant A must not make tenant B's capacity visible.

A recovery incident in tenant A or tenant B must not produce recovery evidence for tenant C.

---

## Safe Tenant Non-Impact Contract

The most important isolation proof is not only that impacted tenants recover.

It is that a safe tenant remains untouched.

A safe tenant is a tenant whose runtime process was not killed and whose workload runs concurrently with impacted tenants.

The safe tenant contract is:

```text
RuntimeProcessKilled = false
CrashImpacted = false
SubmittedRuns = expected
CompletedRuns = expected
ReplayProofs = expected
RecoveredWork = 0
RecoveryForensics = 0
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
CrossTenantLedgerLeakDetected = false
```

This proves recovery is not a global panic button.

It is scoped to the unsafe runtime instances and their assigned work.

---

## Recovery Forensics

Every recovered work item receives a recovery forensics record.

The forensics record is not a log line.

It is durable, queryable, tenant-scoped recovery evidence.

### In-Flight ForensicsId

```text
runtime-recovery:{ExecutionId}:{SharedRunId}:{LocalRunId}
```

### Local-Queued ForensicsId

```text
runtime-recovery:local-queued:{SharedRunId}:{LocalRunId}
```

### RuntimeFailureIncidentId

Each recovery record is linked to a runtime failure incident:

```text
runtime-failure:{...}:{RuntimeInstanceId}
```

This connects all recovered work back to the unsafe runtime event that caused the reconciliation.

---

## Forensics Timeline: In-Flight Resume

The validated in-flight timeline contains DAG resume evidence:

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

The presence of `dag.resume.started` and `dag.resume.completed` is important.

It proves the system resumed an existing DAG execution instead of starting a new one.

---

## Forensics Timeline: Local-Queued Redispatch

The validated local-queued timeline is shorter because no DAG exists yet:

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

There is no `dag.resume.started` event because there is no existing `ExecutionId` to resume.

The proof for local-queued work is durable shared-run redispatch, not DAG resume.

---

## Ledger Evidence

Runtime crash recovery writes and validates ledger evidence across two levels.

### Execution Ledger

The execution ledger records lifecycle and DAG execution facts correlated by `ExecutionId`.

For recovered in-flight work, the same `ExecutionId` should show history across:

```text
original runtime instance
failed local run
replacement runtime instance
DAG resume
DAG completion
replay validation
```

### Control-Plane Causal Chain Ledger

The control-plane causal chain records infrastructure decisions around recovery:

```text
scale-out request persisted
scale-out watcher observed request
provider selected
runtime host manager created host
process runtime host started
runtime capacity became visible
registry / capacity lookup saw runtime
execution recovery reconciled assigned work
recovered work redispatched
```

These are infrastructure decisions, not step execution events.

Both levels are needed.

Execution ledger proves the DAG history.

Control-plane ledger proves the recovery infrastructure path.

---

## Replay / Trace / Ledger Proof After Recovery

Recovery is not considered validated only because the DAG completed.

After recovery, the runtime must prove that every execution remains observable and replayable.

For each recovered or safe execution, the validation checks:

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

`Synthetic = false` matters because recovery proof must come from real recovered executions, not synthetic reconstruction.

Recovery without replay is operational resilience.

Recovery with replay, ledger, trace, and forensics is audit resilience.

---

## Validated Multi-Tenant Scenario

The strongest validated scenario uses one shared control plane and three tenant-scoped runtime processes.

```text
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
```

Each tenant submits:

```text
3 runs
50 DAG steps per run
```

For impacted tenants, the kill happens after the in-flight execution reaches the configured step threshold.

At kill time, each impacted tenant has:

```text
1 InFlightExecution
2 LocalQueued
```

Total scenario shape:

```text
Tenants = 3
Total runs = 9
Impacted tenants = 2
Safe tenants = 1
Expected recovered work = 6
Expected safe tenant recovered work = 0
Replay validated executions = 9
```

Validated outcome:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
Safe tenant recovered work = 0
Safe tenant completed work = 3
Strict replay validation = 9/9
Cross-tenant ledger leak = false
Safe tenant recovery leak = false
```

---

## Validated Test Names

Primary HTTP recovery scenarios include:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

and:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

Primary gRPC recovery scenarios include:

```text
Grpc_ProcessHost_Should_Requeue_Real_InFlight_Dag_After_Runtime_Process_Kill
```

```text
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

```text
Grpc_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

The second scenario is the strongest isolation proof because it validates impacted tenants and a non-impacted tenant in the same control plane.

---

## What Recovery Must Not Do

Runtime crash recovery must not:

```text
consume business retry budget for infrastructure failure
create a new ExecutionId for an in-flight resumed DAG
create duplicate SharedRun submissions for local-queued work
recover safe tenant work that was never assigned to the failed runtime
write recovery forensics for the safe tenant
allow impacted tenant queries to see safe tenant recovery entries
allow tenant A to see tenant B ledger entries
allow tenant B to see tenant A ledger entries
route dedicated tenant recovery to unrelated tenant capacity
mark recovery complete only because replacement capacity exists
let the HTTP provider own execution recovery
use local runtime queue state as durable truth
```

These are invariants, not preferences.

---

## Failure and Idempotence Behavior

Recovery can involve concurrent signals.

For example, several recovery paths may request or observe replacement capacity around the same time.

The system must handle this idempotently.

Expected behavior:

```text
first request creates replacement runtime capacity
concurrent duplicate creation request is denied or no-oped safely
registry/capacity state remains consistent
recovered work is redispatched once
forensics records remain one per recovered work item
shared runs are not duplicated
```

Duplicate host creation denial is a valid outcome when it proves the runtime capacity already exists.

The denial must be structured, observable, and non-corrupting.

---

## Recovery Completion Criteria

A recovered work item is complete only when all required criteria are satisfied.

### In-Flight Execution Completion

```text
original ExecutionId preserved
replacement runtime selected
replacement local run registered
resume context seeded
DAG resume started
DAG resume completed
execution completed
forensics completed
ledger evidence present
trace evidence present
replay validation passed
```

### Local-Queued Completion

```text
original SharedRunId preserved
failed local run marked requeued for recovery
replacement runtime selected
replacement local run registered
new execution created normally
execution completed
forensics completed
ledger evidence present
trace evidence present
replay validation passed
```

### Safe Tenant Completion

```text
safe runtime process not killed
safe submitted runs completed
safe replay proofs present
safe recovered work = 0
safe recovery forensics = 0
safe recovery ledger contamination = 0
safe tenant non-impact validated
```

---

## Operational Notes

### Local Queue Volatility Is Intentional

The local queue should not be made the durable source of truth.

Trying to make local queue state durable inside the runtime process makes crash recovery harder and less honest.

The correct model is:

```text
local queue can die
shared/control-plane durable records reconstruct what must happen next
```

### Scale-Out Fulfillment Is Not Enough

Creating a replacement runtime proves only that capacity exists.

It does not prove assigned work recovered.

Recovery proof requires:

```text
assigned work reconciliation
redispatch or resume
execution completion
forensics
ledger
trace
replay
```

### Safe Tenant Proof Is Required

A recovery system that only proves failed work recovered may still hide cross-tenant contamination.

A safe tenant running concurrently is the strongest isolation check.

---

## Current Status

| Capability | Status |
|---|---|
| Runtime heartbeat / unsafe capacity detection | Implemented / validated in process-host recovery scenarios |
| Runtime health vs execution recovery boundary | Implemented / validated |
| HTTP provider transport failure boundary | Implemented / validated |
| gRPC provider transport failure boundary | Implemented / validated |
| Real `RuntimeInstanceOnly` process-host execution | Implemented / validated for HTTP and gRPC |
| Real runtime process kill scenario | Implemented / validated |
| Assigned work inventory for unsafe runtime | Implemented / validated |
| In-flight execution recovery | Implemented / validated |
| Preserve `ExecutionId` during in-flight resume | Implemented / validated |
| Local-queued shared-run recovery | Implemented / validated |
| Preserve `SharedRunId` during local-queued redispatch | Implemented / validated |
| Tenant-aware replacement runtime selection | Implemented / validated |
| Runtime Host Manager process-host replacement capacity | Implemented / validated for HTTP and gRPC |
| Runtime recovery forensics | Implemented / validated |
| Runtime failure incident id | Implemented / validated |
| Recovery forensics timelines | Implemented / validated |
| Control-plane causal chain ledger | Implemented / validated |
| Replay / ledger / trace proof after recovery | Implemented / validated |
| Multi-tenant two-runtime crash recovery | Implemented / validated |
| Safe tenant non-impact proof | Implemented / validated |
| Cross-tenant ledger leak detection | Implemented / validated |
| Kubernetes pod crash recovery | Implemented / validated |
| Production multi-control-plane recovery leadership | Planned |
| Recovery dashboard UI | Planned |

---

## Related Test Evidence

The recovery scenarios validate these proof points:

```text
Real external runtime host processes are used.
No fixture runtime is accepted.
Tenant A and tenant B each lose one runtime instance.
Tenant C is safe and is not killed.
Impacted in-flight DAG executions resume with the same durable ExecutionId.
Impacted local queued work is recovered through durable SharedRunId redispatch.
Safe tenant runs complete normally.
Safe tenant exposes replay, ledger, and trace evidence.
Safe tenant has zero recovered work.
Safe tenant has zero recovery forensics.
No cross-tenant ledger leak is detected.
No duplicate recovery is detected.
No safe-tenant recovery contamination is detected.
```

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Retry and Recovery](retry-and-recovery.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability](observability.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

Do not describe runtime process crash recovery as a future capability when referring to the HTTP or gRPC process-host recovery scenarios.

The validated capability is:

```text
real HTTP and gRPC process-host runtime crash recovery with in-flight DAG resume, local-queued shared-run redispatch, recovery forensics, replay / ledger / trace proof, and safe tenant non-impact validation
```

Still-planned capabilities should remain explicit:

```text
Kubernetes pod crash recovery
production multi-control-plane recovery leadership
recovery dashboard UI
external operational polish
```

