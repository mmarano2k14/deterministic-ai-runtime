# Runtime Recovery Forensics

Status: Implemented and validated for real process-host runtime crash recovery, in-flight DAG resume, local-queued redispatch, tenant-scoped recovery timelines, safe-tenant non-impact proof, and MCP-readable recovery evidence.

This document describes the runtime recovery forensics model used by the Deterministic AI Runtime.

Runtime recovery forensics is the durable audit surface that explains what happened to each work item assigned to a runtime instance after that runtime became unsafe.

It complements:

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)

---

## Purpose

A runtime crash recovery system is not complete if it only makes work finish eventually.

Production systems need to answer:

```text
Which runtime failed?
Which work was assigned to it?
Which work was in-flight?
Which work was only local-queued?
Which durable identity was preserved?
Which replacement runtime was selected?
Was the work redispatched or resumed?
Was recovery completed?
Was unrelated tenant work untouched?
Can this be queried after the fact?
```

Runtime recovery forensics exists to answer those questions with durable, tenant-scoped, per-work-item evidence.

It is not a console log.

It is not a transient debug trace.

It is a queryable recovery timeline that can be exposed through the MCP observability surface.

---

## Core Principle

Recovery forensics records recovery decisions at the work-item level.

The important unit is not only:

```text
runtime instance failed
```

The important unit is:

```text
this specific assigned work item was recovered through this exact path
```

A failed runtime may own different categories of work at the moment it becomes unsafe:

```text
InFlightExecution
    = a durable DAG execution already exists
    = ExecutionId must be preserved
    = recovery mode is resume existing execution

LocalQueued
    = work was dispatched to the runtime local queue but not yet executing
    = no ExecutionId may exist yet
    = SharedRunId must be redispatched without duplicate submission
```

The forensics model must preserve that distinction.

---

## Recovery Forensics vs Logs vs Trace vs Ledger

Runtime recovery produces several observability surfaces.

| Surface | Purpose |
|---|---|
| Logs | Human-readable operational diagnostics during the run. |
| Trace | Timeline and operation flow diagnostics. |
| Ledger | Structured decisions and lifecycle facts. |
| Recovery forensics | Per-incident and per-work-item recovery timeline. |
| Replay proof | Post-recovery validation that execution remains replayable and auditable. |

Forensics is not a replacement for ledger or trace.

It is the recovery-specific audit envelope that links runtime failure, assigned work, recovery mode, replacement selection, redispatch/resume, and completion.

---

## Identity Model

Runtime recovery forensics depends on preserving the correct identities.

```text
RuntimeFailureIncidentId
    identifies the runtime failure incident

ForensicsId
    identifies the recovery record for one assigned work item

ExecutionId
    durable DAG execution identity, present for in-flight executions

SharedRunId
    durable shared run submission identity

LocalRunId
    runtime-local attempt identity on a specific runtime instance

RuntimeInstanceId
    runtime instance that owned or later received the work

TenantId
    durable tenant boundary

TenantGroupId
    optional enterprise/group boundary
```

These identities must not be collapsed.

Especially:

```text
ExecutionId != LocalRunId
SharedRunId != LocalRunId
TenantId != ContextKey
```

---

## RuntimeFailureIncidentId

A runtime failure incident identifies the failed runtime instance and the failure window being reconciled.

Conceptual format:

```text
runtime-failure:{controlPlaneId}:{runtimeInstanceId}:{timestamp-or-incident-id}
```

Example shape from the validated scenario:

```text
RuntimeFailureIncidentId='runtime-failure:...:tenant-real-crash-a-runtime-1'
```

The incident id is attached to every recovered work item assigned to that unsafe runtime.

This allows operators and auditors to ask:

```text
Show me all work recovered because this runtime failed.
```

---

## ForensicsId

A forensics record is created per recovered work item.

The `ForensicsId` encodes the recovery category and durable identities.

### In-flight execution

For an execution that already had a durable `ExecutionId`:

```text
runtime-recovery:{ExecutionId}:{SharedRunId}:{LocalRunId}
```

This means:

```text
This specific durable execution was in-flight on this local runtime attempt and was recovered from this shared run.
```

### Local-queued run

For work that was dispatched to a dead runtime local queue but had not yet created a durable execution:

```text
runtime-recovery:local-queued:{SharedRunId}:{LocalRunId}
```

This means:

```text
This shared run had a failed local runtime queue attempt and was redispatched from durable shared-run state.
```

---

## Recovery Work Types

### InFlightExecution

An in-flight execution already has a durable DAG execution.

Required recovery behavior:

```text
ExecutionId before crash == ExecutionId after recovery
```

The replacement runtime must resume that same execution.

It must not create a new execution that happens to replay the same pipeline.

It must not consume business retry budget simply because the runtime process died.

### LocalQueued

A local-queued run may not have a durable `ExecutionId` yet.

Required recovery behavior:

```text
SharedRunId is requeued / redispatched
LocalRunId changes
ExecutionId is created only when the replacement runtime starts DAG execution
```

The system must not pretend that the dead local queue survived.

The correct source of truth is the durable shared run and shared queue state.

---

## Source of Truth

Runtime recovery forensics must be based on durable control-plane and execution records, not on volatile local queue memory.

Durable recovery truth includes:

```text
SharedRunStore
SharedQueue
RuntimeRunExecutionIndex
DAG execution store
Runtime registry
Runtime capacity store
Recovery forensics store
Decision ledger
Trace store
Replay artifacts
```

The local queue is intentionally volatile.

If the runtime process dies, the local queue dies with it.

Forensics should record that the work was recovered from durable records, not from a surviving local queue.

---

## Health vs Recovery Boundary

Runtime recovery forensics must preserve the separation between health and recovery.

```text
RuntimeInstanceHealthReconciler
    detects unsafe runtime capacity
    prevents unsafe routing
    marks runtime unavailable/draining/unhealthy where appropriate

RuntimeExecutionRecoveryReconciler
    enumerates assigned work on unsafe runtime instances
    opens forensics records
    resumes or redispatches work
    marks recovery complete

HTTP provider
    reports transport and endpoint failure signals
    does not own recovery
    does not kill or replace runtimes directly

Runtime Host Manager / lifecycle owner
    creates, starts, attaches, or supervises runtime hosts
```

The HTTP provider may surface signals such as:

```text
http-circuit-open
http-provider-unavailable
http-dispatch-timeout
```

Those signals can contribute to runtime health decisions.

They do not make the HTTP provider the recovery owner.

---

## In-Flight Resume Timeline

An in-flight recovered execution has a longer timeline because there is an existing DAG execution to resume.

Validated timeline shape:

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

Meaning:

| Step | Meaning |
|---|---|
| `execution.recovery.candidate.detected` | The reconciler found an in-flight execution assigned to an unsafe runtime. |
| `shared.run.requeued.for.resume` | The shared run was prepared for resume dispatch, not fresh submission. |
| `failed.local.run.marked.requeued.for.recovery` | The dead local run attempt was marked as recovered/requeued. |
| `replacement.runtime.selected` | Tenant-visible replacement capacity was selected. |
| `replacement.local.run.registered` | A new local runtime attempt was registered on the replacement runtime. |
| `resume.context.seeded` | The replacement runtime received resume context including the durable `ExecutionId`. |
| `dag.resume.started` | DAG resume began. |
| `dag.resume.completed` | DAG resume completed. |
| `execution.recovery.completed` | The recovery record was closed as completed. |

The presence of `dag.resume.started` and `dag.resume.completed` is important.

It proves the system resumed an existing execution instead of merely redispatching a new run.

---

## Local-Queued Recovery Timeline

A local-queued recovered run has a shorter timeline because no durable DAG execution may exist yet.

Validated timeline shape:

```text
SharedRunRequeuedForLocalQueuedRecovery
→ failed.local.run.marked.requeued.for.recovery
→ replacement.runtime.selected
→ replacement.local.run.registered
→ resume.context.seeded
```

Meaning:

| Step | Meaning |
|---|---|
| `SharedRunRequeuedForLocalQueuedRecovery` | The shared run was requeued because its local runtime queue attempt died before execution. |
| `failed.local.run.marked.requeued.for.recovery` | The failed local run attempt was recorded and marked as recovery-handled. |
| `replacement.runtime.selected` | Tenant-visible replacement capacity was selected. |
| `replacement.local.run.registered` | A replacement local run was created. |
| `resume.context.seeded` | The replacement runtime received the required shared-run/runtime context. |

Local-queued recovery is not DAG resume.

It is durable shared-run redispatch.

---

## Replacement Runtime Selection

Replacement runtime selection must use the same tenant-aware admission and capacity visibility rules as normal dispatch.

Recovery must not bypass admission.

Recovery must not dispatch to unrelated tenant capacity.

Recovery must not select an unsafe runtime.

Required selection inputs include:

```text
ExecutionContextSnapshot.TenantId
ExecutionContextSnapshot.TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
RuntimeInstanceIdPrefix
Runtime registry
Runtime capacity store
Health state
Admission reservation state where enabled
```

This makes recovery a tenant-scoped control-plane operation rather than a global panic path.

---

## Duplicate Recovery Protection

Recovery can be observed concurrently by background services, retries, or repeated reconciliation loops.

Forensics must support idempotence.

Required behavior:

```text
same failed work item + same incident
    → one effective recovery record
    → no duplicate redispatch
    → no duplicate resume
    → no duplicate completion proof
```

Duplicate runtime creation should be denied cleanly when the replacement already exists.

Example failure reason shape:

```text
process-runtime-instance-already-started:{runtimeInstanceId}
```

This is not a recovery failure.

It is expected idempotence under concurrent control-plane activity.

---

## Tenant Scope

Forensics records are tenant-scoped.

Every record must carry or be queryable through:

```text
TenantId
TenantGroupId
ControlPlaneId
RuntimeInstanceId
PipelineKey
SharedRunId
ExecutionId when available
```

A tenant-scoped query must not return another tenant's recovery record.

A safe tenant query must not show recovery records when that tenant's runtime was not affected.

A query from tenant A must not show tenant B forensics.

A query from tenant B must not show tenant A forensics.

---

## Safe Tenant Non-Impact Proof

The safe tenant proof is as important as the recovered tenant proof.

A multi-tenant recovery system must prove not only:

```text
Tenant A and Tenant B recovered.
```

It must also prove:

```text
Tenant C was not touched by recovery.
```

Validated safe tenant invariants:

```text
SafeTenantNonImpactValidated = true
SafeTenantRecoveryLeakDetected = false
CrossTenantLedgerLeakDetected = false
RuntimeProcessKilled = false
CrashImpacted = false
```

Expected safe tenant recovery surface:

```text
SubmittedRuns = 3
CompletedRuns = 3
ReplayProofs = 3
RecoveredWork = 0
RecoveryForensics = 0
RuntimeProcessKilled = false
CrashImpacted = false
```

This proves that recovery did not become a global side effect.

---

## Cross-Tenant Contamination Checks

Forensics must be validated together with tenant-scoped ledger queries.

Important checks include:

```text
TenantBEntriesVisibleFromTenantA = 0
TenantAEntriesVisibleFromTenantB = 0
SafeTenantRecoveryEntriesVisibleFromImpactedQueries = 0
CrossTenantLedgerLeakDetected = false
```

Forensics and ledger should agree:

```text
If safe tenant recovered work = 0
then safe tenant recovery forensics = 0
and impacted-tenant scoped queries must not expose safe-tenant recovery entries
```

---

## MCP Forensics API

Recovery forensics is intended to be exposed through the MCP control-plane observability surface.

The relevant capability boundary is:

```text
runtime-recovery:forensics:read
runtime-recovery:forensics:query
```

A caller without the required capability must not query recovery forensics.

A caller with the capability must still be constrained by the active tenant context.

The MCP layer is therefore both:

```text
capability-scoped
and
tenant-scoped
```

This matters for compliance because forensics can reveal runtime failure and recovery details.

---

## RBAC Boundary

Recovery forensics must be protected by TRN/capability checks before query execution.

Representative capabilities:

```text
trn:{project}:runtime-recovery:forensics:read
trn:{project}:runtime-recovery:forensics:query
trn:{project}:observability:ledger:read
trn:{project}:observability:ledger:query
trn:{project}:replay:execution:run
```

The capability check is not a replacement for tenant filtering.

Both are required.

---

## Replay, Ledger, and Trace Relationship

A recovery forensics record proves the recovery path for a work item.

Replay, ledger, and trace prove the recovered or unaffected execution remains auditable after convergence.

For a complete recovery proof, the system validates:

```text
execution ledger evidence
execution trace evidence
completion evidence
step completion evidence
replay report readable
replay ledger readable
replay trace readable
strict replay validation
```

In the validated three-tenant crash scenario:

```text
Replay validated executions = 9/9
Ledger evidence = 9/9
Trace evidence = 9/9
Completion evidence = 9/9
Step completion evidence = 9/9
```

This includes impacted tenants and the safe tenant.

---

## What Forensics Must Not Do

Recovery forensics must not:

```text
replace runtime recovery logic
replace replay validation
replace tenant authorization
pretend local queue memory is durable
mark safe tenants as recovered
create duplicate recovery records for the same work item
hide failed recovery attempts
mix tenant records in one unscoped query
consume business retry budget
```

Forensics records facts.

Recovery logic performs the recovery.

Replay validates the recovered execution surface.

Ledger records structured decisions.

Trace records runtime flow.

---

## Validated Scenario Shape

The strongest validated scenario is a real process-host multi-tenant crash recovery test.

Scenario:

```text
1 shared control plane
3 tenants
3 real external RuntimeInstanceOnly process-host runtimes
3 runs per tenant
50 DAG steps per run
Tenant A runtime process killed
Tenant B runtime process killed
Tenant C runtime process not killed
```

At kill time for each impacted tenant:

```text
1 InFlightExecution
2 LocalQueued
```

Expected recovery:

```text
Tenant A recovered work = 3
Tenant B recovered work = 3
Safe tenant recovered work = 0
Total recovered work = 6
```

Final proof:

```text
Total submitted runs = 9
Replay validated executions = 9
Safe tenant completed runs = 3
Safe tenant recovery forensics = 0
Cross-tenant ledger leak = false
Safe tenant recovery contamination = false
```

---

## Validated Invariants

Runtime recovery forensics validates these invariants:

```text
A failed runtime incident can be queried after recovery.
Every recovered work item has a forensics record.
In-flight recovery preserves ExecutionId.
Local-queued recovery redispatches through SharedRunId.
Replacement runtime selection is tenant-scoped.
Recovery timelines differ by work type.
Safe tenants have zero recovery forensics.
Cross-tenant forensics leakage is not allowed.
Recovery records remain readable after completion and replay validation.
Forensics, ledger, trace, and replay agree after convergence.
```

---

## Current Status

| Capability | Status |
|---|---|
| Runtime failure incident id | Implemented / validated |
| Per-work-item recovery forensics id | Implemented / validated |
| In-flight recovery forensics | Implemented / validated |
| Local-queued recovery forensics | Implemented / validated |
| In-flight resume timeline | Implemented / validated |
| Local-queued recovery timeline | Implemented / validated |
| Replacement runtime selection evidence | Implemented / validated |
| Resume context seeded evidence | Implemented / validated |
| DAG resume started/completed evidence | Implemented / validated |
| Recovery completed evidence | Implemented / validated |
| Safe tenant zero-forensics proof | Implemented / validated |
| Tenant-scoped forensics querying | Implemented / validated |
| MCP forensics capability boundary | Implemented / validated |
| Replay/ledger/trace correlation after recovery | Implemented / validated |
| Duplicate recovery protection evidence | Implemented / validated |
| UI for recovery forensics | Planned |
| Exported recovery incident report format | Planned |
| Long-term recovery analytics dashboard | Planned |

---

## Responsibilities by Component

| Component | Responsibility |
|---|---|
| RuntimeInstanceHealthReconciler | Detects unsafe runtime capacity and prevents unsafe routing. |
| RuntimeExecutionRecoveryReconciler | Enumerates assigned work and drives recovery. |
| Recovery forensics recorder/store | Records incident and per-work-item recovery timelines. |
| SharedRunStore | Provides durable shared-run source of truth. |
| SharedQueue | Provides durable redispatch path. |
| RuntimeRunExecutionIndex | Links shared/local runtime work to durable execution identity where available. |
| DAG store | Provides durable execution state and step position for resume. |
| Runtime registry/capacity stores | Provide tenant-visible replacement capacity and unsafe runtime state. |
| Runtime Host Manager | Creates or attaches replacement runtime capacity when needed. |
| HTTP provider | Reports transport failure signals and dispatches over HTTP; does not own recovery. |
| MCP observability tools | Expose tenant-scoped forensics, ledger, trace, and replay proof. |

---

## Testing Requirements

Recovery forensics tests should validate:

```text
real process-host runtime kill
unsafe runtime detection
assigned work enumeration
in-flight recovery forensics record creation
local-queued recovery forensics record creation
in-flight timeline step order
local-queued timeline step order
ExecutionId preserved for in-flight resume
SharedRunId preserved for local-queued redispatch
replacement runtime selected under tenant context
safe tenant has zero recovery forensics
cross-tenant forensics queries return zero unrelated records
recovery records remain readable after completion
recovery records remain readable after replay validation
ledger and trace evidence exist for recovered executions
strict replay validation passes for recovered and safe executions
```

The key rule:

```text
Recovery is not complete only because work completed.
Recovery is complete when completion, forensics, ledger, trace, and replay agree.
```

---

## Design Rules

### Do

```text
Create one forensics record per recovered work item.
Preserve the distinction between InFlightExecution and LocalQueued.
Use ExecutionId only when a durable execution already exists.
Use SharedRunId as durable submission identity.
Record replacement runtime selection.
Record resume context seeding.
Record DAG resume events for in-flight recovery.
Keep records tenant-scoped.
Make records queryable after recovery completes.
Validate safe tenants have zero recovery forensics.
```

### Do Not

```text
Do not treat local queue memory as durable truth.
Do not create a new ExecutionId for in-flight recovery.
Do not consume retry budget because a runtime process died.
Do not let HTTP provider own runtime recovery.
Do not mix health reconciliation with execution recovery.
Do not write recovery forensics for safe tenants.
Do not rely on logs as the only recovery evidence.
Do not expose forensics without capability and tenant checks.
```

---

## Future Improvements

Planned improvements should build on the current validated model.

Possible next steps:

```text
Recovery incident report export
MCP recovery incident summary tool
Dashboard timeline for runtime failure incidents
Recovery duration metrics
Recovery SLA metrics
Failure reason grouping
Replacement runtime selection diagnostics
Duplicate recovery attempt reporting
Cross-control-plane recovery analytics
Long-term storage/indexing for recovery incidents
```

These are additive.

They should not change the core recovery boundary:

```text
Health detects unsafe capacity.
Execution recovery recovers assigned work.
Forensics records what happened.
Ledger records decisions.
Trace records flow.
Replay proves post-recovery auditability.
```

---

## Related Documents

- [Runtime Process Crash Recovery](runtime-process-crash-recovery.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Recovery Replay Ledger Trace Proof](recovery-replay-ledger-trace-proof.md)
- [Control-Plane Ledger Causal Chain](control-plane-ledger-causal-chain.md)
- [Retry and Recovery](retry-and-recovery.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Execution-Correlated Decision Ledger](execution-correlated-ledger.md)
- [Observability](observability.md)
- [Observability, Metrics, and Tracing](observability-tracing.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

Do not describe recovery forensics as a future capability.

The current process-host crash recovery scenarios validate per-work-item recovery forensics, safe tenant zero-forensics proof, tenant-scoped recovery visibility, and replay/ledger/trace evidence after recovery.

Do not describe the HTTP provider as the recovery owner.

The HTTP provider reports transport failures and dispatches over HTTP. Runtime health reconciliation, execution recovery reconciliation, and host lifecycle remain separate responsibilities.
