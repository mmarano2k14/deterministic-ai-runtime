# Multi-Tenant Runtime Crash Isolation

Status: Implemented / validated through real HTTP process-host runtime crash recovery scenarios.

This document describes the multi-tenant runtime crash isolation model validated by the Deterministic AI Runtime.

It focuses on the strongest production recovery scenario currently validated:

```text
One shared control plane
Three tenant-scoped real runtime host processes
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
Automatic recovery for impacted tenants only
Replay / ledger / trace / forensics proof for every work item
Zero safe-tenant recovery contamination
```

This document complements:

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)

---

## Purpose

Runtime crash recovery is not sufficient by itself in a multi-tenant control plane.

A production runtime must prove two things at the same time:

```text
1. Impacted tenants recover correctly.
2. Non-impacted tenants remain untouched.
```

The second property is just as important as the first.

Recovering tenant A and tenant B proves the recovery mechanism.

Proving that tenant C receives no recovery work, no recovery forensics, no recovery ledger contamination, and no runtime kill proves the isolation architecture.

The purpose of this document is to define the isolation contract for runtime process crash recovery and document the validated proof surface.

---

## Validated Scenario

The validated scenario uses a shared control plane with three active tenants.

```text
Tenant A = impacted tenant
Tenant B = impacted tenant
Tenant C = safe tenant
```

Each tenant receives tenant-scoped runtime capacity.

In the process-host scenario, the test host starts real external `RuntimeInstanceOnly` processes.

The control plane is shared, but the runtime processes are tenant-scoped for this scenario.

```text
MCP test host / shared control plane
    ├── Tenant A runtime process
    ├── Tenant B runtime process
    └── Tenant C runtime process
```

The test then kills only the tenant A and tenant B runtime processes.

Tenant C remains alive.

```text
Kill Tenant A runtime process
Kill Tenant B runtime process
Do not kill Tenant C runtime process
```

The system must recover only work that was assigned to the killed runtime instances.

The safe tenant must continue normal execution and must not be pulled into the recovery surface.

---

## Workload Contract

The validated workload uses three tenants, three runs per tenant, and a deterministic DAG shape.

```text
Tenants = 3
Runs per tenant = 3
DAG steps per run = 50
Kill point = after 25 completed steps on the in-flight execution
Total submitted runs = 9
Impacted submitted runs = 6
Safe tenant submitted runs = 3
Expected recovered work = 6
Expected safe tenant recovered work = 0
```

At the moment of the crash, each impacted tenant has:

```text
1 InFlightExecution
    - already has durable ExecutionId
    - mid-DAG
    - approximately 25/50 completed steps

2 LocalQueued runs
    - dispatched to the dead runtime local queue
    - not yet started
    - no durable ExecutionId yet
    - recoverable through SharedRunId
```

The safe tenant has active work but its runtime process is never killed.

---

## Isolation Contract

The crash isolation contract is strict.

```text
Impacted tenant work may be recovered.
Safe tenant work must not be recovered.
Impacted tenant forensics may be written.
Safe tenant recovery forensics must not be written.
Impacted tenant ledger recovery entries may exist.
Safe tenant recovery ledger contamination must not exist.
Impacted tenant runtime processes may be replaced.
Safe tenant runtime process must not be killed or replaced by recovery.
```

The core invariants are:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
Tenant C recovered work = 0

Tenant A recovery forensics > 0
Tenant B recovery forensics > 0
Tenant C recovery forensics = 0

CrossTenantLedgerLeakDetected = false
SafeTenantRecoveryLeakDetected = false
SafeTenantNonImpactValidated = true
```

---

## Tenant Boundary

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

`ContextKey` is not the durable tenant boundary.

Metadata is not the durable tenant boundary.

Tenant-specific runtime isolation must be evaluated from strong fields and restored execution context snapshots.

The relevant fields include:

```text
TenantId
TenantGroupId
IsolationMode
RuntimeInstanceIdPrefix
ExecutionContextSnapshot
```

Every asynchronous or distributed hop must carry or restore tenant context before making tenant-sensitive decisions.

This includes:

```text
MCP request
SharedRunRecord persistence
SharedQueueDispatcher context restore
Dispatch-time admission
Runtime registry / capacity filtering
Scale-out request publication
Scale-out watcher provider request
Runtime host creation request
Runtime local queue enqueue
Background controller execution
Replay / ledger / trace / forensics queries
```

---

## Runtime Capacity Isolation

Runtime capacity must remain tenant-visible, not globally visible.

The validated scenario uses tenant-scoped process-host runtime capacity.

A runtime instance belongs to a tenant or tenant group through strong descriptor fields.

Tenant A must not see tenant B runtime capacity.

Tenant B must not see tenant A runtime capacity.

The safe tenant must not be selected as replacement capacity for impacted tenant recovery.

```text
Tenant A recovery selects Tenant A visible replacement capacity.
Tenant B recovery selects Tenant B visible replacement capacity.
Tenant C runtime capacity remains Tenant C visible only.
```

This prevents recovery from becoming a global panic path.

Recovery is tenant-scoped capacity selection plus assigned-work reconciliation.

---

## Recovery Boundary

Runtime crash isolation depends on a strict separation of responsibilities.

```text
RuntimeInstanceHealthReconciler
    detects unsafe runtime capacity
    suppresses unsafe capacity from new admission
    prevents routing to dead or stale runtimes

RuntimeExecutionRecoveryReconciler
    enumerates work assigned to unsafe runtime instances
    recovers in-flight executions
    redispatches local queued shared runs
    writes recovery evidence and forensics

HTTP provider
    reports transport failure signals
    dispatches commands when a runtime endpoint is healthy
    participates in scale-out when selected
    does not own recovery
    does not kill or restart runtimes directly

Runtime Host Manager / lifecycle owner
    creates or attaches replacement runtime capacity
```

The HTTP provider is not the recovery owner.

The runtime health reconciler is not the assigned-work recovery owner.

The lifecycle owner creates capacity, but recovery is not complete just because replacement capacity exists.

Recovery is complete only when the assigned work has been reconciled and the proof surface is written.

---

## In-Flight Execution Isolation

An in-flight execution already has a durable execution identity.

When the runtime process dies, the replacement runtime must resume the same `ExecutionId`.

The contract is:

```text
ExecutionIdBefore == ExecutionIdAfter
```

The recovered execution is not a new execution.

It is the same durable DAG execution, continued on a replacement runtime instance.

Tenant isolation requires that the resume request is scoped to the impacted tenant.

```text
Tenant A in-flight ExecutionId resumes under Tenant A context.
Tenant B in-flight ExecutionId resumes under Tenant B context.
Tenant C has no in-flight recovery resume because Tenant C runtime was not killed.
```

The safe tenant may have normal executions, ledger entries, traces, and replay evidence.

It must not have runtime recovery entries.

---

## Local Queued Work Isolation

Local queued work is different from in-flight DAG execution.

A local queued run was dispatched to a runtime local queue but did not start DAG execution yet.

Therefore it has:

```text
SharedRunId = available
LocalRunId = failed / abandoned attempt
ExecutionId = not created yet
```

When the runtime process dies, local queued work cannot be recovered from the local queue.

The local queue is volatile.

The system must reconstruct the correct state from durable shared control-plane records.

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
Registry / capacity state
```

The recovery path is:

```text
local queued shared run assigned to unsafe runtime
    ↓
shared run requeued for local-queued recovery
    ↓
failed local run marked requeued for recovery
    ↓
replacement runtime selected under same tenant context
    ↓
new local run registered
    ↓
normal DAG execution starts
```

Tenant C local queued work is not touched because Tenant C runtime did not become unsafe.

---

## Safe Tenant Contract

The safe tenant is the strongest isolation proof.

In the validated scenario, the safe tenant is:

```text
tenant-real-crash-safe
```

The safe tenant contract is:

```text
RuntimeProcessKilled = false
CrashImpacted = false
SubmittedRuns = 3
CompletedRuns = 3
ReplayProofs = 3
RecoveredWork = 0
RecoveryForensics = 0
SafeTenantNonImpactValidated = true
SafeTenantRecoveryLeakDetected = false
```

The safe tenant must still produce normal observability.

It should have:

```text
execution ledger entries
step ledger entries
trace entries
completion evidence
replay report
replay ledger
replay trace
```

It must not have:

```text
runtime recovery forensics
recovery redispatch evidence
runtime failure incident for its runtime
recovery contamination visible from impacted tenant queries
safe tenant work counted as recovered work
```

This distinction is important.

The safe tenant is not invisible.

The safe tenant is visible as normal execution.

It is absent only from the recovery surface.

---

## Ledger Isolation

The ledger must prove both positive and negative facts.

Positive facts:

```text
Tenant A has recovery entries.
Tenant B has recovery entries.
Recovered executions have completion evidence.
Recovered executions have step completion evidence.
Recovered executions have replay evidence.
```

Negative facts:

```text
Tenant A cannot see Tenant B ledger entries.
Tenant B cannot see Tenant A ledger entries.
Impacted tenant recovery queries cannot see safe tenant recovery entries.
Safe tenant has no recovery contamination.
```

Validated query invariants include:

```text
TenantBEntriesVisibleFromTenantA = 0
TenantAEntriesVisibleFromTenantB = 0
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
CrossTenantLedgerLeakDetected = false
```

Ledger isolation is enforced through tenant-scoped query context.

The MCP ledger API must apply tenant scope before returning records.

---

## Forensics Isolation

Recovery forensics are per work item and tenant-scoped.

Impacted tenants receive recovery forensics records.

Safe tenant does not.

For in-flight execution recovery, the forensics id shape is:

```text
runtime-recovery:{ExecutionId}:{SharedRunId}:{LocalRunId}
```

For local-queued recovery, the forensics id shape is:

```text
runtime-recovery:local-queued:{SharedRunId}:{LocalRunId}
```

Each forensics record links to a runtime failure incident:

```text
runtime-failure:{...}:{RuntimeInstanceId}
```

The safe tenant must not receive a runtime failure incident because its runtime was not killed.

The safe tenant forensics query must return:

```text
ExpectedSafeRecovery = 0
ActualSafeRecovery = 0
SafeTenantRecoveryForensicsDetected = false
```

This proves recovery evidence is not merely filtered at display time.

It is scoped correctly at write and query time.

---

## Replay Isolation

Replay is validated for all executions, including the safe tenant.

This is important because safe tenant isolation does not mean safe tenant invisibility.

The safe tenant must remain replayable as normal work.

Validated replay proof:

```text
Strict replay validation = 9/9
Replay reports readable = 9/9
Replay ledger readable = 9/9
Replay trace readable = 9/9
Synthetic = false for all executions
```

The safe tenant contributes three normal replay proofs.

It contributes zero recovery proofs.

```text
Tenant A recovered executions = replayable
Tenant B recovered executions = replayable
Tenant C normal executions = replayable
Tenant C recovery entries = 0
```

This distinction is the audit boundary.

---

## Trace Isolation

Trace data must follow the same tenant-scoped correlation model.

Recovered impacted executions should expose trace evidence showing execution progress before and after recovery.

Safe tenant executions should expose normal trace evidence.

Safe tenant traces should not show runtime recovery flow.

Useful trace correlation fields include:

```text
TenantId
TenantGroupId
SharedRunId
LocalRunId
ExecutionId
RuntimeInstanceId
WorkerId
PipelineKey
ForensicsId
RuntimeFailureIncidentId
```

Trace is not the source of recovery truth.

Trace complements ledger and forensics.

The recovery proof is strongest when:

```text
forensics timeline
+ ledger causal chain
+ trace evidence
+ replay proof
```

all agree.

---

## Control-Plane Causal Chain Isolation

The control-plane ledger causal chain should show the recovery path for impacted tenants without pulling in safe tenant recovery work.

Validated causal chain domains include:

```text
scale-out request persisted
scale-out watcher observed request
provider selected
runtime host manager created host
process runtime host started
runtime capacity became visible
runtime instance visible through registry/capacity lookup
execution recovery reconciled assigned work
recovered work redispatched
```

The causal chain proves that recovery used the normal control-plane path.

It also proves that recovery remained scoped to assigned work on unsafe runtime instances.

The safe tenant may have normal scale-out, dispatch, execution, ledger, and replay entries depending on scenario timing.

It must not have recovery causal chain entries.

---

## Local Queue Volatility Rule

The local queue is allowed to die with the runtime process.

This is a design rule, not a limitation.

```text
Local queue = volatile execution staging
SharedRunStore = durable submission truth
SharedQueue = durable dispatch coordination
RuntimeRunExecutionIndex = durable assignment/execution index
DAG store = durable execution truth after ExecutionId exists
Ledger / trace / forensics = durable proof surface
```

A dead local queue is never trusted as the source of recovery truth.

For impacted tenants, recovery reconstructs assigned work from durable records.

For the safe tenant, no recovery reconstruction is performed because the runtime was not unsafe.

---

## Duplicate Recovery Prevention

Multi-tenant recovery may produce overlapping signals.

For example:

```text
heartbeat stale detection
scale-out request fulfillment
queue redispatch
provider readiness
runtime registration
```

The system must prevent duplicate recovery.

Important invariants:

```text
same in-flight ExecutionId is not recovered twice
same local queued SharedRunId is not redispatched twice
same replacement runtime is not created repeatedly for same tenant scope
safe tenant work is not incorrectly counted as duplicate or recovered work
```

Duplicate runtime creation attempts may be denied idempotently.

This is expected when concurrent control-plane paths attempt to satisfy the same capacity requirement.

The important behavior is that duplicate denial is structured, observable, and does not create duplicate execution.

---

## Validated Test Name

The primary validated scenario is:

```text
Http_ProcessHost_Should_Recover_Two_Tenants_After_Real_Runtime_Process_Kills_Without_Impacting_Safe_Tenant_With_Strict_Dag_Resume_Replay_Ledger_And_Trace
```

This scenario validates:

```text
real external RuntimeInstanceOnly processes
three active tenants
Tenant A runtime killed
Tenant B runtime killed
Tenant C runtime not killed
automatic recovery reconciliation
in-flight DAG resume with same ExecutionId
local queued redispatch through SharedRunId
safe tenant normal completion
safe tenant replay proof
safe tenant zero recovered work
safe tenant zero recovery forensics
no cross-tenant ledger leak
no safe-tenant recovery contamination
strict replay validation for all executions
ledger / trace / forensics proof after convergence
```

---

## Proof Matrix

| Invariant | Expected | Validated |
|---|---:|---:|
| Tenant A submitted runs | 3 | yes |
| Tenant B submitted runs | 3 | yes |
| Tenant C submitted runs | 3 | yes |
| Tenant A recovered work | 3 | yes |
| Tenant B recovered work | 3 | yes |
| Tenant C recovered work | 0 | yes |
| Tenant A runtime process killed | true | yes |
| Tenant B runtime process killed | true | yes |
| Tenant C runtime process killed | false | yes |
| In-flight ExecutionId preserved | true | yes |
| Local queued work redispatched | true | yes |
| Safe tenant completed runs | 3 | yes |
| Safe tenant replay proofs | 3 | yes |
| Safe tenant recovery forensics | 0 | yes |
| Cross-tenant ledger leak | false | yes |
| Safe tenant recovery leak | false | yes |
| Strict replay validation | 9/9 | yes |

---

## What This Proves

This scenario proves that the runtime can:

```text
lose real runtime processes for two tenants
mark unsafe capacity without routing new work to it
recover only assigned work for impacted tenants
resume in-flight DAG executions with preserved ExecutionId
redispatch local queued shared runs through durable SharedRunId
create or use replacement tenant-visible capacity
complete all recovered work
complete safe tenant work normally
validate replay / ledger / trace after recovery
write per-work-item recovery forensics for impacted tenants
avoid recovery forensics for the safe tenant
avoid cross-tenant ledger visibility
avoid safe-tenant recovery contamination
```

The test proves more than durability.

It proves tenant-scoped recovery under process failure.

---

## What This Does Not Prove

This scenario is intentionally specific.

It does not prove:

```text
Kubernetes pod replacement
multi-control-plane leader election
network partition behavior between control plane and Redis/Mongo
corrupted durable storage recovery
Byzantine runtime behavior
global throughput limits
production autoscaling policy quality
full shared runtime pooling semantics
```

Those remain separate validation targets.

This scenario proves the runtime crash isolation contract under real process-host failure with durable Redis/Mongo-backed state and tenant-scoped MCP observability.

---

## Design Rules

### Do

```text
Use ExecutionContextSnapshot.TenantId as durable tenant boundary.
Restore tenant context before registry/capacity/admission/recovery queries.
Treat local queue as volatile.
Recover assigned work from durable shared run, queue, execution index, and DAG state.
Keep health reconciliation separate from execution recovery reconciliation.
Keep HTTP provider as transport/signal provider, not recovery owner.
Write recovery forensics only for impacted work.
Validate safe tenant normal completion and zero recovery surface.
Validate replay / ledger / trace after recovery.
Assert no cross-tenant ledger leakage.
```

### Do Not

```text
Do not recover by scanning all tenants globally.
Do not use ContextKey as durable tenant partition.
Do not treat runtime replacement as recovery completion.
Do not trust local queue state after process death.
Do not let impacted tenant recovery queries see safe tenant recovery entries.
Do not count safe tenant normal execution as recovery.
Do not make the HTTP provider kill, restart, or recover runtimes directly.
Do not mark validated health/recovery boundaries as future work.
```

---

## Related Documents

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Runtime Recovery Forensics](runtime-recovery-forensics.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability](observability.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

Do not describe runtime process crash recovery as global restart behavior.

Do not describe safe tenant isolation as absence of observability.

The correct statement is:

```text
Impacted tenants produce recovery observability.
Safe tenants produce normal execution observability.
Safe tenants produce zero recovery observability.
```

Crash recovery is only valid when both sides are proven.

```text
Failed work recovered.
Unrelated tenant work untouched.
```
