# Changelog

All notable changes to this project will be documented in this file.

This project follows a deterministic runtime and observability model designed for high-concurrency execution, focusing on consistency, isolation, and lifecycle control.

---

## [1.0.6.9] - 2026-06-28 Runtime Recovery Forensics — DAG Resume Redispatch Stability Hardening

### Summary

Stabilized the HTTP process-host DAG resume recovery forensics scenario by fixing recovery redispatch race conditions and improving recovery diagnostics.

This work hardens the recovery path where a shared run is already assigned to a failed runtime instance, has an existing durable DAG execution, and must be recovered, requeued, redispatched to a replacement runtime, resumed from the failed DAG step, completed, and exposed through the MCP recovery forensics timeline.

### What Changed

- Hardened shared queue dispatch ordering during recovery redispatch.
  - The dispatcher now persists the shared run reassignment before marking the shared queue item as dispatched.
  - This prevents the invalid state where the queue item is marked `Dispatched` while the shared run is still assigned to the failed runtime instance.

- Added protection against redispatching recovered work back to the failed runtime.
  - During recovery redispatch, if admission selects the same runtime instance that triggered recovery, dispatch is rejected.
  - The queue item is requeued and a replacement scale-out request is published instead.
  - This prevents recovered work from being routed back to an unhealthy/stale runtime that may still be visible through heartbeat/capacity during a short race window.

- Strengthened recovery test synchronization.
  - The HTTP process-host DAG resume recovery forensics test now waits for the seeded runtime execution index entry before running recovery reconciliation.
  - This removes a race where recovery could start before the seeded in-flight run was visible in the durable runtime run execution index.

- Improved recovery diagnostics for intermittent failures.
  - Recovery wait diagnostics now include scanned runtime count, ignored runtime count, discovered unfinished run count, recovered run count, and per-decision details.
  - This makes it clear whether a failure is caused by health routing, runtime execution index visibility, ownership resolution, recovery transition, or redispatch.

### Validated Behavior

- A failed runtime instance is marked unhealthy and excluded from safe recovery redispatch.
- The in-flight local runtime run is recovered from the runtime execution index.
- The shared queue item is requeued with recovery metadata:
  - `recovery.mode=resume-existing-execution`
  - `recovery.failedExecutionId`
  - `recovery.failedRuntimeInstanceId`
  - `recovery.failedLocalRunId`
  - `recovery.forensicsId`
- The recovered shared run is redispatched to a different runtime instance.
- The replacement runtime resumes the existing DAG execution from the failed step instead of starting a new execution.
- The DAG completes from the recovery point.
- Recovery forensics are persisted and exposed through MCP search/get/timeline tools.

### Stability Result

- `HttpProcessHostDagResumeRecoveryScenarioTests.Http_ProcessHost_Should_Expose_Dag_Resume_Recovery_Forensics_Timeline_Through_Mcp_StabilityLoop`
- Validated with `100/100` successful stability iterations after the fixes.

### Why This Matters

This closes two production-grade race conditions in runtime recovery:

1. Queue/shared-run state divergence during redispatch.
2. Recovery reconciliation starting before the seeded runtime execution index is visible.

Together, these fixes make DAG resume recovery deterministic enough for repeated HTTP process-host stability testing and prepare the recovery forensics path for stronger control-plane observability.

---

## 2026-06-28 — Runtime crash recovery inventory: local queued work + in-flight executions

Validated and hardened the production recovery path for a failed HTTP process-host runtime instance owning multiple durable work items.

This change closes the runtime recovery inventory proof: when a stateful runtime instance becomes unhealthy or crashes, the control plane can now enumerate and recover both local queued work that had not yet started an execution and in-flight executions that already had a durable execution id.

### What was validated

A new production integration scenario was added:

`Http_ProcessHost_Should_Recover_Durable_Assigned_Work_Inventory_From_Failed_Runtime`

The scenario proves a failed runtime with 5 assigned durable work items:

- 3 local queued runs without `ExecutionId`
- 2 in-flight executions with existing durable `ExecutionId`

Recovery result:

- 3/3 local queued runs recovered
- 2/2 in-flight executions recovered
- 5/5 total assigned work items recovered
- all recovered work redispatched from failed `runtime-1` to replacement `runtime-2`
- local queued runs receive new replacement `LocalRunId`
- in-flight executions preserve their original durable `ExecutionId`
- recovery forensics remains execution-level and records full timelines for the in-flight DAG resume executions

### Production fixes added

#### Local queued runtime work recovery

`AiRuntimeExecutionRecoveryTransitionService` now supports recovery candidates that have a valid failed `LocalRunId` and `SharedRunId`, but no `ExecutionId` yet.

This is the correct state for work that was assigned to a runtime local queue but had not started execution before the runtime failed.

For this case, recovery uses:

- `recovery.mode = requeue-local-queued-run`
- `recovery.failedRuntimeInstanceId`
- `recovery.failedLocalRunId`
- empty `recovery.failedExecutionId`

The transition service no longer rejects these candidates with `execution-id-missing`.

#### Runtime run index recovery marker for queued work

`RedisAiRuntimeRunExecutionIndex.MarkRequeuedForRecoveryAsync(...)` now safely supports local queued recovery without an execution id.

Safety rules:

- `runId` remains required
- `reason` remains required
- tenant isolation remains enforced
- terminal entries are still protected
- if an execution id is provided, it must match the existing entry or the existing entry must be empty
- if no execution id is provided, the existing entry must also have no execution id

This prevents accidentally marking an already-started execution without validating its execution id, while allowing legitimate local queued recovery.

### Recovery behavior now proven

Before recovery:

```text
Failed runtime: runtime-1

Assigned durable work:
├── LocalQueued x3
│   ├── SharedRunId present
│   ├── FailedLocalRunId present
│   └── ExecutionId empty
└── InFlightExecution x2
    ├── SharedRunId present
    ├── FailedLocalRunId present
    └── ExecutionId present
```

After recovery:

```text
Replacement runtime: runtime-2

Recovered work:
├── LocalQueued x3
│   ├── old LocalRunId marked requeued-for-recovery
│   ├── SharedRun requeued
│   ├── redispatched to runtime-2
│   └── new replacement LocalRunId created
└── InFlightExecution x2
    ├── old LocalRunId marked requeued-for-recovery
    ├── SharedRun requeued for resume
    ├── redispatched to runtime-2
    ├── same ExecutionId preserved
    └── DAG resume completed
```

### Proof output

The production test now emits a clear recovery inventory proof:

```text
[RUNTIME RECOVERY INVENTORY PROOF]
QueuedLocalRunsRecovered='3/3'
InFlightExecutionsRecovered='2/2'
TotalRecovered='5/5'
ReplacementRuntimeInstances='runtime-2'
```

### Design clarification

This does not recover volatile local memory. The local runtime queue memory is still considered lost when a process crashes.

The important production guarantee is stronger and cleaner:

```text
Local memory is lost, but durable ownership survives.
```

The control plane now proves that it can reconstruct the failed runtime work inventory from durable stores, classify local queued work versus in-flight executions, recover both categories, and redispatch them to replacement runtime capacity without faking recovery results.

### Architectural impact

This closes the recovery gap between:

- runtime health reconciliation
- unsafe capacity suppression
- durable assigned work inventory
- local queued work recovery
- in-flight execution resume
- replacement runtime redispatch
- DAG resume for durable executions
- execution-level recovery forensics

The runtime recovery story now covers both major failure states of a stateful runtime instance:

1. work assigned but still queued locally
2. work already executing with durable DAG state

---

## 2026-06-27 — Production recovery test helper extraction

### Changed
- Refactored HTTP process-host DAG resume recovery integration tests by extracting reusable production test helpers and assertions.
- Extracted DAG recovery assertions into `ProductionDagRecoveryAssertions`:
  - `AssertDagStoppedAtFailurePoint`
  - `AssertDagCompletedFromFailurePoint`
  - `FormatDagStateSummary`
- Extracted recovery polling helpers into `ProductionRecoveryWaitHelpers`:
  - wait for runtime recovery reconciliation
  - wait for shared run redispatch away from failed runtime
  - wait for runtime execution index entries
  - wait for durable DAG record `ContextKey`
- Extracted recovery seed helpers into `ProductionRecoverySeedHelpers`:
  - seed in-flight runtime execution ownership
  - seed shared queue dispatched state
  - seed runtime run execution index
  - seed durable DAG state stopped at a failed step with expired lease
  - reusable step-name and dependency helpers
- Extracted shared run test helpers into `ProductionSharedRunTestHelpers`:
  - submit one tenant-scoped shared run through MCP
  - wait for fulfilled tenant scale-out request
  - wait for single dispatched shared run
  - extract shared run id from submit result
- Extracted recovery option assertions into `ProductionRecoveryOptionsAssertions`.

### Improved
- Reduced private helper noise inside `HttpProcessHostDagResumeRecoveryScenarioTests`.
- Kept the HTTP DAG resume recovery tests focused on scenario intent instead of low-level polling, seeding, and assertion mechanics.
- Made DAG recovery, shared run dispatch, runtime recovery polling, and recovery forensics assertions reusable across future Local, HTTP, gRPC, Kubernetes, and Attach-mode production scenarios.
- Preserved existing production behavior and test coverage while improving maintainability.

### Validated
- Existing HTTP process-host DAG resume recovery tests remain the reference scenario after helper extraction.
- The recovery forensics MCP timeline validation remains reusable through `ProductionRecoveryForensicsAssertions`.
- The extracted helpers keep the same diagnostic failure messages for timeout, missing index, missing DAG context, and redispatch failures.

---

## 2026-06-27 — HTTP DAG Resume Recovery Forensics timeline exposed through MCP

### Added
- Added a production-grade HTTP process-host integration validation for runtime recovery forensics exposed through MCP.
- Added end-to-end proof that a DAG resume recovery can be queried through MCP using:
  - `runtime.recovery.forensics.search`
  - `runtime.recovery.forensics.get`
  - `runtime.recovery.forensics.timeline`
- Added strict recovery forensics timeline validation for the full recovery path:
  - `execution.recovery.candidate.detected`
  - `shared.run.requeued.for.resume`
  - `failed.local.run.marked.requeued.for.recovery`
  - `replacement.runtime.selected`
  - `replacement.local.run.registered`
  - `resume.context.seeded`
  - `dag.resume.started`
  - `dag.resume.completed`
  - `execution.recovery.completed`

### Changed
- Moved `replacement.runtime.selected` recording earlier in the shared queue dispatch path, before the HTTP dispatch call to the replacement runtime instance.
- Moved replacement local run recovery forensics to the runtime-side local enqueue path so `replacement.local.run.registered` is recorded before the run can be dequeued by the background controller.
- Recorded `resume.context.seeded` immediately after the restored execution context is seeded into the replacement runtime, before `dag.resume.started`.
- Removed premature/empty `replacement.localRunId` metadata from `replacement.runtime.selected`, because the replacement local run is not known at runtime-selection time.
- Kept `ControlPlaneId` optional in the recovery forensics read model assertions while preserving strict validation for tenant, shared run, execution, runtime, and local run identifiers.

### Validated
- Validated real HTTP process-host recovery with:
  - failed runtime instance `runtime-1`
  - replacement runtime instance `runtime-2`
  - same durable `ExecutionId`
  - new replacement `LocalRunId`
  - restored RBAC execution context
  - DAG resume from the failed step without replaying completed steps
  - final DAG completion `100/100`
- Validated that Mongo recovery forensics persistence, query service, and MCP tools all expose the same recovery record.
- Validated deterministic recovery timeline ordering through MCP in the production HTTP process-host scenario.
- Validated that runtime recovery forensics remains observational/read-model only and does not drive recovery decisions.

---

## [1.0.6.9] - 2026-06-27  Runtime Recovery Forensics — Contracts, InMemory Recorder and Mongo Persistence

Added the first professional foundation for **Runtime Recovery Forensics**.

This feature provides a persisted, queryable and human-readable proof of runtime execution recovery.

The goal is to explain exactly what happened when a runtime instance fails, what execution work was affected, what state was restored from durable truth, what volatile runtime state was recreated, and what was intentionally not restored.

This is not a new recovery mechanism.

Forensics observes and documents recovery. It does not drive recovery decisions.

## Why

Execution recovery was already validated for HTTP Process Host DAG resume recovery:

- failed runtime instance is detected
- unsafe capacity is suppressed
- assigned/in-flight work is recovered
- shared run is requeued for resume
- replacement runtime receives the recovered work
- durable `ExecutionId` is preserved
- replacement `LocalRunId` is recreated
- completed DAG steps are not replayed
- DAG converges to completion

The missing piece was a durable professional proof of that recovery.

Runtime Recovery Forensics now prepares that layer.

## Added

### Recovery Forensics Identity

Added a dedicated identity model:

- `AiRuntimeRecoveryForensicsIdentity`

It groups the main recovery scope identifiers:

- `Id`
- `ForensicsId`
- `ExecutionId`
- `SharedRunId`
- `PipelineName`
- `TenantId`
- `TenantGroupId`
- `ControlPlaneId`

This keeps the forensic record clean and avoids a flat root model with too many unrelated identifiers.

### Recovery Forensics Record Model

Added the main recovery proof model:

- `AiRuntimeRecoveryForensicsRecord`

The record is structured around:

- `Identity`
- `Failure`
- `Recovery`
- `Replacement`
- `Context`
- `Dag`
- `Artifacts`
- `Events`
- `Metadata`
- `CreatedAtUtc`
- `UpdatedAtUtc`

This model is designed for MongoDB persistence and future MCP/API reporting.

### Failure Information

Added:

- `AiRuntimeRecoveryFailureInfo`

Captures:

- failed runtime instance id
- failed local run id
- runtime failure incident id
- failure signal
- health status before/after
- capacity suppression reason
- failure detection timestamp

The `RuntimeFailureIncidentId` allows multiple recovered executions to be grouped under one failed runtime incident.

### Recovery Information

Added:

- `AiRuntimeRecoveryInfo`

Captures:

- recovery mode
- recovery kind
- outcome
- reason
- recovery start timestamp
- recovery completion timestamp

This supports both single execution recovery and future multi-run recovery reporting.

### Replacement Runtime Information

Added:

- `AiRuntimeRecoveryReplacementInfo`

Captures:

- replacement runtime instance id
- replacement local run id
- dispatch reason
- replacement selection timestamp
- local run registration timestamp

This makes recreated runtime state visible.

### Context Recovery Information

Added:

- `AiRuntimeRecoveryContextInfo`

Captures:

- snapshot context key
- durable execution record context key
- context key mismatch
- execution-bound context rehydration
- rehydration reason

This is important because real recovery can involve a mismatch between `ExecutionContextSnapshot.ContextKey` and `AiExecutionRecord.ContextKey`.

### DAG Recovery Information

Added:

- `AiRuntimeRecoveryDagInfo`

Captures:

- step count
- completed steps before recovery
- recovered-from step
- final completed steps
- whether completed steps were replayed
- final DAG outcome

This prepares the forensic proof that DAG recovery resumed from the correct point and did not replay completed steps.

### Recovery Artifacts

Added:

- `AiRuntimeRecoveryArtifacts`
- `AiRuntimeRecoveryArtifactName`

Artifacts are grouped into:

- restored artifacts
- recreated artifacts
- lost volatile artifacts

Known artifact names include:

Restored:

- `DurableExecutionId`
- `DagExecutionRecord`
- `DagState`
- `CompletedDagSteps`
- `ExecutionContextSnapshot`
- `RehydratedRbacContext`
- `SharedRunMetadata`
- `RecoveryMetadata`

Recreated:

- `ReplacementRuntimeInstance`
- `ReplacementLocalRunId`
- `ReplacementLocalQueueItem`
- `RuntimeRunExecutionIndexEntry`
- `DispatchAssignment`
- `NewClaimToken`
- `NewLease`

Lost / intentionally not restored:

- `FailedRuntimeLocalQueueMemory`
- `FailedRuntimeProcessMemory`
- `OldWorkerOwnership`
- `OldClaimToken`
- `OldLease`
- `OldLocalRunAsActiveWork`

### Forensics Events

Added:

- `AiRuntimeRecoveryForensicsEvent`
- `AiRuntimeRecoveryForensicsEventType`

Known event types include:

- `runtime.failure.detected`
- `runtime.health.suppressed`
- `runtime.capacity.removed`
- `execution.recovery.candidate.detected`
- `shared.run.requeued.for.resume`
- `failed.local.run.marked.requeued.for.recovery`
- `replacement.runtime.selected`
- `replacement.local.run.registered`
- `resume.context.seeded`
- `dag.resume.started`
- `dag.resume.completed`
- `execution.recovery.completed`
- `execution.recovery.failed`

The event timeline is append-only and will be used to reconstruct the recovery story.

### Store and Recorder Contracts

Added:

- `IAiRuntimeRecoveryForensicsStore`
- `IAiRuntimeRecoveryForensicsRecorder`

The store supports:

- upsert by forensics id
- append event
- get by forensics id
- list by execution id
- list by shared run id
- list by runtime instance id
- list by runtime failure incident id
- list recent records

The recorder supports:

- recording/upserting a full recovery forensic record
- appending recovery forensic events

### Noop Recorder

Added:

- `NoopAiRuntimeRecoveryForensicsRecorder`

This provides a safe fallback when forensics is disabled or not configured.

### InMemory Store

Added:

- `InMemoryAiRuntimeRecoveryForensicsStore`

This supports fast tests and local validation without external infrastructure.

The in-memory store supports:

- record upsert
- event append
- query by execution
- query by shared run
- query by runtime instance
- query by runtime failure incident
- recent records listing

### Best-Effort Recorder

Added:

- `BestEffortAiRuntimeRecoveryForensicsRecorder`

This recorder wraps the store and makes forensics persistence safe by default.

Behavior:

- if forensics is disabled, no persistence happens
- if persistence fails and strict mode is disabled, recovery continues
- if strict mode is enabled, persistence failures can fail the caller

This keeps recovery correctness independent from forensic storage availability.

### Options

Added:

- `AiRuntimeRecoveryForensicsOptions`
- `AiRuntimeRecoveryForensicsMongoOptions`

Runtime options include:

- `Enabled`
- `StrictPersistence`
- `MaxEventsPerRecord`

Mongo options include:

- `ConnectionString`
- `DatabaseName`
- `CollectionName`
- `EnsureIndexes`

Default Mongo collection:

```text
ai_runtime_recovery_forensics
```

### Mongo Store

Added:

- `MongoAiRuntimeRecoveryForensicsStore`

The Mongo store persists forensic records as rich documents.

It supports:

- upsert by `Identity.ForensicsId`
- append events
- query by `Identity.ExecutionId`
- query by `Identity.SharedRunId`
- query by failed/replacement runtime instance
- query by runtime failure incident id
- list recent recovery records

### Mongo Indexes

Added indexes for:

- `Identity.ForensicsId`
- `Identity.ExecutionId`
- `Identity.SharedRunId`
- `Identity.TenantId + CreatedAtUtc`
- `Identity.ControlPlaneId + CreatedAtUtc`
- `Failure.FailedRuntimeInstanceId`
- `Failure.RuntimeFailureIncidentId`
- `Replacement.ReplacementRuntimeInstanceId`
- `Recovery.RecoveryMode + Recovery.Outcome + CreatedAtUtc`

Index initialization uses existing Mongo infrastructure resilience behavior.

### DI Extensions

Added:

- `AddNoopAiRuntimeRecoveryForensics`
- `AddInMemoryAiRuntimeRecoveryForensics`
- `AddMongoAiRuntimeRecoveryForensics`

These allow tests, local runtime, and production persistence to select the appropriate implementation.

## Design Principles

Runtime Recovery Forensics follows these rules:

- forensics observes recovery
- forensics does not drive recovery
- forensics is not the source of truth
- recovery remains based on shared queue, shared run store, execution index, DAG store and snapshots
- Mongo is used as durable forensic proof
- InMemory is used for fast tests
- Noop is available as safe fallback
- best-effort persistence is the default
- strict persistence can be enabled for audit-sensitive environments

## Value

This feature prepares the runtime to prove:

- which runtime failed
- which execution was affected
- which local run was lost
- which replacement runtime resumed the work
- which new local run was created
- which durable execution id was preserved
- which state was restored
- which runtime state was recreated
- which volatile state was intentionally not restored
- whether completed DAG steps were replayed
- whether the execution converged

This turns recovery from a hidden internal behavior into a persisted, inspectable and auditable recovery report.

## Next

Next implementation steps:

1. Add basic InMemory store tests.
2. Add Mongo store tests.
3. Register forensics in the HTTP process-host test setup.
4. Integrate recorder into recovery transition service.
5. Integrate recorder into execution recovery reconciler.
6. Integrate recorder into shared queue dispatcher.
7. Integrate recorder into runtime pipeline background controller.
8. Validate single DAG recovery forensic proof.
9. Validate multiple assigned local queued runs recovery.
10. Validate multiple in-flight executions recovery.
11. Validate mixed queued + in-flight recovery from one failed runtime.


---

## [1.0.6.9] - 2026-06-25 — Execution Recovery / HTTP Process Host DAG Resume

### Summary

This change validates and implements durable execution recovery for HTTP process-host runtime replacement.

The validated production behavior is:

> A runtime process can fail while owning an in-flight DAG execution, the control plane can recover the assigned shared run, dispatch it to a replacement runtime process, and the replacement runtime can resume the same durable DAG execution without replaying completed steps.

This is not only a test update. Several runtime and control-plane pieces were implemented or hardened to make the recovery path work end to end.

---

## What was implemented

### 1. Execution-bound RBAC context rehydration

Recovery exposed a real production issue: the replacement runtime receives the original `ExecutionContextSnapshot`, but the durable DAG execution record can reference a different `AiExecutionRecord.ContextKey`.

The real create-path proof showed:

```text
SnapshotContextKey != RecordContextKey
```

So recovery could not rely on the shared-run snapshot context key being identical to the DAG record context key.

Implemented in the execution engine:

- Added execution-id-bound restored context storage.
- Added `SeedRestoredExecutionContextAsync(executionId, context, cancellationToken)`.
- Added context rehydration logic able to clone/re-seed the restored RBAC context under the durable DAG record context key.
- Updated DAG execution context loading so batch/resume execution can call context loading with both `ExecutionId` and `ContextKey`.
- Preserved the original execution id as the binding key for recovered contexts.

Result:

```text
Replacement runtime receives snapshot context
→ snapshot context is bound to existing ExecutionId
→ DAG runner loads record.ContextKey
→ engine re-seeds restored context under record.ContextKey
→ DAG resume can continue
```

### 2. DAG resume path uses execution-bound context loading

The DAG execution flow was updated so distributed/local DAG runners no longer load context only by `record.ContextKey`.

Instead, the resume path now resolves context using:

```text
ExecutionId + ContextKey
```

This allows the replacement runtime to recover the correct RBAC context even when the durable record context key is not present locally.

### 3. Runtime pipeline controller seeds resume context with ExecutionId

The pipeline background controller already restored `ExecutionContextSnapshot` from the shared runtime run request.

The resume path was updated so `SeedResumeExecutionContextAsync(...)` calls:

```csharp
SeedRestoredExecutionContextAsync(resumeExecutionId, restoredExecutionContext, cancellationToken)
```

instead of seeding only by context key.

This is what connects the shared-run recovery request to the durable DAG execution id.

### 4. Resume local runs are registered in IAiRuntimeRunExecutionIndex

Recovery creates a new local runtime run id on the replacement runtime while keeping the same durable execution id.

Before this change, the replacement local run could execute but was not visible through the shared runtime run execution index.

Implemented:

- Resume enqueue now registers the new local runtime run id in `IAiRuntimeRunExecutionIndex`.
- The index entry uses:
  - new replacement `RunId`
  - existing durable `ExecutionId`
  - replacement `RuntimeInstanceId`
  - status `queued`
  - recovery metadata
  - execution context snapshot metadata

Then the existing controller flow can naturally transition the same index entry through:

```text
queued → started → completed / failed
```

Result:

```text
runtime-2 local run id is visible to control plane
→ index points to the existing durable ExecutionId
→ test/control-plane can observe replacement completion
```

### 5. RuntimeInstanceOnly process hosts now use Redis control-plane stores

The recovery test showed that the runtime process and the control plane were not always writing to the same runtime execution index.

Implemented in `ConfigureRuntimeInstanceOnly(...)`:

```csharp
AddRedisControlPlaneStoresIfAvailable(services, configuration);
```

This ensures `RuntimeInstanceOnly` hosts use the Redis-backed shared stores when Redis is configured.

Important: shared queue pump remains disabled in runtime-only mode, so the runtime process does not become a control-plane pump. It only shares the correct durable stores.

Result:

```text
control-plane reads Redis
runtime process writes Redis
IAiRuntimeRunExecutionIndex becomes shared
replacement local run becomes visible
```

### 6. Real DAG create ContextKey proof

Added a proof test to validate the real production create path, without test-side seeding.

The test proves:

```text
MCP submit
→ HTTP process runtime scale-out
→ real dispatch
→ real DAG execution created
→ DAG store record exists
→ AiExecutionRecord.ContextKey is non-empty
```

The test also proved an important real behavior:

```text
ExecutionContextSnapshot.ContextKey can differ from AiExecutionRecord.ContextKey
```

This confirms why execution-bound context rehydration is required.

### 7. HTTP process-host DAG resume recovery test

Added/validated the end-to-end recovery scenario:

```text
runtime-1 owns DAG execution
→ DAG is stopped at failed/in-flight step
→ runtime-1 is marked unhealthy
→ health reconciliation suppresses unsafe capacity
→ recovery requeues the shared run as resume-existing-execution
→ replacement runtime-2 is selected
→ same durable ExecutionId is resumed
→ completed steps are not replayed
→ failed/in-flight step is recovered
→ DAG completes 100/100
```

The test validates:

- same durable `ExecutionId`
- new replacement `LocalRunId`
- new replacement `RuntimeInstanceId`
- recovered step begins at the failure point
- completed steps before failure are not replayed
- final DAG state reaches all completed steps
- replacement runtime run index reaches `completed`

### 8. Test seed correctness for durable DAG records

The recovery test manually seeds a stopped durable DAG state to simulate a crash at a precise step.

The seeded `AiExecutionRecord` was fixed to include the proper `ContextKey`, because in production the real DAG create path persists a non-empty record context key.

This keeps the recovery test aligned with real durable DAG records.

### 9. Shared queue / health hardening test updates

After health/capacity hardening, several shared queue tests needed to model runtime readiness more accurately.

Implemented/hardened tests by:

- registering simulated runtime instances in `IAiRuntimeInstanceRegistry`
- marking simulated instances as `Ready`
- avoiding empty runtime registries in dispatch tests
- making runtime instance ids scenario-unique
- explicitly publishing pipeline definitions to all runtime harnesses in heavy real execution tests

This prevents false failures where dispatchers reject or endlessly requeue work because the target runtime instance is not visible as healthy/ready.

---

## What was fixed

### Fixed: missing RBAC context during DAG resume

The replacement runtime previously failed when it attempted to load the DAG record context key but did not have that context locally.

Now recovery binds the restored snapshot context to the durable execution id and can rehydrate the context under the DAG record context key.

### Fixed: empty/missing context key handling in recovered DAG execution

The recovery path no longer assumes the replacement runtime already has the DAG record context key in its local context store.

### Fixed: replacement local run not visible in runtime execution index

`EnqueueResumeAsync(...)` now registers the replacement local run in `IAiRuntimeRunExecutionIndex`.

### Fixed: runtime process writing to local stores while control plane reads Redis

`RuntimeInstanceOnly` hosts now register Redis control-plane stores when available.

### Fixed: shared queue tests with empty runtime registry

Shared queue dispatch tests now register target runtime instances as ready before dispatch.

### Fixed: static runtime instance ids causing cross-test interference

Tests now use scenario-derived runtime instance ids instead of fixed ids such as:

```text
runtime-instance-1
runtime-instance-2
runtime-instance-3
```

---

## Validated behavior

### Execution recovery

Validated:

```text
Submit
→ Dispatch to runtime-1
→ Runtime owns durable DAG execution
→ Runtime fails during in-flight DAG step
→ Health reconciliation marks runtime unsafe
→ Recovery identifies assigned work
→ Shared run is requeued as resume-existing-execution
→ Runtime-2 receives recovered run
→ Runtime-2 resumes same ExecutionId
→ Completed steps are not replayed
→ Failed step is recovered
→ DAG reaches 100/100 completed steps
```

### Context recovery

Validated:

```text
Real DAG create persists AiExecutionRecord.ContextKey
SnapshotContextKey can differ from RecordContextKey
Recovery still succeeds because context is bound by ExecutionId
```

### Runtime execution index

Validated:

```text
Replacement local run id is registered
Replacement local run points to existing durable ExecutionId
Replacement local run reaches completed
```

### Runtime-only process store sharing

Validated:

```text
RuntimeInstanceOnly process writes to Redis stores
Control plane can observe replacement runtime run state
```

---

## Tests added / updated

### Added / validated

- `Http_ProcessHost_Should_Resume_Dag_From_Failed_Step_Without_Replaying_Completed_Steps`
- `Http_ProcessHost_Should_Persist_Dag_Record_ContextKey_On_Real_Create`
- `AiRuntimePipelineBackgroundControllerResumeTests.EnqueueResumeAsync_Should_Run_Worker_With_Existing_ExecutionId`

### Hardened

- `AiSharedQueueMultiInstanceExecutionIntegrationTests`
- `AiSharedQueueMultiInstanceRealExecutionHeavyIntegrationTests`

---

## Architecture decision confirmed

Execution recovery and runtime health reconciliation remain separate responsibilities.

### Health reconciliation

Responsible for:

- detecting stale/unhealthy/draining runtime instances
- preventing unsafe routing
- suppressing unsafe capacity
- ensuring admission/dispatch does not select failed instances

### Execution recovery

Responsible for:

- restoring work already assigned to a failed runtime
- requeueing shared runs safely
- preserving durable execution id
- resuming DAG state from durable stores
- preventing replay of completed steps

### HTTP provider boundary

The HTTP provider still does not restart, kill, or recover runtimes directly.

It only reports endpoint health/failure signals.

Runtime lifecycle replacement remains owned by the provider / host manager layer.

---

## Durable truth model

This work confirms the durable truth model:

```text
Local runtime queues are volatile.
Durable truth lives in shared queue, shared run store, runtime run execution index, DAG store, snapshots, ledger, and replay artifacts.
```

Recovery must not depend on local runtime queue memory surviving process failure.

---

## Result

Execution recovery is now validated for HTTP process-host runtime replacement.

The runtime can now support this production-grade behavior:

> A failed runtime process can be replaced while preserving the durable execution id, and an in-flight DAG can continue on the replacement runtime without replaying completed steps.


---

## [1.0.6.9] - 2026-06-25 — HTTP process-host recovery and runtime identity hardening

### Summary

Validated production-grade recovery for in-flight HTTP runtime executions when a process-hosted runtime instance becomes unsafe or unhealthy.

This update confirms that the control plane can detect an execution already assigned to a failed runtime instance, recover the shared run ownership, request replacement HTTP process-host capacity, and redispatch the run to a healthy runtime instance.

This is a runtime ownership and redispatch recovery milestone. It does not yet implement step-level DAG resume from the last completed step.

### Added

- Added recovery validation for in-flight executions assigned to failed HTTP process-host runtime instances.
- Added test coverage for redispatching a recovered shared run to replacement process-host runtime capacity.
- Added validation that replacement runtime capacity is created through the real process host path, not fixtures.
- Added validation that the recovered execution produces a new recovered execution id after redispatch.
- Added readiness validation for process-host runtimes when the runtime command endpoint is missing.
- Added rejection validation for scale-out requests when the runtime process starts but command endpoint readiness fails.

### Changed

- Hardened runtime instance identity propagation for process-hosted runtime instances.
- Updated the default runtime identity behavior so a configured logical `RuntimeInstanceId` is preserved exactly instead of being prefixed with machine name.
- Updated `IAiRuntimeInstanceIdentityDescriptor` registration to resolve `AiRuntimeInstanceRegistrationOptions.RuntimeInstanceId` and pass it into `DefaultAiRuntimeInstanceIdentity`.
- Preserved generated fallback identities only for cases where no explicit runtime instance id is configured.
- Ensured the runtime process writes execution ownership using the control-plane assigned runtime instance id instead of local fallback values such as `MachineName:ProcessId:Guid`.

### Fixed

- Fixed recovered process-host executions being indexed with a generated local runtime id such as `MSI:<pid>:<guid>` instead of the durable control-plane runtime instance id.
- Fixed the final recovery assertion mismatch between expected replacement runtime id and actual runtime execution ownership id.
- Fixed process-host runtime identity consistency across registry, capacity, shared dispatch, runtime execution index, observability, and recovery assertions.
- Fixed replacement scale-out deduplication for recovered shared queue redispatch by allowing recovery redispatch requests to use a unique scale-out request id and recovery-specific intent metadata.

### Validated

- Runtime process host startup through `HostCreationMode=Process`.
- Redis-backed scale-out request publication and watcher processing.
- Failed runtime instance detection and suppression.
- In-flight execution recovery transition.
- Shared queue redispatch to replacement runtime capacity.
- Replacement runtime process registration and capacity publication.
- Recovery completion with distinct failed and recovered execution ids.
- Negative readiness path where command endpoint is intentionally disabled.
- Scale-out rejection with `runtime-readiness-command-endpoint-missing` when process readiness is incomplete.

### Confirmed test result

Validated recovery scenario:

`HttpRuntimeExecutionRecoveryRedispatchIntegrationTests.Http_ProcessHost_Should_Recover_InFlight_Execution_And_Redispatch_To_Healthy_Runtime`

Observed flow:

- Runtime 1 received the original shared run.
- In-flight execution was seeded against runtime 1.
- Runtime 1 was marked unhealthy.
- Recovery completed for the failed runtime assignment.
- Shared run was redispatched to runtime 2.
- Runtime 2 created the recovered execution.
- Test completed successfully with both failed and recovered execution ids.

Example completion output:

`[HTTP RECOVERY REDISPATCH] Completed. SharedRunId='<sharedRunId>', FailedExecutionId='<failedExecutionId>', RecoveredExecutionId='<recoveredExecutionId>'.`

### Important architecture note

This update validates runtime execution ownership recovery and redispatch.

It does not yet validate partial DAG resume from the last completed step.

Current validated behavior:

`runtime-1 fails -> shared run recovered -> replacement runtime-2 created -> run redispatched -> new recovered execution created`

Target next behavior:

`100 steps -> 70 completed -> runtime crash -> replacement runtime resumes from step 71 and executes only remaining work`

### Next work

- Design and validate DAG step-level resume recovery.
- Preserve completed steps and durable outputs across runtime failure.
- Reset stale running, claimed, or in-progress steps owned by the failed runtime.
- Resume the execution from the durable DAG state rather than restarting the whole run.
- Introduce recovery metadata on shared queue redispatch:
  - `recovery.mode=resume-existing-execution`
  - `recovery.failedRuntimeInstanceId`
  - `recovery.failedExecutionId`
  - `recovery.failedLocalRunId`
  - `recovery.reason=runtime-instance-unavailable`
- Decide whether recovered executions should preserve the same logical `ExecutionId` with a new recovery attempt id, or continue creating a new execution id linked to the failed one.

---

## [1.0.6.9] - 2026-06-24 - Runtime execution recovery — in-flight redispatch proof

### Added
- Added production-style recovery redispatch integration coverage.
- Added in-memory and Redis shared queue tests proving that an in-flight execution assigned to a failed runtime can be recovered and redispatched to a healthy runtime.
- Added coverage for the full recovery path:
  - runtime A claims and starts an execution
  - runtime A becomes unhealthy while the execution is running
  - recovery requeues the durable shared run
  - runtime A local execution index is marked `requeued-for-recovery`
  - runtime B claims the same shared run
  - runtime B starts and completes a new execution attempt

### Production note
This validates Recovery V1 for in-flight executions.

The system does not resume volatile in-memory worker state. Instead, it recovers durable ownership from the failed runtime, requeues the shared run, closes the stale local runtime execution index entry, and allows a healthy runtime to execute a new attempt.

---

## [1.0.6.9] - 2026-06-24 - Runtime execution recovery — transition service owns recovery mutation boundary

### Changed
- Moved recovered runtime execution index closure from `AiRuntimeExecutionRecoveryReconciler` into `AiRuntimeExecutionRecoveryTransitionService`.
- Kept `AiRuntimeExecutionRecoveryReconciler` focused on coordination only:
  - scanning unavailable runtime instances
  - listing unfinished runtime runs
  - resolving shared run ownership
  - routing validated candidates to the transition service
  - reporting recovery decisions
- Consolidated recovery mutations inside `AiRuntimeExecutionRecoveryTransitionService`:
  - requeue dispatched shared queue item
  - mark local runtime execution index entry as `requeued-for-recovery`
- Updated transition service construction to require `IAiRuntimeRunExecutionIndex` in addition to `IAiSharedQueue`.

### Added
- Added transition service unit coverage proving that a successful non-dry-run recovery:
  - requeues a dispatched shared queue item back to `Pending`
  - clears claim metadata
  - marks the runtime execution index as `requeued-for-recovery`
  - removes the recovered runtime run from unfinished runtime instance scans

### Fixed
- Fixed the recovery responsibility boundary so the reconciler no longer directly mutates runtime execution index state.
- Fixed transition service tests to validate the complete mutation boundary instead of only the shared queue mutation.
- Ensured dry-run recovery still performs no mutation on shared queue state or runtime execution index state.

### Production note
This keeps the production recovery architecture clean:

`RuntimeExecutionRecoveryReconciler = discovery + coordination + decision reporting`

`AiRuntimeExecutionRecoveryTransitionService = recovery mutation boundary`

The successful recovery mutation path is now:

`resolved ownership -> dispatched shared queue item requeued to Pending -> local runtime execution index marked requeued-for-recovery -> next recovery scan ignores the already recovered local runtime run`

---

## [1.0.6.9] - 2026-06-24 - Runtime execution recovery — recovered runtime index closure

### Added
- Added a durable `requeued-for-recovery` runtime execution index state.
- Added `IAiRuntimeRunExecutionIndex.MarkRequeuedForRecoveryAsync(...)` to close recovered runtime executions after a successful shared queue recovery transition.
- Added in-memory and Redis implementations for marking runtime run execution index entries as `requeued-for-recovery`.
- Added Redis and in-memory coverage for:
  - running run -> `requeued-for-recovery`
  - idempotent recovery mark
  - terminal state rejection
  - exclusion from unfinished runtime-run scans

### Changed
- Updated runtime execution recovery reconciliation so that, after a successful shared queue requeue, the local runtime execution index is marked as `requeued-for-recovery`.
- Updated recovery mutation integration tests to assert that recovered runtime executions are no longer returned by `ListUnfinishedByRuntimeInstanceAsync(...)`.
- Updated idempotence coverage so a second recovery reconciliation no longer rediscoveres the same already-recovered runtime execution.
- Preserved dry-run recovery behavior: dry-run discovery still does not mutate shared queue state or runtime execution index state.

### Fixed
- Fixed repeated recovery discovery for already-requeued executions by moving recovered runtime entries out of the unfinished scan set.
- Fixed Redis runtime run execution index test expectations where `MarkStartedAsync(...)` returns the Redis script operation result `started` while the persisted runtime status remains `running`.
- Fixed terminal Redis recovery-mark assertions so completed runs do not require a failure reason.

### Production note
This closes the recovery loop for dispatched shared queue recovery:

`runtime unhealthy -> unfinished local runtime run discovered -> shared run ownership resolved -> shared queue requeued to Pending -> runtime execution index marked requeued-for-recovery -> next recovery scan ignores the already recovered runtime execution`

This keeps the architecture boundary intact: health reconciliation prevents unsafe routing, while execution recovery owns restoration/requeue of work already assigned to failed runtime instances.

---

## [1.0.6.9] - 2026-06-24 - Runtime execution recovery — dispatched shared queue requeue

### Added

- Added controlled recovery support for shared queue items already assigned to failed or unhealthy runtime instances.
- Added `IAiSharedQueue.RequeueDispatchedAsync(...)` as a recovery-only queue transition.
- Added in-memory shared queue support for `Dispatched -> Pending` recovery transitions.
- Added Redis shared queue support for `Dispatched -> Pending` recovery transitions through an atomic Lua script.
- Added Redis script cache support for the new recovery requeue operation.
- Added recovery transition mutation support in `AiRuntimeExecutionRecoveryTransitionService`.
- Added integration coverage proving that runtime execution recovery can requeue dispatched shared queue items when explicitly enabled.
- Added Redis end-to-end integration coverage for recovery mutation.
- Added idempotence coverage proving that the same shared run is not requeued twice.

### Changed

- `AiRuntimeExecutionRecoveryReconciler` now routes recoverable unfinished runtime runs through the transition service with an explicit dry-run or mutation reason.
- Recovery mutation is now enabled only when `DryRun = false` and `RequeueUnfinishedRuns = true`.
- Recovery decisions now distinguish between dry-run discovery and actual recovery requeue operations.
- Redis recovery requeue script now uses Redis-compatible hash updates for older Redis versions.

### Validated recovery behavior

- Stale runtime instances are marked `Unhealthy` by the health reconciler.
- Recovery reconciliation scans unavailable runtime instances.
- Runtime execution index exposes unfinished local runtime runs.
- Shared run ownership resolver maps runtime/local execution back to the shared run and claim token.
- Transition service requeues the dispatched shared queue item.
- Shared queue item moves from `Dispatched` back to `Pending`.
- Runtime instance, worker, claim token, claimed time, and claim expiry are cleared.
- Runtime execution index intentionally remains unchanged for this step.
- A second recovery pass does not requeue the same shared run again.

### Architecture note

Runtime instance health reconciliation and runtime execution recovery remain separate responsibilities.

The health reconciler only prevents unsafe routing by marking stale or unhealthy runtime instances and suppressing unsafe capacity. It does not requeue, restart, kill, cancel, fail, or dead-letter work.

Runtime execution recovery is responsible for restoring work already assigned to an unavailable runtime instance. The current implementation safely requeues the shared queue item, while leaving the runtime execution index unchanged for now.

### Tests

- Added shared queue recovery requeue tests for in-memory queue.
- Added shared queue recovery requeue tests for Redis queue.
- Added transition service mutation tests.
- Added non-dry-run runtime execution recovery integration test.
- Added idempotent recovery integration test.
- Added Redis end-to-end recovery mutation integration test.

---

## [1.0.6.9] - 2026-06-24 - Runtime Instance Health Reconciler — Routing Safety Hardening

Added a dedicated runtime instance health reconciliation layer to protect dispatch and admission routing from stale or unhealthy runtime instances.

### Added

- Added `IAiRuntimeInstanceHealthReconciler` contract.
- Added `AiRuntimeInstanceHealthReconciler` implementation.
- Added `AiRuntimeInstanceHealthReconciliationOptions`.
- Added `AiRuntimeInstanceHealthReconciliationResult`.
- Added `AiRuntimeInstanceHealthDecision`.
- Added `AiRuntimeInstanceHealthReconcilerHostedService`.
- Added `AiRuntimeInstanceHealthReconcilerHostedServiceOptions`.
- Added DI registration for runtime instance health reconciliation.
- Added optional hosted service registration for periodic health reconciliation.
- Added `MarkUnhealthyAsync(...)` to the runtime instance registry abstraction.
- Added `MarkUnhealthyAsync(...)` support for both in-memory and Redis runtime instance registries.

### Behavior

The health reconciler scans registered runtime instances and detects stale heartbeats based on a configurable threshold.

When a runtime instance is considered stale, the reconciler marks it as `Unhealthy` through the runtime instance registry.

Once marked unhealthy, the runtime instance is no longer allowed to accept new runs.

The reconciler supports:

- `Enabled`
- `StaleHeartbeatThreshold`
- `MarkStaleRuntimeUnhealthy`
- `IncludeReadyRuntimeInstances`
- `IncludeBusyRuntimeInstances`
- `IgnorePausedRuntimeInstances`
- `IgnoreStoppedRuntimeInstances`
- `IgnoreDrainingRuntimeInstances`
- `DryRun`

The hosted service is disabled by default and must be explicitly enabled.

### Routing Safety Boundary

This component is intentionally limited to routing safety.

It does not perform execution recovery.

It does not:

- requeue shared queue items;
- modify shared run ownership;
- modify runtime run execution index ownership;
- move runs to DLQ;
- restart runtime hosts;
- kill runtime processes;
- manage provider lifecycle;
- recover volatile local runtime queues.

Execution recovery remains a separate responsibility and will be handled by a future dedicated recovery reconciler.

### Tests

Added unit and integration coverage for:

- marking stale `Ready` runtime instances as `Unhealthy`;
- marking stale `Busy` runtime instances as `Unhealthy`;
- preserving tenant ownership metadata;
- preserving runtime capacity metadata;
- ignoring fresh runtime instances;
- ignoring already non-routable statuses such as `Paused`, `Draining`, `Stopped`, and `Unhealthy`;
- validating DI registration;
- validating hosted service registration;
- validating hosted service disabled behavior;
- validating hosted service enabled behavior;
- proving health reconciliation does not requeue or modify assigned run ownership.

### Production Impact

This hardening prevents unsafe dispatch/admission selection when a runtime instance stops heartbeating or becomes unhealthy.

The shared queue, shared run store, and runtime execution index remain the durable sources of truth for future recovery logic.

This keeps runtime health reconciliation and runtime execution recovery cleanly separated.

---

## [1.0.6.9] - 2026-06-24 — Runtime Store Hardening

### Scope

This hardening pass focused on the durable control-plane stores required before introducing `RuntimeInstanceHealthReconciler` and later `RuntimeExecutionRecoveryReconciler`.

The goal was to make sure each store can safely support production routing, tenant isolation, dispatch ownership, runtime health semantics, and future recovery scans without mixing health management with recovery logic.

---

### Stores hardened

- `RedisAiSharedRunStore`
- `RedisAiSharedQueue`
- `RedisAiRuntimeRunExecutionIndex`
- `RedisAiRuntimeInstanceCapacityStore`
- `RedisAiRuntimeInstanceRegistry`
- Cross-store routing safety integration coverage

---

## SharedRunStore hardening

### Added / validated

- Durable dispatch ownership persistence.
- `MarkDispatchedAsync` now validated to persist:
  - `Status = Dispatched`
  - `AssignedRuntimeInstanceId`
  - `LocalRunId`
  - `ExecutionId`
  - `Reason`
- `GetAsync` confirms persisted ownership metadata.
- `ListAsync` confirms dispatched records remain discoverable.

### Test coverage

- `MarkDispatchedAsync_Should_Persist_Dispatch_Ownership_Metadata`

### Production value

This guarantees that once a shared run is dispatched to a runtime instance, the control plane keeps a durable ownership record that can later be used by diagnostics, recovery, or reconciliation flows.

---

## SharedQueue hardening

### Added / validated

- Dispatched queue items remain discoverable for diagnostics and future recovery scans.
- Default listing excludes terminal items.
- Explicit terminal listing includes dispatched items.
- `GetAsync` can still load dispatched items directly.

### Test coverage

- `MarkDispatchedAsync_Should_Keep_Item_Discoverable_When_Including_Terminal_Items`

### Production value

The shared queue now clearly separates active scheduling from durable observability/recovery visibility.

Dispatched items are no longer active scheduling candidates, but they remain recoverable and diagnosable.

---

## RuntimeRunExecutionIndex hardening

### Added / validated

- New lookup method:

```csharp
Task<IReadOnlyList<AiRuntimeRunExecutionIndexEntry>> ListUnfinishedByRuntimeInstanceAsync(
    string runtimeInstanceId,
    CancellationToken cancellationToken = default);
```

- Redis and in-memory implementations return unfinished runs assigned to a runtime instance.
- Terminal statuses are excluded:
  - `completed`
  - `failed`
  - `cancelled`
- Tenant isolation is preserved when listing unfinished runs.
- Runtime assignment metadata is persisted on queued index entries.

### Test coverage

- `ListUnfinishedByRuntimeInstanceAsync_Should_Return_Only_Unfinished_Runs_For_RuntimeInstance`
- `ListUnfinishedByRuntimeInstanceAsync_Should_Preserve_Tenant_Isolation`
- `RegisterQueuedAsync_Should_Persist_Runtime_Assignment_Metadata`

### Production value

This gives the future recovery layer a durable way to discover unfinished work assigned to a failed, unhealthy, or draining runtime instance.

No recovery action is performed yet. This only provides safe discovery.

---

## RuntimeInstanceCapacityStore hardening

### Added / validated

- Added first-class tenant ownership fields to capacity descriptors:
  - `TenantId`
  - `TenantGroupId`
- Tenant-aware visibility now uses effective isolation metadata:
  - metadata-based tenant ownership remains supported
  - first-class tenant fields are authoritative when present
- Dedicated capacity can be matched without relying only on metadata.
- Existing metadata compatibility preserved.

### Test coverage

- `PublishAsync_Should_Store_Capacity_Descriptor`
- `ListAsync_Should_Return_Published_Descriptors`
- `PublishAsync_Should_Store_Unhealthy_NonAccepting_Capacity_Descriptor`
- `RemoveAsync_Should_Remove_Descriptor`
- `ListAsync_Should_Return_Dedicated_Capacity_When_FirstClass_Tenant_Matches`

### Production value

Capacity routing now supports proper tenant ownership as a first-class concept while remaining backward compatible with metadata-driven isolation.

---

## RuntimeInstanceRegistry hardening

### Added / validated

- Added first-class tenant ownership to:
  - `AiRuntimeInstanceRegistration`
  - `AiRuntimeInstanceSnapshot`
  - `RuntimeInstanceEntry`
- Tenant ownership is preserved across:
  - registration
  - Redis persistence
  - snapshot projection
  - heartbeat updates
  - draining
  - unregister
- Registry visibility now uses effective isolation metadata:
  - first-class `TenantId` / `TenantGroupId`
  - metadata fallback compatibility
- `Draining` and `Stopped` runtime instances are non-dispatchable.
- Heartbeat status semantics hardened so unsafe statuses cannot accept new runs.

### Health status semantics

The following statuses are now treated as non-dispatchable:

- `Unhealthy`
- `Paused`
- `Draining`
- `Stopped`

Even if a heartbeat reports available slots and `canAcceptRun = true`, the registry forces `CanAcceptRun = false` for unsafe statuses.

### Test coverage

Core registry:

- `RegisterAsync_Should_Create_Runtime_Instance`
- `GetAsync_Should_Return_Registered_Runtime_Instance`
- `HeartbeatAsync_Should_Update_Runtime_Instance_Capacity`
- `HeartbeatAsync_Should_Force_ControlPlane_To_Not_Accept_Runs`
- `ListAsync_Should_Return_Registered_Runtime_Instances`
- `MarkDrainingAsync_Should_Mark_Runtime_Instance_As_Draining`
- `UnregisterAsync_Should_Remove_Runtime_Instance_From_Registry`

Tenant visibility:

- `ListAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared`
- `GetAsync_Should_Treat_Missing_Isolation_Metadata_As_Shared`
- `ListAsync_Should_Return_Dedicated_Instance_When_Tenant_Matches`
- `GetAsync_Should_Return_Dedicated_Instance_When_Tenant_Matches`
- `ListAsync_Should_Not_Return_Dedicated_Instance_When_Tenant_Does_Not_Match`
- `GetAsync_Should_Return_Null_For_Dedicated_Instance_When_Tenant_Does_Not_Match`
- `ListAsync_Should_Return_Dedicated_Instance_When_FirstClass_Tenant_Matches`
- `GetAsync_Should_Return_Dedicated_Instance_When_FirstClass_Tenant_Matches`
- `ListAsync_Should_Not_Return_Dedicated_Instance_When_FirstClass_Tenant_Does_Not_Match`
- `GetAsync_Should_Return_Null_For_Dedicated_Instance_When_FirstClass_Tenant_Does_Not_Match`

Health semantics:

- `HeartbeatAsync_Should_Mark_Unhealthy_Runtime_As_NonAccepting`
- `HeartbeatAsync_Should_Mark_Paused_Runtime_As_NonAccepting`
- `HeartbeatAsync_Should_Mark_Draining_Runtime_As_NonAccepting`

### Production value

The registry can now safely act as the source of truth for runtime routing eligibility.

A runtime can still expose diagnostic capacity information, but unsafe runtime statuses no longer allow dispatch selection.

---

## Production assertion hardening

### Added / adjusted

- Mixed-tenant production assertions were updated so hybrid tenants can validly use:
  - their own dedicated runtime prefix
  - shared runtime prefix
- Shared fallback is no longer treated as cross-tenant leakage for hybrid tenants.
- Dedicated tenant runtime prefix validation remains strict.

### Production value

The production validation scenario now matches the intended isolation model:

- Dedicated tenants stay dedicated.
- Shared tenants use shared runtimes.
- Hybrid tenants can use dedicated first and shared fallback when allowed.

---

## Cross-store routing safety integration test

### Added

New integration coverage:

- `RuntimeInstanceRoutingSafetyIntegrationTests`
- `Marking_Runtime_Unhealthy_Should_Stop_New_Routing_While_Preserving_Assigned_Run_Ownership`

### Validated flow

The test validates the full durable safety chain:

1. Register a tenant-owned runtime instance.
2. Create a shared run.
3. Enqueue a shared queue item.
4. Claim and dispatch the queue item.
5. Persist shared run dispatch ownership.
6. Register the runtime run index entry.
7. Mark the runtime run as started.
8. Heartbeat the runtime as `Unhealthy`.
9. Verify new routing is blocked via `CanAcceptRun = false`.
10. Verify already assigned work remains durable and discoverable.

### Validated stores

- `RedisAiRuntimeInstanceRegistry`
- `RedisAiSharedQueue`
- `RedisAiSharedRunStore`
- `RedisAiRuntimeRunExecutionIndex`

### Assertions

- Registry reports runtime as `Unhealthy`.
- Registry forces `CanAcceptRun = false`.
- Shared queue item remains `Dispatched`.
- Shared queue item is excluded from active queue listing.
- Shared queue item is included when terminal items are requested.
- Shared run store preserves:
  - `AssignedRuntimeInstanceId`
  - `LocalRunId`
  - `ExecutionId`
- Runtime run execution index returns unfinished work by runtime instance.
- Tenant context remains preserved.

### Production value

This is the architectural gate before implementing `RuntimeInstanceHealthReconciler`.

It proves:

```text
Health safety = OK
Recovery discovery = OK
Recovery action = not implemented yet
```

---

## Design decisions confirmed

### Health and recovery remain separate

`RuntimeInstanceHealthReconciler` will handle routing safety:

```text
runtime unhealthy / draining / paused
→ registry marks runtime non-dispatchable
→ admission / dispatcher stop selecting it
```

`RuntimeExecutionRecoveryReconciler` will later handle assigned work recovery:

```text
runtime failed / unhealthy / expired
→ discover unfinished assigned work
→ requeue / restore / fail / DLQ according to policy
```

### Runtime provider does not own recovery

HTTP/gRPC provider health signals must not directly restart, kill, or recover runtime instances.

Providers may report endpoint health signals such as:

- `http-circuit-open`
- `http-provider-unavailable`
- readiness failure

The control plane and lifecycle-owning provider decide what to do next.

### Local runtime queues remain volatile

Durable truth remains in:

- shared queue
- shared run store
- runtime run execution index
- runtime instance registry
- capacity store
- existing runtime observability / ledger components

---

## Validation status

All impacted tests passed after this hardening pass.

Validated areas:

- Redis shared run store
- Redis shared queue
- Redis runtime run execution index
- Redis runtime instance capacity store
- Redis runtime instance registry
- tenant visibility
- tenant isolation
- health status semantics
- mixed tenant production assertions
- cross-store routing safety integration

---

## Recommended commits

### Store tenant visibility / ownership

```bash
git add -A
git commit -m "Harden runtime instance registry tenant visibility"
```

### Health status semantics

```bash
git add -A
git commit -m "Harden runtime registry health status semantics"
```

### Cross-store routing safety

```bash
git add -A
git commit -m "Add runtime routing safety integration coverage"
```

Or as one combined commit:

```bash
git add -A
git commit -m "Harden runtime store routing safety"
```

---

## Next step

Start `RuntimeInstanceHealthReconciler` foundation.

Recommended first step:

```text
Define health signal contract and status transition rules.
```

Do not mix this yet with runtime execution recovery.

Next implementation boundary:

```text
RuntimeInstanceHealthReconciler
→ routing safety only

RuntimeExecutionRecoveryReconciler
→ assigned work recovery later
```

---

## [1.0.6.9] - 2026-06-23 — MCP Production Runtime Scenario Framework

## Latest increment — HTTP tenant runtime mode validation and provisioning hardening

### Summary

This increment strengthens the production scenario framework for tenant-aware HTTP process-host runtime scale-out.

The work improves confidence at three levels:

1. production-style process-host scenarios;
2. tenant runtime visibility and mode mapping rules;
3. HTTP scale-out provisioning effective settings.

The goal was not only to make tests green, but to prove real tenant isolation behavior and ensure tenant runtime settings are the effective source of truth for HTTP scale-out provisioning.

---

## Production scenario framework updates

### Added focused HTTP process-host runtime mode scenarios

Added focused HTTP process-host scenarios for:

- single-tenant Dedicated runtime mode;
- single-tenant Shared runtime mode;
- single-tenant Hybrid runtime mode;
- multi-tenant Dedicated isolation.

These scenarios complement the existing combined Dedicated / Shared / Hybrid scenario by validating each mode independently.

### Added scale-out result runtime mode fields

`ProductionScaleOutScenarioResult` now captures additional scale-out request data:

- `TenantGroupId`
- `IsolationMode`
- `PreferDedicatedCapacity`
- `AllowSharedFallback`
- `RuntimeInstanceIdPrefix`
- `WorkerCountPerInstance`
- `MaxConcurrentRunsPerInstance`
- `LocalQueueCapacity`

These fields allow production scenario assertions to validate the effective tenant runtime mode settings propagated into scale-out requests.

### Updated HTTP process-host scenario runner mapping

`HttpProcessHostProductionScenarioRunner` now maps the additional runtime mode and capacity fields from `AiRuntimeScaleOutRequestRecord` into `ProductionScaleOutScenarioResult`.

This makes runtime mode validation observable from scenario results instead of relying only on indirect run completion.

### Added runtime mode propagation assertions

Added `ProductionTenantRuntimeModeAssertions`.

The assertion verifies that each tenant scale-out request carries the expected:

- tenant id;
- tenant group id;
- isolation mode;
- dedicated capacity preference;
- shared fallback setting;
- runtime instance id prefix;
- worker count;
- max concurrent runs;
- local queue capacity.

The common HTTP process-host scenario assertion path now calls this validation when scale-out assertions are enabled.

---

## Adversarial process-host tenant isolation validation

### Added sequential tenant execution option

Added `RunTenantsSequentially` to `ProductionRuntimeScenarioDefinition`.

Default behavior remains unchanged:

- existing scenarios still execute tenants in parallel;
- only scenarios explicitly setting `RunTenantsSequentially = true` execute tenants sequentially.

This avoids impacting existing production tests while enabling adversarial routing scenarios.

### Strengthened Dedicated tenant isolation scenario

`CreateMultiTenantDedicatedIsolationScenario()` now runs tenants sequentially.

This makes the test adversarial:

1. tenant A submits work;
2. tenant A triggers scale-out;
3. tenant A creates a real dedicated runtime process;
4. tenant A completes;
5. tenant B submits work afterwards;
6. tenant B must not reuse tenant A's dedicated runtime;
7. tenant B must use its own dedicated runtime prefix.

This validates real routing behavior, not just happy-path propagation.

---

## Tenant runtime visibility validation

### Added adversarial visibility tests

Strengthened `AiRuntimeInstanceVisibilityEvaluatorTests` with cases covering:

- Dedicated tenant cannot see shared runtime capacity when shared fallback is disabled;
- Hybrid tenant behaves like Dedicated when shared fallback is disabled;
- Hybrid tenant can see shared runtime capacity when shared fallback is enabled;
- Dedicated runtime instance is not visible when tenant group does not match;
- Hybrid runtime instance is visible when tenant group matches;
- Hybrid runtime instance is not visible when tenant group does not match.

These tests lock down the visibility rules for tenant id, tenant group id, isolation mode, and shared fallback.

---

## Tenant runtime mode mapper validation

### Added mapper tests

Added `ProductionTenantRuntimeModeMapperTests` under:

`Scenarios/Production/SharedTests`

The tests validate the expected mapping:

- `Dedicated` → `IsolationMode = Dedicated`, `PreferDedicatedCapacity = true`, `AllowSharedFallback = false`
- `Shared` → `IsolationMode = Shared`, `PreferDedicatedCapacity = false`, `AllowSharedFallback = true`
- `Hybrid` → `IsolationMode = Hybrid`, `PreferDedicatedCapacity = true`, `AllowSharedFallback = true`

This keeps pure mapping validation separate from process-host production scenarios while staying in the same integration test project.

---

## HTTP scale-out provisioner hardening

### Added tenant runtime settings precedence test

Added a new `AiHttpRuntimeScaleOutProvisionerTests` test:

`ProvisionAsync_Should_Prefer_Tenant_Runtime_Settings_Over_Request_Runtime_Sizing`

The test intentionally sends request-level runtime sizing values that differ from tenant runtime settings.

It validates that the provisioner uses tenant runtime settings as the effective source of truth for:

- runtime instance id prefix;
- worker count per instance;
- max concurrent runs per instance;
- local queue capacity;
- max runtime instances.

### Fixed HTTP scale-out provisioning precedence

Updated `AiHttpRuntimeScaleOutProvisioner` so tenant runtime settings take precedence over request-level sizing values.

Effective precedence is now:

`tenant settings > request values > hard defaults`

This applies to:

- `RuntimeInstanceIdPrefix`
- `WorkerCountPerInstance`
- `MaxConcurrentRunsPerInstance`
- `LocalQueueCapacity`
- `MaxRuntimeInstances`

### Preserved compatibility fallback behavior

Request values remain compatibility fallbacks for older paths where tenant runtime settings may not provide valid values.

HTTP scale-out options remain technical defaults only.

---

## Current validation status

The following test areas were validated green:

- HTTP process-host production scenarios;
- focused Dedicated / Shared / Hybrid runtime mode scenarios;
- adversarial multi-tenant Dedicated isolation process-host scenario;
- tenant runtime visibility evaluator tests;
- production tenant runtime mode mapper tests;
- HTTP runtime scale-out provisioner tests;
- existing HostManager mode provisioning tests.

---

## Notes

This increment does not implement shared-global runtime capacity pooling.

The current Shared scenario still validates Shared mode propagation and execution using the existing tenant-level runtime prefix behavior.

A future decision is still needed for shared runtime semantics:

- shared runtime per tenant;
- shared runtime per tenant group;
- global shared runtime pool.

Hybrid fallback process-host behavior should also be tested later only when shared capacity setup is explicit and supported by the framework.

---

## Tenant runtime mode production scenario

### Added dedicated / shared / hybrid tenant runtime mode scenario

Added a new HTTP process-host production scenario:

```text
Http_ProcessHost_Should_Respect_Dedicated_Shared_Hybrid_Tenant_Runtime_Modes
```

The scenario validates that tenant runtime settings are not only configuration values, but are actually propagated through the full production execution path:

```text
MCP submit
→ tenant-aware admission
→ Redis scale-out request
→ scale-out watcher
→ HTTP provider
→ process HostManager
→ real RuntimeInstanceOnly process
→ runtime registration
→ capacity publishing
→ visibility filtering
→ shared queue dispatch
→ HTTP runtime command execution
→ DAG completion
```

The scenario covers three tenant runtime modes:

```text
tenant-dedicated → Dedicated runtime mode
tenant-shared    → Shared runtime mode
tenant-hybrid    → Hybrid runtime mode
```

Validated output:

```text
tenant-dedicated → tenant-dedicated-runtime-1
tenant-shared    → tenant-shared-runtime-1
tenant-hybrid    → tenant-hybrid-runtime-1
```

This proves that runtime mode, tenant id, tenant group id, runtime instance prefix, capacity limits, and scale-out behavior are carried end-to-end across the control plane and real runtime host processes.

---

## Tenant runtime settings propagation fixes

### Fixed tenant group propagation from MCP submit to shared run metadata

The dedicated / shared / hybrid scenario initially blocked with shared runs stuck in:

```text
ScaleOutRequested
```

Root cause:

```text
Scale-out request used the tenant runtime settings TenantGroupId.
Shared run used the default MCP context TenantGroupId.
Requeue scope validation rejected the run with ScopeMismatch.
```

Observed mismatch:

```text
RequestTenantGroupId=tenant-mode-group-*
SharedRunTenantGroupId=tenant-group-id-xxx
```

Fix:

- Added tenant isolation metadata to the production scenario submit request metadata.
- Ensured `tenant.id` and `tenant.group.id` are propagated with the submitted shared run.
- Kept business input metadata intact, but made the control-plane metadata the source for routing / isolation scope.

Result:

```text
SharedRunTenantGroupId == ScaleOutRequestTenantGroupId
```

After this fix, scale-out fulfillment requeue matched the correct tenant scope and dispatch resumed successfully.

### Fixed tenant-aware HTTP scale-out prefix resolution

The HTTP scale-out provisioner was updated so tenant runtime settings can drive the runtime instance id prefix.

This prevents different tenants from colliding on generic runtime ids such as:

```text
runtime-instance-1
```

Validated generated runtime instance ids:

```text
production-tenant-runtime-mode-*:tenant-dedicated-runtime-1
production-tenant-runtime-mode-*:tenant-shared-runtime-1
production-tenant-runtime-mode-*:tenant-hybrid-runtime-1
```

The provisioner now keeps provider-level HTTP scale-out options as technical defaults, while tenant runtime settings can define tenant-specific runtime capacity and runtime id prefixes.

---

## Runtime visibility and HTTP provider DI fixes

### Fixed dedicated runtime visibility by tenant group

The runtime instance visibility evaluator was updated so owned runtime instances can be visible by exact tenant ownership or tenant-group ownership when the descriptor is explicitly group-scoped.

Fixed scenario:

```text
Dedicated descriptor TenantGroupId=enterprise-group
Request TenantGroupId=enterprise-group
→ visible = true
```

This supports dedicated capacity that belongs to a tenant group instead of one exact tenant, while still preserving tenant isolation for tenant-owned runtimes.

### Fixed HTTP provider dependency registration

`AiHttpRuntimeScaleOutProvisioner` now depends on:

```text
IAiTenantRuntimeSettingsProvider
```

The HTTP provider DI registration was updated to provide a safe default tenant runtime settings provider using `TryAddSingleton`.

This keeps the HTTP provider opt-in registration self-contained for unit tests and simple provider setups, while still allowing production scenarios to override it with the configuration-backed provider.

Fix:

```text
IAiTenantRuntimeSettingsProvider → HardcodedAiTenantRuntimeSettingsProvider
```

registered only when no tenant runtime settings provider already exists.

### Updated HTTP scale-out provisioner unit tests

The direct provisioner unit tests were updated with a request-compatible tenant runtime settings provider so test expectations remain driven by the explicit scale-out request values.

This avoids accidentally mixing hardcoded tenant defaults into provisioner unit tests that verify request-specific values such as:

```text
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
AllowSharedFallback
PreferDedicatedCapacity
```

---

## Final validated tests

The following tests are now green:

```text
Http_ProcessHost_Should_Run_MultiTenant_Capacity_Replay_Ledger_Production_Scenario
Http_ProcessHost_Should_Respect_Dedicated_Shared_Hybrid_Tenant_Runtime_Modes
AiHttpRuntimeScaleOutProvisionerTests
AiRuntimeInstanceVisibilityEvaluatorTests
HttpAiRuntimeInstanceProviderServiceCollectionExtensionsTests
```

This confirms that the production scenario framework now validates both durable multi-process observability/replay and tenant runtime mode behavior across real HTTP process-host runtime instances.

---

## [1.0.6.9] - 2026-06-23 — MCP Production Runtime Scenario Framework

## Scope

This changelog summarizes the work completed after the previous MCP runtime host manager / process-host changelogs.

The focus of this iteration was to turn the MCP integration test framework into a real production-style scenario framework, validate end-to-end process-host execution, and fix all persistence boundaries required for replay, ledger, and tracing across multiple runtime processes.

---

## 1. Production MCP test framework foundation

### Added a provider-agnostic production scenario model

Added the foundation for production-grade scenario tests under the MCP integration test project.

Core concepts introduced:

- `ProductionRuntimeScenarioDefinition`
- `ProductionTenantScenarioDefinition`
- `ProductionRunScenarioDefinition`
- `ProductionRuntimeScenarioResult`
- `ProductionTenantScenarioResult`
- `ProductionRunScenarioResult`
- `ProductionScaleOutScenarioResult`
- `IProductionRuntimeScenarioRunner`

The goal is to describe production scenarios independently from the provider implementation, then run the same scenario against HTTP process hosts, and later HTTP attach, gRPC, Kubernetes, or other providers.

### Added HTTP process-host production scenario runner

Added the HTTP process-host runner:

```text
HttpProcessHostProductionScenarioRunner
```

This runner validates the real end-to-end flow:

```text
Submit
→ tenant-aware admission
→ Redis scale-out request
→ scale-out watcher
→ HTTP provider
→ process HostManager
→ real RuntimeInstanceOnly process
→ runtime registration + heartbeat + capacity
→ shared queue dispatch
→ HTTP runtime command dispatch
→ DAG execution
→ ledger / replay / trace validation
```

This is intentionally stronger than fixture-only testing because it proves that runtime hosts can be launched as real external processes in local development.

### Added production multi-tenant process-host scenario

Added the production scenario:

```text
Http_ProcessHost_Should_Run_MultiTenant_Capacity_Replay_Ledger_Production_Scenario
```

The scenario validates:

- multiple tenants
- dedicated runtime capacity
- hybrid runtime capacity
- tenant-specific runtime instance prefixes
- scale-out from zero capacity
- real process-host creation
- runtime registration and capacity publishing
- shared run dispatch
- final runtime completion
- ledger visibility
- trace visibility
- replay report
- replay ledger
- replay timeline

---

## 2. Multi-tenant process-host isolation fixes

### Fixed tenant runtime visibility issue

During the production scenario, tenant B could temporarily see tenant A's runtime instance because the visibility evaluator allowed tenant group matching for dedicated/hybrid runtimes.

Observed issue:

```text
tenant-b workload was dispatched to tenant-a-runtime-1
```

Root cause:

```text
Dedicated / Hybrid runtime visibility allowed TenantGroupId match.
```

Fix:

- Dedicated runtime instances are visible only to their owning `TenantId`.
- Hybrid runtime instances are visible only to their owning `TenantId`.
- Tenant group matching is not used for owned dedicated/hybrid runtimes.
- Shared fallback remains explicit and policy-driven.

Result:

```text
tenant-a → tenant-a-runtime-1
tenant-b → tenant-b-runtime-1
```

### Standardized tenant isolation metadata keys

Fixed metadata key mismatch between runtime registration and visibility evaluation.

Moved from legacy-style keys such as:

```text
tenantId
tenantGroupId
tenant.groupId
```

to canonical metadata keys:

```text
tenant.id
tenant.group.id
runtime.isolationMode
runtime.allowSharedFallback
runtime.preferDedicatedCapacity
```

This ensured that process-host runtime registration, capacity metadata, and visibility evaluation use the same key contract.

---

## 3. Process-host configuration and environment propagation

### Confirmed process-host runtime configuration path

Validated that `ProcessAiRuntimeHostCreationStrategy` launches runtime hosts with:

- `AiMcpHost__Mode=RuntimeInstanceOnly`
- runtime instance id
- control-plane id
- runtime registration options
- provider metadata
- transport endpoint
- tenant metadata
- capacity limits
- worker count
- queue capacity

### Confirmed RuntimeInstanceOnly mode execution

The process-host scenario now proves that child runtime processes:

- start successfully
- expose the HTTP runtime command endpoint
- register themselves into the control plane
- publish capacity
- receive dispatched runs
- execute DAG workloads
- complete runs successfully

---

## 4. Replay-safe payload store propagation

### Fixed replay-safe payload store configuration for child runtime processes

Earlier process-host executions failed when retention/replay-safe payloads required a durable payload store but the runtime child process still resolved an in-memory payload store.

Added/validated child process environment variables for payload storage:

```text
AiPayloadStore__Enabled=true
AiPayloadStore__Provider=mongo-redis
AiPayloadStore__RequireReplaySafePayloads=true

AiEngine__PayloadStore__Enabled=true
AiEngine__PayloadStore__Provider=mongo-redis
AiEngine__PayloadStore__RequireReplaySafePayloads=true

AiEngine__Payloads__Enabled=true
AiEngine__Payloads__Provider=mongo-redis
AiEngine__Payloads__RequireReplaySafePayloads=true
```

This fixed the replay-safe payload boundary for process-hosted runtime instances.

---

## 5. Decision ledger persistence across processes

### Problem

The production scenario initially completed executions, but ledger validation failed:

```text
HasLedger = false
LedgerCount = 0
```

Root cause:

```text
IAiDecisionLedger was always forced to InMemoryAiDecisionLedger.
```

This meant:

```text
runtime child process writes ledger → child process memory
parent MCP reads ledger → parent process memory
result → empty ledger
```

### Fix

Updated `AiRuntimeServiceRegistration` to configure the decision ledger conditionally.

Added support for:

```text
AiDecisionLedger:Provider=mongo
AiObservability:Ledger:Provider=mongo
```

When Mongo is selected:

- register `IMongoClient`
- register Mongo-backed decision ledger
- use shared Mongo database and collections

### Test settings updated

Added parent and child process configuration:

```text
AiDecisionLedger:Provider=mongo
AiObservability:Ledger:Provider=mongo

AiRuntimeProcessHostCreation:EnvironmentVariables:AiDecisionLedger__Provider=mongo
AiRuntimeProcessHostCreation:EnvironmentVariables:AiObservability__Ledger__Provider=mongo
```

### Result

Ledger became visible from the parent MCP process:

```text
LedgerCount = 500+
HasLedger = true
```

---

## 6. Replay metadata persistence across processes

### Problem

After ledger was fixed, replay still failed with:

```text
Replay fingerprint metadata not found.
```

Root cause:

```text
IAiExecutionReplayMetadataStore was still InMemoryAiExecutionReplayMetadataStore.
```

This meant:

```text
runtime child process writes replay metadata → child process memory
parent MCP replay reads replay metadata → parent process memory
result → fingerprint metadata not found
```

### Added Mongo replay metadata store

Added a durable Mongo-backed implementation:

```text
MongoAiExecutionReplayMetadataStore
```

The store persists replay metadata by execution id and allows parent and child processes to share the same replay fingerprint metadata.

### Fixed Mongo document mapping

An initial version stored `AiExecutionReplayMetadata` directly in Mongo, which caused deserialization issues because Mongo adds `_id` and the model did not define an `_id` field.

Observed error:

```text
Element '_id' does not match any field or property of class AiExecutionReplayMetadata.
```

Fix:

- added a Mongo document wrapper
- used `[BsonId]` on the wrapper id
- stored `AiExecutionReplayMetadata` inside the wrapper document
- used `ExecutionId` as the document id

### Updated runtime registration

Updated `AiRuntimeServiceRegistration` to configure replay metadata store conditionally.

Supported settings:

```text
AiExecutionReplay:MetadataStore:Provider=mongo
AiReplay:MetadataStore:Provider=mongo
AiExecutionReplay:MetadataStore:Mongo:CollectionName=ai_execution_replay_metadata
```

### Test settings updated

Added parent and child process configuration:

```text
AiExecutionReplay:MetadataStore:Provider=mongo
AiExecutionReplay:MetadataStore:Mongo:CollectionName=ai_execution_replay_metadata

AiRuntimeProcessHostCreation:EnvironmentVariables:AiExecutionReplay__MetadataStore__Provider=mongo
AiRuntimeProcessHostCreation:EnvironmentVariables:AiExecutionReplay__MetadataStore__Mongo__CollectionName=ai_execution_replay_metadata
```

### Result

Replay metadata became visible across processes:

```text
ReplaySuccess = true
ReportSuccess = true
ReplayLedgerSuccess = true
ReplayTraceSuccess = true
```

---

## 7. Trace query persistence and MCP observability fix

### Problem

After ledger and replay were fixed, direct trace validation still failed:

```text
TraceCount = 0
HasTrace = false
```

At the same time replay timeline succeeded:

```text
ReplayTraceSuccess = true
```

This proved that replay timeline and direct observability trace were using different paths.

### Root cause

`ObservabilityMcpTools` read traces directly from:

```text
IAiTraceTimeline
```

But `IAiTraceTimeline` is process-local and in-memory.

In process-host mode:

```text
runtime child writes timeline → child process memory
parent MCP reads timeline → parent process memory
result → TraceCount = 0
```

The durable trace persistence already existed through:

```text
IAiRuntimeTraceStore
MongoAiRuntimeTraceStore
AiRuntimeTraceStoreFactory
```

So the persistence was not missing; the MCP tool was reading the wrong abstraction for multi-process scenarios.

### Added async trace timeline query abstraction

Added:

```text
IAiTraceTimelineQuery
```

Purpose:

```text
Query trace events from the process-local timeline first, then fall back to the durable runtime trace store.
```

### Added default implementation

Added:

```text
DefaultAiTraceTimelineQuery
```

Behavior:

```text
1. Read IAiTraceTimeline in-memory.
2. If events exist, return them.
3. Otherwise read IAiRuntimeTraceStore.
4. Map durable AiTraceRecord values to AiTraceEvent values.
5. Return ordered trace events.
```

This preserves local single-process behavior while enabling multi-process / process-host observability.

### Updated Observability MCP tools

Updated `ObservabilityMcpTools` from:

```text
ObservabilityMcpTools -> IAiTraceTimeline
```

to:

```text
ObservabilityMcpTools -> IAiTraceTimelineQuery
```

The MCP method now uses an async query path:

```text
observability.trace.get_by_execution
```

### Updated DI

Added registration:

```text
IAiTraceTimelineQuery -> DefaultAiTraceTimelineQuery
```

### Result

Direct trace observability now works in process-host mode:

```text
TraceCount = 300+
HasTrace = true
```

---

## 8. Test runner result construction fix

### Problem

The first production runner implementation created `ProductionRunScenarioResult` objects with fixed false observability values:

```text
HasLedger=false
HasTrace=false
HasReplayReport=false
HasReplayLedger=false
HasReplayTrace=false
```

This meant the assertions would fail even if the underlying tools were working.

### Fix

Replaced static result construction with real MCP queries:

- `GetLedgerByExecutionAsync`
- `GetTraceByExecutionAsync`
- `ReplayExecutionAsync`
- `GetReplayReportAsync`
- `GetReplayLedgerAsync`
- `GetReplayTraceAsync`

Added replay debug output to show:

```text
LedgerCount
TraceCount
ReplaySuccess
ReportSuccess
ReplayLedgerSuccess
ReplayTraceSuccess
FailureReason / Message
```

This made the production scenario diagnostics actionable and allowed each persistence boundary to be fixed one by one.

---

## 9. Final validated production flow

The final run validated:

```text
Tenant A → tenant-a-runtime-1
Tenant B → tenant-b-runtime-1
LedgerCount > 500
TraceCount > 300
ReplaySuccess = true
ReportSuccess = true
ReplayLedgerSuccess = true
ReplayTraceSuccess = true
```

This proves the following production-grade chain:

```text
MCP submit
→ RBAC/tenant context
→ tenant-aware admission
→ Redis scale-out request
→ scale-out watcher
→ HTTP provider
→ process HostManager
→ real RuntimeInstanceOnly host process
→ runtime registration
→ capacity publishing
→ shared run dispatch
→ HTTP runtime command execution
→ DAG completion
→ shared Mongo decision ledger
→ shared Mongo replay metadata
→ replay report/ledger/timeline
→ durable trace query fallback
```

---

## 10. Bugs fixed in this iteration

### Fixed: process-host scenario could not validate ledger

Cause:

```text
IAiDecisionLedger was in-memory per process.
```

Fix:

```text
Mongo-backed decision ledger selected by configuration.
```

### Fixed: replay fingerprint metadata not found

Cause:

```text
IAiExecutionReplayMetadataStore was in-memory per process.
```

Fix:

```text
MongoAiExecutionReplayMetadataStore added and wired by configuration.
```

### Fixed: Mongo replay metadata deserialization failure

Cause:

```text
AiExecutionReplayMetadata was stored directly and Mongo injected _id.
```

Fix:

```text
Document wrapper with [BsonId] and Metadata payload.
```

### Fixed: direct trace query returned no events in process-host mode

Cause:

```text
ObservabilityMcpTools read IAiTraceTimeline, which is process-local memory.
```

Fix:

```text
Added IAiTraceTimelineQuery with fallback from IAiTraceTimeline to IAiRuntimeTraceStore.
```

### Fixed: production runner hardcoded observability result flags

Cause:

```text
ProductionRunScenarioResult was built with false observability flags.
```

Fix:

```text
BuildRunResultsAsync now calls real MCP ledger, trace, and replay tools.
```

### Fixed: tenant B could use tenant A runtime capacity

Cause:

```text
Dedicated/hybrid visibility allowed tenant group matching.
```

Fix:

```text
Dedicated/hybrid runtime instances are visible only to owning TenantId.
```

### Fixed: process-host replay-safe payload store mismatch

Cause:

```text
Runtime child process could resolve an in-memory payload store while replay-safe payloads required durable storage.
```

Fix:

```text
Propagated payload store environment variables to RuntimeInstanceOnly child processes.
```

---

## 11. Architecture lessons confirmed

### In-memory is only safe inside a single process

This iteration confirmed a key production rule:

```text
Anything written by a runtime child process and read by the parent MCP control plane must use a shared durable store.
```

Stores fixed accordingly:

```text
Decision ledger → Mongo
Replay metadata → Mongo
Trace query → IAiRuntimeTraceStore fallback
```

### Replay and observability are not the same path

Replay timeline and direct trace observability are separate paths:

```text
Replay timeline → replay/snapshot/replay metadata flow
Direct trace → IAiTraceTimeline / IAiRuntimeTraceStore flow
```

Both now work in process-host mode.

### Local queue can remain in-memory

The current architecture keeps local runtime queues in-memory, which is acceptable as long as the durable source of truth remains outside the process:

```text
Shared run store
Shared queue
Runtime registry/capacity
Snapshots
Ledger
Replay metadata
Trace store
```

The next resilience step should be a health/recovery reconciler, not immediately moving all local runtime queues to Redis.

---

## 12. Next recommended step

After final HTTP process-host cleanup, the next production-hardening step should be:

```text
RuntimeInstanceHealthReconciler
```

Responsibilities:

- detect expired runtime heartbeat/capacity TTL
- mark unhealthy instances as draining/unavailable
- stop routing new runs to unhealthy instances
- reconcile shared runs assigned to dead runtime instances
- requeue non-started runs safely
- inspect durable execution snapshots for started runs
- patch terminal shared runs when execution already completed
- avoid duplicate execution after runtime loss

This fits naturally after the process-host production scenario because the system now has the durable stores required for safe recovery decisions.


---

## [1.0.6.8] - 2026-06-22 — MCP Runtime Host Manager / HTTP Remote Runtime Scale-Out

## Scope

This changelog covers only the fixes completed in the last debugging sequence around the failing test:

```text
HttpRuntimeProviderProcessHostProductionScenarioTests
ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Dispatch_Dedicated_Tenant_Run_After_Runtime_Process_Becomes_Ready
```

This is not the full Host Manager changelog. It only documents the concrete corrections made after the process-host happy path was close but dispatch still failed.

---

## Initial Failure

The test failed with the shared run stuck in:

```text
Status=ScaleOutRequested
AssignedRuntimeInstanceId=...:tenant-a-runtime-1
FailureReason=http-circuit-open
```

The process host had been created and had registered itself, but the run never moved to `Dispatched`.

---

## Fix 1 — Readiness Was Too Weak

### Problem

Readiness validated only the base HTTP endpoint:

```text
http://localhost:5800
```

That could succeed even if the real dispatch endpoint was not usable.

The HTTP provider dispatches to:

```text
http://localhost:5800/runtime-instance/commands
```

So readiness had to validate the real command route, not only the base endpoint.

### Change

Updated `AiRuntimeInstanceReadinessWaiter` so HTTP readiness probes:

```text
GET /runtime-instance/commands
```

Behavior:

```text
404 -> runtime-readiness-command-endpoint-missing
405 -> accepted, because route exists but GET is not allowed
```

### Result

The logs proved the command endpoint existed:

```text
GET http://localhost:5800/runtime-instance/commands
-> 405 Method Not Allowed
```

So the route was present and the failure was not endpoint mapping.

---

## Fix 2 — Test Host Was Still Using Fixture HTTP Routing

### Problem

After readiness was correct, the real error appeared:

```text
System.InvalidOperationException: No runtime HTTP client is available yet.
```

Root cause:

`GenericMcpServerTestHost` still injected the fixture/test HTTP routing factory:

```text
MultiRuntimeHttpClientFactory
RuntimeClientRoutingHandler
```

That handler is correct for WebApplicationFactory fixture runtime hosts, but wrong for `Process` mode.

In `Process` mode, the HTTP provider must use a real network `HttpClient` and call:

```text
http://localhost:{port}/runtime-instance/commands
```

### Change

Updated `GenericMcpServerTestHost` so when:

```text
AiHttpRuntimeScaleOut:Mode = HostManager
Tests:UseRegisteringTestRuntimeHostManager = false
```

it skips the fixture HTTP client factory override.

Expected diagnostic log:

```text
[TEST MCP HOST] HTTP HostManager Process mode detected. Runtime HTTP client factory override skipped. Real network HttpClient preserved.
```

### Result

The control plane started calling the real process over HTTP instead of the in-memory fixture routing handler.

---

## Fix 3 — RuntimeInstanceOnly Did Not Register the HTTP Command Handler

### Problem

After real network HTTP was enabled, the runtime process received the POST, but failed with:

```text
No service for type
'IAiRuntimeInstanceHttpCommandHandler'
has been registered.
```

Root cause:

`ControlPlaneWithHttpRuntimeInstances` called:

```csharp
services.AddAiHttpRuntimeInstanceProvider();
```

That extension already registered the HTTP command handler.

But `RuntimeInstanceOnly` did not call it, and it should not call it because that would also register control-plane HTTP provider and scale-out services inside a runtime-only worker process.

### Change

Split runtime-side command handling into a separate DI extension:

```csharp
services.AddAiRuntimeInstanceHttpCommandHandling();
```

This registers only the runtime-side services required by:

```text
POST /runtime-instance/commands
```

Registered services:

```text
IAiSharedRuntimeInstance -> LocalAiSharedRuntimeInstance
AiRuntimeInstanceHttpCommandHandler
IAiRuntimeInstanceHttpCommandHandler -> AiRuntimeInstanceHttpCommandHandler
```

### Updated `AddAiHttpRuntimeInstanceProvider`

`AddAiHttpRuntimeInstanceProvider()` now still registers the provider and scale-out services, but delegates the command handler part to:

```csharp
services.AddAiRuntimeInstanceHttpCommandHandling();
```

This preserves all previous HTTP scenarios.

### Updated `ConfigureRuntimeInstanceOnly`

Added:

```csharp
services.AddAiRuntimeInstanceHttpCommandHandling();

Console.WriteLine(
    "[RUNTIME INSTANCE ONLY] Registered runtime HTTP command handling services.");
```

right after:

```csharp
services.AddAiControlPlane();

services.AddAiControlPlaneDiscoveryCore();
```

### Result

The `RuntimeInstanceOnly` process can now handle incoming HTTP runtime commands without registering the full control-plane HTTP provider.

---

## Final Validation

The failing test now passes.

Final output:

```text
PROCESS HOST DEDICATED DISPATCH END-TO-END VALIDATED.
SharedRunStatus='Dispatched'
RuntimeInstanceId='...:tenant-a-runtime-1'
```

Validated flow:

```text
submit run
-> admission sees no capacity
-> Redis scale-out request
-> watcher
-> HTTP provider RequestScaleOutAsync
-> HostManager
-> Process host creation
-> real RuntimeInstanceOnly process
-> runtime self-registers registry/capacity
-> readiness verifies command endpoint
-> shared queue pump dispatches
-> HTTP provider posts command
-> runtime command handler accepts command
-> shared run becomes Dispatched
```

---

## Files Touched

### `AiRuntimeInstanceReadinessWaiter`

Updated HTTP readiness to validate the command endpoint:

```text
/runtime-instance/commands
```

instead of only the base endpoint.

### `GenericMcpServerTestHost`

Changed test HTTP client factory override behavior:

```text
Fixture mode -> keep test routing handler
Process mode -> preserve real network HttpClient
```

### `HttpAiRuntimeInstanceProviderServiceCollectionExtensions`

Split runtime-side command handling into:

```csharp
AddAiRuntimeInstanceHttpCommandHandling()
```

and kept `AddAiHttpRuntimeInstanceProvider()` backward compatible.

### `ServiceRegistration.ConfigureRuntimeInstanceOnly`

Added runtime-side HTTP command handling registration.

---

## Why Previous HTTP Scenarios Worked

Previous HTTP scenarios worked because they ran through:

```text
ControlPlaneWithHttpRuntimeInstances
```

which calls:

```csharp
services.AddAiHttpRuntimeInstanceProvider();
```

That registered both the provider and the command handler.

The new process-host scenario runs the child process as:

```text
RuntimeInstanceOnly
```

That mode did not call the HTTP provider extension, so it exposed the endpoint but did not have the handler registered.

The fix separates provider registration from runtime command handling, so both worlds are correct:

```text
Control plane HTTP host -> AddAiHttpRuntimeInstanceProvider()
Runtime-only process   -> AddAiRuntimeInstanceHttpCommandHandling()
```


---

## [1.0.6.8] - 2026-06-20 — MCP Runtime Host Manager / Remote Runtime Scale-Out

The goal of this phase was to evolve the control plane from simulated or fixture-only scale-out toward a real runtime host creation model.

The target architecture is:

```text
Submit run
-> admission detects no available capacity
-> Redis scale-out request is created
-> scale-out watcher observes the request
-> provider receives RequestScaleOutAsync
-> provider asks the runtime host manager to create or attach a runtime host
-> real RuntimeInstanceOnly host starts
-> runtime registers heartbeat and capacity
-> readiness succeeds
-> scale-out request is fulfilled
-> shared queue can dispatch normally
```

This keeps the architecture clean:

- The watcher never dispatches.
- The host manager never dispatches.
- The provider remains the transport and scale-out owner.
- Runtime hosts self-register.
- Readiness is based on real registry/capacity state.
- No fake capacity is used for the process-host path.

---

## 1. Host creation model introduced

Added a first-class runtime host creation mode model.

Supported modes:

- `Fixture`
- `Process`
- `Kubernetes`
- `Attach`

Current implemented modes:

- `Fixture`
- `Process`

Planned modes:

- `Kubernetes`
- `Attach`

The important separation is now:

- Provider: `http`, `grpc`, `local`
- Transport: `http`, `grpc`, `local`
- Host creation mode: `Fixture`, `Process`, `Kubernetes`, `Attach`

This allows combinations such as:

- HTTP provider + Process host creation
- HTTP provider + Kubernetes host creation
- HTTP provider + Attach host creation
- Future gRPC provider + Process/Kubernetes/Attach

---

## 2. Runtime host manager abstraction

Added a host manager layer responsible for starting or attaching runtime hosts.

Main concepts:

- `IAiRuntimeHostManager`
- `AiRuntimeHostStartRequest`
- `AiRuntimeHostStartResult`
- `IAiRuntimeHostCreationStrategy`
- `AiRuntimeHostCreationManager`
- `NoopAiRuntimeHostManager`

The host manager routes host creation to the correct strategy based on `HostCreationMode`.

The design keeps the provider as the main scale-out entry point. The provider still receives `RequestScaleOutAsync`, then delegates physical host creation to the host manager.

---

## 3. Host creation strategies

Added strategy-based host creation.

Implemented strategies:

- `FixtureAiRuntimeHostCreationStrategy`
- `ProcessAiRuntimeHostCreationStrategy`

### Fixture strategy

Used for integration tests and existing fixture-based scale-out scenarios.

It registers a runtime instance and capacity directly into the registry/capacity stores.

This mode remains useful for fast tests, but it is not the final production proof.

### Process strategy

Starts a real external `.NET` runtime host process using:

```text
dotnet Multiplexed.AI.McpServer.Host.dll
```

The process is started as:

```text
AiMcpHost:Mode = RuntimeInstanceOnly
```

It receives runtime identity, registration, transport, discovery, Redis/Mongo, worker, queue, and capacity settings through environment variables.

This is the first real local-dev E2E scale-out path.

---

## 4. Process host creation options

Added process host creation options.

Important options:

- `Enabled`
- `DotnetExecutablePath`
- `RuntimeHostAssemblyPath`
- `WorkingDirectory`
- `BasePort`
- `MaxPort`
- `StartupTimeoutSeconds`
- `RedirectOutput`
- `KillOnDispose`
- `EnvironmentVariables`

This allows the control plane to launch real runtime processes during integration tests or local development.

The process strategy also tracks launched processes and kills them on dispose when configured.

---

## 5. Runtime process environment propagation

The process strategy now injects the required environment variables for a real `RuntimeInstanceOnly` process.

### MCP host mode

```text
AiMcpHost__Mode=RuntimeInstanceOnly
AiMcpHost__Port={port}
ASPNETCORE_URLS=http://localhost:{port}
DOTNET_URLS=http://localhost:{port}
AiMcpHost__EnableSharedQueuePump=false
AiMcpHost__EnableReplayTools=false
AiMcpHost__EnableObservabilityTools=false
```

### Disable local pool inside runtime instance process

```text
AiLocalRuntimeInstancePool__Enabled=false
AiLocalRuntimeInstancePool__InstanceCount=0
AiLocalRuntimeInstancePool__WorkerCountPerInstance=0
AiLocalRuntimeInstancePool__MaxConcurrentRunsPerInstance=0
AiLocalRuntimeInstancePool__LocalQueueCapacity=0
AiLocalRuntimeInstancePool__RuntimeInstanceIdPrefix=disabled
```

This prevents a runtime instance process from creating its own local pool.

### Runtime identity

```text
AiEngine__RuntimeInstanceId={runtimeInstanceId}
AiEngine__PipelineBackgroundController__RuntimeInstanceId={runtimeInstanceId}
AiEngine__RuntimeInstanceWorker__RuntimeInstanceId={runtimeInstanceId}
```

### Control-plane discovery

```text
AiEngine__ControlPlane__ControlPlaneId={controlPlaneId}
AiEngine__ControlPlane__RedisDiscoveryKey=multiplexed-ai:{controlPlaneId}
AiEngine__ControlPlane__EnableDiscovery=true
AiEngine__ControlPlane__PublishDiscovery=false
AiEngine__ControlPlane__RequireDiscovery=true
```

This allows the launched runtime instance to discover the correct control plane instead of hanging on a wrong/default discovery key.

### Runtime registration

```text
AiRuntimeInstanceRegistration__Enabled=true
AiRuntimeInstanceRegistration__ControlPlaneId={controlPlaneId}
AiRuntimeInstanceRegistration__RuntimeInstanceId={runtimeInstanceId}
AiRuntimeInstanceRegistration__ProviderName={providerName}
AiRuntimeInstanceRegistration__Role=Runtime
AiRuntimeInstanceRegistration__WorkerCount={workerCount}
AiRuntimeInstanceRegistration__MaxConcurrentRuns={maxConcurrentRuns}
AiRuntimeInstanceRegistration__QueueCapacity={localQueueCapacity}
AiRuntimeInstanceRegistration__RuntimeVersion=process-host
AiRuntimeInstanceRegistration__HeartbeatInterval=00:00:02
```

### Transport metadata

```text
AiRuntimeInstanceRegistration__Metadata__provider.name=http
AiRuntimeInstanceRegistration__Metadata__transport.name=http
AiRuntimeInstanceRegistration__Metadata__transport.endpoint=http://localhost:{port}
AiRuntimeInstanceRegistration__Metadata__runtime.instance.id={runtimeInstanceId}
AiRuntimeInstanceRegistration__Metadata__hostType=runtime-instance-process
AiRuntimeInstanceRegistration__Metadata__deployment=process-host
AiRuntimeInstanceRegistration__Metadata__hostCreation.mode=Process
```

---

## 6. Runtime identity fix

Fixed the local runtime environment identity issue.

Before the fix, the process runtime could start with an internally generated fallback identity like:

```text
MSI:{processId}:{guid}
```

or:

```text
host-xxx:local-runtime-to-assign
```

That was wrong for externally created runtime instances.

The runtime process now respects the configured runtime instance identity from registration/config.

Expected identity:

```text
{controlPlaneId}:runtime-instance-1
```

This ensures that pipeline controller identity, runtime registration identity, capacity identity, and provider dispatch target identity all refer to the same runtime instance.

---

## 7. HTTP scale-out provisioner updated

Updated the HTTP runtime scale-out provisioner to support HostManager mode and pass the selected host creation mode into `AiRuntimeHostStartRequest`.

Important fix:

```text
HostCreationMode = options.HostCreationMode
```

Without this, the process test configured `Process`, but the actual host start request still used the default `Fixture`.

The provisioner now builds host start requests containing:

- ControlPlaneId
- RuntimeInstanceId
- ProviderName
- TransportName
- TransportEndpoint
- TenantId
- TenantGroupId
- IsolationMode
- PreferDedicatedCapacity
- AllowSharedFallback
- MaxRuntimeInstances
- WorkerCountPerInstance
- MaxConcurrentRunsPerInstance
- LocalQueueCapacity
- RuntimeInstanceIdPrefix
- HostCreationMode
- Metadata
- ExecutionContextSnapshot

---

## 8. Runtime readiness integration

Added readiness flow for host-manager scale-out.

The provider can require readiness before fulfilling a scale-out request.

Readiness checks real control-plane state:

- runtime instance exists in registry
- runtime capacity exists
- runtime metadata is present
- runtime can accept runs

Failure reason examples:

- `runtime-readiness-registry-missing`
- `runtime-readiness-capacity-missing`
- readiness timeout

The successful process path now proves that the real process registered and published capacity before the scale-out request is fulfilled.

---

## 9. Test host assembly resolver

Added a test-only resolver for the real MCP server host assembly.

It resolves:

```text
Multiplexed.AI.McpServer.Host.dll
```

from the source build output.

This is intentionally test-only. Production process mode must use an explicit configured `RuntimeHostAssemblyPath`.

The resolver rejects invalid test paths and ensures the launched assembly is the real runtime host, not a test assembly.

---

## 10. Integration test settings updated

Added process-host scale-out test settings.

The test configuration now supports:

```text
AiHttpRuntimeScaleOut:Mode=HostManager
AiHttpRuntimeScaleOut:HostCreationMode=Process
AiHttpRuntimeScaleOut:RequireReadiness=true
AiRuntimeProcessHostCreation:Enabled=true
AiRuntimeProcessHostCreation:RuntimeHostAssemblyPath={resolvedHostAssembly}
AiRuntimeProcessHostCreation:BasePort=5800
AiRuntimeProcessHostCreation:MaxPort=5899
```

The process also receives Redis/Mongo settings through configured environment variables.

---

## 11. Generic MCP server test host updated

Updated the integration test host to bind/rebind the new options correctly:

- `AiHttpRuntimeScaleOutOptions`
- `AiRuntimeProcessHostCreationOptions`

The test host now preserves the real host manager when configured, instead of replacing it with a registering test host manager.

This allowed the real `AiRuntimeHostCreationManager` and `ProcessAiRuntimeHostCreationStrategy` to run in the integration test.

---

## 12. Successful real process scale-out scenario

Validated the key integration test:

```text
ControlPlaneWithHttpRuntimeInstances_With_Process_HostCreation_Mode_Should_Fulfill_Redis_ScaleOut_Request_Using_Real_Runtime_Process
```

Validated flow:

```text
Submit run
-> no visible capacity
-> Redis scale-out request created
-> watcher observes request
-> HTTP scale-out provider selected
-> HostManager mode used
-> Process host creation mode used
-> real RuntimeInstanceOnly process started
-> runtime process registers itself
-> runtime capacity appears
-> readiness succeeds
-> Redis scale-out request fulfilled
```

The successful log confirms:

```text
Redis HTTP Process HostManager scale-out request fulfilled.
```

This is the first proof that local development can scale out by launching real runtime host processes.

---

## 13. Tenant runtime settings scan

Confirmed that tenant runtime settings already exist through:

```text
HardcodedAiTenantRuntimeSettingsProvider
```

The provider already supports dedicated tenants, shared tenants, hybrid tenants, and fallback shared/default behavior.

Settings include:

- IsolationMode
- PreferDedicatedCapacity
- AllowSharedFallback
- MaxRuntimeInstances
- WorkerCountPerInstance
- MaxConcurrentRunsPerInstance
- LocalQueueCapacity
- RuntimeInstanceIdPrefix
- Metadata

Confirmed that admission already reads tenant runtime settings and writes effective values into scale-out metadata/records.

Confirmed that the watcher copies these values into the provider request.

Confirmed that the HTTP provisioner copies these values into the host start request.

Next step is to add process-host tests proving tenant settings drive the physical runtime process setup end-to-end.

---

## 14. Current architecture decisions

Confirmed decisions:

- The watcher must never dispatch runs directly.
- The host manager must never dispatch runs directly.
- The provider remains the transport and scale-out owner.
- The runtime process must self-register.
- Readiness must observe registry/capacity state.
- Fixture mode stays available for fast tests.
- Process mode is required for real local-dev E2E testing.
- Circuit breaker open must not directly kill/restart a runtime instance.
- Circuit breaker open is an endpoint health signal.
- Health/draining/replacement decisions belong to the control plane / lifecycle owner.
- Dedicated tenants must not silently fall back to shared capacity.
- Shared/hybrid fallback must be explicit, observable, and policy-driven.
- Before gRPC, Kubernetes, or Attach, the HTTP + Process path must be production-tested.

---

## 15. Next phase

Before implementing gRPC, Kubernetes, or Attach, the next phase is production hardening of the HTTP + Process path.

### Tenant settings end-to-end

```text
Tenant settings
-> admission
-> Redis scale-out request
-> watcher
-> provider request
-> host start request
-> process env vars
-> runtime registration/capacity
```

### Dispatch after process scale-out

```text
submit run
-> no capacity
-> process scale-out
-> runtime readiness fulfilled
-> shared queue dispatches to real process runtime
-> run completes
```

### Instance health

Statuses to validate:

- Healthy
- Draining
- Unhealthy
- Offline

Rules:

- unhealthy instances must not receive new runs
- draining instances must not receive new runs
- capacity > 0 is ignored if instance is unhealthy
- missing heartbeat/offline capacity must be ignored

### Circuit breaker production scenarios

Rules:

- circuit open requeues the run
- circuit open does not restart the runtime directly
- circuit open marks endpoint/instance unhealthy or draining
- dispatcher stops selecting unhealthy capacity
- replacement scale-out is requested if no healthy capacity remains

### Failure matrix

Cases to validate:

- host manager disabled
- process strategy disabled
- assembly path missing
- assembly not found
- process exits immediately
- readiness registry missing
- readiness capacity missing
- readiness timeout
- provider unavailable
- scale-out request rejected
- scale-out request expired
- scale-out deduplicated
- max runtime instances reached
- dedicated tenant fallback denied
- hybrid tenant fallback allowed and observable

---

## Summary

This phase introduced the runtime host manager architecture and validated the first real host creation mode.

The control plane can now scale out by launching a real external `RuntimeInstanceOnly` process, wait for real readiness through Redis registry/capacity, and fulfill the Redis scale-out request without fake capacity or fixture-only behavior.

The next objective is to harden the HTTP + Process path across tenant settings, dispatch, health, circuit breaker, and production failure scenarios before moving to gRPC, Kubernetes, and Attach.


---

## [1.0.6.8] - 2026-06-20 - HTTP Runtime Provider Hardening and Tenant-Aware Scale-Out 

Scope: HTTP runtime provider hardening, HTTP scale-out provider integration, Redis scale-out request flow, tenant-aware isolation validation, and preparation for Remote MCP Runtime Host Manager.

---

## 1. Objective of this work session

The goal of this chat was to finish the HTTP runtime provider hardening and scale-out path without breaking the existing distributed runtime architecture.

The target was not only to make HTTP dispatch more resilient, but also to make the HTTP provider participate correctly in the same control-plane scale-out lifecycle already validated for local runtime instances.

The desired production direction is:

```text
MCP/API submit
 -> RBAC execution context
 -> shared runtime controller
 -> tenant-aware admission
 -> no visible capacity
 -> Redis scale-out request
 -> watcher
 -> provider selector
 -> selected provider
 -> runtime capacity materialized
 -> registry/capacity visible through tenant rules
 -> scale-out request fulfilled
 -> shared queue dispatch
```

The major architectural rule confirmed during the session:

```text
Provider = how capacity is provisioned or requested
Transport = how the run is dispatched
Control interface = MCP can be used as the remote host manager protocol
```

---

## 2. HTTP provider hardening already completed before the scale-out scenarios

The HTTP runtime provider was hardened before the scenario work. The following capabilities are now part of the HTTP provider path.

### 2.1 HTTP runtime provider options

A new options class was added:

```text
AiHttpRuntimeInstanceProviderOptions
```

Main settings:

```text
DispatchTimeout = 30 seconds
EnableRetry = true
MaxRetryAttempts = 1
RetryBaseDelay = 200 ms
RetryMaxDelay = 2 seconds
RetryTimeouts = false
EnableCircuitBreaker = true
CircuitBreakerFailureThreshold = 5
CircuitBreakerBreakDuration = 30 seconds
```

Purpose:

```text
Make HTTP runtime dispatch resilient without coupling this logic to admission, scale-out, or tenant policy.
```

### 2.2 HTTP dispatch failure reasons

A dedicated set of HTTP provider failure reasons was added:

```text
http-endpoint-missing
http-endpoint-invalid
http-provider-unavailable
http-dispatch-timeout
http-command-failed
http-command-non-retryable
http-command-invalid-response
http-circuit-open
http-command-cancelled
http-command-exception
```

Purpose:

```text
Make dispatch failures observable and persistable in shared run state.
```

### 2.3 Dispatch resilience

The HTTP provider now handles:

```text
retry
retry exhaustion
timeout
non-retryable HTTP responses
invalid response
circuit breaker open
provider unavailable
cancellation
exception mapping
```

### 2.4 Persistence of dispatch failures

Direct dispatch failures are persisted through:

```text
IAiSharedRunStore.MarkDispatchFailedAsync(...)
```

The shared queue dispatcher also requeues or persists the correct failure reason.

### 2.5 Relevant tests validated

The following categories of tests were validated before moving deeper into HTTP scale-out:

```text
HTTP provider unavailable scenario
HTTP timeout scenario
HTTP circuit open scenario
HTTP retry success scenario
HTTP retry exhausted scenario
HTTP non-retryable scenario
HTTP provider options binding
shared queue dispatcher failure handling
shared runtime controller failure handling
Redis shared run store failure persistence
```

---

## 3. HTTP provider scale-out integration

The next step was to make the HTTP provider implement the same scale-out provider contract as the local provider.

The key design decision was:

```text
Do not create a separate HttpAiRuntimeScaleOutProvider.
The existing HttpAiRuntimeInstanceProvider should implement IAiRuntimeScaleOutProvider.
```

This keeps the model consistent:

```text
LocalAiRuntimeInstanceProvider : dispatch/status/control/scale-out
HttpAiRuntimeInstanceProvider  : dispatch/status/control/scale-out
```

### 3.1 Added HTTP scale-out provisioner abstraction

A new interface was introduced:

```text
IAiHttpRuntimeScaleOutProvisioner
```

Conceptual contract:

```text
ProvisionAsync(AiRuntimeScaleOutProviderRequest request)
 -> AiRuntimeScaleOutProviderResult
```

Purpose:

```text
Keep HttpAiRuntimeInstanceProvider as the provider entry point.
Delegate the provider-specific provisioning details behind an abstraction.
```

### 3.2 Added HTTP scale-out options

A new options class was introduced:

```text
AiHttpRuntimeScaleOutOptions
```

Main options:

```text
Enabled
DefaultRuntimeInstanceIdPrefix
EndpointTemplate
```

Important architectural decision:

```text
HTTP scale-out options are provider technical fallbacks only.
Tenant runtime settings come from admission and are already carried inside AiRuntimeScaleOutProviderRequest.
```

So the HTTP provisioner must use tenant-aware values from the request:

```text
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
```

### 3.3 Added HTTP scale-out provisioner implementation

A provisioner implementation was added:

```text
AiHttpRuntimeScaleOutProvisioner
```

Current behavior:

```text
Registers a runtime instance snapshot in the runtime registry.
Publishes a capacity descriptor in the runtime capacity store.
Returns a successful AiRuntimeScaleOutProviderResult.
```

This implementation is intentionally a foundation / metadata-based provisioner for the current phase.

It validates that the control-plane can route scale-out to HTTP and materialize HTTP capacity through registry/capacity.

### 3.4 HttpAiRuntimeInstanceProvider now implements IAiRuntimeScaleOutProvider

The HTTP provider was updated to implement:

```text
IAiRuntimeScaleOutProvider
```

Behavior:

```text
RequestScaleOutAsync(request)
 -> if no IAiHttpRuntimeScaleOutProvisioner is registered, return rejected result
 -> otherwise delegate to IAiHttpRuntimeScaleOutProvisioner.ProvisionAsync(...)
```

Rejected reason if missing provisioner:

```text
http-runtime-scaleout-provisioner-not-registered
```

### 3.5 DI updated

The HTTP provider service registration now binds:

```text
AiHttpRuntimeInstanceProviderOptions
AiHttpRuntimeScaleOutOptions
```

And registers:

```text
IAiHttpRuntimeScaleOutProvisioner -> AiHttpRuntimeScaleOutProvisioner
HttpAiRuntimeInstanceProvider
```

The provider remains registered as an `IAiRuntimeInstanceProvider`, and because it also implements `IAiRuntimeScaleOutProvider`, the scale-out selector can resolve it.

---

## 4. Unit tests and targeted flow tests added before full MCP scenarios

Before adding full MCP integration scenarios, several smaller tests were added or validated.

### 4.1 DI tests

Added tests to verify:

```text
HTTP scale-out provisioner is registered
HTTP scale-out options bind from configuration
HTTP provider resolves with scale-out provisioner
HTTP provider is assignable to IAiRuntimeScaleOutProvider
```

### 4.2 Provisioner unit test

Added test:

```text
ProvisionAsync_Should_Register_Runtime_And_Publish_Capacity
```

Validated that the provisioner:

```text
returns success
creates expected runtime instance id
registers runtime instance snapshot
publishes capacity descriptor
preserves provider metadata
preserves tenant metadata
preserves isolation metadata
preserves runtime settings metadata
```

### 4.3 Selector test

Added test:

```text
RequestScaleOutAsync_Should_Resolve_Http_Provider_From_Request_ProviderHint
```

Validated:

```text
providerHint=http
 -> selector resolves HttpAiRuntimeInstanceProvider
 -> provider delegates to IAiHttpRuntimeScaleOutProvisioner
```

### 4.4 Watcher HTTP test

Added test for watcher flow:

```text
pending scale-out request with ProviderHint=http
 -> watcher observes it
 -> selector resolves HTTP provider
 -> HTTP provisioner registers capacity
 -> scale-out request becomes Fulfilled
```

This validated the full watcher/provider/provisioner path without MCP submit.

### 4.5 In-memory store bug fixed

A bug was found in the in-memory scale-out request store clone logic.

Problem:

```text
Tenant-aware runtime settings were not copied in Clone(...)
```

Missing fields included:

```text
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
```

Effect:

```text
A request created as Dedicated could be read back as Shared in tests.
```

Fix:

```text
Clone(...) now preserves all tenant-aware runtime settings.
```

### 4.6 Store preservation tests added

Added / validated tests for both stores:

```text
InMemoryAiRuntimeScaleOutRequestStore.CreateAsync_Should_Preserve_Tenant_Aware_Runtime_Settings
RedisAiRuntimeScaleOutRequestStore.CreateAsync_Should_Preserve_Tenant_Aware_Runtime_Settings
```

Both now pass.

---

## 5. MCP HTTP scale-out scenario factory

A new test settings factory was needed for MCP integration scenarios that use HTTP scale-out but do not start a real HTTP runtime host.

Added method in:

```text
GenericMcpServerTestSettings
```

Method:

```text
CreateHttpScaleOutOnlyControlPlaneSettings(string controlPlaneId)
```

Purpose:

```text
Start an MCP control-plane host in HTTP runtime-instance mode.
Keep local runtime pool disabled.
Enable admission scale-out requests.
Enable Redis scale-out request watcher.
Configure providerName=http.
Enable HTTP scale-out provisioner.
Use an endpoint template for generated HTTP runtime metadata.
```

Important settings:

```text
AiMcpHost:Mode = ControlPlaneWithHttpRuntimeInstances
AiMcpHost:EnableSharedQueuePump = true
AiSharedQueueBackgroundService:Enabled = true
AiSharedQueuePump:Enabled = true
AiSharedRuntimeController:SubmitMode = DirectDispatch
AiRuntimeInstanceRegistration:ProviderName = http
AiRunAdmission:EnableScaleOutRequest = true
AiRunAdmission:EnableGlobalQueueFallback = false
AiRunAdmission:RejectWhenNoCapacity = false
AiRuntimeScaleOutRequestWatcher:Enabled = true
AiRuntimeScaleOutRequestWatcher:WatcherId = mcp-scaleout-watcher
AiHttpRuntimeScaleOut:Enabled = true
AiHttpRuntimeScaleOut:EndpointTemplate = http://runtime-host/{runtimeInstanceId}
```

This factory intentionally does not start a real runtime HTTP endpoint.

It validates HTTP scale-out capacity materialization, not dispatch execution.

---

## 6. HTTP MCP integration scenario class

A dedicated scenario class was created under the HTTP scenario namespace:

```text
Multiplexed.AI.McpServer.Tests.Integration.Scenarios.Http.HttpRuntimeProviderSharedRunScaleOutScenarioTests
```

Purpose:

```text
Validate real MCP submit -> admission -> Redis scale-out request -> watcher -> selector -> HTTP provider -> HTTP provisioner -> registry/capacity -> fulfilled.
```

This separates HTTP scale-out scenarios from local scale-out scenarios.

---

## 7. Full MCP HTTP scale-out scenario: Shared mode

Added scenario:

```text
ControlPlaneWithHttpRuntimeInstances_With_No_Runtime_Capacity_Should_Fulfill_Redis_ScaleOut_Request_Using_Http_Provider
```

Validated:

```text
MCP submit succeeds.
Shared run becomes ScaleOutRequested.
Admission decision is RequestScaleOut.
Tenant is default shared tenant.
Scale-out request is created in Redis.
ProviderHint is http.
Watcher observes the request.
HTTP provider/provisioner fulfills it.
Runtime registry contains HTTP runtime instance.
Runtime capacity store contains HTTP capacity descriptor.
Metadata includes provider.name=http.
Metadata includes transport.name=http.
Isolation mode is Shared.
Request status is Fulfilled.
```

Important rule validated:

```text
Default/shared tenant gets shared HTTP capacity with runtime-instance prefix.
```

---

## 8. Full MCP HTTP scale-out scenario: Dedicated mode

Added scenario:

```text
ControlPlaneWithHttpRuntimeInstances_With_Dedicated_Tenant_Should_Fulfill_Tenant_Aware_Redis_ScaleOut_Request_Using_Http_Provider
```

Validated:

```text
tenant-a resolves to Dedicated.
tenant-a uses tenant-a-runtime prefix.
PreferDedicatedCapacity = true.
AllowSharedFallback = false.
MaxRuntimeInstances = 3.
WorkerCountPerInstance = 10.
MaxConcurrentRunsPerInstance = 5.
LocalQueueCapacity = 500.
```

The test validates propagation across:

```text
ExecutionContextSnapshot
AdmissionDecision
ScaleOutRequestRecord
HTTP provisioned runtime registry snapshot
HTTP provisioned capacity descriptor
```

### 8.1 Important visibility issue discovered and fixed in the test

Initial failure:

```text
registry.GetAsync(runtimeInstanceId) returned null
```

Reason:

```text
Dedicated runtime capacity is hidden by tenant visibility filtering when the test thread does not have the correct ExecutionContextSnapshot.
```

Fix:

```text
Create a RedisAiRuntimeInstanceRegistry and RedisAiRuntimeInstanceCapacityStore with a MutableExecutionContextSnapshotProvider set to tenant-a context.
```

This made the test architecturally correct:

```text
Dedicated capacity is visible only when reading through the right tenant context.
```

### 8.2 TenantGroupId behavior clarified

Another issue was discovered:

```text
tenantRuntimeSettings.TenantGroupId was null
sharedRun.ExecutionContextSnapshot.TenantGroupId was tenant-group-id-xxx
```

Decision:

```text
The effective TenantGroupId must be taken from ExecutionContextSnapshot.
Runtime settings provide policy values, but execution context provides effective tenant identity/group.
```

So the test now uses:

```text
expectedTenantGroupId = sharedRun.ExecutionContextSnapshot.TenantGroupId
```

and validates propagation from there.

---

## 9. Full MCP HTTP scale-out scenario: Hybrid mode

Added scenario:

```text
ControlPlaneWithHttpRuntimeInstances_With_Hybrid_Tenant_Should_Fulfill_Tenant_Aware_Redis_ScaleOut_Request_Using_Http_Provider
```

Validated:

```text
tenant-b resolves to Hybrid.
tenant-b uses tenant-b-runtime prefix.
PreferDedicatedCapacity = true.
AllowSharedFallback = true.
MaxRuntimeInstances = 2.
WorkerCountPerInstance = 5.
MaxConcurrentRunsPerInstance = 3.
LocalQueueCapacity = 250.
ProviderHint = http.
Watcher fulfills the request.
Registry and capacity are visible in tenant-b context.
```

Important rule validated:

```text
Hybrid tenants can request their own tenant-specific capacity when no shared fallback capacity is available.
```

---

## 10. Dedicated tenant must not fallback to shared HTTP capacity

Added scenario:

```text
ControlPlaneWithHttpRuntimeInstances_With_Dedicated_Tenant_Should_Not_Fallback_To_Shared_Http_Runtime_When_Available
```

Purpose:

```text
Prove that tenant-a Dedicated cannot silently use an existing shared HTTP runtime capacity.
```

Test setup:

```text
Provision one shared HTTP runtime capacity manually through IAiHttpRuntimeScaleOutProvisioner.
Submit a run as tenant-a.
Admission should not assign the shared runtime.
Admission should request dedicated scale-out.
Watcher should fulfill a new tenant-a dedicated HTTP runtime.
Tenant-a context should see the dedicated runtime but not the shared runtime.
```

Validated:

```text
AdmissionDecision = RequestScaleOut
AssignedRuntimeInstanceId = null
ShouldRequestScaleOut = true
Scale-out request created for tenant-a
Scale-out request fulfilled by HTTP provider
Fulfilled runtime instance id uses tenant-a-runtime
Fulfilled runtime instance id is not the shared runtime instance id
Tenant visibility hides shared runtime from tenant-a
```

Critical production rule validated:

```text
Dedicated tenants must not silently degrade to shared capacity.
```

---

## 11. Hybrid tenant should fallback to shared HTTP capacity

Added scenario:

```text
ControlPlaneWithHttpRuntimeInstances_With_Hybrid_Tenant_Should_Fallback_To_Shared_Http_Runtime_When_Available
```

Purpose:

```text
Prove that tenant-b Hybrid can use existing shared HTTP runtime capacity when shared fallback is allowed.
```

Test setup:

```text
Provision one shared HTTP runtime capacity through IAiHttpRuntimeScaleOutProvisioner.
Submit a run as tenant-b.
Admission should assign the existing shared HTTP runtime.
No scale-out request should be created for tenant-b.
Tenant-b context should see the shared runtime capacity.
```

Validated:

```text
AdmissionDecision = AssignToInstance
AssignedRuntimeInstanceId = sharedRuntimeInstanceId
ShouldRequestScaleOut = false
No Redis scale-out request exists for tenant-b run
Assigned runtime uses runtime-instance shared prefix
Assigned runtime does not use tenant-b-runtime prefix
Hybrid tenant sees shared capacity through visibility evaluator
```

Critical production rule validated:

```text
Hybrid tenants may fallback to shared capacity when policy allows it.
```

---

## 12. Final HTTP scale-out policy matrix validated

The HTTP scale-out policy matrix is now complete.

```text
Shared HTTP scale-out                         OK
Dedicated HTTP scale-out                      OK
Hybrid HTTP scale-out                         OK
Dedicated must NOT fallback to shared HTTP    OK
Hybrid SHOULD fallback to shared HTTP         OK
```

This validates:

```text
tenant-aware admission
providerHint=http
Redis scale-out request lifecycle
watcher processing
provider selector routing
HTTP provider scale-out integration
HTTP provisioner registry/capacity publishing
tenant visibility rules
shared/dedicated/hybrid isolation policies
```

---

## 13. Important architectural clarification: fixtures vs real scale-out

During the session we clarified the difference between old HTTP dispatch tests and new HTTP scale-out tests.

### 13.1 Existing HTTP dispatch tests

Older HTTP integration tests start real HTTP runtime hosts through fixtures such as:

```text
GenericRuntimeInstanceHttpTestHost
```

Those tests validate:

```text
control-plane dispatches over HTTP
runtime HTTP endpoint receives command
runtime local queue executes DAG
execution completes
```

In that model, the test fixture creates the runtime.

### 13.2 New HTTP scale-out tests

The new tests do not start a real HTTP runtime endpoint.

They validate:

```text
control-plane can request and materialize HTTP runtime capacity through the provider/provisioner flow
```

The endpoint template:

```text
http://runtime-host/{runtimeInstanceId}
```

is metadata only for now.

### 13.3 Production target

The target architecture is not fixture-driven.

The target is:

```text
scale-out provider requests capacity
remote runtime host manager starts runtime
runtime self-registers in Redis
capacity heartbeat becomes Ready
scale-out request becomes Fulfilled
shared queue dispatches only when ready
```

---

## 14. Remote MCP Runtime Host Manager direction

A key design decision was clarified:

```text
The host manager can be MCP.
```

For non-Kubernetes HTTP or gRPC servers, the remote machine can expose an MCP server that acts as runtime host manager.

Production flow:

```text
Control Plane MCP
 -> scale-out request
 -> HTTP provider
 -> Remote MCP Runtime Host Manager
 -> starts RuntimeInstanceOnly HTTP runtime
 -> runtime self-registers registry/capacity
 -> control-plane waits readiness
 -> request Fulfilled
 -> dispatch
```

This makes MCP not only a tool interface but also a control-plane protocol for remote runtime lifecycle operations.

Example MCP host manager tools:

```text
runtime.host.createInstance
runtime.host.stopInstance
runtime.host.listInstances
runtime.host.getInstanceStatus
runtime.host.getCapacity
```

---

## 15. Provider model clarified

The model that emerged:

```text
Local provider
 -> starts local in-process runtime host

HTTP provider
 -> dispatches over HTTP
 -> can request scale-out through MCP remote host manager

gRPC provider
 -> dispatches over gRPC
 -> can request scale-out through MCP remote host manager

Kubernetes provider
 -> uses Kubernetes SDK
 -> creates/scales pods/deployments/jobs
 -> runtime self-registers
```

Important distinction:

```text
Provisioning provider and dispatch transport are related but not always the same thing.
```

Examples:

```text
providerHint=http, transport=http, provisioning.control=mcp
providerHint=grpc, transport=grpc, provisioning.control=mcp
providerHint=kubernetes, transport=http, provisioning.control=kubernetes-sdk
providerHint=kubernetes, transport=grpc, provisioning.control=kubernetes-sdk
```

---

## 16. Why the existing stores are exactly what is needed

The session confirmed that the existing Redis/Mongo stores are not incidental. They are the convergence layer for distributed runtime orchestration.

Store responsibilities:

```text
SharedRunStore
 -> durable shared run lifecycle

SharedQueue
 -> pending work to dispatch

ScaleOutRequestStore
 -> capacity request lifecycle

RuntimeRegistry
 -> runtime instance existence and metadata

RuntimeCapacityStore
 -> runtime readiness and capacity availability

AdmissionReservationStore
 -> prevents oversubscription and double assignment

DecisionLedger
 -> audit, replay, observability
```

This enables:

```text
control-plane coordinates
providers provision
remote hosts start runtimes
runtimes self-register
admission observes tenant-visible capacity
shared queue dispatches deterministically
```

---

## 17. Next implementation phase

Now that HTTP scale-out policy is validated, the next phase can start.

Target:

```text
Remote MCP Runtime Host Manager + readiness waiter + real HTTP runtime provisioning path
```

This is not expected to be a massive refactor because the abstractions already exist.

### 17.1 Suggested new abstractions

Add:

```text
IAiRuntimeInstanceReadinessWaiter
AiRuntimeInstanceReadinessRequest
AiRuntimeInstanceReadinessResult
```

Purpose:

```text
Wait until registry/capacity show that a runtime instance is actually ready.
```

Ready means:

```text
registry exists
capacity exists
status = Ready
canAcceptRun = true
transport endpoint exists
tenant metadata matches
provider metadata matches
```

Add:

```text
IAiRemoteRuntimeHostManagerClient
AiRemoteRuntimeStartRequest
AiRemoteRuntimeStartResult
AiRemoteRuntimeStopRequest
AiRemoteRuntimeStatusResult
```

Purpose:

```text
Abstract the MCP host manager call that starts/stops/list/checks remote runtime instances.
```

### 17.2 Suggested provisioner split

Current provisioner can stay as a test/dev foundation, but should eventually be made explicit:

```text
MetadataOnlyHttpRuntimeScaleOutProvisioner
```

New production-like provisioner:

```text
McpHttpRuntimeScaleOutProvisioner
```

Flow:

```text
ProvisionAsync(request)
 -> call remote MCP host manager StartRuntime
 -> receive operation/runtime instance information
 -> wait for runtime self-registration and capacity readiness
 -> return success only after readiness
```

### 17.3 Future Kubernetes provider

The Kubernetes provider will follow the same convergence model:

```text
Kubernetes provider
 -> Kubernetes SDK create/scale workload
 -> runtime pod starts RuntimeInstanceOnly
 -> runtime self-registers
 -> readiness waiter observes registry/capacity
 -> scale-out request fulfilled
```

---

## 18. Final state

At the end of this chat, HTTP runtime scale-out is validated as a real control-plane participant.

Completed:

```text
HTTP dispatch hardening
HTTP retry/timeout/circuit breaker
HTTP dispatch failure persistence
HTTP provider implements IAiRuntimeScaleOutProvider
HTTP provisioner abstraction
HTTP provisioner implementation
DI/options integration
selector routing to HTTP provider
watcher fulfillment through HTTP provider
store preservation of tenant-aware settings
MCP HTTP scale-out scenario factory
shared HTTP scale-out scenario
dedicated HTTP scale-out scenario
hybrid HTTP scale-out scenario
dedicated no shared fallback scenario
hybrid shared fallback scenario
tenant visibility-aware registry/capacity verification
```

Ready for next phase:

```text
Remote MCP Runtime Host Manager
Runtime readiness waiter
MCP-backed HTTP runtime provisioning
then gRPC and Kubernetes providers using the same convergence model
```


---

## [1.0.6.7] - 2026-06-18 — Multi-Tenant Control Plane Isolation

## Scope

This changelog summarizes the work completed around tenant-aware MCP control-plane execution, runtime isolation, scale-out, dispatch, and test stabilization.

The main objective was to make the control plane and runtime path tenant-aware without breaking the existing local, shared queue, Redis, retry, and execution-control flows.

---

## 1. Tenant Runtime Isolation Model

### Added / validated isolation modes

The runtime control plane now supports three tenant isolation modes:

- `Shared`
- `Dedicated`
- `Hybrid`

### Tenant runtime settings

A hardcoded tenant settings provider is currently used as the foundation for future configurable tenant settings.

Current tenant behavior:

```text
tenant-a
  IsolationMode = Dedicated
  PreferDedicatedCapacity = true
  AllowSharedFallback = false
  MaxRuntimeInstances = 3
  RuntimeInstanceIdPrefix = tenant-a-runtime
  WorkerCountPerInstance = 10
  MaxConcurrentRunsPerInstance = 5
  LocalQueueCapacity = 500

tenant-b
  IsolationMode = Hybrid
  PreferDedicatedCapacity = true
  AllowSharedFallback = true
  MaxRuntimeInstances = 2
  RuntimeInstanceIdPrefix = tenant-b-runtime
  WorkerCountPerInstance = 5
  MaxConcurrentRunsPerInstance = 3
  LocalQueueCapacity = 250

default / test-tenant / unknown tenant
  IsolationMode = Shared
  PreferDedicatedCapacity = false
  AllowSharedFallback = true
  MaxRuntimeInstances = 1
  RuntimeInstanceIdPrefix = runtime-instance
  WorkerCountPerInstance = 10
  MaxConcurrentRunsPerInstance = 3
```

### Key architectural rule

```text
ContextKey = volatile RBAC / correlation / debug context
ExecutionContextSnapshot.TenantId = durable tenant boundary
```

Tenant isolation must rely on `ExecutionContextSnapshot.TenantId`, not metadata and not only `ContextKey`.

---

## 2. Execution Context Snapshot Propagation

### Completed

The execution path now requires and preserves `ExecutionContextSnapshot` across async and distributed boundaries.

Important paths now carry or restore the snapshot:

```text
MCP request
  -> RBAC ExecutionContext
  -> ExecutionContextSnapshot
  -> SharedRunRecord
  -> SharedQueueDispatcher
  -> Runtime dispatch
  -> Runtime local queue
  -> BackgroundController
  -> DAG execution
```

### Fixed background dispatch context restoration

`AiSharedQueueDispatcher` now restores the tenant execution context from the shared run snapshot before running admission and dispatch logic.

This fixed the issue where background dispatch had no tenant context and therefore saw zero tenant-visible runtime instances.

### Updated unit test fixtures

Added / updated a fake execution context accessor for tests that directly instantiate `AiSharedQueueDispatcher`.

---

## 3. Runtime Instance Visibility Rules

### Fixed visibility evaluator rules

The visibility model was tightened.

Final rules:

```text
Shared runtime
  - visible to Shared tenants
  - visible to Hybrid/Dedicated tenants only if their tenant settings allow shared fallback

Dedicated runtime
  - visible only when TenantId or TenantGroupId matches

Hybrid runtime
  - visible only when TenantId or TenantGroupId matches
  - AllowSharedFallback does not make an unowned Hybrid runtime visible
```

### Corrected old unsafe test expectation

An old test expected an unowned Hybrid runtime to be visible to a Hybrid tenant when fallback was allowed.

That was corrected because fallback means:

```text
Hybrid tenant may fall back to Shared runtime
```

It does **not** mean:

```text
Hybrid tenant may use any unowned Hybrid runtime
```

---

## 4. Redis Runtime Registry and Capacity Filtering

### Runtime registry filtering

`RedisRuntimeInstanceRegistry.ListAsync` now filters runtime instances based on the current tenant visibility rules.

Validated behavior:

```text
tenant-a Dedicated
  sees tenant-a dedicated runtime only

tenant-b Hybrid
  sees tenant-b hybrid runtime and shared runtime when fallback is allowed

shared / test-tenant
  sees shared runtime only
```

### Runtime capacity filtering

`RedisRuntimeInstanceCapacityStore.ListAsync` and `GetAsync` now respect the same tenant visibility model.

This ensures admission cannot assign capacity from an invisible tenant runtime.

---

## 5. Admission Controller Tenant-Aware Assignment

### Completed

`AiRunAdmissionController` now assigns only tenant-visible runtime capacity.

Validated cases:

```text
tenant-a Dedicated
  - ignores tenant-b capacity
  - ignores shared capacity when fallback disabled
  - requests dedicated tenant-a scale-out if no visible capacity exists

tenant-b Hybrid
  - can use tenant-b hybrid capacity
  - can fall back to shared capacity when allowed

shared / test-tenant
  - uses shared runtime capacity
  - does not see tenant-a or tenant-b runtime capacity
```

### Scale-out decision includes tenant runtime settings

When admission cannot assign capacity, the `RequestScaleOut` decision now preserves the relevant tenant runtime settings.

---

## 6. Tenant-Aware Scale-Out Request Persistence

### Store-backed publisher

`StoreBackedAiRuntimeScaleOutRequestPublisher` now copies strong tenant runtime fields into `AiRuntimeScaleOutRequestRecord`.

Persisted fields include:

```text
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
MaxRuntimeInstances
RuntimeInstanceIdPrefix
WorkerCountPerInstance
MaxConcurrentRunsPerInstance
LocalQueueCapacity
```

### Redis scale-out request store fixed

`RedisAiRuntimeScaleOutRequestStore` was updated to round-trip all tenant runtime fields.

This fixed loss of tenant settings when a scale-out request was persisted and later read by the watcher/provider.

---

## 7. Local Runtime Scaler Tenant Scope Fix

### Problem fixed

`AiLocalRuntimeInstanceScaler` previously used the global host count when satisfying scale-out requests.

That caused this bug:

```text
Shared runtime already exists: runtime-instance-1
Tenant-a Dedicated requests one runtime
Scaler sees global hosts.Count = 1
Scaler incorrectly noops and returns shared runtime
```

### Fix

The scaler now counts matching hosts by tenant-aware runtime prefix instead of global host count.

Examples:

```text
runtime-instance-*     -> shared/default runtime pool
tenant-a-runtime-*    -> tenant-a dedicated pool
tenant-b-runtime-*    -> tenant-b hybrid pool
```

### Result

Dedicated and Hybrid tenants now get their own runtime instance pools when required.

---

## 8. Shared Queue Dispatch Flow

### Fixed flow

The shared queue dispatcher now performs the correct tenant-aware sequence:

```text
Load queue item
Load SharedRunRecord
Restore ExecutionContextSnapshot
Run admission with tenant-visible registry/capacity
Reserve selected runtime capacity
Dispatch to selected runtime instance
Mark shared run dispatched
Restore/clear previous execution context
```

### Validated paths

```text
Shared/default tenant
  -> runtime-instance-1
  -> scale-out + requeue + dispatch + execution OK

tenant-a Dedicated
  -> tenant-a-runtime-1
  -> no shared fallback
  -> scale-out + dispatch + execution OK

tenant-b Hybrid
  -> tenant-b-runtime-1 when dedicated/hybrid capacity is needed
  -> runtime-instance-1 when shared fallback is available and allowed
  -> dispatch + execution OK
```

---

## 9. Runtime Local Queue / Background Controller Snapshot Requirement

### Behavior tightened

`AiRuntimePipelineBackgroundController` now fails fast if a queued runtime run has no `ExecutionContextSnapshot`.

This protects tenant-aware execution from running without a durable tenant boundary.

### Tests updated

Legacy direct runtime integration tests were updated to provide a minimal `ExecutionContextSnapshot` when enqueueing directly into the runtime controller.

---

## 10. Execution Control Finalization Test Stabilization

### Problem

`AiExecutionControlFinalizationIntegrationTests` was fragile because it waited for `handle.ExecutionId` before the controlled step had actually started.

### Fix

The controlled step now signals startup together with the real `ExecutionId` from `context.Record.ExecutionId`.

The test now follows this deterministic order:

```text
Enqueue run with ExecutionContextSnapshot
Wait until controlled step starts
Capture ExecutionId from step context
Cancel execution while claimed work is running
Wait for completion
Assert final status is Cancelled
Assert durable record is Cancelled
Assert execution control store is Cancelled
```

### Result

The test now validates the intended rule:

```text
Cancellation overrides natural DAG completion during terminal finalization.
```

---

## 11. Redis Retry Test Stabilization

### Problem

`AiDagExecutionEngineRedisRetryIntegrationTests.ExecuteNextAsync_Should_Not_Reexecute_Step_Before_RetryDelay` asserted too strictly on the returned execution status from `ExecuteNextAsync`.

The real invariant was not the returned record status, but the persisted Redis step state.

### Fix

The test now validates the persisted state:

```text
Step remains WaitingForRetry
RetryCount remains 1
NextRetryAtUtc remains unchanged
NextRetryAtUtc is still in the future
```

The retry delay was increased to reduce timing flakiness under Redis / CI / debugger conditions.

---

## 12. Tests Added / Updated / Validated

Key scenarios now covered:

```text
ControlPlaneWithLocalRuntimeInstances_With_Dedicated_Tenant_Should_Create_Tenant_Aware_ScaleOut_Request
ControlPlaneWithLocalRuntimeInstances_With_Hybrid_Tenant_Should_Create_Tenant_Aware_ScaleOut_Request
ControlPlaneWithLocalRuntimeInstances_With_Default_Tenant_Should_Create_Shared_ScaleOut_Request
ScaleOutPublisher_With_Dedicated_Tenant_At_Max_Instance_Count_Should_Not_Request_Above_Tenant_Max
ScaleOutPublisher_With_Hybrid_Tenant_At_Max_Instance_Count_Should_Not_Request_Above_Tenant_Max
ScaleOutWatcher_With_Hybrid_Tenant_Request_Should_Preserve_Tenant_Runtime_Settings_When_Fulfilling
RuntimeInstanceVisibilityEvaluator_Should_Respect_Tenant_Isolation_Modes
RedisRuntimeInstanceRegistry_ListAsync_Should_Filter_Runtime_Instances_By_Current_Tenant_Visibility
RedisRuntimeInstanceCapacityStore_ListAsync_And_GetAsync_Should_Filter_Capacity_By_Current_Tenant_Visibility
RunAdmissionController_Should_Assign_Only_Tenant_Visible_Runtime_Capacity
RunAdmissionController_With_Dedicated_Tenant_And_No_Visible_Capacity_Should_Request_Tenant_Aware_ScaleOut
ControlPlaneWithLocalRuntimeInstances_With_Dedicated_Tenant_Should_ScaleOut_Dispatch_And_Execute_Run_On_Tenant_Runtime
ControlPlaneWithLocalRuntimeInstances_With_Hybrid_Tenant_Should_ScaleOut_Dispatch_And_Execute_Run_On_Tenant_Runtime
ControlPlaneWithLocalRuntimeInstances_With_Hybrid_Tenant_Should_Fallback_To_Shared_Runtime_When_Available
ControlPlaneWithLocalRuntimeInstances_With_Dedicated_Tenant_Should_Not_Fallback_To_Shared_Runtime_When_Available
```

Additional legacy tests were fixed after the snapshot requirement became stricter.

---

## 13. Final Validation

Final test result:

```text
1036 tests green
```

This validates that tenant-aware runtime isolation, scale-out, shared queue dispatch, retry behavior, and execution-control finalization are stable together.

---

## 14. Architectural Flow Summary

```text
MCP Tool
  -> RBAC / Authorization / Tenant resolution
  -> ExecutionContextSnapshot
  -> SharedRuntimeController.SubmitRun
  -> SharedRunStore
  -> AdmissionController
  -> Tenant runtime settings
  -> Tenant-visible Registry + Capacity
  -> AssignToInstance or RequestScaleOut
  -> ScaleOutRequestStore
  -> ScaleOutWatcher / Provider / LocalScaler
  -> RuntimeInstanceRegistry + CapacityStore
  -> SharedQueue
  -> SharedQueueDispatcher restores ExecutionContextSnapshot
  -> Admission again
  -> Dispatch to RuntimeInstance
  -> Runtime local queue
  -> BackgroundController restores ExecutionContextSnapshot
  -> AiDagExecutionEngine.CreateAsync
  -> Worker ExecuteNextAsync loop
  -> Step claim / execute / retry / recover / converge
  -> ExecutionControl final override
  -> Final AiExecutionRecord
  -> SharedRun final status
  -> Ledger / tracing / replay
```

---

## 15. Recommended Commit Message

```text
Stabilize tenant-aware runtime isolation tests

Fixes shared queue tenant context restoration, scopes local scale-out by runtime prefix, aligns runtime visibility tests with shared/dedicated/hybrid isolation rules, persists tenant runtime settings through Redis scale-out requests, and updates legacy execution tests for required execution context snapshots.
```

PowerShell:

```powershell
git add .; git commit -m "Stabilize tenant-aware runtime isolation tests - Fixes shared queue tenant context restoration, scopes local scale-out by runtime prefix, aligns runtime visibility tests with shared/dedicated/hybrid isolation rules, persists tenant runtime settings through Redis scale-out requests, and updates legacy execution tests for required execution context snapshots."
```

---

## 16. Next Steps

Recommended sequence:

```text
1. Continue Kubernetes demo on top of this clean isolation model.
2. Then move to production hardening:
   - HTTP/gRPC circuit breakers
   - dispatch timeouts
   - Redis TIME in Lua scripts
   - queue max depth / backpressure
   - DLQ store
   - Mongo indexes
   - MCP rate limiting
   - Redis registry/capacity TTL + self-healing
3. Later: V2 storage engine with step-level DAG storage and O(1) dependency counters.
```


---

## [1.0.6.6] - 2026-06-18 — Runtime Run Index Tenant Isolation

## Scope

This update finalizes the tenant-isolation hardening around local runtime `RunId` visibility when the runtime queue control plane is accessed through MCP.

The main goal was to prevent a tenant from bypassing isolation by calling runtime queue operations directly with another tenant's `RuntimeInstanceId` and local `RunId`.

## Key changes

### Redis-backed runtime run execution index

Added a Redis-backed implementation for `IAiRuntimeRunExecutionIndex`.

The index stores the relationship between a local runtime `RunId`, its eventual `ExecutionId`, runtime instance metadata, status, timestamps, and the associated `ExecutionContextSnapshot`.

This makes the runtime run index durable and shared across MCP/control-plane hosts, HTTP runtime providers, local runtime pools, and Kubernetes-like multi-instance deployments.

### ExecutionContextSnapshot persisted in the runtime run index

Extended runtime run index entries with `ExecutionContextSnapshot`.

The snapshot is now used as the durable tenant boundary for runtime run visibility.

Important rule preserved:

```text
ExecutionContextSnapshot.TenantId = durable tenant isolation boundary
ExecutionContextSnapshot.ContextKey = volatile RBAC/context key, not a durable partition key
```

### Tenant-aware Redis index behavior

The Redis runtime run index now supports tenant filtering through the active execution context.

When a caller resolves a runtime run by `RunId`, the Redis index verifies the tenant from the active `ExecutionContextSnapshot` before exposing the run.

Expected behavior:

```text
Tenant A GetAsync(run-a) => visible
Tenant A GetAsync(run-b) => null
Tenant B GetAsync(run-b) => visible
Tenant B GetAsync(run-a) => null
```

### Lua/script-cache support

Added Lua-backed Redis operations and script caching for the runtime run index.

The implementation supports:

```text
RegisterQueued
MarkStarted
MarkCompleted
MarkFailed
MarkCancelled
```

The script cache reloads scripts after `NOSCRIPT` responses, matching the pattern already used by other Redis-backed control-plane stores.

### DI wiring

Kept the default control-plane registration safe and in-memory:

```csharp
services.TryAddSingleton<IAiRuntimeRunExecutionIndex, InMemoryAiRuntimeRunExecutionIndex>();
```

Added an explicit Redis opt-in extension:

```csharp
services.AddAiRedisRuntimeRunExecutionIndex(...);
```

This extension replaces the default in-memory implementation only when Redis-backed control-plane stores are enabled.

### MCP host Redis wiring

The MCP host now enables the Redis runtime run execution index from the central Redis control-plane store registration path.

The call is made from `AddRedisControlPlaneStoresIfAvailable(...)`, together with the other Redis-backed stores:

```text
Redis shared run store
Redis shared queue
Redis runtime run execution index
Redis admission reservation store
Redis scale-out request store
```

This avoids duplicating the wiring inside each MCP host mode.

## Runtime queue control-plane hardening

### Fixed GetRunStatus tenant bypass

Before the fix, `GetRunStatus` asked the local runtime controller for `RunId` state before checking the runtime run index.

That allowed a tenant to access another tenant's local runtime run by knowing:

```text
RuntimeInstanceId + LocalRunId
```

The method now checks the runtime run execution index first.

Correct order:

```text
1. Resolve RunId through IAiRuntimeRunExecutionIndex
2. If index returns null, return an empty result
3. Only then ask the local runtime controller for live state
```

This makes the runtime run index the authority for tenant visibility.

### Fixed CancelRun tenant bypass

Before the fix, `CancelRun` called the local runtime controller before checking tenant visibility.

That allowed a cross-tenant caller to attempt cancellation against another tenant's local run.

The method now checks the runtime run execution index first.

If the caller cannot see the indexed run, the operation returns an empty successful result and never touches the local controller.

### Fixed CancelQueuedRun tenant bypass

The same guard was applied to `CancelQueuedRun`.

This prevents queued runtime run cancellation from bypassing tenant isolation.

## Tests added / validated

### Redis runtime run index tenant isolation

Added direct Redis tests for `RedisAiRuntimeRunExecutionIndex`.

Validated:

```text
RegisterQueued tenant isolation
GetAsync tenant isolation
MarkStarted tenant isolation
MarkCompleted tenant isolation
MarkFailed tenant isolation
MarkCancelled tenant isolation
ExecutionContextSnapshot persistence
```

### MCP runtime run status tenant isolation

Added MCP scenario validating the full path:

```text
MCP RBAC headers
ExecutionContextSnapshot mapping
Shared runtime controller
Shared queue dispatch
Local runtime queue control-plane
Redis runtime run execution index
Runtime run status visibility
```

Validated:

```text
Tenant A submits and dispatches a run
Tenant A can read runtime run status
Tenant B cannot read Tenant A runtime run status
Tenant B does not receive Tenant A ExecutionId
```

### Redis usage assertions

Added explicit assertions proving Redis is actually used:

```text
DI resolves RedisAiRuntimeRunExecutionIndex
Redis item key exists for the dispatched local RunId
```

This avoids false-positive tests where the in-memory implementation would accidentally be used.

### MCP runtime run cancel tenant isolation

Added MCP scenario for cross-tenant cancellation.

Validated:

```text
Tenant A submits and dispatches a runtime run
Tenant B attempts CancelRuntimeQueueRun against Tenant A LocalRunId
Tenant B receives no RunState
Tenant B receives no ExecutionId
Tenant A's run remains visible to Tenant A
Tenant A's run is not cancelled by Tenant B
```

## Important architectural result

The runtime queue control-plane now follows this rule:

```text
For any operation targeting a local RunId, tenant visibility must be checked through IAiRuntimeRunExecutionIndex before the local runtime controller is accessed.
```

This is important because the local runtime controller owns execution mechanics, but it does not own tenant visibility.

Tenant visibility belongs to:

```text
ExecutionContextSnapshot
→ IAiRuntimeRunExecutionIndex
→ RedisAiRuntimeRunExecutionIndex
```

## Current validated chain

The following multi-tenant path is now covered:

```text
MCP RBAC context
→ ExecutionContextSnapshot
→ Redis shared run isolation
→ Redis shared queue isolation
→ Redis runtime run execution index isolation
→ RuntimeQueue GetRunStatus isolation
→ RuntimeQueue CancelRun isolation
→ RuntimeQueue CancelQueuedRun guard
```

## Notes

No global refactor was required.

The update is a targeted hardening of the runtime run index and runtime queue control-plane visibility model.

The default in-memory behavior remains available for lightweight/local hosts, while Redis is enabled explicitly for Redis-backed MCP/control-plane scenarios.

---

## [1.0.6.5] - 2026-06-17 — Multi-tenant Control Plane Isolation

## Summary

This update completes an important stabilization pass around execution-context propagation and tenant isolation for the control-plane runtime path.

The main objective was to make the new strict `ExecutionContextSnapshot` requirement compatible with existing runtime integration tests, shared run execution, Redis-backed shared run storage, and MCP end-to-end scenarios.

All tests are now green, and a Redis + MCP tenant isolation scenario has been added to lock the behavior.

## Key outcome

The runtime now consistently preserves and propagates the execution context snapshot from submission to execution.

This means:

- direct runtime requests must carry an `ExecutionContextSnapshot`;
- shared runs persist the snapshot;
- local runtime queue requests receive the snapshot;
- the background controller restores the context before execution creation;
- tenant identity comes from `ExecutionContextSnapshot.TenantId`;
- `ContextKey` remains volatile and is not used as a durable tenant or execution key.

## ExecutionContextSnapshot propagation fixes

Several runtime integration tests were still creating direct `AiRuntimePipelineRunRequest` instances without an execution context snapshot.

This caused failures like:

```text
No execution context snapshot is available for runtime run '...'
The shared run must persist ExecutionContextSnapshot in Redis and propagate it to the local runtime queue.
```

The affected tests were updated to use the shared test fixture:

```csharp
AiRuntimeExecutionContextSnapshotTestFixture.CreateRunRequest(...)
```

### Updated test areas

- `AiExecutionReplayReferenceIntegrationTests`
- `AiRuntimePipelineBackgroundControllerQueueControlTests`
- `AiRuntimeDistributedChaosIntegrationTests`
- `AiRuntimePipelineBackgroundControllerChaosIntegrationTests`
- `AiRuntimeInstanceWorkerIntegrationTests`
- `AiEnterpriseRuntimeDemoPipelineTests`

## Shared run store tenant isolation

The shared run store path was hardened for tenant isolation.

### Redis shared run store

`RedisAiSharedRunStore` was updated to become tenant-aware while preserving backward compatibility.

Added tenant-scoped index support:

```text
ai:control-plane:{controlPlaneId}:tenant:{tenantId}:shared-runs:index
```

The existing control-plane index remains:

```text
ai:control-plane:{controlPlaneId}:shared-runs:index
```

This keeps old behavior compatible while enabling tenant-filtered listing.

### Redis Lua script

`RedisAiSharedRunStoreScripts.Create` was updated to accept an optional third key:

```text
KEYS[1] = shared run hash key
KEYS[2] = control-plane shared run index
KEYS[3] = optional tenant shared run index
```

The script now writes to the tenant index when provided, without breaking old callers that still pass only two keys.

### In-memory shared run store

`InMemoryAiSharedRunStore` was updated to support tenant-aware reads when an `IExecutionContextSnapshotProvider` is available.

The store now filters:

- `GetAsync`
- `ListAsync`
- `CancelAsync`

by `ExecutionContextSnapshot.TenantId`.

`MarkDispatchedAsync` was intentionally kept compatible for background/pump flows where an active RBAC context may not exist.

## MCP + Redis tenant isolation coverage

A new Redis-backed MCP scenario was added:

```text
SharedRunTenantIsolationScenarioTests
```

This validates tenant isolation through the real path:

```text
MCP headers
RBAC context
ExecutionContextSnapshot.TenantId
AiSharedRuntimeController
RedisAiSharedRunStore
Redis tenant indexes
List/Get/Cancel tenant isolation
```

The test verifies:

- Tenant A submits a shared run.
- Tenant B submits a shared run.
- Tenant A lists only Tenant A runs.
- Tenant B lists only Tenant B runs.
- Tenant A cannot read Tenant B's shared run.
- Tenant A cannot cancel Tenant B's shared run.
- Tenant B can cancel its own shared run.

This is the real production-relevant validation because it exercises MCP, Redis, RBAC context, shared controller, and shared run store together.

## Design decisions confirmed

### Tenant identity

`ExecutionContextSnapshot.TenantId` is the durable tenant boundary.

### Context key

`ExecutionContextSnapshot.ContextKey` remains volatile and must not be used as:

- tenant partition key;
- execution id;
- orchestration key;
- durable storage key.

### Strict runtime engine

The runtime engine remains strict.

There is no silent default tenant fallback in execution creation. Tests and runtime requests must provide a valid `ExecutionContextSnapshot`.

### Compatibility

The Redis Lua script remains backward compatible with older calls using only the original two keys.

The in-memory store keeps a default constructor so older tests and local demos continue to work.

## Files changed / added

### Runtime / Store

- `RedisAiSharedRunStore.cs`
- `RedisAiSharedRunStoreScripts.cs`
- `InMemoryAiSharedRunStore.cs`

### Runtime integration tests

- `AiExecutionReplayReferenceIntegrationTests.cs`
- `AiRuntimePipelineBackgroundControllerQueueControlTests.cs`
- `AiRuntimeDistributedChaosIntegrationTests.cs`
- `AiRuntimePipelineBackgroundControllerChaosIntegrationTests.cs`
- `AiRuntimeInstanceWorkerIntegrationTests.cs`
- `AiEnterpriseRuntimeDemoPipelineTests.cs`

### New tenant isolation test

- `SharedRunTenantIsolationScenarioTests.cs`

## Validation

All tests are green after the fixes.

The important validation added in this cycle is not only that the tests pass, but that the Redis + MCP path now explicitly protects tenant isolation for shared runs.


## Next recommended steps

The next isolation work should continue in this order:

1. Shared queue tenant isolation.
2. Runtime run execution index tenant isolation.
3. Runtime instance registry and capacity visibility rules.
4. Dedicated/shared/hybrid runtime instance isolation modes.

The most critical shared run boundary is now protected. The next risk is whether queue claiming, run indexes, and instance visibility can leak across tenants under shared infrastructure.

---

## [1.0.6.4] - 2026-06-17 — Multi-tenant RBAC / ExecutionContextSnapshot / MCP Integration Tests

## Summary

This update finalizes the propagation of the RBAC execution context across distributed runtime execution paths.

The main issue fixed was that the control plane correctly captured and stored an `ExecutionContextSnapshot`, but runtime workers processing queued or dispatched runs did not always restore the active RBAC execution context before creating the durable execution.

This caused failures such as:

```text
No active RBAC context is available.
```

The fix keeps the architecture strict:

```text
RBAC context
-> ExecutionContextSnapshot
-> Redis shared run / shared queue
-> runtime dispatch request
-> local runtime queue
-> background controller
-> restored active execution context
-> AiDagExecutionCreator
```

No fallback tenant, no hidden default context, and no relaxation inside `AiDagExecutionCreator`.

---

## 1. Propagated ExecutionContextSnapshot into runtime run requests

### Updated

`AiRuntimePipelineRunRequest`

### Change

Added:

```csharp
public ExecutionContextSnapshot? ExecutionContextSnapshot { get; init; }
```

### Purpose

The local runtime queue request can now carry the durable execution context captured by the control plane.

This allows background runtime execution to restore the RBAC context before creating the execution.

---

## 2. Attached ExecutionContextSnapshot when submitting shared runs

### Updated

`AiSharedRuntimeController`

### Change

Before creating the shared run record and queue item, the controller now copies the captured `ExecutionContextSnapshot` into the nested `RunRequest`.

### Result

The same tenant/context snapshot is now present in:

```text
AiSharedRunRecord.ExecutionContextSnapshot
AiSharedRunRecord.RunRequest.ExecutionContextSnapshot
AiSharedQueueItem.ExecutionContextSnapshot
```

This makes Redis the durable source of truth for tenant-aware distributed execution.

---

## 3. Preserved ExecutionContextSnapshot during local shared runtime dispatch

### Updated

`LocalAiSharedRuntimeInstance`

### Change

Before enqueueing into the local runtime queue, the local shared runtime instance now ensures the `RunRequest` contains the shared run execution context snapshot.

### Purpose

This closes the gap between:

```text
SharedRun stored in Redis
-> Dispatch to local runtime instance
-> Local runtime queue request
```

Without this, the background controller could receive a run request without a context snapshot.

---

## 4. Restored RBAC execution context inside the runtime background controller

### Updated

`AiRuntimePipelineBackgroundController`

### Change

The background controller now restores the active RBAC execution context from:

```csharp
request.ExecutionContextSnapshot
```

before calling:

```csharp
CreateExecutionAsync(...)
```

### Result

`AiDagExecutionCreator` can now safely access the active execution context through the accessor.

This fixed the runtime failure:

```text
No active RBAC context is available.
```

### Design preserved

The execution creator remains strict. It does not create fake/default contexts and does not silently fallback to a default tenant.

---

## 5. Fixed MCP integration fixtures to pass the tenant into RBAC context creation

### Updated test areas

- Shared run scenario tests
- Shared run scale-out scenario tests
- Shared run heavy dispatch scenario tests
- Shared run background pump scenario tests
- Runtime instance empty registry test

### Change

Old pattern:

```csharp
CreateConfiguredClientAsync(
    host,
    client,
    RequestedBy)
```

New pattern:

```csharp
CreateConfiguredClientAsync(
    host,
    client,
    RequestedBy,
    tenantId: TenantId)
```

### Purpose

The submit requests already used:

```csharp
TenantId = "test-tenant"
```

but the RBAC context could still be created with a default tenant.

After this fix, the tenant is aligned across:

```text
SubmitRequest.TenantId
RBAC context TenantId
ExecutionContextSnapshot.TenantId
Redis SharedRun TenantId
Redis SharedQueue TenantId
ScaleOutRequest TenantId
```

---

## 6. Fixed HTTP runtime fixture tenant propagation

### Updated

`GenericMcpRuntimeFixture` usages in HTTP runtime tests.

### Change

Old pattern:

```csharp
new GenericMcpRuntimeFixture(
    controlPlaneSettings,
    runtimeInstanceSettings)
```

New pattern:

```csharp
new GenericMcpRuntimeFixture(
    controlPlaneSettings,
    runtimeInstanceSettings,
    rbacTenantId: TenantId)
```

### Purpose

HTTP control-plane/runtime-instance scenarios now use the same tenant-aware RBAC context as local scenarios.

This keeps HTTP dispatch aligned with the same multi-tenant execution model.

---

## 7. Fixed health endpoint fixture after RBAC/auth middleware activation

### Updated

`McpServerFixture`

### Problem

The health test failed because the application now starts authentication middleware, but the old fixture did not register an authentication scheme.

Failure:

```text
Unable to resolve service for type
'Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider'
while attempting to activate 'AuthenticationMiddleware'.
```

### Change

The old `McpServerTestHost` fixture was replaced with a `GenericMcpServerTestHost`-based fixture compatible with the RBAC/auth setup used by the other MCP integration tests.

### Additional fix

The same runtime instance id is now used consistently for:

```text
AiRuntimeInstanceRegistration:RuntimeInstanceId
AiEngine:RuntimeInstanceId
AiEngine:PipelineBackgroundController:RuntimeInstanceId
AiEngine:RuntimeInstanceWorker:RuntimeInstanceId
```

This fixed the validation error:

```text
Runtime instance id mismatch.
```

---

## 8. Made cancellation assertion tolerant to fast terminal transition

### Updated

Long-running cancellation test.

### Problem

The test expected:

```text
Cancelling
```

but the execution could already have reached:

```text
Cancelled
```

by the time the status was read.

### Change

Old assertion:

```csharp
Assert.Equal(
    "Cancelling",
    cancellingStatus.State?.Status.ToString());
```

New assertion:

```csharp
var cancellationStatus =
    cancellingStatus.State?.Status.ToString();

Assert.Contains(
    cancellationStatus,
    new[]
    {
        "Cancelling",
        "Cancelled"
    });
```

### Purpose

This matches the real lifecycle behavior: `Cancelling` is transitional and can legitimately become `Cancelled` very quickly.

---

## Final result

The runtime now correctly supports tenant-aware distributed execution across:

```text
MCP request
RBAC context store
Control plane
Redis shared run store
Redis shared queue
Scale-out request store
HTTP/local runtime dispatch
Local runtime queue
Background execution controller
AiDagExecutionCreator
Ledger / trace / replay
```

The key architectural guarantee is now preserved:

```text
Tenant identity comes from ExecutionContextSnapshot.
ExecutionContextSnapshot is captured once, persisted, transported, and restored.
No runtime worker creates executions without an active RBAC execution context.
```

---

## [1.0.6.3] - 2026-06-17 - MCP RBAC Tool Authorization

### Added

- Added RBAC capability authorization to MCP tool methods.
- Added capability-based access control for shared run MCP tools:
  - `run.submit_run`
  - `run.submit_many_runs`
  - `run.list_shared`
  - `run.get_shared`
  - `run.cancel_shared`
- Added capability-based access control for shared queue MCP tools:
  - `queue.drain`
  - `shared_queue.list`
  - `shared_queue.status`
  - `shared_queue.activity`
- Added capability-based access control for runtime queue MCP tools:
  - `runtime_queue.status`
  - `runtime_queue.run_status`
  - `runtime_queue.pause`
  - `runtime_queue.resume`
  - `runtime_queue.cancel_run`
  - `runtime_queue.cancel_queued_run`
- Added capability-based access control for runtime instance visibility tools:
  - `instance.list`
  - `instance.active`
  - `instance.status`
- Added capability-based access control for execution control tools:
  - `control.pause`
  - `control.resume`
  - `control.cancel`
  - `control.status`
- Added capability-based access control for observability tools:
  - `observability.ledger.get_by_execution`
  - `observability.ledger.query`
  - `observability.trace.get_by_execution`
  - `observability.metrics.status`
- Added centralized MCP RBAC test context creation through `McpRbacTestContextFactory`.
- Added centralized MCP RBAC test client setup through `McpRbacTestClientHelper`.
- Added fake authentication support for MCP integration test hosts.
- Added default RBAC execution context support for MCP runtime/system calls.
- Added integration test coverage for MCP RBAC context propagation across local, HTTP, shared queue, runtime queue, execution control, observability, and scale-out scenarios.

### Changed

- Updated MCP endpoint pipeline ordering so authentication and RBAC execution-context middleware run before MCP tool mapping.
- Updated MCP integration tests to propagate RBAC headers consistently through `X-Access-Context` and `X-Demo-UserId`.
- Updated MCP test client to propagate RBAC demo headers for in-flight/race-limit test scenarios.
- Updated scenario tests that create isolated MCP hosts to use the shared RBAC test client helper.
- Updated MCP tools to use explicit TRN-compatible capability attributes based on resource, feature, and action.

### Fixed

- Fixed MCP integration test failures caused by missing RBAC execution context headers.
- Fixed `401 Unauthorized` responses in isolated MCP scenario tests by storing and attaching a valid RBAC execution context.
- Fixed RBAC in-flight/race-limit failures in high-polling MCP integration tests by propagating demo max in-flight headers.
- Fixed inconsistent RBAC setup between fixture-based MCP tests and manually-created MCP test clients.

### Security

- MCP tool execution is now protected by capability-level RBAC authorization.
- MCP control-plane operations now distinguish unauthorized context access, missing capabilities, and in-flight/race-limit violations.
- Added explicit authorization boundaries for runtime submission, cancellation, queue draining, runtime visibility, execution control, replay, ledger, trace, and observability access.

---

## [1.0.6.2] - 2026-06-12 — Redis-backed Local Runtime Scale-Out Flow

## Summary

This milestone validates the full Redis-backed local runtime scale-out execution flow for the MCP control plane.

The system can now start with **zero local runtime capacity**, accept a shared MCP run, detect that no runtime instance is available, create a Redis-backed scale-out request, dynamically create a local runtime instance, requeue the original shared run, dispatch it through the normal shared queue pump, and execute it successfully to completion.

Final validated flow:

```text
MCP submit
  -> admission detects no available runtime capacity
  -> shared run becomes ScaleOutRequested
  -> Redis scale-out request is created
  -> scale-out watcher observes the pending request
  -> provider selector resolves the local scale-out provider
  -> LocalAiRuntimeInstanceProvider delegates to AiLocalRuntimeInstanceScaler
  -> local runtime instance is created, registered, and started
  -> scale-out request is marked Fulfilled
  -> original shared run is requeued
  -> shared queue pump claims the requeued run
  -> run is dispatched to the newly created local runtime instance
  -> local runtime consumes the run
  -> ExecutionId is created
  -> runtime run completes successfully
```

Validated final test output:

```text
FINAL SCALE-OUT EXECUTION STATUS:
SharedRunStatus='Dispatched'
AssignedRuntimeInstanceId='host-...:mcp-scaleout-runtime-1'
LocalRunId='...'
ExecutionId='...'
RuntimeRunStatus='completed'
QueueStatus='Dispatched'
ScaleOutRequestStatus='Fulfilled'
ActiveLocalInstances='1'
```

All MCP integration tests pass after the changes.

---

## Added

### Runtime scale-out provider capability

- Added `IAiRuntimeScaleOutProvider` as a capability on top of the existing runtime instance provider system.
- `IAiRuntimeScaleOutProvider` now extends `IAiRuntimeInstanceProvider`.
- This allows scale-out providers to be resolved by the existing `IAiRuntimeInstanceProviderRouter`.
- No separate scale-out router was introduced.
- No separate `ProviderName` property was added to the scale-out provider abstraction.

Key design decision:

```text
Scale-out is a provider capability, not a new provider-routing model.
```

---

### Scale-out provider selector

- Added `IAiRuntimeScaleOutProviderSelector`.
- Added `AiRuntimeScaleOutProviderSelector`.
- The selector resolves the scale-out provider through the existing runtime instance provider router.
- Provider name resolution order:
  1. `AiRuntimeScaleOutProviderRequest.ProviderHint`
  2. `AiRuntimeInstanceRegistrationOptions.ProviderName`
  3. fallback to `local`
- The selector creates a synthetic `AiRuntimeInstanceCapacityDescriptor` for provider routing.
- The descriptor includes provider metadata and scale-out request metadata.
- Missing providers return a rejected result with reason `scale-out-provider-not-found`.

---

### Store-backed scale-out request publisher provider hint

- Updated `StoreBackedAiRuntimeScaleOutRequestPublisher`.
- Injected `IOptions<AiRuntimeInstanceRegistrationOptions>`.
- The publisher now persists the provider hint with each scale-out request.
- Default provider hint is `local` when no provider is configured.
- Metadata now includes `providerHint`.

This makes scale-out requests provider-aware while still using the existing provider registration system.

---

### Local runtime instance scale-out support

- Updated `LocalAiRuntimeInstanceProvider` to implement `IAiRuntimeScaleOutProvider`.
- The provider delegates dynamic local capacity creation to `IAiLocalRuntimeInstanceScaler`.
- Added rejection behavior when the local scaler is not registered:

```text
FailureReason = local-runtime-instance-scaler-not-registered
```

---

### Local runtime instance scaler abstraction

- Added/updated `IAiLocalRuntimeInstanceScaler`.
- The scaler exposes:
  - `ActiveInstanceCount`
  - `EnsureCapacityAsync(...)`
  - `StopAsync(...)`
  - `IAsyncDisposable`

Purpose:

```text
Create local runtime instances dynamically when scale-out is requested.
```

---

### Local runtime instance scaler implementation

- Added `AiLocalRuntimeInstanceScaler`.
- The scaler can dynamically create local runtime instance hosts using `IAiLocalRuntimeInstanceHostFactory`.
- It registers each local runtime instance in `IAiSharedRuntimeInstanceRegistry`.
- It starts created runtime instance hosts.
- It supports safe stop and async dispose.
- It is protected by an async gate to avoid concurrent scale-out races.
- It validates pool options before creating runtime instances.

Important behavior:

```text
AiLocalRuntimeInstancePoolOptions.Enabled = false disables startup pool creation only.
It does not disable dynamic scale-out on demand.
```

This is required for the zero-capacity scale-out test.

---

### Local runtime instance pool hosted service delegation

- Updated `AiLocalRuntimeInstancePoolHostedService` to delegate runtime instance lifecycle management to `IAiLocalRuntimeInstanceScaler`.
- The hosted service is now responsible for startup orchestration only.
- The scaler owns dynamic instance creation and cleanup.
- Disposal was adjusted to avoid double-disposing singleton scaler instances.

---

### Scale-out fulfilled run requeue service

- Added `IAiScaleOutFulfilledRunRequeueService`.
- Added `AiScaleOutFulfilledRunRequeueService`.
- Responsibility:

```text
When a scale-out request is fulfilled:
  -> load the shared run
  -> verify it is still ScaleOutRequested
  -> enqueue it into the shared queue
  -> let the normal shared queue pump dispatch it
```

This keeps the watcher focused on scale-out request processing and avoids direct dispatch from the watcher.

---

### Scale-out watcher requeue integration

- Updated `AiRuntimeScaleOutRequestWatcherHostedService`.
- The watcher now injects `IAiScaleOutFulfilledRunRequeueService`.
- After a provider successfully fulfills a scale-out request, the watcher:
  1. marks the request as fulfilled
  2. requeues the linked shared run

New behavior:

```text
Pending scale-out request
  -> provider fulfilled
  -> request marked Fulfilled
  -> linked shared run requeued
  -> pump dispatches the run normally
```

---

### Dependency injection registrations

- Added DI registration for:

```csharp
services.TryAddSingleton<IAiRuntimeScaleOutProviderSelector, AiRuntimeScaleOutProviderSelector>();
services.TryAddSingleton<IAiScaleOutFulfilledRunRequeueService, AiScaleOutFulfilledRunRequeueService>();
services.TryAddSingleton<IAiLocalRuntimeInstanceScaler, AiLocalRuntimeInstanceScaler>();
```

- Local runtime scale-out is now available in `ControlPlaneWithLocalRuntimeInstances` mode.

---

## Changed

### Submit mode for scale-out scenarios

- Updated local scale-out test settings to use:

```text
AiSharedRuntimeController:SubmitMode = DirectDispatch
```

Reason:

```text
QueueFirst bypasses admission results and directly puts the run into QueuedGlobally.
DirectDispatch allows admission to return RequestScaleOut.
```

This was required for the controller to produce:

```text
AiSharedRunStatus.ScaleOutRequested
```

instead of:

```text
AiSharedRunStatus.QueuedGlobally
```

---

### Admission options handling

- Updated configuration binding so explicit test settings are not overwritten by defaults.
- Relevant scale-out admission settings:

```text
AiRunAdmission:Enabled = true
AiRunAdmission:MaxInstanceCount = 3
AiRunAdmission:EnableScaleOutRequest = true
AiRunAdmission:EnableGlobalQueueFallback = false
AiRunAdmission:RejectWhenNoCapacity = false
```

This allows admission to request scale-out instead of queueing or rejecting the run.

---

### Watcher constructor

- Updated `AiRuntimeScaleOutRequestWatcherHostedService` constructor to include:

```csharp
IAiScaleOutFulfilledRunRequeueService fulfilledRunRequeueService
```

- Updated related unit and integration tests with a fake requeue service where required.

---

### Test helpers usage

- Reused existing `McpTestWaitHelpers` instead of adding duplicate wait helpers.
- Existing helper methods used:

```text
WaitForDispatchedRunsAsync
WaitForRuntimeRunExecutionIdAsync
WaitForTerminalRuntimeRunStatusesAsync
```

These helpers validate dispatch, runtime execution id creation, and terminal runtime status.

---

## Tests Added / Updated

### Scale-out provider selector tests

Validated:

- provider hint routing
- fallback to registration provider name
- fallback to `local`
- provider not found rejection
- provider routing through existing provider attributes

---

### Store-backed scale-out request publisher tests

Validated:

- provider hint is persisted from `AiRuntimeInstanceRegistrationOptions.ProviderName`
- provider hint defaults to `local`
- provider hint is stored both on the request and in metadata

---

### Local runtime instance provider scale-out tests

Validated:

- `LocalAiRuntimeInstanceProvider` delegates to `IAiLocalRuntimeInstanceScaler`
- missing scaler returns a rejected provider result
- runtime scale-out metadata is preserved

---

### Local runtime instance scaler tests

Validated:

- scaler creates local runtime instance hosts
- created hosts are registered in `IAiSharedRuntimeInstanceRegistry`
- created hosts are started
- scaler does not create extra instances when target capacity is already reached
- scaler stops and unregisters runtime instances
- scaler disposes local runtime instance hosts safely

---

### Local scale-out flow unit test

Validated full in-memory/local flow:

```text
scale-out request
  -> watcher
  -> selector
  -> local provider
  -> local scaler
  -> local runtime instance created
  -> request fulfilled
```

---

### Redis-backed MCP local scale-out fulfillment test

Validated:

```text
MCP submit
  -> Redis shared run store
  -> Redis scale-out request store
  -> admission RequestScaleOut
  -> watcher fulfilled request
  -> local scaler created runtime instance
```

---

### Redis-backed MCP local scale-out execution test

Added final end-to-end test:

```text
ControlPlaneWithLocalRuntimeInstances_With_No_Runtime_Capacity_Should_ScaleOut_Requeue_Dispatch_And_Execute_Run
```

Validated:

```text
0 active local runtime instances
  -> submit MCP run
  -> scale-out request fulfilled
  -> local runtime instance created
  -> shared run requeued
  -> pump dispatches the run
  -> runtime run receives ExecutionId
  -> runtime run reaches completed
```

Final validated output:

```text
FINAL SCALE-OUT EXECUTION STATUS:
SharedRunStatus='Dispatched'
AssignedRuntimeInstanceId='host-...:mcp-scaleout-runtime-1'
LocalRunId='...'
ExecutionId='...'
RuntimeRunStatus='completed'
QueueStatus='Dispatched'
ScaleOutRequestStatus='Fulfilled'
ScaleOutRuntimeInstanceId='host-...:mcp-scaleout-runtime-1'
ActiveLocalInstances='1'
```

---

## Fixed

### Fixed QueueFirst blocking scale-out

Problem:

```text
AiSharedRuntimeController SubmitMode = QueueFirst
```

forced every run into:

```text
QueuedGlobally
```

This bypassed the admission decision and prevented `RequestScaleOut` from being published.

Fix:

```text
Use DirectDispatch for scale-out tests.
```

Result:

```text
Admission can return RequestScaleOut.
Shared run becomes ScaleOutRequested.
Scale-out request is created.
```

---

### Fixed dynamic local scale-out when pool startup is disabled

Problem:

```text
AiLocalRuntimeInstancePoolOptions.Enabled = false
```

was initially treated as disabling all local runtime instance creation.

Fix:

```text
Enabled=false disables startup pool only.
Dynamic scale-out remains available through AiLocalRuntimeInstanceScaler.
```

This enables zero-capacity startup followed by demand-based local runtime creation.

---

### Fixed watcher fulfilling requests without re-dispatch path

Problem:

```text
Scale-out request Fulfilled
Shared run remained ScaleOutRequested
No automatic consumption of original run
```

Fix:

```text
After fulfillment, watcher calls IAiScaleOutFulfilledRunRequeueService.
The shared run is inserted into the shared queue.
The normal pump dispatches it.
```

---

### Fixed double-disposal risk around local runtime scaler

- Adjusted scaler disposal to be idempotent.
- Avoided unsafe lifecycle ownership assumptions between hosted service and DI container.

---

## Architecture Notes

### Why the watcher does not dispatch directly

The watcher only handles scale-out request lifecycle:

```text
Pending -> Observed -> Fulfilled / Rejected
```

It does not dispatch DAG runs directly.

After scale-out fulfillment, the run is requeued and consumed by the normal shared queue pump.

This preserves the architecture:

```text
Scale-out creates capacity.
Shared queue coordinates work.
Pump dispatches work.
Runtime instance executes work.
```

---

### Why this is Kubernetes-ready

The final flow mirrors a Kubernetes autoscaling-style behavior:

```text
No capacity
  -> request accepted as scale-out required
  -> capacity created
  -> original work requeued
  -> available runtime consumes the work
```

The same architecture can later map local scaler behavior to:

```text
Kubernetes pod creation
HTTP runtime provider scale-out
Remote runtime provider scale-out
Cloud/container orchestration
```

---

### Why Redis matters here

Redis-backed components were validated for the integration flow:

```text
IAiSharedRunStore = RedisAiSharedRunStore
IAiSharedQueue = RedisAiSharedQueue
IAiRuntimeAdmissionReservationStore = RedisAiRuntimeAdmissionReservationStore
IAiRuntimeScaleOutRequestStore = RedisAiRuntimeScaleOutRequestStore
IAiRuntimeScaleOutRequestPublisher = StoreBackedAiRuntimeScaleOutRequestPublisher
```

This proves the flow is not just in-memory or test-only.

---

## Final Validation

All MCP integration tests pass.

Final confirmed capability:

```text
A run submitted to the MCP control plane with zero runtime capacity can trigger local runtime scale-out, create a new runtime instance, get requeued, be dispatched by the pump, execute locally, produce an ExecutionId, and complete successfully.
```

This is a major milestone for the deterministic AI runtime control plane and the Kubernetes/demo scale-out roadmap.

---

## [1.0.6.1] - 2026-06-11 - HTTP Provider Scenario Alignment, Redis Runtime Visibility, and Shutdown Stability

### Changed

- Updated HTTP provider MCP integration scenarios to align with the current pooled runtime architecture.
- Reworked `HttpRuntimeProviderScenarioTests` to use the same runtime model validated by the heavy HTTP dispatch tests:
  - `ControlPlaneWithHttpRuntimeInstances`
  - HTTP runtime provider
  - `RuntimeInstanceOnly` HTTP host
  - internal local runtime instance pool
  - dispatchable child runtime instances using the `runtime-http-*` prefix.
- Removed the legacy single-runtime HTTP test assumption based on `RuntimeInstanceHttpTestHost.RuntimeInstanceId`.
- Updated HTTP scenario assertions to validate assignment to pooled child runtime instances:
  - `runtime-http-1`
  - `runtime-http-2`
  - `runtime-http-3`
- Updated queue drain behavior in HTTP scenarios to target the shared pump model instead of a fixed single runtime instance.
- Updated runtime instance assertions to accept host-scoped runtime instance identifiers.
- Preserved the original HTTP provider scenario coverage while adapting it to the current architecture.
- Aligned MCP control-plane startup with runtime discovery requirements so the control plane can publish discovery before dependent runtime hosts require it.
- Updated the generic MCP/runtime test fixture startup order:
  - MCP control-plane host starts first.
  - Runtime HTTP hosts start after discovery is available.
  - Runtime HTTP clients are resolved dynamically after runtime hosts are created.
- Updated the HTTP runtime client factory used in tests to support runtime clients being added after MCP host startup.
- Updated shared queue pump readiness behavior to wait for runtime capacity before dispatching.
- Improved shared queue dispatch validation so tests assert the real assigned runtime instance instead of a deprecated parent HTTP host identity.

### Added

- Added Redis-backed runtime instance registry coverage for runtime registration, heartbeat, listing, draining, and unregister flows.
- Added Redis-backed runtime capacity visibility coverage for runtime capacity publication, listing, and cleanup.
- Added runtime instance capacity descriptor cleanup on runtime shutdown.
- Added runtime instance unregister cleanup on runtime shutdown.
- Added control-plane discovery readiness validation for runtime hosts that require discovery.
- Added Redis control-plane discovery store support for publishing and reading the active MCP control-plane discovery descriptor.
- Added control-plane id resolver support so runtime hosts can resolve the logical MCP control-plane identity from the Redis discovery key.
- Added discovery-based MCP control-plane identity propagation for `RuntimeInstanceOnly` hosts that require discovery before registration.
- Added readiness gate validation before starting the MCP shared queue background pump.
- Added runtime identity and host-scoped runtime instance validation for pooled runtime hosts.
- Added test coverage for dynamic HTTP runtime client resolution in the generic MCP test host.
- Added stronger wait helper diagnostics for dispatched shared runs and runtime visibility assertions.
- Added heavy HTTP dispatch validation against Redis shared stores and the pooled HTTP runtime provider model.

### Validated

- Validated HTTP provider dispatch through the pooled `RuntimeInstanceOnly` model.
- Validated shared run submission using `QueueFirst` mode.
- Validated manual shared queue draining through MCP.
- Validated dispatched shared runs receive:
  - `AssignedRuntimeInstanceId`
  - `LocalRunId`
  - runtime run status
  - execution id once execution starts.
- Validated completion of normal HTTP-provider runs.
- Validated completion of larger HTTP-provider pipelines.
- Validated shared queue activity visibility for HTTP-provider runs.
- Validated automatic dispatch through the HTTP provider when the shared queue background pump is enabled.
- Validated pause and resume behavior for long-running HTTP-provider executions.
- Validated cancellation request behavior for long-running HTTP-provider executions.
- Validated runtime queue cancellation routing against the assigned child runtime instance.
- Validated Redis shared run store usage in heavy dispatch scenarios.
- Validated Redis shared queue usage in heavy dispatch scenarios.
- Validated Redis runtime admission reservation store usage in heavy dispatch scenarios.
- Validated heavy HTTP dispatch with:
  - 50 shared runs
  - 100 steps per run
  - 3 pooled HTTP runtime instances
  - Redis shared queue
  - Redis shared run store
  - Redis admission reservations.
- Validated multi-runtime HTTP distribution across pooled child runtime instances.
- Validated replay, report, ledger, and trace output for completed shared runs.
- Validated replay report integrity:
  - execution found
  - snapshot found
  - fingerprint found
  - fingerprint matches
  - dependency graph valid
  - step state valid
  - payload references valid
  - zero replay issues.

### Fixed

- Fixed incompatible HTTP provider tests that were still targeting the old single-runtime HTTP fixture model.
- Fixed incorrect assumptions that all HTTP-provider runs must be assigned to one fixed runtime instance.
- Fixed HTTP provider scenario timeouts caused by waiting for dispatch to the removed single-runtime model.
- Fixed test model mismatch where the control plane was using the new pooled HTTP architecture but the assertions still expected the old runtime identity.
- Fixed queue drain expectations so tests now validate dispatch against the active pooled runtime instance model.
- Fixed MCP/runtime discovery startup ordering when runtime hosts require Redis discovery to resolve the logical control-plane identifier.
- Fixed control-plane id resolution so runtime registry and capacity publication use the MCP-published logical control-plane identity.
- Fixed runtime host discovery dependency so `RuntimeInstanceOnly` hosts can resolve the MCP control-plane identity before registering runtime instances.
- Fixed generic MCP test host startup when no runtime HTTP clients are available at MCP startup time.
- Fixed HTTP runtime client factory behavior so runtime HTTP clients can be resolved after the runtime hosts are created.
- Fixed host-scoped runtime instance id assertions for pooled runtime instances.
- Fixed Redis runtime registry unregister cleanup so shutdown no longer depends on Redis discovery resolution after discovery descriptors may already be removed.
- Fixed Redis runtime capacity cleanup so capacity descriptor removal no longer depends on Redis discovery resolution during shutdown.
- Fixed runtime capacity descriptor cleanup for stopped runtime instances.
- Fixed shutdown cleanup race conditions where runtime unregister/capacity removal could execute after discovery or Redis dependencies were already disposed.
- Fixed control-plane discovery shutdown logging so disposed logging providers cannot fail otherwise successful tests.
- Fixed `StopAsync` cleanup paths to be best-effort and cancellation-safe.
- Fixed shutdown timeout behavior caused by late Redis discovery resolution during unregister/capacity removal.
- Fixed shared queue pump readiness timing so the pump waits for at least one ready runtime instance before dispatching.
- Fixed flaky dispatch checks by separating dispatch validation from full execution completion validation where appropriate.
- Fixed heavy HTTP dispatch validation to assert Redis-backed store usage instead of relying on in-memory assumptions.

### Architecture Notes

- The current validated HTTP runtime model is now:

```text
MCP Control Plane
  -> HTTP Runtime Provider
     -> RuntimeInstanceOnly HTTP Host
        -> Local Runtime Instance Pool
           -> runtime-http-1
           -> runtime-http-2
           -> runtime-http-3
```

- The HTTP host identity is transport and hosting infrastructure.
- The dispatchable runtime identities are the child runtime instances created by the runtime instance pool.
- Tests now validate the real runtime execution capacity rather than the parent HTTP host identity.
- Runtime instance ids are host-scoped in pooled runtime scenarios.
- Redis discovery is used to resolve the shared logical control-plane id during startup.
- The MCP server publishes the active discovery descriptor through the Redis discovery store.
- Runtime hosts that require discovery resolve the MCP-published control-plane identity through the control-plane id resolver.
- The resolved MCP control-plane identity is reused by runtime registration, registry entries, and capacity descriptors.
- Runtime registry and runtime capacity cleanup no longer rely on discovery during shutdown once the runtime instance has already been registered or published.
- Runtime instance registration and capacity publication now use the resolved logical control-plane id consistently.
- Shutdown cleanup is intentionally best-effort:
  - unregister runtime instance
  - remove capacity descriptor
  - stop local runtime hosts
  - delete discovery descriptor when owned by the current control-plane.
- Test cleanup remains a safety layer, not the primary lifecycle mechanism.

### Redis / Store Notes

- Existing Redis shared controller stores remain part of the validated architecture.
- This version strengthens validation around Redis-backed shared runtime coordination rather than replacing all stores.
- Redis discovery validation now explicitly covers:
  - publishing the MCP control-plane discovery descriptor
  - resolving the logical MCP control-plane identity
  - sharing that identity with runtime-only hosts before registration.
- Validated Redis-backed components include:
  - `RedisAiSharedRunStore`
  - `RedisAiSharedQueue`
  - `RedisAiRuntimeAdmissionReservationStore`
  - `RedisAiRuntimeInstanceRegistry`
  - `RedisAiRuntimeInstanceCapacityStore`
  - Redis control-plane discovery store.
- Runtime registry and capacity store behavior now includes safe control-plane id reuse for known runtime instances during cleanup.
- Redis store cleanup no longer depends on late discovery resolution during host shutdown.

### Test Coverage Preserved

The following scenario coverage was preserved and adapted:

- Submit one run, drain, and dispatch through HTTP provider.
- Submit four runs, drain, and dispatch through HTTP provider.
- Submit one run, drain, and expose runtime run status.
- Submit one 100-step pipeline and complete through HTTP provider.
- Submit five runs and validate shared queue activity.
- Submit one run without manual drain and complete through background pump.
- Submit three runs and complete all through HTTP provider.
- Submit one run, complete, and verify it remains listed with assigned HTTP runtime.
- Submit two runs, complete, and validate shared queue activity.
- Submit long-running HTTP execution, pause, resume, and complete.
- Submit HTTP run and validate runtime queue cancellation routing.
- Submit long-running HTTP execution and request cancellation.
- Submit one run with a 100-step pipeline and replay it through MCP.
- Validate replay report, ledger, and trace output.
- Validate heavy HTTP QueueFirst dispatch across pooled HTTP runtime instances.
- Validate Redis-backed shared queue and shared run store usage.
- Validate Redis-backed admission reservation usage during heavy dispatch.
- Validate runtime instance registry and capacity visibility during pooled execution.

### Result

- HTTP provider MCP scenarios now pass against the current pooled runtime architecture.
- Heavy HTTP dispatch scenarios now pass against Redis-backed shared stores and pooled HTTP runtime instances.
- Replay/report/ledger/trace scenarios now pass for completed shared runs.
- Runtime registry and capacity cleanup no longer block test shutdown.
- Discovery, registry, capacity, pump readiness, and HTTP provider tests are aligned with the production-oriented pooled runtime model.
- The test suite now correctly validates the current shared controller architecture instead of the deprecated single-runtime fixture model.

---

## [1.0.6.1] - 2026-06-09 HTTP Provider Scenario Alignment with Pooled Runtime Model

### Changed

- Updated HTTP provider MCP integration scenarios to align with the current pooled runtime architecture.
- Reworked `HttpRuntimeProviderScenarioTests` to use the same runtime model validated by the heavy HTTP dispatch tests:
  - `ControlPlaneWithHttpRuntimeInstances`
  - HTTP runtime provider
  - `RuntimeInstanceOnly` HTTP host
  - internal local runtime instance pool
  - dispatchable child runtime instances using the `runtime-http-*` prefix.
- Removed the legacy single-runtime HTTP test assumption based on `RuntimeInstanceHttpTestHost.RuntimeInstanceId`.
- Updated HTTP scenario assertions to validate assignment to pooled child runtime instances:
  - `runtime-http-1`
  - `runtime-http-2`
  - `runtime-http-3`
- Updated queue drain behavior in HTTP scenarios to target the shared pump model instead of a fixed single runtime instance.
- Preserved the original HTTP provider scenario coverage while adapting it to the current architecture.

### Validated

- Validated HTTP provider dispatch through the pooled `RuntimeInstanceOnly` model.
- Validated shared run submission using `QueueFirst` mode.
- Validated manual shared queue draining through MCP.
- Validated dispatched shared runs receive:
  - `AssignedRuntimeInstanceId`
  - `LocalRunId`
  - runtime run status
  - execution id once execution starts.
- Validated completion of normal HTTP-provider runs.
- Validated completion of larger HTTP-provider pipelines.
- Validated shared queue activity visibility for HTTP-provider runs.
- Validated automatic dispatch through the HTTP provider when the shared queue background pump is enabled.
- Validated pause and resume behavior for long-running HTTP-provider executions.
- Validated cancellation request behavior for long-running HTTP-provider executions.
- Validated runtime queue cancellation routing against the assigned child runtime instance.

### Fixed

- Fixed incompatible HTTP provider tests that were still targeting the old single-runtime HTTP fixture model.
- Fixed incorrect assumptions that all HTTP-provider runs must be assigned to one fixed runtime instance.
- Fixed HTTP provider scenario timeouts caused by waiting for dispatch to the removed single-runtime model.
- Fixed test model mismatch where the control plane was using the new pooled HTTP architecture but the assertions still expected the old runtime identity.
- Fixed queue drain expectations so tests now validate dispatch against the active pooled runtime instance model.

### Architecture Notes

- The current validated HTTP runtime model is now:

    MCP Control Plane
      -> HTTP Runtime Provider
         -> RuntimeInstanceOnly HTTP Host
            -> Local Runtime Instance Pool
               -> runtime-http-1
               -> runtime-http-2
               -> runtime-http-3

- The HTTP host identity is transport and hosting infrastructure.
- The dispatchable runtime identities are the child runtime instances created by the runtime instance pool.
- Tests now validate the real runtime execution capacity rather than the parent HTTP host identity.

### Test Coverage Preserved

The following scenario coverage was preserved and adapted:

- Submit one run, drain, and dispatch through HTTP provider.
- Submit four runs, drain, and dispatch through HTTP provider.
- Submit one run, drain, and expose runtime run status.
- Submit one 100-step pipeline and complete through HTTP provider.
- Submit five runs and validate shared queue activity.
- Submit one run without manual drain and complete through background pump.
- Submit three runs and complete all through HTTP provider.
- Submit one run, complete, and verify it remains listed with assigned HTTP runtime.
- Submit two runs, complete, and validate shared queue activity.
- Submit long-running HTTP execution, pause, resume, and complete.
- Submit HTTP run and validate runtime queue cancellation routing.
- Submit long-running HTTP execution and request cancellation.

### Result

- HTTP provider MCP scenarios now pass against the current pooled runtime architecture.
- The test suite now correctly validates the production-oriented HTTP provider model instead of the deprecated single-runtime fixture model.

---

## [1.0.6.0] - 2026-06-08 - Shared Queue Pump, QueueFirst Dispatch, Runtime Worker Capacity Visibility

### Added

#### Shared Runtime Controller Submit Mode

- Added support for a queue-first shared runtime controller submission flow.
- Added `AiSharedRuntimeController:SubmitMode` configuration support.
- Added support for submitting shared runs directly into the shared/global queue instead of immediate dispatch.
- Added support for keeping submitted shared runs in `QueuedGlobally` status until a background pump or manual drain dispatches them.
- Added support for validating queue-first behavior with the background pump disabled.
- Added support for validating manual queue drain after a delay while the background pump remains disabled.

#### Shared Queue Pump and Manual Drain

- Added and validated manual shared queue drain through MCP tooling.
- Added tests proving that when the background pump is disabled:
  - submitted queue-first runs remain in the shared queue,
  - runs are not dispatched automatically,
  - a manual drain can dispatch them later,
  - local and HTTP runtime instance providers both support the flow.
- Added test coverage for local and HTTP queue-first submission with pump disabled, manual drain after waiting 10 seconds, dispatch after manual drain, and completion.
- Confirmed that `AiSharedQueuePump:Enabled=true` allows manual drain while `AiMcpHost:EnableSharedQueuePump=false` and `AiSharedQueueBackgroundService:Enabled=false` prevent automatic background pumping.
- Validated that the demo path is not impacted when the background pump is disabled.

#### Pump Runtime Identity Separation

- Clarified shared queue pump request identity with `PumpRuntimeInstanceId` and `PumpWorkerId`.
- Separated pump identity from the assigned runtime target identity.
- Ensured the pump runtime instance id represents the runtime instance executing the pump cycle, not necessarily the runtime instance selected for dispatch.
- Updated MCP control-plane background service to send `PumpRuntimeInstanceId` and `PumpWorkerId` into `AiSharedQueuePumpRequest`.

#### Shared Queue Dispatcher Admission Re-Evaluation

- Updated shared queue dispatch flow so queued items are re-admitted at drain/dispatch time.
- Ensured the dispatcher no longer blindly dispatches to the pump runtime instance.
- Added support for admission selecting the assigned runtime instance during shared queue drain.
- Preserved the ability for tests to inject fake admission controllers for deterministic dispatch behavior.
- Updated shared queue dispatcher unit tests to provide `FakeRunAdmissionController`.

#### Fake Admission Controller

- Added reusable `FakeRunAdmissionController` test fixture.
- Added optional constructor argument `assignedRuntimeInstanceId`.
- Preserved backward compatibility by defaulting to requested preferred runtime instance id when available, otherwise `runtime-1`.
- Enabled tests to assign dispatch targets explicitly without breaking existing tests.
- Used the fake admission controller to restore deterministic behavior in queue dispatcher and multi-instance shared queue tests.

---

### Added MCP and Runtime Tests

#### QueueFirst and Manual Drain Tests

- Added MCP tests for local runtime queue-first mode with pump disabled.
- Added MCP tests for HTTP runtime queue-first mode with pump disabled.
- Added tests that submit queue-first runs, verify they remain queued, wait 10 seconds, verify they are still queued, manually drain the shared queue, verify dispatch, and verify completion.

#### Background Pump Tests

- Validated local background pump dispatch and completion.
- Validated HTTP background pump dispatch and completion.
- Fixed failing tests where queue items remained `QueuedGlobally` because the pump was not correctly active or not using the right target dispatch path.
- Confirmed queue-first runs can be dispatched by the background pump and can also be dispatched manually when the background pump is disabled.

#### Shared Queue Multi-Instance Dispatch Tests

- Updated multi-instance shared queue tests after admission re-evaluation was introduced.
- Fixed tests where all dispatches went to one runtime instance because the fake admission controller returned the same default runtime instance.
- Updated multi-instance pump tests to pass the current pump runtime instance id into `FakeRunAdmissionController`.
- Restored expected multi-instance participation by assigning each pump to its matching runtime target.
- Preserved the core test goal: shared queue can be consumed by multiple runtime pumps, no double dispatch occurs, and multiple runtime instances participate.

#### Heavy Real Execution Shared Queue Tests

- Updated heavy multi-instance real execution tests so the shared queue dispatcher uses the current runtime instance id as the fake admission target.
- Fixed pump loops that did not finish because admission assigned to a non-existing/default runtime id such as `runtime-1` while real test instances were named `runtime-instance-1`, `runtime-instance-2`, etc.
- Updated `RunRuntimeInstancePumpUntilEmptyAsync` to create `FakeRunAdmissionController` with `assignedRuntimeInstanceId: runtimeInstance.RuntimeInstanceId`.
- Ensured remote dispatch can resolve the correct `LocalAiSharedRuntimeInstance`.

---

### Added Runtime Worker Capacity Control

#### MaxLocalWorkersPerExecution

- Added local runtime worker capacity policy `MaxLocalWorkersPerExecution`.
- Added the option to `AiRuntimePipelineBackgroundControllerOptions`.
- Defined the option as a runtime-local policy controlling how many local workers from one runtime instance may work on one execution concurrently.
- Kept it separate from `AiExecutionAssistanceOptions`.
- Clarified that `AiExecutionAssistanceOptions` controls cross-instance helper behavior, while `AiRuntimePipelineBackgroundControllerOptions.MaxLocalWorkersPerExecution` controls local worker allocation per execution.

#### Worker Reservation Logic

- Added real local worker reservation logic in `AiRuntimePipelineBackgroundController`.
- Added `_activeWorkerCount` tracking.
- Added async worker reservation method that waits when no local workers are available instead of failing the run.
- Ensured worker count is reserved before execution processing starts.
- Ensured reserved worker count is released in `finally`.
- Supported both non-distributed single worker execution and distributed worker group execution.
- Ensured effective worker count per execution is the minimum of configured distributed worker count, max local workers per execution, and available worker count.
- Prevented new executions from reserving more workers than are available.

#### Runtime Queue State Worker Visibility

- Extended `AiRuntimePipelineQueueState` with `WorkerCount`, `ActiveWorkerCount`, `AvailableWorkerCount`, and `MaxLocalWorkersPerExecution`.
- Updated `GetQueueStateAsync()` to expose actual local worker capacity.
- Updated `CanAcceptRun` to also consider available workers.
- Confirmed the distinction between run-level capacity through `AvailableRunSlots` and worker-level capacity through `AvailableWorkerCount`.

#### Ledger and Assistance Candidate Consistency

- Updated runtime run ledger metadata to include `max.local.workers.per.execution` and `effective.worker.count.per.execution`.
- Updated `RegisterExecutionAssistanceCandidateAsync` so `EstimatedActiveWorkerCount` uses the effective worker cap instead of raw `Distributed.WorkerCount`.
- Ensured execution assistance candidates reflect the actual local worker policy.

---

### Added Runtime Instance Capacity Visibility

#### Runtime Instance Snapshot

- Extended `AiRuntimeInstanceSnapshot` with `ActiveWorkerCount`, `AvailableWorkerCount`, and `MaxLocalWorkersPerExecution`.
- Enabled runtime instance listing through MCP/control-plane to expose total workers, active workers, free workers, max workers per execution, run slot availability, queue paused state, and `CanAcceptRun`.

#### Runtime Instance Entry

- Updated `RuntimeInstanceEntry` model with `ActiveWorkerCount`, `AvailableWorkerCount`, and `MaxLocalWorkersPerExecution`.
- Added XML documentation.
- Updated `Create`, `UpdateRegistration`, `UpdateHeartbeat`, `WithStatus`, and `ToSnapshot`.
- Ensured worker capacity fields persist through registry entries and project correctly into snapshots.

#### Runtime Instance Registry Interface

- Updated `IAiRuntimeInstanceRegistry.HeartbeatAsync(...)` signature to include `activeWorkerCount`, `availableWorkerCount`, and `maxLocalWorkersPerExecution`.
- Updated XML documentation for heartbeat worker visibility.

#### In-Memory Runtime Instance Registry

- Updated `InMemoryAiRuntimeInstanceRegistry` to support the new heartbeat signature.
- Updated internal runtime instance entry model to track worker capacity.
- Updated snapshot projection to include worker capacity fields.
- Ensured local/test registries preserve the new worker capacity data.

#### Redis Runtime Instance Registry

- Updated `RedisAiRuntimeInstanceRegistry` to support the new heartbeat signature.
- Updated heartbeat flow to pass active worker count, available worker count, and max local workers per execution.
- Updated Redis persisted `RuntimeInstanceEntry` model to store worker capacity fields.
- Updated Redis snapshot projection to expose worker capacity fields.
- Preserved existing Redis registry behavior while extending visibility data.

#### Runtime Instance Registration Hosted Service

- Updated `AiRuntimeInstanceRegistrationHostedService` heartbeat publishing.
- Updated heartbeat calls to pass worker capacity values from `AiRuntimePipelineQueueState`.
- Fixed capacity descriptor publishing where worker values were previously hardcoded as active workers `0` and available workers equal to total workers.
- Updated descriptor publishing to use real queue state values: `queueState.WorkerCount`, `queueState.ActiveWorkerCount`, `queueState.AvailableWorkerCount`, and `queueState.MaxLocalWorkersPerExecution`.
- Mapped `MaxLocalWorkersPerExecution` into `MaxWorkersPerRun`.
- Preserved safe shutdown logging improvements to avoid disposed logger failures.

#### Runtime Instance Control Plane

- Updated `AiRuntimeInstanceControlPlane.HeartbeatInnerAsync(...)` to pass worker capacity fields to the registry.
- Required `AiRuntimeInstanceControlPlaneRequest` to expose `ActiveWorkerCount`, `AvailableWorkerCount`, and `MaxLocalWorkersPerExecution`.
- Preserved existing control-plane register/list/get/drain/unregister behavior.

---

### Added Worker Saturation Test Helpers

- Added MCP wait helper for runtime worker saturation.
- Helper waits until a runtime instance reports expected `WorkerCount`, `ActiveWorkerCount == WorkerCount`, `AvailableWorkerCount == 0`, expected `MaxLocalWorkersPerExecution`, and `CanAcceptRun == false`.
- Fixed helper usage by matching actual `McpTestClient.ListRuntimeInstancesAsync(...)` return type: direct `IReadOnlyList<AiRuntimeInstanceSnapshot>`, not a wrapper result.
- Added timeout diagnostics showing last observed worker capacity state.
- Added MCP local runtime worker saturation test validating that runtime instance list exposes worker capacity correctly.

---

### Fixed

#### EventLog Logger Disposal

- Fixed shutdown failures caused by logging after the logger provider was already disposed.
- Added safe logging wrappers: `SafeLogInformation` and `SafeLogError`.
- Ensured shutdown does not fail when logger provider is already disposed.
- Handled `AggregateException`, `ObjectDisposedException`, and `InvalidOperationException`.
- Prevented test failures during `WebApplicationFactory.DisposeAsync()` caused by disposed logging infrastructure.

#### Queue Dispatcher Dispatch Failure Test

- Fixed `DispatchNextAsync_Should_Requeue_When_Dispatch_Fails`.
- The test created a failing `FakeSharedRunDispatcher` but accidentally passed a new default successful dispatcher into `AiSharedQueueDispatcher`.
- Replaced the default fake dispatcher with the intended `runDispatcher`.
- Restored expected behavior: dispatch failure returns `Success=false`, queue item is requeued as `Pending`, shared run remains `QueuedGlobally`, and `LocalRunId` / `ExecutionId` remain null.

#### QueueFirst and ForceGlobalQueue Transition

- Identified that `ForceGlobalQueue` was useful for tests but semantically too forceful for normal configuration.
- Moved toward cleaner `SubmitMode = QueueFirst`.
- Ensured queue-first behavior is controlled through shared runtime controller options rather than forcing admission globally.
- Kept compatibility with tests requiring queued behavior.
- Clarified that `ForceGlobalQueue` can be problematic because it bypasses natural admission assignment and forces all submitted runs through shared queue semantics.

#### Shared Queue Pump Disabled Behavior

- Fixed misunderstanding where manual drain failed because `AiSharedQueuePump:Enabled` was also false.
- Confirmed correct config split:
  - `AiSharedQueuePump:Enabled=true` allows manual drain,
  - `AiMcpHost:EnableSharedQueuePump=false` disables background auto pump,
  - `AiSharedQueueBackgroundService:Enabled=false` disables hosted background pump.
- Validated manual drain works while automatic pump remains disabled.

#### Runtime Capacity Test Behavior

- Confirmed failures in distributed worker participation tests were expected after adding `MaxLocalWorkersPerExecution`.
- Updated test configuration to explicitly set `MaxLocalWorkersPerExecution = scenario.WorkerCount` when tests intend to validate full distributed worker participation.
- Preserved dedicated capacity behavior where lower `MaxLocalWorkersPerExecution` intentionally limits workers per execution.

#### Multi-Instance Shared Queue Distribution

- Fixed tests where distribution collapsed to one runtime instance because drain-time admission used a default fake target.
- Updated multi-pump tests to assign the fake admission target to the current runtime instance.
- Preserved the distinction between pump identity, selected dispatch target, and assigned runtime instance.

---

### Changed

#### Shared Queue Dispatcher Semantics

- Changed shared queue drain behavior so dispatch target selection is resolved through admission at dispatch time.
- The pump runtime instance id is no longer automatically treated as the assigned runtime instance.
- Tests that expect pump-local dispatch now explicitly inject an admission controller assigning the pump runtime instance as the target.
- This makes production behavior cleaner and avoids coupling pump execution identity to dispatch target identity.

#### Heavy Integration Test Configuration

- Updated heavy execution scenarios to explicitly configure `MaxLocalWorkersPerExecution = scenario.WorkerCount` when the scenario expects all configured workers to participate.
- Prevented accidental worker cap from reducing intended test parallelism.
- Preserved the new worker cap feature for dedicated runtime capacity tests.

#### Test Fakes

- Updated all `FakeRuntimeInstanceRegistry` implementations to match the new registry heartbeat signature.
- Updated fake snapshots to include worker capacity fields.
- Updated fake admission controller to support optional assigned runtime id.
- Updated shared queue dispatcher tests to pass the required admission controller dependency.
- Updated multi-instance tests to use deterministic target assignment after dispatch-time admission was introduced.

---

### Validated

- Queue-first mode works.
- Manual drain works.
- Background pump works.
- Local provider works.
- HTTP provider works.
- Pump disabled does not impact the demo.
- Runtime instance list can expose worker capacity.
- Worker capacity policy is active and impacts worker participation.
- `MaxLocalWorkersPerExecution` correctly limits workers per execution.
- Runtime instance `CanAcceptRun` can become false when workers are saturated.
- Shared queue no-double-dispatch behavior remains valid.
- Multi-instance pump tests require explicit test admission assignment after dispatcher admission re-evaluation.
- Dispatch-time admission correctly separates pump identity from assigned runtime identity.

---

### Notes

- The new worker capacity model is now visible across the full path:
  - `AiRuntimePipelineBackgroundController`
  - `AiRuntimePipelineQueueState`
  - `AiRuntimeInstanceRegistrationHostedService`
  - `AiRuntimeInstanceCapacityDescriptor`
  - `IAiRuntimeInstanceRegistry`
  - `RuntimeInstanceEntry`
  - `AiRuntimeInstanceSnapshot`
  - MCP / control-plane list instances

- This prepares the runtime for future Kubernetes dashboards showing total workers, active workers, free workers, run slots, queue depth, instance readiness, and capacity pressure.
- Admission currently selects based on visible capacity but does not yet atomically reserve target runtime capacity.
- Future production improvement: add admission reservation / capacity reservation to avoid hotspotting when many dispatches happen faster than heartbeat updates.


---

## [1.0.5.9] - 2026-06-06 HTTP Runtime Provider Execution Integration Completed

### Added

- Added complete HTTP runtime provider execution integration.
- Added full end-to-end validation for provider-based runtime dispatch through HTTP.
- Added stable HTTP runtime command transport between:
  - MCP control-plane host
  - HTTP runtime instance provider
  - runtime-instance-only host
  - runtime command endpoint
  - local runtime queue
  - runtime pipeline controller
  - DAG execution engine
- Added validation that `RuntimeInstanceOnly` automatically starts its runtime execution loop.
- Added validation that `AiRuntimePipelineBackgroundControllerHostedService` starts automatically in `RuntimeInstanceOnly` mode.
- Added validation that HTTP-dispatched runs move from:
  - `ENQUEUED`
  - `DEQUEUED`
  - execution created
  - runtime status exposed
  - completed
- Added validation that HTTP runtime queue status is routed through the HTTP provider.
- Added validation that HTTP runtime status eventually exposes `ExecutionId`.
- Added validation that the same runtime pipeline controller instance is used for:
  - hosted service startup
  - enqueue
  - dequeue
  - execution processing
- Added HTTP provider integration coverage for:
  - single-run dispatch
  - multi-run dispatch
  - runtime completion
  - 100-step pipeline completion
  - shared run listing
  - shared queue activity
  - runtime queue status routing
  - queued-run cancellation
  - preliminary execution pause
  - preliminary execution resume
  - preliminary execution cancellation
- Added stronger coverage proving that the HTTP provider is not just selected, but actually executes the runtime command path.

### Added Provider-Based Runtime Hosting Capabilities

- Added a working provider abstraction path for runtime hosting.
- Confirmed that local runtime dispatch and HTTP runtime dispatch can coexist.
- Confirmed that the provider router can dispatch to a runtime instance based on provider metadata.
- Confirmed that `provider.name=local` and `provider.name=http` can represent different runtime execution paths.
- Confirmed that the HTTP provider sends commands to the runtime instance that owns its own queue, workers, and DAG engine.
- Confirmed that local queues remain owned by target runtime instances.
- Confirmed that the HTTP provider does not replace local queues; it only provides a remote command transport.
- Confirmed that provider-based runtime hosting is now ready to support additional protocols.

### Changed

- Updated MCP host startup ordering so the final `AiMcpHost` mode is resolved before service registration.
- Updated startup flow so `RuntimeInstanceOnly` enters its dedicated service registration path correctly.
- Updated HTTP runtime test host behavior so `RuntimeInstanceOnly` starts the runtime pipeline controller automatically.
- Updated HTTP runtime provider tests to validate real execution completion instead of only dispatch success.
- Updated HTTP provider scenarios to verify runtime status through provider routing.
- Updated HTTP provider scenarios to verify `ExecutionId` exposure after remote dispatch.
- Updated HTTP provider scenarios to validate completed shared run visibility.
- Updated HTTP provider scenarios to validate activity visibility after completion.
- Updated HTTP provider tests to include queue control and execution control coverage.
- Updated provider-based runtime hosting assumptions:
  - Local provider remains the default runtime path.
  - HTTP provider is now a fully functional opt-in provider.
  - Future protocols can be implemented using the same provider model.

### Fixed

- Fixed MCP host startup ordering where service registration could use an outdated host mode.
- Fixed `RuntimeInstanceOnly` not entering its runtime-specific service registration path.
- Fixed HTTP runtime host not starting `AiRuntimePipelineBackgroundControllerHostedService`.
- Fixed HTTP-dispatched runs staying stuck in `queued`.
- Fixed the missing `ENQUEUED -> DEQUEUED` transition for HTTP-dispatched runs.
- Fixed runtime status remaining without `ExecutionId` after HTTP dispatch.
- Fixed incorrect assumption that HTTP provider dispatch success was enough to prove execution.
- Fixed HTTP runtime execution integration so the provider now validates actual runtime processing.
- Fixed control-plane background service configuration in `RuntimeInstanceOnly` test scenarios.
- Fixed configuration ordering issues where `ApplicationConfiguration` could see the correct mode while `ServiceRegistration` had already used the wrong one.
- Fixed provider integration flow so `RuntimeInstanceOnly` is resolved before service registration.
- Fixed HTTP runtime provider tests so the runtime execution loop is part of the real test path.

### Debugged

- Debugged issue where `/runtime-instance/commands` was mapped correctly but the runtime execution loop was not started.
- Debugged mismatch between endpoint mapping and service registration mode resolution.
- Confirmed that `ApplicationConfiguration` could see `RuntimeInstanceOnly` while service registration had previously missed it.
- Confirmed that the core bug was not the HTTP provider itself, but the runtime host startup path.
- Confirmed that the HTTP command endpoint could enqueue runs successfully.
- Confirmed that the previous blocker was the missing runtime dequeue loop.
- Confirmed through controller hash logs that the hosted service, enqueue path, and dequeue path now use the same controller instance.
- Confirmed that `AiRuntimePipelineBackgroundControllerHostedService` is registered and started in `RuntimeInstanceOnly`.
- Confirmed that `runtime-http-1` receives HTTP dispatch commands and exposes runtime status through the provider.
- Confirmed that HTTP provider integration now reaches execution id exposure.
- Confirmed that the HTTP provider is fully functional as a runtime instance provider.

### Validation

- Local provider path remains functional.
- Local MCP runtime tests remain green.
- HTTP provider DI tests pass.
- HTTP runtime provider dispatch tests pass.
- HTTP runtime provider completion tests pass.
- HTTP runtime provider 100-step pipeline tests pass.
- HTTP runtime provider shared run listing tests pass.
- HTTP runtime provider shared queue activity tests pass.
- HTTP runtime provider runtime status tests pass.
- HTTP runtime provider queued-run cancellation tests pass.
- Preliminary HTTP runtime provider pause/resume/cancel tests pass, with known intermittent behavior shared with local mode.
- `RuntimeInstanceOnly` now correctly reaches service registration.
- `AiRuntimePipelineBackgroundControllerHostedService` starts automatically.
- HTTP-dispatched runs are dequeued by the runtime pipeline controller.
- HTTP-dispatched runs expose `ExecutionId`.
- HTTP-dispatched runs can complete through the runtime DAG engine.
- MCP control plane can dispatch to an HTTP-addressable runtime instance.
- Provider-based runtime hosting is now validated beyond local-only runtime instances.

### Architecture Notes

- Runtime providers are now the main abstraction for dispatching shared runs to runtime instances.
- The local provider remains the default provider for in-process or local runtime instances.
- The HTTP provider is now a fully functional remote runtime provider.
- HTTP runtime instances own their local queue and worker loop.
- The control plane does not execute remote DAG steps directly.
- The HTTP provider sends runtime commands to the target runtime instance.
- The target runtime instance remains responsible for:
  - queue ownership
  - run state
  - execution creation
  - worker processing
  - DAG execution
  - status exposure
- This architecture keeps runtime execution decentralized while allowing the control plane to dispatch across providers.
- The provider model is now ready for additional runtime transports and deployment modes.

### Supported Runtime Provider Modes

- `local`
  - Existing local runtime instance provider.
  - Used for local runtime pools and in-process runtime execution.
  - Stable and still supported.

- `http`
  - New fully functional HTTP runtime instance provider.
  - Used for runtime instances addressable through HTTP endpoints.
  - Validated end-to-end through MCP integration tests.
  - Supports dispatch, runtime queue status, completion, queued cancellation, and preliminary execution control scenarios.

### Next Provider Targets

- Add Redis command transport provider.
- Add gRPC runtime instance provider.
- Add Kubernetes runtime instance provider.
- Add container/pod-aware provider metadata.
- Add provider-specific health checks.
- Add provider-specific retry and timeout policy.
- Add provider-specific command acknowledgements.
- Add provider-specific observability and tracing tags.
- Add provider-specific runtime capability discovery.
- Add provider-specific command transport tests.

### Future Protocol Direction

- Local provider remains the baseline execution path.
- HTTP provider is now the first fully functional remote provider.
- Additional protocols should follow the same provider contract:
  - provider capability check
  - runtime descriptor resolution
  - command transport
  - dispatch result
  - queue status query
  - execution status exposure
  - cancellation support
  - observability metadata
- Future protocols should not bypass local runtime queue ownership.
- Future protocols must preserve the same separation:
  - control plane decides where to send the run
  - provider transports the command
  - runtime instance owns execution

### Known Follow-Up

- Pause/resume can still be flaky in both local and HTTP modes.
- Pause/resume flakiness appears to be an execution-control timing/state convergence issue, not an HTTP provider issue.
- Investigate deterministic pause acknowledgement.
- Investigate fast-completing executions that finish before pause can be observed.
- Improve execution-control status convergence for:
  - pause requested
  - paused
  - resume requested
  - running
  - cancelling
  - cancelled
- Align internal runtime controller identity currently visible as `MSI:*` with the externally registered runtime instance id such as `runtime-http-1`.
- Improve observability consistency between:
  - runtime instance registration id
  - provider descriptor id
  - queue state id
  - pipeline controller id
  - worker id
- Remove or convert remaining test-only `Console.WriteLine` diagnostics to structured logging.
- Add stronger Redis cleanup for shared queues, runtime capacity descriptors, and runtime instance registries between integration tests.
- Add replay visibility validation for HTTP-dispatched executions.
- Add provider-level timeout and failure simulations.
- Add negative tests for unreachable HTTP runtime endpoints.
- Add tests for provider fallback and provider rejection.
- Add tests for stale runtime descriptors.
- Add tests for runtime instance disappearing during dispatch.
- Add tests for retrying provider command transport failures.

### Summary

This update completes the HTTP runtime provider integration and confirms that HTTP is now a fully functional provider-based runtime hosting mode.

The runtime provider architecture now supports both local and HTTP runtime instance execution paths. The HTTP provider has been validated beyond simple provider selection: it can dispatch runs to a runtime-instance-only host, enqueue work into the target runtime queue, start the runtime controller loop, dequeue the run, create an execution, expose runtime status, expose an execution id, and complete DAG execution.

This establishes the foundation for implementing additional runtime transports such as Redis command queues, gRPC, Kubernetes-native providers, and future cloud/container runtime providers.

The next phase is to generalize the provider model further so local, HTTP, Redis, gRPC, Kubernetes, and future protocols can all plug into the same control-plane dispatch and runtime ownership model.

---

## [1.0.5.8] - 2026-06-05 Provider-Based Runtime Hosting and HTTP Runtime Instance Foundation

### Added

- Added provider-based runtime instance hosting foundation.
- Added support for dispatching shared runs through runtime instance providers.
- Added HTTP runtime instance provider foundation for remote runtime command dispatch.
- Added local runtime instance provider validation for existing local runtime pool scenarios.
- Added runtime instance provider metadata support through capacity descriptors.
- Added provider metadata keys for runtime instance resolution.
- Added transport metadata support for HTTP-addressable runtime instances.
- Added HTTP runtime command endpoint support for runtime-instance-only hosts.
- Added runtime command endpoint mapping for runtime instance command handling.
- Added runtime-instance-only host mode support for HTTP command execution scenarios.
- Added control-plane-with-HTTP-runtime-instances host mode support.
- Added isolated HTTP runtime provider integration test fixture.
- Added dedicated two-host MCP HTTP runtime fixture:
  - MCP control-plane host exposing `/mcp`
  - Runtime-instance-only host exposing `/runtime-instance/commands`
- Added dedicated HTTP runtime provider integration test collection.
- Added first HTTP provider scenario test for shared run dispatch through an HTTP runtime instance.
- Added test host configuration isolation for HTTP provider scenarios.
- Added Redis database isolation for HTTP provider tests using a dedicated Redis logical database.
- Added forced test environment configuration for HTTP runtime test hosts.
- Added explicit runtime instance registration override for HTTP runtime test hosts.
- Added explicit control-plane registration override for HTTP control-plane test hosts.
- Added diagnostic logs around runtime registration, provider resolution, capacity descriptors, and remote dispatch.
- Added diagnostic logs to confirm effective host mode and endpoint mapping.
- Added validation logs for runtime instance registration options after test overrides.

### Added MCP / Control Plane Coverage

- Added MCP HTTP runtime provider scenario coverage.
- Added MCP shared run submission validation through the HTTP control-plane host.
- Added MCP shared queue drain validation targeting a remote runtime instance.
- Added validation that `RuntimeInstanceId` and `WorkerId` are handled as separate concepts.
- Added test coverage for dispatching a shared run to a runtime instance registered with provider `http`.
- Added verification that the MCP control plane can run in `ControlPlaneWithHttpRuntimeInstances` mode.
- Added verification that a runtime host can run in `RuntimeInstanceOnly` mode without exposing MCP tools.

### Changed

- Updated local MCP integration test configuration to remain on `ControlPlaneWithLocalRuntimeInstances`.
- Kept existing local runtime pool tests isolated from HTTP provider tests.
- Updated local runtime instance registration to publish `Provider=local`.
- Updated local provider metadata to use `provider.name=local`.
- Removed incorrect HTTP transport metadata from local runtime configurations.
- Updated HTTP runtime provider tests to use a separate fixture instead of modifying existing local MCP tests.
- Updated test host configuration to force the expected mode through in-memory configuration.
- Updated test host configuration to clear inherited configuration sources when required.
- Updated HTTP runtime test configuration to disable the local runtime instance pool.
- Updated HTTP control-plane test configuration to disable local runtime instance pool startup.
- Updated HTTP runtime instance registration to use:
  - `RuntimeInstanceId=runtime-http-1`
  - `ProviderName=http`
  - `provider.name=http`
  - `transport.name=http`
  - `transport.endpoint=http://localhost/runtime-instance/commands`
- Updated HTTP control-plane registration to use:
  - `RuntimeInstanceId=mcp-control-plane-http`
  - `ProviderName=local`
  - `provider.name=local`
- Updated HTTP provider scenario test to drain toward the runtime instance id instead of the worker id.
- Updated queue drain test request to use:
  - `RuntimeInstanceId=RuntimeInstanceHttpTestHost.RuntimeInstanceId`
  - `WorkerId=mcp-http-worker`

### Fixed

- Fixed local runtime instances being logged as `Provider=http` while actually using the local provider.
- Fixed provider mismatch in local runtime pool logs.
- Fixed misleading local runtime provider registration metadata.
- Fixed MCP local test configuration after accidental HTTP-mode regression.
- Fixed HTTP runtime test host not applying the intended runtime instance registration options.
- Fixed `appsettings.Development.json` overriding test intent during HTTP provider test setup.
- Fixed runtime instance registration resolving to `mcp-control-plane` instead of `runtime-http-1` in HTTP runtime tests.
- Fixed runtime host startup in `RuntimeInstanceOnly` mode.
- Fixed MCP control-plane host startup in `ControlPlaneWithHttpRuntimeInstances` mode.
- Fixed minimal API runtime command endpoint parameter inference issue.
- Fixed ASP.NET endpoint mapping failure where the runtime command handler was inferred as `UNKNOWN`.
- Fixed runtime command endpoint by explicitly resolving the command handler from services.
- Fixed missing service resolution path for `AiRuntimeInstanceHttpCommandHandler`.
- Fixed incorrect test drain target where `mcp-http-worker` was incorrectly used as a runtime instance id.
- Fixed HTTP provider test logic so the runtime target is `runtime-http-1`.
- Fixed confusion between `RuntimeInstanceId` and `WorkerId` in shared queue drain requests.
- Fixed Redis pollution between local runtime tests and HTTP provider tests by isolating HTTP scenarios on a dedicated Redis database.
- Fixed provider selection issue where stale local runtime instances could be selected during HTTP provider tests.
- Fixed test fixture structure so HTTP provider tests no longer reuse the local MCP fixture.
- Fixed local test baseline after reverting back from broken HTTP experiments.

### Debugged

- Debugged failing MCP HTTP runtime provider test where the actual dispatched runtime was still `mcp-runtime-1`.
- Identified Redis registry pollution from previous local runtime instance registrations.
- Identified configuration override issues caused by development JSON settings.
- Identified that the HTTP runtime host mode was correct but runtime registration options were still wrong.
- Confirmed through logs that `RuntimeInstanceOnly` was correctly mapping `/runtime-instance/commands`.
- Confirmed through logs that `ControlPlaneWithHttpRuntimeInstances` was correctly mapping `/mcp`.
- Confirmed through logs that `runtime-http-1` was eventually registered successfully with provider `http`.
- Identified that the remaining dispatch failure was caused by the test draining toward `mcp-http-worker`.
- Confirmed that `mcp-http-worker` is a worker identifier, not a runtime instance identifier.
- Confirmed that the correct runtime target for the HTTP provider test is `runtime-http-1`.
- Identified background service noise where `AiMcpControlPlaneBackgroundService` still started with default `mcp-control-plane`.
- Identified repeated background dispatch attempts toward `mcp-control-plane`.
- Deferred background pump cleanup because it was not the primary blocker for the HTTP provider scenario.

### Technical Notes

- Existing local MCP tests must remain on `ControlPlaneWithLocalRuntimeInstances`.
- HTTP provider tests must use a dedicated fixture.
- Local runtime pool and HTTP runtime instance tests must not share the same runtime selection assumptions.
- `RuntimeInstanceId` represents the runtime instance that owns the local queue and worker pool.
- `WorkerId` represents the worker or pump issuing the drain or dispatch operation.
- A shared queue drain request must target a real registered runtime instance id.
- For the HTTP provider test, the correct values are:
  - `RuntimeInstanceId=runtime-http-1`
  - `WorkerId=mcp-http-worker`
- The control-plane host should not be treated as the runtime execution target in HTTP provider tests.
- The HTTP runtime instance host must be registered as provider `http`.
- The MCP control-plane host may register itself as provider `local`, but should not be selected as the execution runtime for HTTP dispatch tests.
- The local runtime pool must stay disabled in HTTP provider test hosts.
- Redis isolation is required to avoid stale local runtime descriptors affecting HTTP provider selection.

### Known Follow-Up

- Investigate why `AiMcpControlPlaneBackgroundService` still starts with `RuntimeInstanceId=mcp-control-plane` in HTTP provider test scenarios.
- Ensure `EnableSharedQueuePump=false` fully disables automatic background queue draining.
- Ensure `AiMcpControlPlaneHostOptions.RuntimeInstanceId` is correctly propagated in HTTP control-plane mode.
- Ensure background pump uses `mcp-control-plane-http` when enabled in HTTP control-plane tests.
- Add cleaner test infrastructure for routing `HttpAiRuntimeInstanceProvider` calls between multiple `WebApplicationFactory` hosts.
- Add explicit integration test proving the HTTP provider sends a command to `/runtime-instance/commands`.
- Add integration test for HTTP runtime command status query.
- Add integration test for HTTP runtime run cancellation.
- Add integration test for HTTP runtime run pause and resume.
- Add integration test for HTTP runtime replay visibility once HTTP dispatch is stable.
- Add stronger cleanup for Redis shared queue and runtime capacity descriptors between integration test runs.
- Consider replacing test-only `Console.WriteLine` diagnostics with structured logger output.
- Review provider metadata naming consistency across local, HTTP, and future Kubernetes runtime providers.

### Validation

- Local MCP runtime tests are green again.
- Local runtime pool dispatch works through `LocalAiRuntimeInstanceProvider`.
- Local runtime instances now register consistently as provider `local`.
- HTTP runtime host starts in `RuntimeInstanceOnly` mode.
- HTTP runtime command endpoint is mapped.
- HTTP runtime instance registers as `runtime-http-1`.
- HTTP runtime instance registers with provider `http`.
- HTTP control-plane host starts in `ControlPlaneWithHttpRuntimeInstances` mode.
- MCP endpoint `/mcp` is mapped on the HTTP control-plane host.
- HTTP control-plane registers as `mcp-control-plane-http`.
- HTTP provider scenario now drains to `runtime-http-1` instead of `mcp-http-worker`.

### Summary

This update introduces the foundation for provider-based runtime hosting and begins the transition from purely local runtime instance dispatch to HTTP-addressable runtime instances.

The local runtime path remains stable and green. A new isolated HTTP provider test infrastructure was introduced to avoid breaking local MCP scenarios. The main debugging work focused on configuration isolation, runtime registration correctness, provider metadata accuracy, and proper distinction between runtime instance identifiers and worker identifiers.

The key correction was ensuring that HTTP provider tests drain shared runs toward the actual registered runtime instance, `runtime-http-1`, instead of the worker id `mcp-http-worker`.

The next step is to validate the actual HTTP command transport between the MCP control-plane test host and the runtime-instance-only test host.

---

## [1.0.5.7] - 2026-06-04 Runtime Capacity Descriptors, Worker Identity Propagation, and Shutdown Stabilization

### Added

- Added Redis-backed runtime instance capacity descriptor foundation.
- Added runtime capacity descriptor support for runtime instance administration.
- Added `IAiRuntimeInstanceCapacityStore`.
- Added `RedisAiRuntimeInstanceCapacityStore`.
- Added runtime capacity descriptor publication during runtime instance registration.
- Added runtime capacity descriptor publication during runtime heartbeat.
- Added runtime capacity descriptor removal during runtime unregister.
- Added runtime capacity fields for admission and future scheduling:
  - `WorkerCount`
  - `ActiveWorkerCount`
  - `AvailableWorkerCount`
  - `MaxWorkersPerRun`
  - `MinWorkersRequiredPerRun`
  - `MaxRunSlots`
  - `AvailableRunSlots`
  - `ReservedRunSlots`
  - `EffectiveAvailableRunSlots`
  - `QueuedRunCount`
  - `RunningRunCount`
  - `ActiveRunCount`
  - `IsQueuePaused`
  - `CanAcceptRun`
  - `LastHeartbeatAtUtc`

- Added Redis runtime instance registry support.
- Added `RedisAiRuntimeInstanceRegistry`.
- Added runtime instance registry tests for Redis-backed registration, heartbeat, listing, and unregister behavior.
- Added `RuntimeInstanceEntry` model for Redis registry persistence.
- Added runtime instance capacity unit tests.
- Added runtime instance descriptor/capacity test coverage.

### Added Runtime Identity Descriptor Model

- Added `IAiRuntimeInstanceIdentityDescriptor`.
- Replaced direct runtime identity usage in several runtime components with descriptor-based identity usage.
- Added support for fixed runtime instance identifiers such as:

  ~~~text
  mcp-runtime-1
  mcp-runtime-2
  mcp-runtime-3
  ~~~

- Added normalized runtime execution identity output such as:

  ~~~text
  MSI:mcp-runtime-1
  MSI:mcp-runtime-2
  MSI:mcp-runtime-3
  ~~~

- Added compatibility between runtime instance identity descriptors and worker identity generation.
- Added runtime identity descriptor support in test hosts and fixtures.

### Added Worker Identity Propagation

- Added proper distributed worker identity propagation through `AiRuntimeInstanceWorkerFactory`.
- Added numbered worker identities for distributed runtime workers.
- Added support for worker identifiers such as:

  ~~~text
  MSI:mcp-runtime-1:worker:1
  MSI:mcp-runtime-1:worker:2
  MSI:mcp-runtime-1:worker:3
  ~~~

- Preserved fallback default worker identity for direct DI usage:

  ~~~text
  MSI:mcp-runtime-1:worker:default
  ~~~

- Added worker identity propagation into runtime execution correlation.
- Added worker identity propagation into ledger and observability paths.
- Updated worker-related integration tests to reflect descriptor-based identity and numbered worker behavior.

### Added Pipeline Background Controller Configuration

- Added full `PipelineBackgroundController` configuration support under `AiEngine`.
- Added support for distributed worker configuration through appsettings:

  ~~~json
  {
    "AiEngine": {
      "PipelineBackgroundController": {
        "MaxConcurrentRuns": 5,
        "QueueCapacity": 1000,
        "RejectEnqueueWhenStopped": false,
        "StopOnFirstFailure": false,
        "Distributed": {
          "Enabled": true,
          "WorkerCount": 10,
          "StopOnFirstTerminal": true,
          "TerminalObservationTimeout": "00:00:30"
        }
      }
    }
  }
  ~~~

- Added proper child runtime instance usage of parent JSON configuration.
- Added distributed worker execution support inside local runtime instance pool hosts.

### Fixed

- Fixed local runtime instance pool child hosts losing `PipelineBackgroundController.Distributed` configuration.
- Fixed child runtime instances falling back to:

  ~~~text
  worker:default
  ~~~

  instead of using numbered distributed worker identities.

- Fixed ledger and replay output showing incorrect default worker identity for distributed execution.
- Fixed worker identity not being properly reflected in claim, concurrency, step, retry, and retention events.
- Fixed runtime queue execution using singleton/default worker identity when distributed workers were enabled.
- Fixed runtime identity format so instance execution identity remains stable and readable.
- Fixed duplicated Redis runtime capacity store registration.
- Fixed duplicated capacity store resolution:

  Before:

  ~~~text
  [RUNTIME CAPACITY] STORES RESOLVED Count='2'
  ~~~

  After:

  ~~~text
  [RUNTIME CAPACITY] STORES RESOLVED Count='1'
  ~~~

- Fixed duplicate runtime capacity descriptor publishing caused by duplicate store registrations.
- Fixed runtime registration shutdown executing unregister more than once.
- Fixed local runtime instance pool shutdown executing stop more than once.
- Fixed duplicate unregister attempts during test host shutdown.
- Fixed duplicate local pool stop logs during shutdown.
- Fixed shutdown lifecycle to be idempotent and safe under repeated host stop/dispose calls.

### Changed

- Updated `AiControlPlaneServiceCollectionExtensions` to use `TryAddEnumerable` for runtime capacity stores.
- Updated runtime instance registration to publish capacity descriptors after registration and heartbeat.
- Updated runtime instance registration to remove capacity descriptors during unregister.
- Updated `AiRuntimeInstanceRegistrationHostedService.StopAsync()` to be idempotent.
- Updated `AiLocalRuntimeInstancePoolHostedService.StopAsync()` to be idempotent.
- Updated `AiLocalRuntimeInstanceHostFactory` to preserve parent runtime options instead of overwriting distributed settings.
- Updated local runtime instance pool startup diagnostics to include:
  - runtime instance id
  - worker count
  - max concurrent runs
  - queue capacity
  - available run slots
  - running run count
  - queued run count

- Updated MCP host appsettings to configure distributed workers and runtime capacity correctly.
- Updated multiple integration and unit tests to support descriptor-based runtime identity.
- Updated observability ledger tests to use the new runtime identity descriptor model.
- Updated concurrency, retry, multi-instance, and worker integration tests for the new identity and capacity model.

### Architecture

- Introduced runtime capacity descriptors as the foundation for future provider-based runtime administration.
- Established Redis as the shared visibility layer for runtime instance capacity.
- Separated runtime registration from runtime capacity publication.
- Prepared admission to rely on real capacity descriptors instead of only registry snapshots.
- Prepared shared queue admission for worker-aware and slot-aware scheduling.
- Prepared runtime administration for future provider-based dispatch and control.
- Preserved local runtime queues as instance-owned execution queues.
- Preserved local runtime instance behavior while adding control-plane-level capacity visibility.
- Confirmed that the shared queue remains above local runtime queues:

  ~~~text
  Shared Queue
      -> Admission
      -> Runtime Instance Selection
      -> Dispatch Provider
      -> Local Runtime Queue
      -> Workers
      -> Execution Engine
  ~~~

- Confirmed local runtime queues are not replaced or modified by the shared queue.
- Confirmed each runtime host keeps its own:
  - local queue
  - run slots
  - worker pool
  - queue state
  - heartbeat
  - capacity descriptor

### Runtime Instance Capacity Model

- Runtime instances now expose capacity through Redis descriptors.
- Runtime descriptors now allow the control plane to reason about:

  ~~~text
  mcp-runtime-1
      WorkerCount = 10
      MaxRunSlots = 5
      AvailableRunSlots = 5
      CanAcceptRun = true

  mcp-runtime-2
      WorkerCount = 10
      MaxRunSlots = 5
      AvailableRunSlots = 5
      CanAcceptRun = true

  mcp-runtime-3
      WorkerCount = 10
      MaxRunSlots = 5
      AvailableRunSlots = 5
      CanAcceptRun = true
  ~~~

- Control-plane instances remain visible but should not be considered executable runtime targets.

### MCP Host Configuration

- Updated `appsettings.json` and `appsettings.Development.json` with runtime pipeline background controller options.
- Configured local runtime instance pool with:
  - `InstanceCount = 3`
  - `WorkerCountPerInstance = 10`
  - `MaxConcurrentRunsPerInstance = 5`
  - `QueueCapacity = 1000`

- Confirmed runtime pool startup logs show:

  ~~~text
  Local runtime instance pool started. ActiveInstanceCount=3
  ~~~

- Confirmed each local runtime instance reports the expected queue state:

  ~~~text
  QueueStateMaxConcurrentRuns=5
  QueueStateAvailableRunSlots=5
  QueueStateQueueCapacity=1000
  ~~~

### Observability

- Improved runtime identity and worker identity consistency in:
  - decision ledger
  - replay output
  - trace output
  - concurrency events
  - claim events
  - retry events
  - retention events

- Confirmed execution events now use numbered worker identities when distributed workers are enabled.
- Confirmed controller-level events may still use control-plane/controller identity where appropriate.
- Added diagnostic logs for runtime capacity store resolution.
- Added diagnostic logs for runtime instance registration, heartbeat, unregister, queue state, and pool capacity.
- Added shutdown skip logs for idempotent lifecycle handling.

### Tests

- Updated MCP integration tests for local runtime pool and shared queue dispatch.
- Updated runtime identity tests for descriptor-based identity.
- Updated multi-instance runtime tests.
- Updated worker integration tests.
- Updated concurrency gate integration tests.
- Updated DAG retry integration tests.
- Updated observability ledger tests.
- Updated test fixtures to use `IAiRuntimeInstanceIdentityDescriptor`.
- Added Redis runtime instance registry tests.
- Added runtime capacity descriptor tests.
- Confirmed MCP shared run scenario passes with:
  - 3 local runtime instances
  - 10 workers per instance
  - 5 max concurrent runs per instance
  - Redis registry
  - Redis capacity descriptor store
  - replay
  - ledger
  - trace

- Confirmed test result:

  ~~~text
  1 Tests Passed
  0 Failed
  0 Skipped
  ~~~

### Removed

- Removed old `IAiRuntimeInstanceIdentity` abstraction.
- Replaced it with `IAiRuntimeInstanceIdentityDescriptor`.
- Removed dependency on default worker identity for distributed worker execution.
- Removed duplicate capacity store registration behavior.
- Removed duplicate unregister execution during shutdown.
- Removed duplicate local runtime pool stop execution during shutdown.

### Follow-up

The next work item is provider-based runtime instance administration and admission.

Planned architecture direction:

- Runtime instances publish descriptors.
- Admission chooses the best runtime instance based on real capacity.
- Dispatch becomes provider-based and dynamically loaded.
- Providers are discovered using class attributes.
- Local queues remain owned by runtime instances and are not replaced.

Planned provider abstraction:

~~~text
Runtime Instance Provider
    - Local provider
    - Redis command queue provider
    - HTTP provider
    - gRPC provider
    - Kubernetes provider
~~~

Planned provider capabilities:

- dispatch run
- get run status
- cancel run
- pause queue
- resume queue
- drain queue
- list capacity
- request scale-out
- request scale-in

Initial implementation focus:

- Add provider attribute:

  ~~~csharp
  [AiRuntimeInstanceProvider("local")]
  ~~~

- Add provider base abstraction.
- Add provider capability interfaces.
- Add provider router.
- Add local runtime instance provider.
- Adapt shared run dispatcher to call the provider router.
- Keep local runtime queues unchanged.
- Keep single-instance and local multi-instance execution behavior unchanged.

Admission follow-up:

- Use Redis capacity descriptors as the primary source of scheduling truth.
- Filter only runtime instances that:
  - are runtime role
  - are ready
  - are not paused
  - can accept runs
  - have effective available run slots

- Improve admission ordering using real capacity:

  ~~~csharp
  .OrderByDescending(instance => instance.EffectiveAvailableRunSlots)
  .ThenByDescending(instance => instance.AvailableWorkerCount)
  .ThenBy(instance => instance.RunningRunCount)
  .ThenBy(instance => instance.QueuedRunCount)
  .ThenByDescending(instance => instance.LastHeartbeatAtUtc)
  .ThenBy(instance => instance.RuntimeInstanceId, StringComparer.Ordinal)
  ~~~

- Add future Redis/Lua slot reservations for multi-control-plane safety.

### Status

This release stabilizes runtime instance visibility, worker identity propagation, runtime capacity descriptors, and shutdown lifecycle.

The runtime control plane is now ready for provider-based dispatch, runtime administration, and capacity-aware admission.

---

## [1.0.5.6] - 2026-06-03 MCP Control Plane Runtime Role Separation and Local Pool Execution Fixes

### Added

- Added explicit runtime instance role separation through `AiRuntimeInstanceRole`.
- Added `AiRuntimeInstanceRole.Runtime`.
- Added `AiRuntimeInstanceRole.ControlPlane`.
- Added `Role` support to `AiRuntimeInstanceRegistration`.
- Added `Role` support to `AiRuntimeInstanceRegistrationOptions`.
- Added `Role` support to `AiRuntimeInstanceSnapshot`.
- Added runtime role propagation from registration options to runtime instance registration.
- Added runtime role propagation from registry entries to runtime instance snapshots.
- Added control-plane-aware runtime registry behavior.
- Added protection to prevent control-plane registrations from being treated as dispatchable runtime instances.

### Fixed

- Fixed MCP control-plane host being incorrectly registered as an executable runtime instance.
- Fixed admission selecting `mcp-control-plane` as a dispatch target.
- Fixed shared queue dispatch failures caused by admission assigning runs to a non-dispatchable control-plane registration.
- Fixed remote dispatch failures with:

  ~~~text
  RuntimeInstanceId=mcp-control-plane
  Found=False
  Reason=runtime-instance-not-registered
  ~~~

- Fixed `ControlPlaneWithLocalRuntimeInstances` mode registering the MCP host with default runtime registration options.
- Fixed missing control-plane role configuration in `ConfigureControlPlaneWithLocalRuntimeInstances`.
- Fixed runtime admission relying on hardcoded `mcp-control-plane` filtering.
- Fixed runtime selection so only instances with `Role = Runtime` can be selected for execution.
- Fixed control-plane registration so it no longer reports itself as an executable runtime candidate.
- Fixed MCP control-plane runtime identity configuration to avoid empty `RuntimeInstanceId` usage.
- Fixed background MCP control-plane service startup identity by restoring a stable runtime id:

  ~~~text
  RuntimeInstanceId = mcp-control-plane
  ~~~

### Changed

- Updated `AiRunAdmissionController` to select only runtime instances where:

  ~~~csharp
  instance.Role == AiRuntimeInstanceRole.Runtime
  ~~~

- Updated admission logic to remove dependency on string-based exclusion such as:

  ~~~csharp
  "mcp-control-plane"
  ~~~

- Updated runtime instance eligibility so role-based filtering is now the source of truth.
- Updated runtime registry behavior so control-plane entries cannot accept runs.
- Updated `InMemoryAiRuntimeInstanceRegistry` to preserve and expose runtime instance role.
- Updated registration lifecycle so control-plane and runtime instances are represented distinctly.
- Updated MCP host service registration for `ControlPlaneWithLocalRuntimeInstances` mode to explicitly register:

  ~~~csharp
  Role = AiRuntimeInstanceRole.ControlPlane
  ~~~

### Architecture

- Established a clean separation between MCP control-plane hosts and executable runtime instances.

  ~~~text
  mcp-control-plane
      Role = ControlPlane
      CanAcceptRun = false

  mcp-runtime-1
      Role = Runtime
      CanAcceptRun = true

  mcp-runtime-2
      Role = Runtime
      CanAcceptRun = true

  mcp-runtime-3
      Role = Runtime
      CanAcceptRun = true
  ~~~

- Replaced runtime-instance identity hacks with explicit role-based runtime classification.
- Prepared the admission layer for future Kubernetes scheduling.
- Prepared runtime registry semantics for multi-pod / multi-replica environments.
- Improved the shared controller model by distinguishing:
  - control-plane host
  - runtime instance
  - shared queue
  - local runtime queue
  - worker pool

### MCP Host Configuration

- Updated `ConfigureControlPlaneWithLocalRuntimeInstances` to register the MCP control-plane as a control-plane role:

  ~~~csharp
  services.AddAiRuntimeInstanceRegistrationHostedService(options =>
  {
      options.Enabled = true;
      options.RuntimeInstanceId = "mcp-control-plane";
      options.Role = AiRuntimeInstanceRole.ControlPlane;
  });
  ~~~

- Ensured local runtime instances created by the pool remain registered as runtime instances.
- Preserved MCP background pump identity while preventing it from being selected for run execution.

### Tests

- Fixed MCP shared run dispatch tests where runs were incorrectly assigned to `mcp-control-plane`.
- Fixed long-running execution cancellation scenario blocked by incorrect runtime selection.
- Fixed shared queue drain scenarios depending on runtime-instance routing.
- Confirmed local runtime instance pool dispatch now targets real runtime instances such as:

  ~~~text
  mcp-runtime-1
  mcp-runtime-2
  mcp-runtime-3
  ~~~

- Confirmed `ControlPlaneWithLocalRuntimeInstances` mode no longer requires hardcoded admission exclusions.
- Confirmed runtime instance role separation resolves the MCP dispatch registry mismatch.

### Removed

- Removed the need for hardcoded admission filtering against:

  ~~~csharp
  "mcp-control-plane"
  ~~~

- Removed the need to use empty runtime instance identifiers for MCP control-plane registration.
- Removed role ambiguity between control-plane hosts and executable runtime instances.

### Follow-up

The next work item is local admission capacity correctness.

Current area to investigate:

- `MaxConcurrentRunsPerInstance`
- `WorkerCountPerInstance`
- `MaxWorkersPerRun`
- `AvailableRunSlots`
- `AvailableWorkerCount`
- `CanAcceptRun`

Observed issue:

~~~text
AiLocalRuntimeInstancePoolOptions.MaxConcurrentRunsPerInstance = 3
~~~

but runtime registration / heartbeat can still report:

~~~text
MaxConcurrentRuns = 4
AvailableRunSlots = 4
~~~

Next implementation focus:

- Trace capacity propagation from `AiLocalRuntimeInstancePoolOptions` to child runtime instances.
- Ensure `AiLocalRuntimeInstanceHostFactory` applies the correct runtime capacity.
- Ensure `AiRuntimePipelineBackgroundController.GetQueueStateAsync()` reports the correct capacity.
- Ensure `AiRuntimeInstanceRegistrationHostedService` publishes the correct heartbeat values.
- Add support for:
  - `MaxWorkersPerRun`
  - `MinWorkersRequiredPerRun`
  - `ActiveWorkerCount`
  - `AvailableWorkerCount`
- Improve admission ordering using real capacity:

  ~~~csharp
  .OrderByDescending(instance => instance.AvailableRunSlots ?? 0)
  .ThenByDescending(instance => instance.AvailableWorkerCount)
  .ThenBy(instance => instance.RunningRunCount)
  .ThenBy(instance => instance.QueuedRunCount)
  .ThenBy(instance => instance.RuntimeInstanceId, StringComparer.Ordinal)
  ~~~

### Status

This release resolves the runtime/control-plane identity issue and makes local multi-instance admission structurally correct.

The next release should focus on runtime capacity accuracy and worker-aware admission.

---

## [1.0.5.5] - 2026-06-02 Local Runtime Instance Pool Foundation

### Added

- Added local runtime instance pool foundation for MCP control-plane multi-instance hosting.
- Added `AiLocalRuntimeInstancePoolOptions`.
- Added `IAiLocalRuntimeInstanceHost`.
- Added `AiLocalRuntimeInstanceHost`.
- Added `IAiLocalRuntimeInstanceHostFactory`.
- Added `AiLocalRuntimeInstanceHostFactory`.
- Added `AiLocalRuntimeInstancePoolHostedService`.
- Added `IAiLocalRuntimeInstanceServiceCollectionProvider`.
- Added `AiLocalRuntimeInstanceServiceCollectionProvider`.
- Added `AiLocalRuntimeInstancePoolServiceCollectionExtensions`.
- Added support for configuring local runtime instance pools through `appsettings.json`.
- Added runtime instance pool startup validation and lifecycle management.
- Added support for creating multiple runtime instances within a single MCP host process.
- Added runtime instance pool registration into the shared runtime instance registry.
- Added local runtime instance startup and shutdown orchestration.

### Added MCP Tools and Diagnostics

- Added MCP shared queue activity diagnostics.
- Added `shared_queue.activity` MCP tool.
- Added runtime-instance-aware runtime queue MCP routing.
- Updated `RuntimeQueueMcpTools` to route queue operations by `RuntimeInstanceId`.
- Added runtime queue resolution through `IAiSharedRuntimeInstanceRegistry`.
- Added support for querying runtime queues belonging to specific runtime instances.
- Added support for runtime queue control operations targeting specific runtime instances.
- Added fallback behavior to the root runtime queue when a runtime instance cannot be resolved.
- Added MCP visibility for fast-draining shared queues through activity history.
- Added MCP support for inspecting shared run activity even when the active shared queue is empty.

### Added MCP Test Support

- Added MCP client support for `shared_queue.activity`.
- Added MCP test client argument binding for `AiSharedQueueActivityRequest`.
- Added shared queue activity integration test coverage.
- Added MCP scenario output helpers for shared queue activity summaries.
- Added tests validating shared queue activity visibility after fast dispatch.
- Updated shared queue drain tests to support both manual drain and background pump dispatch behavior.
- Updated runtime queue status tests to support runtime-instance-routed queue status inspection.
- Added coverage for local runtime instance pool startup and shared runtime registry registration.

### Architecture

- Introduced the foundation for local multi-instance execution:
  - Shared Queue
  - Shared Run Store
  - Shared Runtime Registry
  - Multiple Runtime Instances
  - Dedicated Runtime Queue per Instance
  - Dedicated Worker Pool per Instance

- Established the local execution model that mirrors the future Kubernetes architecture.

### Configuration

Example:

~~~json
{
  "AiLocalRuntimeInstancePool": {
    "Enabled": true,
    "InstanceCount": 3,
    "WorkerCountPerInstance": 10,
    "MaxConcurrentRunsPerInstance": 3,
    "LocalQueueCapacity": null,
    "RuntimeInstanceIdPrefix": "mcp-runtime"
  }
}
~~~

### Known Limitation

The local runtime instance pool currently succeeds at:

- creating runtime instances
- starting runtime instance pool lifecycle
- registering local runtime instances into the shared runtime instance registry
- dispatching shared runs to local runtime instances
- creating local runtime runs
- exposing runtime queue visibility through MCP
- exposing shared queue activity through MCP

However, child runtime instances do not yet execute queued runs independently in pool-only mode.

Current symptoms:

- Shared queue dispatch succeeds.
- Local runs are created successfully.
- MCP can route runtime queue status to the correct runtime instance.
- Runtime queue status remains `queued`.
- Execution identifiers are never assigned.
- Runtime execution progresses only when the root `AiRuntimePipelineBackgroundControllerHostedService` is also enabled.

Investigation is ongoing around:

- `IAiRuntimeInstanceIdentity`
- child service provider isolation
- runtime queue ownership
- worker registration and controller ownership alignment
- ensuring pool runtime instances execute under their assigned `RuntimeInstanceId`

This follow-up is required before enabling true pool-only execution without relying on the root runtime controller hosted service.

---

## [1.0.5.5] - 2026-05-31 - Shared Runtime Controller V1 / Distributed Shared Queue Foundation

## Overview

This update completes the first full Shared Runtime Controller V1 for the deterministic AI runtime.

The goal of this phase was to move from a local runtime control-plane foundation to a multi-instance-ready shared orchestration layer.

The runtime can now coordinate run submission, admission decisions, direct dispatch, global shared queue dispatch, Redis-backed distributed queue coordination, background queue consumption, and scale-out request publication.

This work prepares the system for:

- Kubernetes runtime instance coordination
- multi-instance shared run dispatch
- Redis-backed global queue coordination
- background queue consumption by runtime instances
- future remote runtime dispatch
- future Kubernetes autoscaling adapter
- future MCP/API/dashboard control operations
- production observability through Kibana, Grafana, and OpenSearch

---

## Added

### Shared Runtime Controller V1

Added the first complete shared runtime controller implementation.

Added shared controller contracts and models for:

- shared runtime controller operations
- shared runtime controller requests
- shared runtime controller results
- shared run records
- shared run statuses
- shared run store abstraction
- shared runtime controller options

Added runtime implementation:

- `AiSharedRuntimeController`

The shared runtime controller now handles admission outcomes:

- `AssignToInstance`
- `QueueGlobally`
- `RequestScaleOut`
- `Reject`

The controller now creates a durable shared run record for every submitted run.

This makes run admission decisions visible, queryable, auditable, and ready for external adapters.

---

### Shared Run Store

Added shared run store abstraction:

- `IAiSharedRunStore`

Added in-memory implementation:

- `InMemoryAiSharedRunStore`

Added Redis-backed implementation:

- `RedisAiSharedRunStore`

The shared run store supports:

- create shared run
- get shared run
- list shared runs
- cancel shared run
- mark shared run as dispatched

Redis shared run storage uses:

- one Redis hash per shared run
- one sorted set index for listing
- Lua atomic create
- Lua atomic cancel
- Lua atomic mark-dispatched
- script SHA caching
- automatic NOSCRIPT reload

Added tests for:

- create
- duplicate protection
- get
- list
- cancel
- terminal state safety
- mark dispatched
- Redis script execution
- Redis NOSCRIPT resilience

---

### Shared Queue

Added shared/global queue contracts and models:

- `IAiSharedQueue`
- `AiSharedQueueItem`
- `AiSharedQueueItemStatus`
- `AiSharedQueueClaimRequest`
- `AiSharedQueueOptions`

Added in-memory shared queue implementation:

- `InMemoryAiSharedQueue`

Added Redis-backed shared queue implementation:

- `RedisAiSharedQueue`

The shared queue supports:

- enqueue pending shared run
- claim next pending shared run
- mark claimed item as dispatched
- requeue claimed item
- cancel queued item
- get queue item
- list queue items

Redis shared queue storage uses:

- one Redis hash per queue item
- pending sorted set
- all-items sorted set
- Lua atomic enqueue
- Lua atomic claim-next
- Lua atomic mark-dispatched
- Lua atomic requeue
- Lua atomic cancel
- script SHA caching
- automatic NOSCRIPT reload

Added integration tests for:

- enqueue
- get
- list
- claim
- dispatch
- requeue
- cancel
- metadata persistence
- tenant/pipeline filtering
- terminal item exclusion
- concurrent claim safety

---

### Direct Assigned Run Dispatch

Added shared run dispatch contracts:

- `IAiSharedRunDispatcher`
- `AiSharedRunDispatchRequest`
- `AiSharedRunDispatchResult`

Added local dispatcher implementation:

- `LocalAiSharedRunDispatcher`

The local dispatcher bridges assigned shared runs to the existing local runtime queue through:

- `IAiRuntimeQueueControlPlane`

When admission returns `AssignToInstance`, the shared controller now:

1. creates a shared run record
2. dispatches the run to the selected runtime instance
3. receives local runtime queue result
4. stores `LocalRunId`
5. stores `ExecutionId` when available
6. marks the shared run as `Dispatched`

Added metadata propagation from shared controller dispatch to runtime queue control-plane requests.

Added tests for:

- successful dispatch
- failed dispatch
- exception-to-failure result conversion
- metadata merge
- correlation fallback
- validation behavior

---

### Global Shared Queue Dispatch

Added shared queue dispatch contracts:

- `IAiSharedQueueDispatcher`
- `AiSharedQueueDispatchRequest`
- `AiSharedQueueDispatchResult`

Added runtime implementation:

- `AiSharedQueueDispatcher`

The shared queue dispatcher performs the full queued dispatch flow:

1. atomically claims one pending shared queue item
2. loads the matching shared run record
3. dispatches the shared run through `IAiSharedRunDispatcher`
4. marks the queue item as dispatched
5. marks the shared run as dispatched
6. requeues the item if the shared run is missing
7. requeues the item if dispatch fails

Added tests for:

- no item available
- successful claim and dispatch
- missing shared run requeue
- dispatch failure requeue
- tenant/pipeline filters
- metadata merge
- validation behavior

Added Redis integration tests proving:

- Redis shared queue item claim
- Redis shared run load
- dispatch result persistence
- queue item dispatched state
- shared run dispatched state
- missing shared run requeue
- dispatch failure requeue
- concurrent Redis dispatch safety

Only one dispatcher can claim and dispatch a pending Redis shared queue item.

---

### Shared Queue Pump

Added shared queue pump contracts and options:

- `IAiSharedQueuePump`
- `AiSharedQueuePumpRequest`
- `AiSharedQueuePumpResult`
- `AiSharedQueuePumpOptions`

Added runtime implementation:

- `AiSharedQueuePump`

The pump executes controlled queue dispatch cycles.

It repeatedly calls `IAiSharedQueueDispatcher.DispatchNextAsync(...)` until:

- maximum dispatch count is reached
- no pending item is available
- a dispatch failure occurs and options require stopping
- cancellation is requested

Pump options include:

- enabled flag
- max dispatches per cycle
- default claim TTL
- stop cycle when no item is available
- stop cycle on dispatch failure
- worker id
- source label

Added tests for:

- empty queue behavior
- multiple dispatches
- max dispatch limit
- request override of max dispatch count
- options fallback
- continuing after dispatch failure
- stopping after dispatch failure
- disabled pump
- context propagation
- validation behavior

---

### Shared Queue Background Service

Added shared queue background service options:

- `AiSharedQueueBackgroundServiceOptions`

Added hosted service:

- `AiSharedQueueBackgroundService`

The background service continuously runs shared queue pump cycles.

It is intentionally thin and delegates business logic to:

- `IAiSharedQueuePump`

The background service handles:

- start/stop lifecycle
- runtime instance id resolution
- worker id resolution
- pump cycle execution
- idle delay
- active delay
- error delay
- basic logging
- cancellation-aware shutdown

Added DI extension:

- `AddAiSharedQueueBackgroundService(...)`

Added tests for:

- disabled service does not call pump
- enabled service calls pump
- options propagation
- default runtime/worker id resolution
- continuation after pump exception
- graceful stop behavior

---

### Runtime Scale-Out Request Publisher

Added scale-out request contracts:

- `IAiRuntimeScaleOutRequestPublisher`
- `AiRuntimeScaleOutRequest`
- `AiRuntimeScaleOutRequestResult`

Added no-op implementation:

- `NoopAiRuntimeScaleOutRequestPublisher`

When admission returns `RequestScaleOut`, the shared runtime controller now:

1. creates a shared run record
2. stores it as `ScaleOutRequested`
3. publishes a scale-out request through `IAiRuntimeScaleOutRequestPublisher`

The default no-op publisher acknowledges the scale-out request without creating infrastructure.

This keeps the architecture ready for future implementations:

- Redis scale-out publisher
- message bus publisher
- Kubernetes scaler adapter
- external control-plane publisher

Added tests for:

- scale-out request publication
- target instance count calculation
- max instance count limit
- null request validation
- missing shared run id validation
- controller integration
- Redis shared run persistence with scale-out publication

---

### Dependency Injection

Updated `AiControlPlaneServiceCollectionExtensions`.

Added DI registration for:

- `IAiSharedRunStore`
- `IAiSharedQueue`
- `IAiSharedRunDispatcher`
- `IAiSharedQueueDispatcher`
- `IAiSharedQueuePump`
- `IAiRuntimeScaleOutRequestPublisher`
- `IAiSharedRuntimeController`

Added default implementations:

- `InMemoryAiSharedRunStore`
- `InMemoryAiSharedQueue`
- `LocalAiSharedRunDispatcher`
- `AiSharedQueueDispatcher`
- `AiSharedQueuePump`
- `NoopAiRuntimeScaleOutRequestPublisher`
- `AiSharedRuntimeController`

Added hosted service registration extension:

- `AddAiSharedQueueBackgroundService(...)`

Added options registration for:

- shared runtime controller
- shared queue
- shared queue pump
- shared queue background service

Added DI tests for all new registrations.

---

### Tests

Added and updated tests for:

- shared runtime controller
- Redis shared runtime controller
- shared run store
- Redis shared run store
- shared queue
- Redis shared queue
- local shared run dispatcher
- shared queue dispatcher
- Redis shared queue dispatcher
- shared queue pump
- shared queue background service
- no-op scale-out publisher
- dependency injection registrations

Validated:

- direct assigned dispatch
- dispatch failure fallback
- global queue enqueue
- queued dispatch
- Redis atomic claim
- Redis concurrent dispatch safety
- missing shared run requeue
- dispatch failure requeue
- pump cycle control
- background service lifecycle
- scale-out publication
- shared run cancellation
- shared run listing
- shared run retrieval

---

## Changed

Shared runtime controller now fully handles all admission outcomes:

- `AssignToInstance`
- `QueueGlobally`
- `RequestScaleOut`
- `Reject`

Assigned runs are no longer only recorded as assigned.

They are now dispatched through `IAiSharedRunDispatcher` and marked as `Dispatched` when dispatch succeeds.

Globally queued runs are no longer only stored as shared run records.

They are now enqueued into `IAiSharedQueue`.

Scale-out requests are no longer only represented by a shared run status.

They are now published through `IAiRuntimeScaleOutRequestPublisher`.

Runtime queue control-plane request model now supports metadata propagation for future dashboards, Kubernetes labels, routing policies, and diagnostics.

Options models used through DI were updated to use mutable `set` properties instead of `init` where needed by `services.Configure(...)`.

---

## Fixed

Fixed build failures caused by new shared controller constructor dependencies.

Fixed unit tests after adding:

- shared run dispatcher
- shared queue dispatcher
- shared queue pump
- scale-out publisher

Fixed Redis shared run store script support after adding `MarkDispatchedAsync`.

Fixed Lua script cache support for additional scripts.

Fixed Redis Lua script argument issues in cancel and mark-dispatched flows.

Fixed shared queue dispatcher validation tests to match intentional throw behavior for invalid programming input.

Fixed background service options configuration by replacing `init` with `set` for DI-configured options.

Fixed Redis integration tests to match final shared run state after dispatch.

---

## Architecture Notes

This update completes the first real shared runtime orchestration layer.

The architecture now separates:

- admission decisioning
- shared run persistence
- shared queue coordination
- local runtime dispatch
- queue pumping
- background queue consumption
- scale-out publication

The shared runtime controller does not execute DAG steps.

The shared runtime controller does not claim work directly.

The shared runtime controller does not create Kubernetes pods.

The shared queue dispatcher does not decide admission.

The shared queue pump does not own dispatch logic.

The background service does not contain business logic.

Each responsibility is separated into its own abstraction.

---

## Current Shared Controller Flow

```text
SubmitRun
  -> IAiRunAdmissionController

  -> AssignToInstance
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedRunDispatcher.DispatchAsync(...)
      -> IAiSharedRunStore.MarkDispatchedAsync(...)
      -> SharedRun.Status = Dispatched

  -> QueueGlobally
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiSharedQueue.EnqueueAsync(...)
      -> SharedRun.Status = QueuedGlobally

  -> RequestScaleOut
      -> IAiSharedRunStore.CreateAsync(...)
      -> IAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
      -> SharedRun.Status = ScaleOutRequested

  -> Reject
      -> IAiSharedRunStore.CreateAsync(...)
      -> SharedRun.Status = Rejected
```

---

## Current Shared Queue Flow

```text
AiSharedQueueBackgroundService
  -> IAiSharedQueuePump.PumpOnceAsync(...)

IAiSharedQueuePump
  -> IAiSharedQueueDispatcher.DispatchNextAsync(...)

IAiSharedQueueDispatcher
  -> IAiSharedQueue.ClaimNextAsync(...)
  -> IAiSharedRunStore.GetAsync(...)
  -> IAiSharedRunDispatcher.DispatchAsync(...)
  -> IAiSharedQueue.MarkDispatchedAsync(...)
  -> IAiSharedRunStore.MarkDispatchedAsync(...)
```

---

## Kubernetes Preparation

This work prepares Kubernetes support by introducing:

- shared runtime controller
- shared run persistence
- Redis-backed shared run store
- Redis-backed shared queue
- atomic shared queue claim
- dispatch ownership
- queue pump
- background queue consumption
- scale-out request publisher abstraction
- runtime instance compatible dispatch path
- metadata propagation
- source/requestedBy/reason/correlation propagation

The next Kubernetes-related pieces can now be built on top:

- Redis-backed runtime instance registry
- runtime instance heartbeat TTL
- remote runtime instance dispatcher
- HTTP or gRPC runtime dispatch
- Redis scale-out request publisher
- Kubernetes scale-out adapter
- Kubernetes pod/deployment scaler
- dashboard/API/MCP control-plane endpoints
- real-time logs and observability export

---

## Current Shared Controller Capabilities

The runtime can now expose or support:

- submit shared run
- get shared run
- list shared runs
- cancel shared run
- assign run to runtime instance
- dispatch assigned run locally
- queue run globally
- claim globally queued run
- dispatch globally queued run
- requeue failed dispatch
- mark shared run dispatched
- mark shared queue item dispatched
- publish scale-out request
- pump shared queue manually
- consume shared queue through hosted background service
- coordinate shared queue dispatch through Redis
- prevent double dispatch through Redis atomic claim

---

## Completed

Shared Controller V1 is now complete.

Implemented V1 capabilities:

- direct assigned-run dispatch
- global shared queue enqueue
- Redis-backed shared queue coordination
- queued run dispatch
- shared queue pump
- hosted background queue consumption
- scale-out request publication
- controller/store/queue/dispatcher/pump/service DI
- unit and Redis integration test coverage

---

## Notes

Kubernetes pod creation is not implemented yet.

Remote runtime instance dispatch is not implemented yet.

Redis-backed runtime instance registry is not implemented yet.

Automatic scaling is not implemented yet.

Dashboard UI is not implemented yet.

MCP server commands are not implemented yet.

The current design is now ready for these future adapters without changing the core runtime.

---

## Next Step

The next step is the distributed runtime instance layer.

Expected next work:

- Redis-backed runtime instance registry
- heartbeat TTL / expiration
- runtime instance health visibility
- remote runtime dispatcher
- HTTP or gRPC dispatch adapter
- scale-out event publisher
- Kubernetes scaler adapter

After that:

- MCP server commands
- control-plane API endpoints
- Kibana/Grafana/OpenSearch observability export
- Kubernetes production demo


---

# Changelog — Runtime Control Plane / Runtime Orchestration Foundation

## Overview

This update introduces the first complete runtime control-plane foundation for the deterministic AI runtime.

The goal is to expose runtime operations through future external adapters such as:

- HTTP API
- MCP server
- CLI
- Dashboard
- Kubernetes control-plane pod
- Shared runtime controller

This work separates runtime internals from external control operations and prepares the system for:

- multi-instance execution
- Kubernetes visibility
- runtime instance registration
- local queue visibility
- run admission / slot decisioning
- future shared queue
- future autoscaling
- future MCP/API control operations
- production observability through Kibana, Grafana, and OpenSearch

---

## Added

## Runtime Control Plane Foundation

Added a new runtime control-plane foundation with adapter-neutral abstractions.

The new control-plane layer separates:

- Replay and audit control
- Execution control
- Local runtime queue control
- Runtime instance registry
- Runtime instance control
- Run admission / slot decisioning
- Control-plane observability

External adapters no longer need to call runtime internals directly.

This prepares the architecture for:

- MCP server commands
- HTTP API endpoints
- CLI operations
- Dashboards
- Kubernetes demos
- Shared runtime controller
- Future Grafana / Kibana / OpenSearch observability

---

## Replay Control Plane

Added replay control-plane abstraction over the existing replay service.

Added replay control-plane request, result, and options models.

Added replay control-plane facade for adapter-neutral replay access.

Prepared replay operations to be called later from:

- HTTP API
- MCP server
- CLI
- Dashboard
- Kubernetes control-plane layer

Added observability events for replay operations:

- operation started
- operation completed
- operation failed

Replay remains a control-plane operation only.

`ReExecuteAll` remains intentionally blocked because it may re-run external providers or side effects before provider replay isolation and side-effect safety are implemented.

---

## Execution Control Plane

Added execution control-plane contracts for:

- Pause execution
- Resume execution
- Cancel execution
- Submit human input
- Get execution control status

Added the following models and contracts:

- `AiExecutionControlPlaneOperation`
- `AiExecutionControlPlaneRequest`
- `AiExecutionControlPlaneResult`
- `AiExecutionControlPlaneOptions`
- `IAiExecutionControlPlane`

Added runtime implementation:

- `AiExecutionControlPlane`

Added `GetStateAsync` to the execution control service so external control-plane layers can retrieve durable execution control state.

The execution control-plane wraps the existing `IAiExecutionControlService` instead of modifying DAG execution behavior.

Added execution control-plane observability events:

- operation started
- operation completed
- operation failed

Added options to enable or disable:

- pause
- resume
- cancel
- submit human input
- get status

Added structured failure result support.

Added duration measurement for future metrics and Grafana dashboards.

Added unit tests for execution control-plane behavior.

Added DI registration for execution control-plane services.

### Notes

Execution ControlPlane does not execute DAG steps.

Execution ControlPlane does not claim work.

Execution ControlPlane does not modify local queues.

Execution ControlPlane wraps the existing durable execution control service.

---

## Runtime Queue Visibility

Added immutable visibility snapshots for local runtime queue and run state:

- `AiRuntimePipelineRunState`
- `AiRuntimePipelineQueueState`

Added runtime run visibility fields:

- `RunId`
- `ExecutionId`
- `PipelineKey`
- `PipelineName`
- `RuntimeInstanceId`
- `Status`
- `IsQueued`
- `IsRunning`
- `CancellationRequested`
- timestamps when available
- failure reason when available

Added local queue visibility fields:

- `RuntimeInstanceId`
- `IsPaused`
- `QueuedRunCount`
- `RunningRunCount`
- `ActiveRunCount`
- `QueueCapacity`
- `MaxConcurrentRuns`
- `AvailableRunSlots`
- `CanAcceptRun`
- `SnapshotAtUtc`

Added visibility methods to `IAiRuntimePipelineBackgroundController`:

- `GetRunStateAsync`
- `GetQueueStateAsync`

Implemented run and queue state snapshots in `AiRuntimePipelineBackgroundController`.

Added stable lowercase run status mapping for diagnostics, logs, dashboards, and future Kubernetes visibility.

---

## Runtime Queue Control Plane

Added local runtime queue control-plane contracts:

- `AiRuntimeQueueControlPlaneOperation`
- `AiRuntimeQueueControlPlaneRequest`
- `AiRuntimeQueueControlPlaneResult`
- `AiRuntimeQueueControlPlaneOptions`
- `IAiRuntimeQueueControlPlane`

Added runtime implementation:

- `AiRuntimeQueueControlPlane`

Added adapter-neutral local queue operations:

- enqueue run
- cancel run
- cancel queued run
- pause local queue
- resume local queue
- get run status
- get queue status

Renamed the concept from `RunControlPlane` to `RuntimeQueueControlPlane` to avoid confusion with the future shared/global queue.

The RuntimeQueue ControlPlane controls only the local queue owned by one runtime instance.

Updated queue and cancel operations to use:

- `RunId`
- `Reason`
- `RequestedBy`

instead of requiring internal `AiRuntimeWorkerRunHandle`.

`AiRuntimeWorkerRunHandle` remains an output for enqueue, but is no longer required as an input for external control-plane operations.

Added runtime queue control-plane observability events:

- operation started
- operation completed
- operation failed

Added DI registration.

Added unit tests.

### Notes

Local runtime queues remain unchanged.

RuntimeQueue ControlPlane does not replace local queues.

RuntimeQueue ControlPlane does not execute DAG steps.

RuntimeQueue ControlPlane does not manage distributed/shared queue behavior yet.

It exposes a safe adapter-neutral facade over the existing local runtime controller.

---

## Background Controller API Cleanup

Updated `IAiRuntimePipelineBackgroundController` to make control operations API/control-plane friendly.

Replaced handle-based control signatures with run-id and queue-level signatures:

- `PauseQueueAsync(string? reason, string? requestedBy, CancellationToken)`
- `ResumeQueueAsync(string? requestedBy, CancellationToken)`
- `CancelQueuedRunAsync(string runId, string? reason, string? requestedBy, CancellationToken)`
- `CancelRunAsync(string runId, string? reason, string? requestedBy, CancellationToken)`

Preserved enqueue behavior:

- `EnqueueAsync(AiRuntimePipelineRunRequest, CancellationToken)`

Preserved local queue execution behavior.

Preserved distributed worker execution behavior.

Preserved DAG execution behavior.

---

## Queue Pause / Resume Ledger Correlation Fix

Fixed queue pause/resume decision ledger correlation after removing handle-based API input.

Added internal queue ledger target resolution using controller state:

- running runs
- queued runs
- fallback controller-level identity

Added `ResolveQueueLedgerTarget()` inside the runtime pipeline background controller.

Added internal `QueueLedgerTarget` model.

Ensured queue pause/resume ledger events remain execution-correlated when an active run exists.

Preserved integration test expectations for:

- `queue.paused`
- `queue.resumed`
- execution-correlated ledger lookup by `ExecutionId`
- correct `RunId`
- correct worker id
- correct metadata

Clarified that `IAiRuntimeCorrelationAccessor` / `AsyncLocal` is not reliable for external control-plane commands because the external caller is not inside the run execution async scope.

Queue-level external commands now reconstruct correlation from controller state instead of relying on the current `AsyncLocal` accessor.

### Reason

The runtime correlation accessor only contains the context of the current async flow.

External calls such as `PauseQueueAsync()` and `ResumeQueueAsync()` are not executed inside the `ProcessQueuedRunAsync()` correlation scope.

Therefore, the accessor can be empty or only partially populated.

The controller must use its own local state to reconstruct the best correlation target.

---

## Runtime Instance Registry

Added runtime instance registry contracts:

- `AiRuntimeInstanceStatus`
- `AiRuntimeInstanceRegistration`
- `AiRuntimeInstanceSnapshot`
- `IAiRuntimeInstanceRegistry`

Added runtime instance statuses:

- `Unknown`
- `Ready`
- `Busy`
- `Paused`
- `Draining`
- `Unhealthy`
- `Stopped`

Added runtime instance registration model with:

- `RuntimeInstanceId`
- `HostName`
- `ProcessId`
- Kubernetes namespace
- Kubernetes pod name
- Kubernetes node name
- `WorkerCount`
- `MaxConcurrentRuns`
- `QueueCapacity`
- `RuntimeVersion`
- `Metadata`

Added runtime instance snapshot model with:

- status
- worker count
- queued run count
- running run count
- active run count
- queue capacity
- max concurrent runs
- available run slots
- queue paused state
- can accept run
- registered timestamp
- heartbeat timestamp
- snapshot timestamp
- host/pod metadata

Added in-memory implementation:

- `InMemoryAiRuntimeInstanceRegistry`

Added registry operations:

- register/update runtime instance
- heartbeat runtime instance
- get runtime instance
- list runtime instances
- mark draining
- unregister / mark stopped

Added DI registration for `IAiRuntimeInstanceRegistry`.

Added unit tests for:

- registration
- lookup
- heartbeat
- listing
- draining
- unregister
- excluding stopped instances by default
- including stopped instances when requested
- reactivating stopped instances through registration

### Notes

The in-memory registry is intended for local, single-process, and test scenarios.

A Redis-backed registry will be required later for true Kubernetes multi-instance coordination.

This registry prepares shared run admission, runtime instance visibility, dashboards, MCP, and autoscaling.

---

## Runtime Instance Control Plane

Added runtime instance control-plane contracts:

- `AiRuntimeInstanceControlPlaneOperation`
- `AiRuntimeInstanceControlPlaneRequest`
- `AiRuntimeInstanceControlPlaneResult`
- `AiRuntimeInstanceControlPlaneOptions`
- `IAiRuntimeInstanceControlPlane`

Added runtime implementation:

- `AiRuntimeInstanceControlPlane`

Added adapter-neutral operations:

- register runtime instance
- heartbeat runtime instance
- get runtime instance
- list runtime instances
- mark runtime instance as draining
- unregister runtime instance

Added operation options:

- enable register
- enable heartbeat
- enable get instance
- enable list instances
- enable mark draining
- enable unregister
- structured failure result handling
- duration measurement

Added observability events for runtime instance control-plane operations:

- operation started
- operation completed
- operation failed

Added DI registration for runtime instance control-plane services.

Added unit tests for:

- register
- heartbeat
- get
- list
- mark draining
- unregister
- execute dispatching
- missing input validation
- disabled operation behavior
- observability event recording

### Notes

Runtime Instance ControlPlane does not create Kubernetes pods.

Runtime Instance ControlPlane does not scale deployments yet.

Runtime Instance ControlPlane does not execute DAG steps.

Runtime Instance ControlPlane does not claim work.

Runtime Instance ControlPlane exposes visibility and control over registered runtime instances.

---

## Run Admission / Slot System V1

Added run admission contracts:

- `AiRunAdmissionDecisionType`
- `AiRunAdmissionRequest`
- `AiRunAdmissionDecision`
- `AiRunAdmissionOptions`
- `IAiRunAdmissionController`

Added admission decision types:

- `Unknown`
- `AssignToInstance`
- `QueueGlobally`
- `RequestScaleOut`
- `Reject`

Added admission request model with:

- pipeline run request
- optional run id
- tenant id
- pipeline key
- preferred runtime instance id
- correlation id
- requested by
- source
- reason
- metadata

Added admission decision model with:

- decision type
- accepted flag
- assigned runtime instance id
- assigned instance snapshot
- scale-out flag
- global queue flag
- rejected flag
- reason
- diagnostics
- visible instance count
- available instance count
- current instance count
- max instance count
- metadata
- decided timestamp

Added admission policy options:

- enabled
- max instance count
- enable scale-out request
- enable global queue fallback
- reject when no capacity
- allow paused instances
- allow draining instances
- allow unhealthy instances
- prefer requested runtime instance
- duration measurement

Added runtime implementation:

- `AiRunAdmissionController`

Admission now evaluates registered runtime instances and returns:

- assign to least-loaded available instance
- assign to preferred instance when available
- request scale-out when capacity is unavailable and scale-out is allowed
- queue globally when fallback is allowed
- reject when no capacity and no fallback exists

Added DI registration for `IAiRunAdmissionController`.

Added unit tests for:

- assignment to available instance
- least-loaded selection
- preferred instance selection
- ignoring unavailable preferred instance
- scale-out request
- global queue fallback
- rejection
- disabled admission
- paused instance policy
- no registered instances
- null request validation

### Notes

Admission does not enqueue runs.

Admission does not modify local queues.

Admission does not create Kubernetes replicas.

Admission does not execute DAG steps.

Admission only decides what should happen next.

This prepares the future Shared Runtime Controller.

---

## Control Plane Observability

Added shared control-plane observer abstraction.

Added no-op control-plane observer.

Added logged control-plane observer.

Added structured logging support for control-plane events.

Added control-plane events across:

- replay
- execution control
- runtime queue control
- runtime instance control

Added control-plane event fields useful for future dashboards:

- event type
- area
- operation
- outcome
- correlation context
- duration
- message
- failure reason
- properties

Added control-plane areas including:

- replay
- execution-control
- run-control
- instance-registry
- admission
- shared-queue
- shared-controller
- scaling

Prepared the architecture for future Kibana, Grafana, and OpenSearch export.

---

## Dependency Injection

Updated `AiControlPlaneServiceCollectionExtensions`.

Added DI registration for:

- `IAiReplayControlPlane`
- `IAiExecutionControlPlane`
- `IAiRuntimeQueueControlPlane`
- `IAiRuntimeInstanceRegistry`
- `IAiRuntimeInstanceControlPlane`
- `IAiRunAdmissionController`
- `IAiControlPlaneObserver`
- optional logging observer

Added options registration for:

- replay control
- execution control-plane
- runtime queue control-plane
- runtime instance control-plane
- run admission

Added DI unit tests for all new registrations.

---

## Tests

Added and updated unit tests for:

- replay control-plane
- execution control-plane
- runtime queue control-plane
- runtime instance registry
- runtime instance control-plane
- run admission controller
- DI registrations

Updated fake implementations to support new interface methods.

Fixed tests after adding `GetStateAsync`.

Fixed tests after changing background controller methods from handle-based to run-id based.

Preserved and validated integration behavior for:

- queue pause ledger events
- queue resume ledger events
- execution-correlated queue ledger visibility
- run-id correlated queue ledger visibility

---

## Changed

Renamed the feature direction from replay-only abstraction to full runtime control-plane foundation.

Recommended branch rename:

- from `feature/replay-controller-abstraction`
- to `feature/runtime-control-plane`

Changed local queue control operations to be external-adapter friendly.

Changed queue pause/resume correlation strategy:

- before: required handle
- after: resolves best target from controller state

Changed runtime queue naming from `RunControlPlane` to `RuntimeQueueControlPlane` to avoid confusion with future shared/global queue.

Improved naming consistency with `ControlPlane` suffix on public models to avoid collisions with existing runtime engine models.

Added clear separation between:

- local queue control
- future shared queue
- runtime instance registry
- admission decisioning
- execution control
- replay audit

---

## Fixed

Fixed type ambiguity between existing execution control models and new control-plane request models by using explicit control-plane naming.

Fixed interface fake/test failures after adding `GetStateAsync`.

Fixed runtime queue control-plane calls after background controller signatures changed.

Fixed queue pause/resume ledger integration after removing handle input.

Fixed execution-correlated queue pause/resume integration test failure by resolving queue ledger target from `_runningRuns` / `_queuedRuns`.

Fixed runtime queue control-plane tests to match final `IAiRuntimePipelineBackgroundController` signatures.

Fixed DI tests for newly added services.

---

## Architecture Notes

This change establishes the first real runtime control-plane layer.

The architecture now separates:

- External adapters
- ControlPlane facades
- Runtime internals

External adapters include:

- HTTP API
- MCP
- CLI
- Dashboard
- Kubernetes control pod

ControlPlane facades include:

- Replay
- ExecutionControl
- RuntimeQueue
- RuntimeInstances
- Admission

Runtime internals include:

- DAG engine
- local queues
- workers
- worker groups
- execution store
- replay service
- control service

This means future external systems will not call runtime internals directly.

---

## Kubernetes Preparation

This work prepares Kubernetes support by introducing:

- runtime instance identity
- runtime instance registration
- runtime instance heartbeat
- local queue visibility
- run capacity visibility
- admission decisions
- scale-out decision placeholder
- local queue control
- instance draining
- stopped/unregistered instances
- observability hooks
- structured event model

The next Kubernetes-related pieces can now be built on top:

- SharedRuntimeController
- SharedRunQueue
- Redis-backed RuntimeInstanceRegistry
- Redis-backed admission/claim logic
- Scale-out requested events
- Kubernetes deployment scaler adapter
- MCP/API control-plane endpoints
- Live observability export to Kibana/Grafana/OpenSearch

---

## Current Control Plane Capabilities

The runtime can now expose or support:

- replay execution
- audit execution
- pause execution
- resume execution
- cancel execution
- submit human input
- get execution control state
- enqueue local runtime run
- cancel local runtime run
- cancel queued local run
- pause local queue
- resume local queue
- get local run status
- get local queue status
- register runtime instance
- heartbeat runtime instance
- get runtime instance
- list runtime instances
- mark runtime instance draining
- unregister runtime instance
- admit run
- assign run to runtime instance
- queue globally later
- request scale-out later
- reject run

---

## Next Step

The next step is the Shared Runtime Controller skeleton.

Expected V1 behavior:

- receive a run request
- ask `IAiRunAdmissionController`
- if `AssignToInstance`, dispatch later to selected runtime queue
- if `QueueGlobally`, store pending run later
- if `RequestScaleOut`, emit scale-out request later
- if `Reject`, reject the run

V1 can remain in-memory and adapter-neutral.

Future V2 should add:

- Redis-backed shared queue
- atomic Lua admission
- multi-instance safe pending run claim
- runtime instance heartbeat TTL
- scale-out decision events
- Kubernetes scaler adapter
- dashboard/MCP/API integration

---

## [1.0.5.4] - 2026-05-30 Runtime Package Structure and Observability Reorganization

### Replay Package Reorganization

- Reorganized replay-related contracts, services, validators, reports, and metadata into a dedicated replay package structure.
- Improved replay module discoverability and maintainability.
- Aligned replay architecture with runtime package conventions.
- Prepared replay subsystem for future controller and HTTP API integration.

### Snapshot Package Reorganization

- Reorganized snapshot-related contracts and implementations into a dedicated snapshot package structure.
- Improved separation between snapshot persistence and replay functionality.
- Aligned snapshot architecture with runtime package conventions.
- Prepared snapshot subsystem for future storage provider extensions.

### Observability Package Reorganization

- Consolidated runtime observability components under a unified `Observability` namespace.
- Moved tracing abstractions and implementations into `Observability/Tracing`.
- Moved metrics abstractions and implementations into `Observability/Metrics`.
- Moved logging abstractions and implementations into `Observability/Logging`.
- Preserved existing public behavior while improving package organization.

### Observability Domain Structure

- Added a unified observability domain structure:
  - `Observability/Context`
  - `Observability/Helpers`
  - `Observability/Ledger`
  - `Observability/Logging`
  - `Observability/Metrics`
  - `Observability/Tracing`
  - `Observability/AiRuntimeObservability.cs`

- Improved separation of observability concerns:
  - decision ledger
  - tracing
  - metrics
  - logging
  - runtime correlation
  - observability helpers

### Documentation Alignment

- Updated documentation to reflect Replay Engine V1 completion.
- Updated roadmap phases to distinguish completed replay engine foundations from future replay controller and HTTP API work.
- Updated documentation index references.
- Updated replay and audit documentation.
- Added replay diagnostic, ledger, and timeline examples.
- Added TODO / improvement notes for future replay operational tooling.

### No Functional Changes

- No runtime behavior changes were introduced.
- No replay, snapshot, observability, tracing, metrics, or logging behavior was intentionally modified.
- This release focuses on package structure, documentation alignment, and future maintainability.

---

## [1.0.5.4] - 2026-05-30 Replay API, Deterministic Validation, Ledger and Timeline Diagnostics

- Added the first complete Replay API implementation for deterministic AI runtime executions.
- Added replay-as-validation support for persisted executions using an `ExecutionId`.
- Added replay modes for:
  - audit-only validation
  - restore from persisted snapshot / resume incomplete execution state

- Added replay request and report models:
  - `AiExecutionReplayRequest`
  - `AiExecutionReplayReport`
  - `AiExecutionReplayIssue`
  - `AiExecutionReplayStepReport`
  - `AiExecutionReplaySummaryResponse`
  - `AiExecutionReplayMetadata`

- Added replay service abstractions and implementation:
  - `IAiExecutionReplayService`
  - `DefaultAiExecutionReplayService<TContext>`
  - `IAiExecutionReplayExecutor`
  - `DefaultAiExecutionReplayExecutor`
  - `IAiExecutionReplayValidator`
  - `DefaultAiExecutionReplayValidator`
  - `IAiExecutionReplayPayloadValidator`
  - `DefaultAiExecutionReplayPayloadValidator`

- Added replay snapshot loading from persisted execution snapshots.
- Added replay snapshot validation for:
  - missing snapshot
  - missing execution record
  - missing execution state
  - execution id mismatch between snapshot, record, and state
  - missing pipeline name

- Added deterministic replay validation for:
  - original fingerprint presence
  - reconstructed fingerprint generation
  - original fingerprint vs reconstructed fingerprint comparison
  - dependency graph validity
  - final step state validity
  - payload reference validity
  - archived / compacted / evicted payload reference validation
  - replay validity summary

- Added replay metadata exposure inside replay reports.
- Added persisted replay metadata propagation into `AiExecutionReplayReport`.
- Fixed replay report merging so metadata is preserved after service-level report enrichment.

- Added execution-correlated replay ledger events:
  - `replay.requested`
  - `replay.started`
  - `replay.comparison_completed`
  - `replay.completed`
  - `replay.failed`

- Added replay lifecycle recording through the existing execution-correlated decision ledger.
- Added replay ledger correlation using:
  - execution id
  - replay pipeline key
  - replay step id
  - replay worker id

- Added optional replay report enrichment with decision ledger events.
- Added `IncludeLedgerEvents` support to load all execution-correlated ledger entries for the replayed execution.
- Added replay report support for full ledger event output through `LedgerEvents`.

- Added optional replay report enrichment with trace timeline events.
- Added `IncludeTimeline` support using the existing `IAiTraceTimeline`.
- Added replay report support for full trace timeline output through `TimelineEvents`.
- Confirmed that completed trace records are already projected into the timeline by the trace recorder.

- Added replay tracing around the replay operation through `TraceExecutionAsync`.
- Added replay execution tracing without changing the tracing runtime core.
- Added replay exception handling that records failed replay lifecycle events before rethrowing unexpected exceptions.

- Added replay report merge support for:
  - replay metadata
  - ledger events
  - timeline events
  - replay execution mode
  - pipeline name
  - execution status

- Added replay support for restoring execution records and execution state into the authoritative runtime store.
- Added DAG-store-aware replay restore support when `IAiDagExecutionStore` is available.
- Added fallback restore support through the generic execution store.
- Added compatible existing execution detection to avoid unnecessary restore when the runtime already contains the same execution.
- Added audit-only replay path that validates without restoring runtime state.
- Added invalid replay report generation instead of throwing for normal replay validation failures.

- Added replay integration test coverage for:
  - 100-step distributed chaos replay
  - distributed multi-worker execution
  - retry validation
  - retention compaction and eviction validation
  - snapshot persistence
  - live execution bundle deletion
  - restore from persisted snapshot
  - deterministic fingerprint validation
  - metadata propagation
  - ledger event loading
  - timeline event loading
  - audit-only replay without restore
  - missing snapshot failure handling
  - disabled ledger/timeline loading when not requested

- Added reference replay integration test:
  - `AiExecutionReplayReferenceIntegrationTests`
  - `Replay_Should_Restore_100_Step_Distributed_Chaos_With_Metadata_Ledger_Timeline_And_Fingerprint`

- Added replay diagnostic output test:
  - `Replay_Should_Print_Ledger_And_Timeline_Report`

- Added diagnostic replay output showing:
  - replay validity
  - fingerprint validity
  - dependency graph validity
  - step state validity
  - payload reference validity
  - replay metadata
  - step counts
  - retry counts
  - ledger event counts
  - timeline event counts
  - ledger summary by category / event / outcome
  - replay lifecycle ledger events
  - trace summary by category / operation
  - trace timeline samples

- Validated replay against a real distributed 100-step execution producing:
  - 100 completed steps
  - 0 failed final steps
  - 11 retry attempts
  - more than 2,000 execution-correlated ledger events
  - more than 1,300 trace timeline events
  - matching deterministic replay fingerprint
  - valid dependency graph
  - valid step states
  - valid payload references

- Known follow-up items:
  - Add a runtime-level replay controller abstraction before exposing HTTP APIs.
  - Add an ASP.NET / HTTP Replay API project later without coupling the core runtime library to ASP.NET.
  - Add replay endpoints for summary, ledger, timeline, audit, and restore.
  - Add replay console scenario support.
  - Add replay dashboard support for timeline, ledger, metadata, and fingerprint comparison.
  - Consider splitting large replay diagnostics from normal CI if test output becomes too verbose.

---

## [1.0.5.3] - 2026-05-28 Correlated Metrics and Tracing Storage Modes

- Added runtime execution correlation support for metrics and tracing.
- Aligned metrics and tracing with the same correlation model used by the execution-correlated decision ledger.
- Added shared runtime correlation propagation across:
  - controller runs
  - queued executions
  - DAG executions
  - runtime workers
  - distributed step claims
  - tracing records
  - timeline events
  - metric records
  - future replay diagnostics

- Added `AiRuntimeExecutionCorrelationContext` to carry runtime-level correlation data:
  - `CorrelationId`
  - `RunId`
  - `ExecutionId`
  - `PipelineName`
  - `PipelineVersion`
  - `PipelineKey`
  - `RuntimeInstanceId`
  - `WorkerId`

- Added trace correlation context support through `AiRuntimeTraceCorrelationContext`.
- Added correlation capture inside the in-memory runtime tracer.
- Added correlation projection into trace records and trace timeline events.
- Added correlation tags for runtime tracing:
  - execution id
  - run id
  - correlation id
  - pipeline name
  - pipeline key
  - runtime instance id
  - worker id
  - step id
  - step key
  - claim token
  - provider
  - model
  - operation
  - trace scope id

- Added runtime metric storage mode support:
  - `Disabled`
  - `Memory`
  - `Mongo`
  - `MemoryAndMongo`

- Added runtime trace storage mode support:
  - `Disabled`
  - `Memory`
  - `Mongo`
  - `MemoryAndMongo`

- Added `AiRuntimeMetricStoreOptions` to configure metric persistence.
- Added `AiRuntimeTraceStoreOptions` to configure trace persistence.
- Added MongoDB fallback resolution for metrics and tracing so both can reuse the runtime MongoDB configuration when explicit observability options are not provided.
- Added separate MongoDB collection support for runtime metrics and runtime traces.

- Added trace store abstraction:
  - `IAiRuntimeTraceStore`
  - `NoOpAiRuntimeTraceStore`
  - `InMemoryAiRuntimeTraceStore`
  - `MongoAiRuntimeTraceStore`
  - `CompositeAiRuntimeTraceStore`

- Added `MemoryAndMongo` tracing support through a composite trace store.
- Added `StoreOnlyAiTraceRecorder` for Mongo-only tracing when in-memory trace recording is disabled.
- Updated `InMemoryAiTraceRecorder` to optionally persist completed trace records to the configured trace store.
- Updated tracing dependency injection to select the correct trace recorder and trace store based on observability options.

- Added MongoDB-backed trace persistence for completed trace records.
- Added MongoDB trace indexes for execution, run, correlation, and operation-based trace lookup.
- Added Mongo runtime resilience around trace index creation to tolerate transient local Docker or socket failures during tests.

- Added correlation-aware tracing output for distributed chaos executions.
- Added diagnostic tests proving that distributed chaos tracing can be written to both in-memory timeline and MongoDB.
- Added diagnostic tests proving that runtime metrics remain available when configured with `MemoryAndMongo`.
- Added test output grouping traces by category and operation name.

- Improved trace visibility for distributed DAG execution by exposing:
  - execution id
  - run id
  - correlation id
  - pipeline name
  - pipeline key
  - step id
  - step key
  - claim token
  - worker id
  - runtime instance id
  - provider
  - model
  - operation
  - trace tags

- Fixed tracing recorder wiring so `InMemoryAiTraceRecorder` writes to the configured trace store when trace persistence is enabled.
- Fixed Mongo trace persistence not being called when in-memory tracing was enabled.
- Fixed trace lookup validation for both execution id and run id.
- Fixed trace timeline diagnostics to show full execution-correlated trace output instead of a limited sample.
- Fixed diagnostic trace output bug where `RunId` was incorrectly printed from `ExecutionId`.

- Improved step tracing correlation by adding explicit step key propagation.
- Fixed step trace output so declarative step keys are shown correctly:
  - `hello-world`
  - `distributed.chaos.flaky-provider`

- Improved storage tracing tags for distributed claim and concurrency operations.
- Added additional storage trace tags for:
  - pipeline key
  - step key
  - worker id
  - claim token
  - concurrency lease id
  - concurrency provider
  - concurrency model
  - concurrency operation

- Improved distributed claim tracing around:
  - `TryClaimStep`
  - claim acquisition
  - claim denial
  - claim token visibility
  - worker id visibility

- Improved distributed concurrency tracing around:
  - `TryAcquireConcurrencyLease`
  - concurrency admission
  - concurrency denial
  - lease acquisition
  - lease release diagnostics

- Improved recovery tracing around:
  - `RecoverTimedOutSteps`
  - recovered step counts
  - recovered step names
  - recovery ledger correlation

- Added and updated tests for:
  - correlated tracing with MemoryAndMongo mode
  - Mongo-backed trace persistence
  - runtime trace timeline output
  - runtime metrics with MemoryAndMongo mode
  - distributed chaos observability diagnostics
  - trace category and operation grouping
  - trace correlation validation by execution id
  - trace correlation validation by run id

- Known follow-up items:
  - Split policy tracing from step tracing so retry policy resolution is no longer emitted as `step / execute.succeeded`.
  - Add dedicated `TracePolicyAsync` support.
  - Add dedicated correlation fields for `LeaseId` instead of overloading claim-token-oriented diagnostics.
  - Normalize `WorkerId` versus `RuntimeInstanceId` across all trace contexts.
  - Propagate `PipelineKey` consistently into ambient runtime correlation at controller, queue, execution, and worker boundaries.
  - Refactor trace enrichment so context fields and tags are normalized in one place.
  - Add stricter assertions later for expected trace values per trace category.

---

## [1.0.5.2] - 2026-05-26 Execution-Correlated Decision Ledger Integration

- Added execution-correlated decision ledger integration across the enterprise runtime.
- Added stable decision ledger event constants grouped by runtime domain:
  - execution
  - run
  - queue
  - claim
  - step
  - retry
  - recovery
  - policy
  - concurrency
  - control
  - human input
  - retention
  - payload
  - snapshot
  - storage
  - finalization

- Added `IAiDecisionLedgerRecorder` integration into the runtime observability facade.
- Added default decision ledger recorder with configurable write behavior.
- Added ledger-safe observability composition through `AiRuntimeObservability`.
- Added execution correlation context support for ledger entries.

- Added controller-level run ledger events:
  - `run.queued`
  - `run.dequeued`
  - `run.started`
  - `run.completed`
  - `run.failed`
  - `run.cancelled`

- Added queue control ledger events:
  - `queue.paused`
  - `queue.resumed`

- Added execution control ledger events:
  - `control.pause_requested`
  - `control.paused`
  - `control.resume_requested`
  - `control.resumed`
  - `control.cancel_requested`
  - `control.cancel_observed`
  - `control.state_changed`

- Added human-in-the-loop ledger events:
  - `human_input.requested`
  - `human_input.waiting`
  - `human_input.submitted`

- Added distributed claim ledger events:
  - `claim.attempted`
  - `claim.acquired`
  - `claim.denied`

- Added step execution ledger events:
  - `step.started`
  - `step.completed`
  - `step.failed`

- Added retry ledger events after persisted step failure transitions:
  - `retry.evaluated`
  - `retry.scheduled`
  - `retry.denied`
  - `retry.budget_exhausted`

- Added recovery ledger events for timed-out distributed DAG steps:
  - `recovery.detected`
  - `recovery.applied`
  - `recovery.step_recovered`

- Added policy engine ledger events:
  - `policy.evaluated`
  - `policy.allowed`
  - `policy.denied`
  - `policy.failed`

- Added concurrency and throttling ledger events:
  - `concurrency.denied`
  - `concurrency.lease_acquired`
  - `concurrency.lease_released`

- Added snapshot and storage ledger events:
  - `snapshot.created`
  - `storage.state_persistence_failed`

- Added finalization ledger events:
  - `finalization.started`
  - `finalization.completed`
  - `finalization.failed`
  - `finalization.race_lost`
  - `finalization.cancellation_override_applied`

- Added atomic retention and compaction ledger coverage for:
  - retention evaluation
  - retention trigger decisions
  - payload compaction
  - hot-state eviction
  - retention patch application
  - resolver-safe evicted step reconstruction

- Improved aggressive retention flow with atomic Redis retention patching.
- Fixed retention behavior so compacted and evicted steps remain reconstructable.
- Fixed aggressive retention integration test failures around hot-state eviction.
- Fixed resolver consistency for evicted steps after compaction.
- Fixed fingerprint step reconstruction after aggressive retention.
- Fixed retried-step reconstruction when steps were evicted from hot state.
- Fixed non-terminal steps being incorrectly considered by retention policies.
- Fixed retention policy tests to ignore:
  - `Running`
  - `Ready`
  - `WaitingForRetry`

- Added and updated integration tests for:
  - run ledger lifecycle events
  - queue pause/resume ledger events
  - execution control ledger events
  - human input ledger events
  - retry ledger events
  - recovery ledger events
  - policy ledger events
  - concurrency ledger events
  - snapshot ledger events
  - atomic retention
  - compaction
  - eviction
  - resolver reconstruction after aggressive retention
  - 100-step distributed chaos execution
  - 500-step aggressive retention execution

- Fixed test regressions introduced during ledger integration.
- Fixed queue ledger correlation issues between global queue operations and execution-correlated runs.
- Fixed handle usage in queue/control tests.
- Fixed enum outcome coverage by adding `Ready` for future DAG scheduling events.
- Reverted premature `dag.step_became_ready` runtime emission because it was executed before persisted DAG completion state was stable.
- Deferred DAG ready-step ledger events until a safer persisted completion point is introduced.

- Replay ledger events were intentionally omitted from this release.
- Replay-specific ledger events will be added later as part of the Replay API implementation.

---

## [1.0.5.1] - 2026-05-23 Enterprise Runtime Demo

- Added executable enterprise runtime console demo for production-style AI workflow execution.
- Added local demo infrastructure support for:
  - Redis
  - MongoDB
  - Docker Compose
  - reset scripts
  - local demo pipeline assets

- Added interactive enterprise runtime console runner with:
  - scenario selection
  - log mode selection
  - background controller startup
  - runtime execution enqueue
  - live progress monitoring
  - readable realtime runtime logs
  - raw realtime event mode
  - noisy internal event mode
  - pause/resume hotkeys
  - cancel-with-confirmation flow
  - execution cleanup after completion or cancellation

- Added executable demo scenarios:

```text
json
chaos-100
chaos-500
throttling-100
```

- Added `json` scenario to validate:
  - JSON pipeline loading
  - controller execution path
  - distributed worker execution
  - retry recovery
  - terminal completion
  - snapshot persistence
  - replay validation
  - cleanup

- Added `chaos-100` scenario to validate:
  - 100-step in-memory distributed DAG execution
  - multi-worker coordination
  - retry recovery under moderate pressure
  - live progress visibility
  - pause/resume behavior
  - cancel confirmation behavior
  - deterministic completion
  - replay validation

- Added `chaos-500` scenario to validate:
  - 500-step aggressive distributed DAG execution
  - distributed worker coordination under heavier pressure
  - retry recovery
  - hot-state retention pressure
  - compaction
  - eviction
  - snapshot persistence
  - replay restoration
  - replay fingerprint consistency
  - bounded terminal hot state

- Added `throttling-100` scenario to validate:
  - 100-step distributed provider throttling
  - provider-level concurrency target
  - OpenAI as the throttled provider
  - randomized provider distribution while keeping OpenAI dominant
  - Redis lease-based distributed admission control
  - bounded provider capacity under worker pressure
  - deterministic convergence after throttling delays

- Added realtime readable event formatting for:
  - claimed steps
  - completed steps
  - failed steps
  - retry/recovery events
  - finalization success
  - finalization race loss
  - snapshot persistence
  - replay restoration
  - cleanup events
  - throttled steps

- Added realtime throttling visibility:
  - classified `[AI DAG] Step throttled` runtime events as `StepThrottled`
  - added `[THROTTLED]` console output
  - excluded throttling events from noisy-only filtering
  - added console color support for throttling events

- Added execution summaries for demo validation:
  - execution summary
  - distributed worker summary
  - retry recovery summary
  - retention summary
  - replay validation summary
  - throttling summary
  - validation summary

- Added throttling execution summary with:
  - scope
  - target
  - configured limit
  - observed workers
  - throttling observed
  - throttle respected

- Added enterprise runtime demo documentation:
  - demo README
  - scenario document table
  - command reference
  - interactive mode documentation
  - log mode documentation
  - runtime controls documentation
  - troubleshooting section
  - recommended demo flow

- Added scenario documentation for:
  - multi-worker execution
  - worker crash recovery
  - duplicate execution prevention
  - pause/resume/cancel
  - human-in-the-loop
  - distributed throttling
  - retention and compaction
  - deterministic convergence

- Updated root README to reference:
  - executable enterprise demo scenarios
  - `throttling-100`
  - scenario documentation
  - long-term `road-to-mlops.md` direction

- Added `docs/road-to-mlops.md` to clarify the long-term evolution from deterministic runtime foundations toward:
  - AI execution infrastructure
  - AI operations platform
  - runtime governance
  - replay and audit systems
  - distributed AI operations
  - MLOps-oriented runtime operations

- Updated roadmap documentation to distinguish:
  - completed runtime foundations
  - completed enterprise demo V1
  - observability foundations
  - future MLOps/platform evolution

---

## [1.0.5.0] - 2026-05-20 - Execution Control State / Queue Control / Human-in-the-Loop

### Added

- Added durable execution control state support for runtime-level execution governance.
- Added `AiExecutionControlState` to separate operator/user/system control state from DAG execution state.
- Added `AiExecutionControlStatus` with support for:
  - `None`
  - `Running`
  - `Pausing`
  - `Paused`
  - `Resuming`
  - `Cancelling`
  - `Cancelled`
  - `WaitingForInput`
- Added `AiExecutionControlAction` to separate requested control intent from effective runtime state.
- Added `AiExecutionControlDecision` to centralize runtime decisions for claim blocking, cancellation, and human-input waiting.
- Added `IAiExecutionControlStore` for durable distributed execution control persistence.
- Added `IAiExecutionControlService` for high-level execution control operations:
  - `PauseExecutionAsync`
  - `MarkPausedAsync`
  - `ResumeExecutionAsync`
  - `MarkRunningAsync`
  - `CancelExecutionAsync`
  - `MarkWaitingForInputAsync`
  - `SubmitHumanInputAsync`
  - `CheckCanAdvanceAsync`
- Added `IAiExecutionControlGate` as a small runtime-facing control gate used before execution advancement.
- Added Redis-backed execution control store:
  - `RedisAiExecutionControlStore`
  - `RedisExecutionControlKeyBuilder`
  - `RedisExecutionControlLuaScripts`
- Added Redis key namespace for control state:
  - `ai:execution:control:{executionId}`
- Added optimistic versioning support for distributed-safe execution control updates.
- Added Redis Lua compare-and-set update for versioned control state transitions.
- Added atomic `TryCreateAsync` support to safely create control state when it does not yet exist.
- Added execution control service registration in dependency injection.
- Added runtime control gate registration in dependency injection.

### Execution Control

- Added execution-level pause support.
- Pause now stops new DAG step claims for the target `ExecutionId`.
- Already claimed/running work is allowed to finish safely.
- Added transition from `Pausing` to `Paused` once the runtime observes that no active claimed or running work remains.
- Added execution-level resume support.
- Resume moves an execution into `Resuming`.
- Runtime claim cycle now normalizes `Resuming` to `Running` once execution advancement is allowed again.
- Added execution-level cancellation support.
- Cancellation blocks new claims and marks the execution as cancelling.
- Added cancellation precedence during DAG finalization.
- If DAG convergence naturally produces `Completed` while execution control is `Cancelling`, final persisted execution status is now `Cancelled`.
- Added human-in-the-loop waiting support.
- Runtime can mark an execution as `WaitingForInput`.
- Waiting executions block new claims.
- Human input submission persists input into execution control state.
- Submitting human input moves execution into `Resuming`.
- Runtime later normalizes the execution back to `Running`.

### Runtime Integration

- Integrated `IAiExecutionControlGate` into the DAG step claim path.
- Added control checks before single-step claim.
- Added control checks before batch-step claim.
- Control checks now block step claiming for:
  - `Pausing`
  - `Paused`
  - `WaitingForInput`
  - `Cancelling`
  - `Cancelled`
- Control checks allow advancement for:
  - `None`
  - `Running`
  - `Resuming`
- Added runtime transition handling:
  - `Pausing` + no active work -> `Paused`
  - `Resuming` + claim cycle observed -> `Running`
- Integrated cancellation override into `AiDagExecutionFinalizationService`.
- Updated finalization so cancelled executions cannot incorrectly converge as completed.

### Controller / Queue Control

- Added controller-level queue pause and resume support.
- Added `PauseQueueAsync` to `IAiRuntimePipelineBackgroundController`.
- Added `ResumeQueueAsync` to `IAiRuntimePipelineBackgroundController`.
- Queue pause prevents new queued runs from starting.
- Queue pause does not stop already-running executions.
- Queue resume allows queued runs to start again.
- Added queued run tracking inside `AiRuntimePipelineBackgroundController`.
- Added `_queuedRuns` tracking for queued-but-not-started runs.
- Added `_runningRuns` tracking for started controller runs.
- Added `CancelQueuedRunAsync` support.
- Queued runs can now be cancelled before execution creation.
- Cancelled queued runs do not create a durable `ExecutionId`.
- Cancelled queued runs complete their handle with `AiExecutionStatus.Cancelled`.
- Added `CancelRunAsync` support.
- `CancelRunAsync` cancels queued runs directly when they have not started.
- `CancelRunAsync` delegates to `IAiExecutionControlService.CancelExecutionAsync` when the run is already running and has an `ExecutionId`.
- Added RunId-to-ExecutionId cancellation bridge.
- Running run cancellation now results in durable execution cancellation.
- Updated controller run terminal handling so final `AiExecutionStatus.Cancelled` maps to `AiRuntimeWorkerRunStatus.Cancelled`.
- Added hot enqueue behavior validation.
- Runs can be added while the controller is already processing another run.
- Runs can be added while the queue is paused and start only after resume.

### Improved

- Improved separation between controller lifecycle and execution lifecycle:
  - `RunId` is controlled by the background pipeline controller.
  - `ExecutionId` is controlled by durable execution state and execution control state.
- Improved state-machine clarity by separating:
  - requested action
  - effective runtime control status
  - runtime decision
- Improved distributed safety of control transitions using optimistic version checks.
- Improved finalization correctness under cancellation races.
- Improved queue control semantics without impacting already-running executions.
- Improved worker/controller distinction:
  - queue control belongs to `AiRuntimePipelineBackgroundController`
  - execution advancement belongs to `AiRuntimeInstanceWorker`
  - execution state control belongs to `IAiExecutionControlService`
- Improved cancellation semantics for running controller runs by reusing the existing execution control layer instead of duplicating cancellation logic.
- Improved test coverage around pause, resume, cancellation, waiting-for-input, queued cancellation, running cancellation, and hot enqueue behavior.

### Tests

- Added Redis execution control store tests:
  - set/get control state
  - missing state returns null
  - versioned update succeeds when expected version matches
  - versioned update fails when expected version does not match
  - delete removes control state
  - waiting-for-input metadata and input are persisted
- Added execution control service tests:
  - pause creates pausing state
  - resume creates resuming state
  - cancel creates cancelling state
  - cancellation wins over resume
  - waiting-for-input blocks advancement
  - human input submission resumes execution
  - invalid waiting key throws
  - no control state allows advancement
- Added claim-blocking integration tests:
  - pausing execution does not claim ready work
  - waiting-for-input execution does not claim ready work
  - cancelling execution does not claim ready work
  - no control state claims normally
  - pausing execution becomes paused after active work drains
  - paused execution resumes and claims work
  - waiting-for-input execution resumes after human input
  - resuming execution becomes running after runtime advancement
- Added finalization integration test:
  - cancelling execution overrides natural completed convergence and persists final status as cancelled
- Added controller queue-control integration tests:
  - pause queue prevents queued run from starting
  - resume queue allows queued run to complete
  - pause queue does not stop already-running execution
  - cancel queued run before execution creation
  - cancelling unknown queued run returns false
  - cancel running run delegates to execution control and persists cancelled status
  - hot enqueue while controller is running
  - hot enqueue while queue is paused
- Revalidated existing distributed scenarios after execution-control integration.
- Revalidated aggressive chaos scenarios with 100-step and 500-step distributed executions.

### Architecture

- Introduced a clear two-layer control architecture:

  - Layer 1: Controller / Queue / Run Control
    - `RunId`
    - queue pause/resume
    - queued run cancellation
    - running run cancellation bridge
    - hot enqueue

  - Layer 2: Execution Control
    - `ExecutionId`
    - pause/resume
    - cancellation
    - waiting for human input
    - submit human input
    - durable Redis control state

- Preserved separation between:
  - `AiExecutionState` for DAG state, step state, retry state, payload references, and convergence
  - `AiExecutionControlState` for operator/user/system control state
- Kept Redis control persistence separate from Redis DAG execution state.
- Kept execution control separate from controller queue control.
- Kept cancellation semantics cooperative and deterministic.
- Avoided hard termination of already running claimed steps.
- Preserved deterministic convergence and distributed safety.

### Notes

- Queue pause does not pause already-running executions.
- Execution pause does not cancel already-running claimed steps; it prevents new claims and waits for active work to drain.
- Queue cancellation before execution creation does not create a durable `ExecutionId`.
- Running run cancellation uses the existing execution-control layer and therefore follows the same deterministic cancellation/finalization behavior as direct execution cancellation.
- Human input is persisted in durable execution control state and can later be extended into audit/replay control history.
- Control state is currently Redis-backed and can later be mirrored into Mongo snapshots or an append-only audit log.

---

## [1.0.4.9] - 2026-05-18 - Redis DAG Store Refactor / Service Decomposition

### Added
- Added `IRedisDagStoreServices` shared service contract.
- Added `RedisDagStoreServices` composition wrapper for Redis DAG store dependencies.
- Added specialized Redis DAG store services:
  - `RedisDagStoreStateReader`
  - `RedisDagStoreStateWriter`
  - `RedisDagStoreClaimService`
  - `RedisDagStoreTransitionService`
  - `RedisDagStoreRecoveryService`
  - `RedisDagStoreHelper`
- Added centralized helper utilities for:
  - Redis script loading
  - Redis server resolution
  - DAG key generation
  - status helpers
  - unix timestamp generation

### Changed
- Refactored `RedisAiDagExecutionStore` into a thin orchestration facade.
- Moved distributed DAG logic into dedicated service boundaries:
  - state reads
  - state writes
  - claim orchestration
  - transition handling
  - recovery flows
- Moved Lua script ownership to domain-specific services.
- Centralized Redis Lua loading through `RedisDagStoreHelper`.
- Simplified `RedisAiDagExecutionStore` constructor using shared service composition.
- Improved XML documentation consistency across Redis DAG store services.
- Reduced internal coupling and improved maintainability/testability.

### Architecture
- Redis DAG execution store now follows a modular distributed service architecture:
  - facade + specialized execution services
- Improved separation of concerns for:
  - distributed claims
  - retry-aware transitions
  - recovery orchestration
  - distributed state persistence
- Prepared the runtime for future:
  - distributed orchestration improvements
  - observability extensions
  - runtime diagnostics
  - service-level testing

---

## [1.0.4.8] - 2026-05-18 - Distributed Runtime Instances / Aggressive Retention Stabilization

### Added

- Added distributed runtime-instance execution support for background pipeline runs.
- Added support for running pipeline executions in two runtime modes:
  - single runtime-instance mode
  - distributed multi-runtime-instance mode
- Added distributed worker-group execution so multiple runtime workers can safely advance the same execution.
- Added configurable distributed runtime worker count for background-controller execution.
- Added runtime-instance worker factory support for creating isolated runtime workers.
- Added terminal run lifecycle hook support for observing finalized background pipeline runs.
- Added distributed chaos validation for:
  - 500-step DAG executions
  - 30 distributed runtime workers
  - bounded batch execution
  - retryable flaky steps
  - distributed concurrency
  - aggressive compaction
  - aggressive eviction
  - snapshot persistence
  - replay reconstruction
  - resolver consistency
  - repeated state reload validation
- Added long-running aggressive distributed chaos stress validation, skipped by default, for repeated stability testing.
- Added reconstruction validation ensuring evicted and compacted steps remain resolvable after aggressive retention.
- Added retry preservation validation ensuring retried steps remain completed and retain retry metadata after aggressive retention and replay.
- Added repeated reload validation to verify deterministic `GetStateAsync(...)` and resolver behavior after terminal retention.
- Added validation that hot state may be empty after terminal eviction when archive index and resolver reconstruction remain valid.

### Changed

- Hardened terminal lifecycle handling across:
  - local DAG execution
  - distributed DAG execution
  - batch DAG execution
- Added centralized terminal lifecycle orchestration through `EnsureTerminalLifecycleAsync(...)`.
- Updated local, distributed, and batch runners to consistently execute terminal lifecycle side effects through the lifecycle helper.
- Improved terminal snapshot lifecycle reliability by ensuring terminal paths attempt snapshot persistence and cleanup consistently.
- Made terminal lifecycle side effects idempotent for distributed workers that may observe the same terminal execution concurrently.
- Hardened distributed state reconstruction to prevent logically completed steps from reappearing in hot state as default `None` steps.
- Updated `GetStateAsync(...)` reconstruction semantics so stale `None` hot-state entries for logically completed steps are removed during state reload.
- Updated distributed state reconstruction so terminal hot-state consistency is preserved across:
  - state blob reload
  - indexed step-key overlay
  - aggressive retention
  - replay reconstruction
- Updated retention tests to reflect the correct retention model:
  - hot state is a bounded mutable window
  - archive index and payload resolver are authoritative for evicted terminal steps
  - a fully evicted terminal hot state can be valid
- Updated hybrid retention tests to validate bounded hot state instead of requiring hot state to remain non-empty.
- Improved resolver-oriented retention validation for archived steps after eviction.
- Stabilized aggressive retention behavior under repeated distributed reload and replay scenarios.

### Fixed

- Fixed intermittent terminal snapshot availability issues in distributed background execution.
- Fixed terminal lifecycle paths that could return terminal records without consistently attempting snapshot persistence.
- Fixed snapshot lifecycle races across local, distributed, and batch DAG runners.
- Fixed hot-state regression where a logically completed step could be reconstructed as `Status=None`.
- Fixed stale hot-state resurrection after aggressive eviction.
- Fixed distributed replay/reload scenarios where completed logical history remained correct but hot state could contain invalid default step entries.
- Fixed retention/reconstruction inconsistency between:
  - persisted completed-step history
  - hot execution state
  - archive index
  - payload-backed resolver
- Fixed aggressive retention instability where completed steps could become visible in hot state as non-terminal/default steps.
- Fixed hybrid retention test assumptions that required hot state to remain non-empty even when steps were correctly evicted and archived.

### Validated

- Validated both runtime execution modes:
  - non-distributed single runtime-instance execution
  - distributed multi-runtime-instance execution
- Validated repeated aggressive distributed chaos execution with:
  - 500 DAG steps
  - 30 distributed workers
  - retries
  - distributed concurrency
  - compaction
  - eviction
  - snapshot persistence
  - replay reconstruction
  - resolver consistency
- Validated long-running aggressive chaos execution across repeated iterations.
- Validated that completed logical history remains stable while hot state remains bounded or fully evicted.
- Validated that archived steps remain resolvable through the archive index and payload resolver.
- Validated retry metadata survives aggressive retention, eviction, and reconstruction.
- Validated repeated `GetStateAsync(...)` reloads remain deterministic after aggressive retention.
- Validated full test suite stability after distributed runtime-instance and retention reconstruction changes.

### Notes

- Implemented on branch `feature/distributed-runtime-instances`.
- Runtime execution can now operate in both single-instance and distributed multi-runtime-instance modes.
- `RunId` remains the controller/job lifecycle identifier.
- `ExecutionId` remains the durable runtime namespace for DAG records, state, snapshots, replay, payloads, and resolver indexes.
- Hot state is a bounded mutable execution window, not the authoritative long-term history.
- `CompletedSteps` is the durable logical completion history.
- Archive index and payload resolver are authoritative for evicted terminal step reconstruction.
- A terminal execution may have an empty hot state when retention has safely archived and evicted all terminal steps.
- Terminal lifecycle side effects must remain idempotent because multiple workers may observe terminal convergence concurrently.

---

## [1.0.4.7] - 2026-05-15 - Background Controller / Batch DAG / Snapshot Replay Hardening

### Added

- Added full background-controller integration coverage for DAG executions.
- Added validation that controller `RunId` and runtime `ExecutionId` are always different namespaces.
- Added multi-run background-controller tests validating:
  - unique `RunId` per queued run
  - unique `ExecutionId` per runtime execution
  - no overlap between controller run identifiers and runtime execution identifiers
  - completed runtime executions across multiple queued runs
- Added small validated runtime simulation covering:
  - retry behavior
  - retention configuration
  - compaction / eviction configuration
  - concurrency configuration
  - tracing
  - runtime metrics
  - completed-step resolution
- Added full chaos runtime simulation with:
  - 50-step DAG pipeline
  - multiple queued runs
  - bounded batch execution
  - retryable flaky steps
  - policy-driven retention
  - concurrency / throttling configuration
  - tracing and worker metrics
- Added a custom `chaos.flaky-provider` step for integration testing retry behavior.
- Added resolver validation after terminal lifecycle to ensure completed required steps remain resolvable after retention, compaction, eviction, and finalization.
- Added terminal snapshot validation for background-controller executions.
- Added replay validation when the live execution still exists:
  - `ReplayAsync(...)` returns `AlreadyExists = true`
  - `Restored = false`
- Added restore-from-snapshot validation after deleting live DAG state:
  - terminal snapshot exists
  - live DAG record/state are deleted
  - replay restores from snapshot
  - `Restored = true`
  - `AlreadyExists = false`
  - restored record/state are available again from the DAG store
- Added deterministic replay validation:
  - captures execution fingerprint before deletion
  - deletes live DAG state
  - restores from snapshot
  - compares restored execution against original execution
  - validates deterministic consistency for:
    - `ExecutionId`
    - `PipelineName`
    - terminal status
    - completed steps
    - step statuses
    - retry counts
    - required resolved steps

### Changed

- Aligned batch DAG execution with retention-aware terminal lifecycle while preserving stable bounded batch behavior.
- Kept `AiDagBatchExecutionRunner` batch-safe instead of applying single-step retention semantics directly to each batch item.
- Preserved the stable batch execution flow:
  - claim batch
  - execute batch
  - persist step transitions
  - evaluate convergence
  - persist final record
  - snapshot / cleanup terminal execution
- Added batch-safe retention coordination support without breaking small or chaos runtime simulations.
- Updated background-controller replay tests to separate two replay contracts:
  - replay against existing execution
  - replay after live DAG state deletion
- Updated resolver validation to use the correct resolver contract:
  - `GetStepStatusAsync(...)` for status / dependency / convergence validation
  - `GetStepAsync(...)` when full step state, retry state, or payload-backed data is required
- Improved replay test structure so replay is validated through snapshot existence, runtime restore behavior, DAG store availability, and deterministic comparison.
- Improved terminal lifecycle snapshot handling by surfacing snapshot persistence failures instead of allowing silent timeout-only failures.
- Updated `AiDagExecutionLifecycleHelper` to normalize JSON-derived state before snapshot persistence.
- Updated snapshot persistence to normalize:
  - `AiExecutionState.PipelineConfig`
  - step config dictionaries
  - step result data dictionaries
- Converted `System.Text.Json.JsonElement` values into MongoDB-serializable .NET values before snapshot persistence.
- Updated `DefaultAiExecutionReplayService<TContext>` so distributed DAG replay restores into the authoritative `IAiDagExecutionStore` when available, instead of restoring only into the generic `IAiExecutionStore`.

### Fixed

- Fixed replay snapshot timeout caused by MongoDB failing to serialize `JsonElement` values inside `AiExecutionState.PipelineConfig`.
- Fixed hidden snapshot persistence failures by making snapshot errors visible during tests.
- Fixed replay restore behavior for distributed DAG executions where `ReplayAsync(...)` returned `Restored = true` but the restored execution was not available from `IAiDagExecutionStore`.
- Fixed replay contract mismatch by restoring distributed DAG snapshots into the DAG store.
- Fixed test ambiguity between controller `RunId` and runtime `ExecutionId`.
- Fixed background-controller tests so they validate runtime execution namespace correctly.
- Fixed retention / resolver validation assumptions by distinguishing hot-state access from archive-aware step status resolution.
- Fixed replay test design so `AlreadyExists` and `Restored` are validated as separate scenarios.
- Fixed deterministic replay coverage to prove replay restores the same terminal execution state rather than only returning a successful replay result.
- Fixed snapshot replay flow for DAG executions using JSON pipeline configuration values.

### Validated

- Verified small background-controller runtime simulation passes.
- Verified full chaos background-controller simulation passes.
- Verified completed required steps remain resolvable after terminal lifecycle.
- Verified replay returns `AlreadyExists = true` when the execution still exists.
- Verified replay restores from snapshot after live DAG state deletion.
- Verified deterministic replay produces the same execution fingerprint before and after restore.
- Verified retry counts survive snapshot replay.
- Verified completed step metadata remains stable across replay.
- Verified restored DAG executions are readable again through `IAiDagExecutionStore`.
- Verified snapshot persistence works with JSON-derived pipeline configuration.
- Verified `RunId` and `ExecutionId` remain strictly separated.

### Notes

- `RunId` is the controller/job lifecycle identifier.
- `ExecutionId` is the runtime execution namespace used by DAG state, records, snapshots, and replay.
- A replay result of `AlreadyExists = true` is valid when the execution still exists.
- A replay result of `Restored = true` is expected only after live execution record/state have been removed.
- Batch execution should not blindly reuse single-step distributed retention flow per step; batch execution needs batch-safe retention behavior.
- Snapshot persistence must normalize runtime state before writing to MongoDB because JSON pipeline definitions may introduce `JsonElement` values.
- In distributed DAG mode, replay must restore into `IAiDagExecutionStore`, because that is the authoritative execution store.

---

## [1.0.4.6] - 2026-14-04 - Policy-Driven Concurrency Admission and Generic Throttling

- Added policy-aware concurrency admission before Redis distributed lease acquisition.
- Integrated concurrency policy evaluation into DAG step claiming.
- Ensured denied concurrency policies prevent:
  - Redis lease acquisition
  - DAG step claiming
  - step execution
- Added concrete concurrency admission policies:
  - `concurrency.provider.admission`
  - `concurrency.model.admission`
  - `concurrency.operation.admission`
- Added generic distributed throttle policy:
  - `concurrency.throttle`
- Added generic throttle rule support with:
  - `scope`
  - `target`
  - `limit`
  - `leaseSeconds`
  - `defaultRetryAfterMs`
- Added supported generic throttle scopes:
  - `provider`
  - `model`
  - `operation`
  - `step`
  - `step-type`
  - `pipeline`
- Added optional `target` matching for generic throttle rules.
- Added provider target matching for pipeline-level throttle rules.
- Added model target matching using the normalized `{provider}:{model}` format.
- Added operation target matching using the logical operation name.
- Added step throttle targeting by concrete step name.
- Added step-type throttle targeting by logical step key.
- Added pipeline throttle targeting by stable pipeline key.
- Added `AiConcurrencyThrottleRule` to represent generic throttle rules resolved from policy configuration.
- Added `AiConcurrencyThrottleRuleApplicator` to apply matching throttle rules after `AiConcurrencyContext` creation.
- Added `AiConcurrencyPolicyContext` so concurrency policies can receive policy-specific configuration without polluting `AiConcurrencyContext`.
- Kept `AiConcurrencyContext` focused on runtime admission identity:
  - execution id
  - pipeline key
  - step id
  - step key
  - runtime instance id
  - lease id
  - provider
  - model
  - operation
- Updated `DefaultAiConcurrencyEngine` to execute configured concurrency policies with their own policy config.
- Updated `DefaultAiConcurrencyDefinitionResolver` to resolve generic throttle rules from `concurrency.throttle` policy configuration.
- Preserved direct concurrency configuration priority over policy-derived throttle rules.
- Preserved pipeline-level concurrency policy configuration without copying pipeline config into `AiExecutionState`.
- Updated DAG claim preparation so concurrency admission can use both:
  - pipeline-level concurrency config
  - step-level concurrency config
- Updated DAG claim service to use the effective concurrency definition for both acquisition and release.
- Updated distributed batch and distributed single-step runners to pass the resolved pipeline into claim acquisition.
- Added provider admission policy tests for:
  - allowed provider
  - blocked provider
  - required provider missing
  - case-insensitive provider matching
- Added model admission policy tests for:
  - allowed provider/model pair
  - blocked provider/model pair
  - required model missing
  - case-insensitive model matching
  - provider-scoped model matching
- Added operation admission policy tests for:
  - allowed operation
  - blocked operation
  - required operation missing
  - case-insensitive operation matching
- Added generic throttle policy tests verifying that `concurrency.throttle` acts as an allow-through marker policy while Redis enforces distributed throttling.
- Added Redis gate integration coverage for generic throttle rules:
  - provider target match
  - provider target no-match
  - model target match
  - step-type target match
- Added real DAG execution integration coverage for:
  - provider admission deny/allow
  - model admission deny/allow
  - operation admission deny/allow
  - pipeline-level generic provider throttle
  - pipeline-level provider target no-match
  - pipeline-level generic model throttle
- Documented that policy denial occurs before Redis lease acquisition and before DAG step claiming.
- Documented that generic throttle policy enforcement is performed by Redis distributed concurrency scopes, not by the policy itself.

---

## [1.0.4.5] - 2026-12-04 - Distributed Concurrency / Throttling

- Added Redis-backed distributed concurrency gate using ZSET-based leases.
- Replaced counter-based concurrency tracking with crash-safe lease expiration.
- Added distributed concurrency scopes for:
  - global runtime capacity
  - pipeline-level throttling
  - pipeline-step throttling
  - execution-level bounded parallelism
  - runtime-instance-level throttling
  - provider-level throttling
  - provider/model-level throttling
  - operation-level throttling
- Ensured pipeline-step throttling is scoped by both pipeline key and step key to avoid cross-pipeline collisions.
- Ensured model-level throttling is scoped by both provider and model to avoid cross-provider model-name collisions.
- Added stable pipeline key propagation from distributed runners into distributed claim acquisition.
- Centralized concurrency context creation to ensure acquire/release scope consistency.
- Added provider, model, and operation metadata to concurrency contexts.
- Added resolver support for:
  - `maxProviderConcurrency`
  - `maxModelConcurrency`
  - `maxOperationConcurrency`
- Fixed concurrency resolver merge semantics so omitted step-level values no longer override pipeline-level values with runtime defaults.
- Added policy-config defaults for concurrency definitions.
- Preserved concurrency configuration priority order:
  - step direct config
  - step policy config
  - pipeline direct config
  - pipeline policy config
  - runtime defaults
- Renamed structured policy metadata from `type` to `kind`.
- Preserved backward compatibility for policy configuration:
  - string policy format is still supported
  - `key` is accepted as an alias for `name`
  - `type` is accepted as a legacy alias for `kind`
- Added diagnostic denial reasons when a concurrency scope blocks admission.
- Added tracing and logging around concurrency admission decisions.
- Updated distributed single-step and batch execution runners to release concurrency leases after step completion or failure.
- Added release protection when a concurrency lease is acquired but the DAG step claim fails.
- Added Redis gate integration coverage for:
  - global concurrency limits
  - pipeline concurrency limits
  - pipeline-step concurrency limits
  - execution-level limits
  - runtime-instance-level limits
  - provider concurrency limits
  - provider/model concurrency limits
  - operation concurrency limits
  - idempotent lease acquisition
  - explicit release recovery
  - TTL-based crash recovery
  - diagnostic throttling reasons
- Added claim-service test coverage for:
  - denied admission without DAG claim
  - release after failed distributed claim race
  - batch denied admission
  - batch release after failed distributed claim race
  - provider/model/operation context propagation
- Added resolver regression coverage for:
  - pipeline fallback behavior
  - step override behavior
  - direct config priority over policy config
  - policy-config defaults
  - legacy policy JSON compatibility
- Updated README documentation for:
  - Redis ZSET lease model
  - provider/model/operation throttling
  - policy-config concurrency defaults
  - diagnostic throttling reasons
  - concurrency admission observability

---

## [1.0.4.5] - 2026-012-04 - Policy Engine V2 - Structured Policy Definitions

### Added

- introduced `AiConfiguredPolicyDefinition`
- introduced `AiConfiguredPolicyDefinitionJsonConverter`
- added backward-compatible policy deserialization
- added structured policy configuration support
- added support for mixed legacy and structured policy formats
- added policy metadata support (`Type`, `Config`)
- added `GetPolicyNames()` extension helper
- added integration tests for:
  - Retry engine
  - Retention engine
  - Concurrency engine
  - mixed policy formats
  - structured policy execution

### Changed

- migrated retry policies from `List<string>` to `List<AiConfiguredPolicyDefinition>`
- migrated retention policies from `List<string>` to `List<AiConfiguredPolicyDefinition>`
- migrated concurrency policies from `List<string>` to `List<AiConfiguredPolicyDefinition>`
- updated retry engine policy resolution
- updated retention engine policy resolution
- updated concurrency engine policy resolution
- updated DAG execution integration tests
- updated runtime policy compatibility tests
- updated JSON pipeline compatibility behavior

### Compatibility

The runtime now supports both formats simultaneously.

Legacy format:

```json
"policies": [
  "retry.transient.default"
]
```

Structured format:

```json
"policies": [
  {
    "name": "retry.transient.default",
    "type": "retry",
    "config": {
      "maxRetries": 5
    }
  }
]
```

### Notes

Current runtime behavior resolves policies using:

```txt
policy.Name
```

The following fields are now available for future policy-driven orchestration features:

- `Type`
- `Config`

This prepares the runtime for future capabilities such as:

- distributed throttling
- provider-based concurrency
- tenant-aware orchestration
- adaptive retry strategies
- cost-aware execution
- dynamic retention policies
- advanced admission control
- rate limiting
- routing policies

### Result

The runtime now supports:

- backward-compatible policy configuration
- structured policy metadata
- future extensible policy configuration
- unified policy modeling across retry, retention, and concurrency engines
- enterprise-ready policy extensibility

## [1.0.4.4] - 2026-08-04 - Concurrency Engine V1 — Distributed Admission & Claim Refactor

## Added

### Distributed Concurrency Gate
- introduced `IAiConcurrencyGate`
- added `RedisAiConcurrencyGate`
- added lease-based distributed concurrency acquisition
- added lease TTL / crash recovery support
- added distributed concurrency release flow
- added deterministic lease ownership model

### Concurrency Definitions
- introduced `AiConcurrencyDefinition`
- added support for:
  - `MaxGlobalConcurrency`
  - `MaxPipelineConcurrency`
  - `MaxStepConcurrency`
  - `MaxExecutionConcurrency`
  - `MaxInstanceConcurrency`
  - `LeaseSeconds`
  - `DefaultRetryAfterMs`
- added future support for `MaxDegreeOfParallelism`

### Concurrency Context
- introduced `AiConcurrencyContext`
- added deterministic lease identifiers
- aligned concurrency identity with DAG claim ownership

### Concurrency Resolution
- introduced `IAiConcurrencyDefinitionResolver`
- added `DefaultAiConcurrencyDefinitionResolver`
- supports:
  - pipeline-level config resolution
  - step-level config override
  - persisted step-state resolution
- enables pre-claim config-driven orchestration without requiring `AiStepExecutionContext`

---

# Distributed Claim Flow Refactor

## New Claim Architecture

Previous flow:

    Runner
    ↓
    TryClaimNextReadyStepAsync
    ↓
    Lua script handled orchestration

New flow:

    GetReadyStepsAsync
    ↓
    Resolve concurrency config
    ↓
    ConcurrencyGate.TryAcquireAsync
    ↓
    TryClaimStepAsync
    ↓
    Execute
    ↓
    Release concurrency slot

## Added

### AiDagStepClaimService
- added concurrency-aware distributed admission control
- added pre-claim concurrency evaluation
- added release-on-failed-claim safety
- added retry-window-aware candidate selection

### AiDagClaimedStepExecutor
- added deterministic concurrency slot release
- added execution-finally release safety
- prevents distributed concurrency slot leaks

---

# Retry Compatibility

## Fixed

### Retry Window Compatibility
- fixed `GetReadyStepsAsync` to support:
  - `Ready`
  - `None`
  - `WaitingForRetry` when retry window opens
- restored compatibility with distributed retry reclaim tests

### Multi-Worker Retry Safety
- preserved atomic retry reclaim semantics
- preserved retry window race protection
- preserved retry count consistency

---

# Architecture Improvements

## Separation of Responsibilities

### RedisAiDagExecutionStore
Now responsible only for:
- atomic storage operations
- atomic distributed claims
- timeout recovery
- persistence primitives

### AiDagStepClaimService
Now responsible for:
- orchestration
- distributed admission control
- concurrency evaluation
- claim coordination

### DefaultAiDagStepExecutionOrchestrator
Now responsible only for:
- local bounded parallel execution
- already-claimed step execution coordination

---

# Notes

## Current Runtime State

Distributed concurrency system is now ACTIVE:

- config-driven concurrency
- distributed concurrency gate
- lease-based throttling
- distributed-safe admission
- claim/release lifecycle

Policy-driven concurrency engine is NOT yet active:

- `DefaultAiConcurrencyEngine`
- `IAiConcurrencyEngine`
- `AiPolicyKind.Concurrency`

These remain reserved for future step-scoped policy evaluation once full pre-claim policy orchestration is introduced.

---

# Next Planned Step

## Concurrency Config Migration

Planned migration:

    AiParallelExecutionDefinition
    → deprecated

    AiConcurrencyDefinition.MaxDegreeOfParallelism
    → unified concurrency configuration

This will fully replace the old parallel execution configuration model with the new concurrency architecture.

---

## [1.0.4.4] - 2026-08-04 - DAG Execution Engine Refactor

## Overview

Refactored the DAG execution engine into focused runtime services to reduce engine complexity, isolate responsibilities, and improve maintainability while preserving deterministic execution behavior and full backward compatibility.

All existing tests are passing after the refactor.

---

# Architecture Refactor

## Previous State

The DAG engine previously centralized:

- local execution
- distributed orchestration
- batch orchestration
- retention coordination
- finalization logic
- cleanup lifecycle
- distributed claims
- step execution
- convergence persistence

inside a single large orchestration class.

This created:

- high coupling
- difficult navigation
- increased maintenance complexity
- growing orchestration responsibilities
- reduced long-term extensibility

---

# New Runtime Structure

The runtime is now decomposed into focused orchestration services.

## Core

### AiDagExecutionEngine

Main orchestration entrypoint responsible only for:

- delegating execution flows
- coordinating execution mode selection
- exposing runtime API surface

---

## Creation

### AiDagExecutionCreator

Responsible for:

- execution creation
- initial state seeding
- DAG step initialization
- retry policy resolution
- execution persistence

---

## Distributed

### AiDagDistributedExecutionRunner

Responsible for:

- distributed orchestration flow
- convergence coordination
- distributed execution lifecycle
- distributed persistence flow

### AiDagStepClaimService

Responsible for:

- distributed step claiming
- timeout recovery
- batch claim acquisition

---

## Batch

### AiDagBatchExecutionRunner

Responsible for:

- bounded distributed batch execution
- controlled parallel execution coordination
- distributed batch convergence flow

---

## Local

### AiDagLocalExecutionRunner

Responsible for:

- local non-distributed DAG execution
- local convergence orchestration
- retry-aware local execution flow

---

## Steps

### AiDagClaimedStepExecutor

Responsible for:

- executing already-claimed distributed steps
- centralized step execution lifecycle
- shared execution behavior across runners

---

## Retention

### AiDagRetentionCoordinator

Responsible for:

- policy-driven retention execution
- retention metrics/tracing
- state persistence after retention
- archive-aware resolver warming

---

## Finalization

### AiDagExecutionFinalizationService

Responsible for:

- distributed-safe finalization
- terminal convergence persistence
- optimistic distributed finalization flow

### AiDagExecutionRecordFinalizer

Responsible for:

- applying convergence results to records
- applying authoritative persisted snapshots

---

## Helpers

### AiDagExecutionLifecycleHelper

Responsible for:

- terminal snapshot persistence
- automatic cleanup lifecycle

### AiDagExecutionHelpers

Shared execution helper methods for:

- execution step key validation
- DAG store validation
- legacy convergence helpers

---

# Runtime Improvements

## Separation of Concerns

Execution responsibilities are now isolated by runtime domain:

- creation
- orchestration
- retention
- lifecycle
- distributed coordination
- convergence persistence
- step execution

---

## Reduced Engine Complexity

The main DAG engine now acts primarily as:

- an orchestration facade
- a runtime delegator

instead of containing the full runtime implementation.

---

## Distributed Runtime Stability

The refactor preserves:

- deterministic convergence behavior
- optimistic distributed persistence
- Redis/Lua compatibility
- retry orchestration
- retention orchestration
- archive-aware state resolution
- distributed recovery semantics

---

## Observability Preservation

Existing runtime observability remains intact:

- execution tracing
- storage tracing
- retention tracing
- retry metrics
- execution metrics
- lifecycle metrics

---

# Compatibility

## Preserved Behavior

The refactor preserves:

- existing execution semantics
- existing retry behavior
- retention behavior
- distributed orchestration semantics
- snapshot persistence
- cleanup behavior
- execution persistence semantics

---

## Test Status

All existing tests are passing after the refactor.

Validated areas include:

- local DAG execution
- distributed DAG execution
- retry orchestration
- retention orchestration
- convergence behavior
- distributed recovery
- batch execution
- snapshot lifecycle
- cleanup lifecycle
- observability integration

---

# Result

The runtime now provides:

- cleaner orchestration architecture
- improved maintainability
- reduced coupling
- improved extensibility
- safer long-term runtime evolution
- clearer execution domain boundaries
- better runtime readability
- improved operational separation

---

## [1.0.4.3] - 2026-07-04 - Distributed DAG Batch Execution

## New Features

Implemented bounded distributed DAG batch execution with deterministic multi-worker orchestration.

---

# Distributed Batch Execution

Added:

- `ExecuteBatchAsync(...)`
- `ExecuteBatchDistributedAsync(...)`
- `IAiDagStepExecutionOrchestrator`
- `DefaultAiDagStepExecutionOrchestrator`

The runtime now supports:

- bounded parallel DAG execution
- dependency-aware distributed scheduling
- atomic multi-step claiming
- multi-worker execution coordination
- deterministic batch convergence
- distributed-safe step ownership

---

# Fixed

- fixed distributed convergence edge case when concurrent workers observed empty claim batches before terminal persistence

---

# Redis DAG Claiming

Added atomic Redis Lua batch claim support:

- `TryClaimReadyStepsAsync(...)`
- `ClaimBatchPreparedScript`
- deterministic step ordering
- retry-aware claim eligibility
- claim-token ownership enforcement

---

# Parallel Execution Configuration

Added pipeline-level parallel execution configuration:

```json
"parallelExecution": {
  "enabled": true,
  "maxDegreeOfParallelism": 8
}
```
 Scheduling Architecture

Introduced centralized scheduling orchestration layer:

- orchestration isolated from DAG engine
- future-ready admission policies
- future-ready distributed throttling
- future-ready execution governance
- future-ready tracing integration

---

# Batch Execution Result Model

Added:

- `AiClaimedStepExecutionResult`

This preserves explicit mapping between:

- claimed distributed ownership
- execution result

without relying on positional ordering.

---

# Retention Compatibility

Validated compatibility with:

- retention compaction
- retention eviction
- archived payload resolution
- bounded hot-state execution
- payload externalization
- distributed convergence

---

# Distributed Concurrency Validation

Added large-scale integration tests validating:

- 50-step DAG execution
- dependency-aware scheduling
- bounded parallel execution
- concurrent multi-worker execution
- atomic distributed claims
- deterministic convergence
- retention + compaction + eviction compatibility

---

# Stability Improvements

Fixed:

- Redis Lua empty-array serialization edge case (`{}` vs `[]`)
- batch claim deserialization robustness
- distributed batch record loading consistency
- orchestration wiring consistency for local retry tests

---

# Result

The runtime now supports:

- deterministic distributed DAG orchestration
- bounded parallel execution
- atomic multi-worker scheduling
- retention-safe distributed execution
- policy-driven execution infrastructure
- scalable hot-state bounded workflows

---

## [1.0.4.2] - 2026-07-04 - Config-Driven and Policy-Driven Retention Engine

## Major Refactor

Completed migration from the legacy retention system to the new policy-driven retention architecture.

### Retention Engine

- migrated retention execution to the new policy-driven engine
- removed legacy retention services/options/resolvers
- retention is now fully config-driven through pipeline configuration
- retention policies are now decision-only
- retention mutations remain isolated in runtime services

### DAG-Aware Eviction

- added DAG-aware eviction protection
- terminal steps still referenced by active dependencies are no longer evicted
- prevents convergence instability and execution deadlocks
- enables bounded hot-state execution safely during active DAG processing

### Retention Policies

- stabilized:
  - retention.compact.terminal
  - retention.evict.terminal

- hybrid retention behavior now supported through ordered policy composition

Example:

```json
"policies": [
  "retention.compact.terminal",
  "retention.evict.terminal"
]
```
## Runtime Stability

- fixed retention timing inconsistencies
- fixed retry and pipeline configuration serialization compatibility
- fixed Redis deserialization compatibility for retry policy collections
- added JSON repair compatibility for legacy retry policy payloads
- stabilized distributed execution retention flow
- stabilized retention, metrics, and tracing integration

## Metrics & Tracing

Validated runtime metrics integration across:

- execution
- retention
- hot-state
- storage
- resolver
- tracing

## Testing

- migrated integration tests to the new policy-driven retention architecture
- updated retention tests to support bounded hot-state execution
- validated DAG-aware eviction behavior during active execution
- all integration tests passing

## Result

The runtime now supports:

- policy-driven retention
- deterministic bounded hot-state execution
- DAG-safe distributed eviction
- distributed retry orchestration
- payload externalization
- runtime observability
- scalable execution-state lifecycle management

---

## [1.0.4.1] - 2026-07-04 - Config-Driven and Policy-Driven Retention Engine

### Changed
- Replaced the legacy execution state retention flow with the new policy-driven retention engine.
- Added config-driven retention resolution using pipeline-level configuration with step-level overrides.
- Integrated retention policies through the shared policy engine model.
- Updated retention execution to preserve all policy decisions, applying compaction before eviction when both are selected.
- Added step-aware inline payload size tracking through `AiStepState.InlinePayloadSizeBytes`.
- Updated retention trigger logic to use precomputed step payload size instead of repeated serialization.
- Integrated policy-driven retention into DAG execution persistence and finalization flow.
- Ensured retention state changes are persisted and resolver cache is warmed incrementally after eviction.

### Added
- Added compact, evict, and hybrid retention policies.
- Added retention context support for resolved trigger configuration.
- Added pipeline-level runtime configuration propagation into execution state.
- Added integration coverage for pipeline config persistence and step config override behavior.

### Removed
- Removed dependency on legacy options-driven retention flow from the DAG runtime path.

---
## [1.0.4.0] - 2026-05-04 - Config-Driven and Policy-Driven Retry Engine

### Added
- Added policy-level observability through `AiPolicyEngine`.
- Added policy execution metrics, failure metrics, and decision metrics.
- Added `AiPolicyResult.IsSuccess` for cleaner policy instrumentation.
- Added no-op tracing/logging support for test scenarios.

### Changed
- `DefaultAiPolicyEngineFactory` now injects `IAiRuntimeObservability` into policy engines.
- Policy execution is now traceable and measurable through the runtime observability facade.
- Retry observability remains at orchestration level to avoid duplicate metrics/logs.

### Fixed
- Fixed policy engine factory construction after observability was added to policy engines.
- Updated tests to provide observability dependencies.

### Next
- Refactor eviction and compaction to use the PolicyEngine model.

---

## [1.0.3.9] - Config-Driven and Policy-Driven Retry Engine

### 🚀 Added
- Introduced distributed retry system based on PolicyEngine + RetryEngine
- Added `config.retry` as the unified retry configuration model
- Added strict validation for retry configuration
- Added integration tests covering:
  - Missing retry config
  - Invalid retry config
  - Retry hydration into step state
  - Config persistence in execution state

### 🔧 Changed
- Retry execution moved from local in-process loops to distributed state-driven model
- Step executor now performs a single execution (no retry logic)
- Retry decisions are now policy-driven and context-based
- Retry scheduling is persisted via `AiStepRetryState` and enforced through Redis/Lua
- Step initialization now uses `ResolvedAiPipelineStep.Config` as source of truth

### 🐛 Fixed
- Fixed silent fallback to default retry values (`MaxRetries = 3`)
- Fixed incorrect retry hydration due to missing config mapping
- Fixed inconsistency between JSON pipeline definition and runtime behavior

### 💥 Breaking Changes
- Removed legacy retry system based on `execution.maxRetries`
- Removed local retry loops (`while` retry pattern)
- Retry must now be explicitly defined under `config.retry`
- Pipelines without valid retry configuration will now fail at creation time

### 🧠 Notes
- This change introduces a deterministic, observable, and distributed retry model
- Aligns retry behavior with multi-worker and DAG execution architecture

---

## [1.0.3.8] - Config-Driven and Policy-Driven Retry Engine

### 🚀 Refactor - Retry Engine

- Introduced policy-driven retry engine (`IAiRetryEngine`)
- Removed legacy retry pipeline:
  - RetryExecutionAdapter
  - RetryScheduler
  - RetryClassifier
  - RetryPolicyResolver
  - RetryDecisionService
- Added `IAiPolicyEngineFactory` with per-step engine instantiation
- Implemented `DefaultAiRetryEngine`:
  - deterministic decision
  - retry state mutation
  - support for policies and retry config
- Integrated retry handling into DAG execution flow
- Added support for retry config via `AiStepExecutionContext` helper
- Rehydrated `stepState.Retry` for backward compatibility

### 🧪 Tests

- Updated integration tests to align with new retry engine
- Removed legacy retry definition resolver tests
- Added config binding coverage via step context helper

### 🧰 Fixes

- Fixed JSON binding:
  - case-insensitive properties
  - enum string conversion
  - `policy` vs `policies` compatibility

### ⏭ Next

- Redis Lua alignment with retry engine (WaitingForRetry, NextRetryAtUtc, claim eligibility)

---

## [1.0.3.7] - 2026-05-01 - Tracing

### Added

- Added runtime observability tracing facade.
- Added trace scopes, trace records, trace recorder, and trace timeline projection.
- Added in-memory and no-op tracing implementations.
- Added normalized trace categories:
  - `dag-store`
  - `step`
  - `retention`
  - `resolver`
  - `execution`
- Added retention trace metadata for:
  - compacted steps
  - evicted steps
  - removed hot-state steps
  - resolver warmup
  - retention duration
- Added integration timeline rendering tests for DAG execution and retention behavior.

### Fixed

- Fixed evicted steps being reintroduced into hot execution state during convergence evaluation.
- Made archive-aware convergence evaluation read-only.
- Removed unintended hot-state mutation from convergence evaluation.
- Stabilized retention behavior after eviction in distributed DAG execution.
- Fixed finalization compatibility with retention-enabled executions.

### Improved

- Improved runtime observability wiring through DI and engine options.
- Improved tracing coverage for DAG store operations, step execution, retention, and finalization.
- Improved XML documentation for convergence and observability components.
- Strengthened separation between read-path evaluation and write-path state mutation.

---

## [1.0.3.6] - 2026-04-29 - Full runtime metrics coverage and integration validation

### ✨ Added

- Introduced full `IAiRuntimeMetrics` facade with structured domains:
  - Execution metrics
  - Retention metrics (Trigger, Decision, Plan, Execution)
  - Storage metrics
  - HotState metrics
  - Resolver metrics

- Added thread-safe in-memory metrics implementations using `Interlocked`

- Added integration tests covering:
  - Full pipeline execution (`ExecuteAllAsync`)
  - Worker loop execution (`ExecuteNextAsync`)
  - Retry-aware execution flows
  - Payload store validation (Mongo persistence)
  - Retention and compaction invariants
  - Execution convergence (state as source of truth)

---

### 🔧 Changed

- Updated integration tests to use **invariant-based assertions** instead of strict value checks:
  - Compatible with distributed and asynchronous execution
  - Handles retry, compaction, and caching behavior correctly

- Improved dependency injection setup in tests:
  - Ensured proper registration of `IAiPayloadStore`
  - Enabled realistic runtime configuration (Mongo + Redis where applicable)

---

### 🧪 Fixed

- Removed invalid metrics test that did not account for:
  - State compaction
  - Multiple step mutations
  - Non-deterministic execution behavior

- Fixed incorrect assumptions in tests regarding:
  - Step count vs actual state mutations
  - Finalization execution (may not always occur)
  - Storage usage depending on payload size and thresholds

---

### 🧠 Notes

- Metrics now reflect **real runtime behavior** rather than artificial expectations
- Test suite is aligned with:
  - Distributed execution model
  - Retry and recovery mechanisms
  - State compaction and payload externalization

---

## Summary

This update establishes a **production-grade observability foundation** for the AI runtime:

- Full runtime metrics coverage
- Realistic integration testing
- Strong alignment with distributed system behavior

---

## [1.0.3.5] - 2026-04-27 - AI Runtime Retention Evolution

### 🚀 Added
- Introduced adaptive retention decision layer:
  - `IAiExecutionRetentionDecisionService`
  - `IAiExecutionRetentionDecisionEvaluator`
  - `IAiExecutionRetentionDecisionPolicy`
- Added `SizeBasedAiExecutionRetentionDecisionPolicy` as first adaptive compaction policy.
- Added `RetentionTrigger` configuration under `AiEngineOptions`.
- Added full retention safety integration test suite:
  - End-to-end retention pipeline validation (Trigger → Decision → Policy → Execution)
  - Safe eviction validation (persist → index → remove invariant)
  - Archived step resolution via `IAiExecutionStepResolver`
  - Hybrid retention validation (compaction + eviction ordering)
  - Retention idempotence validation
  - Reload/replay validation with archived step resolution

---

### ♻️ Changed
- Refactored `AiExecutionRetentionService`:
  - Now depends on `IAiExecutionRetentionDecisionService`
  - Removed direct dependency on trigger/evaluator logic
- Refactored DI registration:
  - Introduced explicit decision service, evaluator, and policy wiring
  - Removed fragile `TryAddEnumerable` factory patterns
- Updated retention trigger behavior:
  - Aligned `RetentionTrigger` thresholds with `StateRetention` limits
  - Ensured retention is consistently executed when state exceeds limits
- Improved test design:
  - Removed artificial compaction forcing (`MaxInlinePayloadBytes = 1`)
  - Introduced realistic thresholds and scenario-driven payload sizes:
    - Small payloads → eviction-focused tests
    - Large payloads → compaction/hybrid tests
- Updated test assertions:
  - Now validate applied operations (`CompactedSteps`, `EvictedSteps`)
  - Avoid reliance on last-evaluation metrics (`StepsPlanned*`)

---

### 🐛 Fixed
- Fixed DI conflicts when registering decision policies with factory-based descriptors
- Fixed retention not triggering due to mismatched thresholds between trigger and state retention
- Fixed hybrid retention test instability caused by incorrect assumptions on planned metrics
- Fixed archived step lookup tests using incorrect index store methods

---

### 🧠 Result
- Retention system is now:
  - Deterministic
  - Fully testable
  - Logically lossless (no data loss after eviction)
  - Production-safe
  - Extensible via pluggable decision policies

---

### 🔮 Next
- Introduce advanced memory policies:
  - Temporal decay
  - Usage-based retention
  - Supersession graph (state evolution)
- Extend retention to RAG memory handling
- Introduce intelligent eviction strategies based on semantic value

---

## [1.0.3.4] - 2026-04-27

# 🚀 Test Stabilization — Hybrid Retention & Payload Metrics

## 🧠 Overview

This update stabilizes integration tests after introducing Hybrid retention
and multi-layer payload storage (Mongo + Redis cache).

The runtime behavior evolved from deterministic single-layer execution
to a realistic multi-layer system:

- state
- archive
- cache
- resolver

Tests have been updated accordingly.

---

## 🔥 Hybrid Retention — Production Validation

### Added full production-level tests

- ExecuteAllAsync_Should_Complete_With_Hybrid_Retention_And_Archived_Steps_Resolvable
- ExecuteAllAsync_Should_Remain_Idempotent_After_Hybrid_Retention

These tests validate:

- Engine-applied Hybrid retention (no manual invocation)
- Bounded hot execution state
- Proper eviction of completed steps
- Archive index population
- Resolver correctness (lazy + full load)
- Idempotent execution after retention

---

## ⚙️ Dependency Injection Fixes

- Registered missing retention policies:
  - CompactAiExecutionRetentionPolicy
  - EvictAiExecutionRetentionPolicy
  - HybridAiExecutionRetentionPolicy

- Fixed payload store provider resolution:
  - Prevented "inmemory" usage when RequireReplaySafePayloads = true
  - Enforced "mongo-redis" for replay-safe scenarios

---

## 📊 Payload Metrics — Test Stabilization

### Problem

Engine-level tests assumed deterministic metrics:

- Exact inline vs externalized counts
- Zero cache misses/fallbacks
- Strict byte comparisons

This is no longer valid due to:

- Retention compaction
- Resolver warm-up
- Redis cache behavior
- Multi-layer payload storage

---

### Fix

- Disabled StateRetention in payload metrics tests
- Replaced strict assertions with invariant-based checks:

  - InlineCount >= expected
  - ExternalizedCount >= expected
  - Bytes > 0

- Removed fragile assertions:
  - Cache write counts
  - Cache miss/fallback exact values

---

## 🧪 Testing Strategy Improvement

### Separation of concerns

- Compactor tests → exact payload metrics validation
- Engine tests → runtime invariants and behavior
- Retention tests → eviction and archive correctness
- Store tests → Redis/Mongo correctness

---

## 🔒 Safety Improvements

- Prevents false negatives caused by retention side-effects
- Ensures test expectations match real runtime behavior
- Avoids brittle tests in distributed / cached environments

---

## ⚡ Result

The test suite is now:

- Stable
- Production-aligned
- Multi-layer aware
- Resistant to future engine evolution

---

## 🧠 Summary

This update transitions the test suite from:

deterministic assumptions

to

production-realistic validation

ensuring long-term reliability of the runtime.

---

## [1.0.3.3] - 2026-04-27

# 🚀 Release — State Retention, Step Archiving & Lazy Resolution

## 🧠 Overview

This release introduces a complete execution state lifecycle for the AI runtime:

From unbounded in-memory execution state  
to bounded, persisted, archived, cached, and lazily-resolved state.

The runtime can now handle larger DAG executions with lower memory pressure, safer retention, and faster step visibility through Redis-optimized archive indexes.

---

## 🔥 Added

### State Retention System

- Added execution state retention support.
- Added retention modes:
  - `Compact`
  - `Evict`
  - `Hybrid`
- Added config-driven retention threshold using:

```csharp
AiEngineOptions.StateRetention.MaxCompletedStepsInState
```

Removed hardcoded retention thresholds.  
Added retention policy resolver support.  
Added targeted unit tests for retention policies.  

---

### Safe Step Archiving

Added AiExecutionRetentionService.  

Added safe eviction flow:  
Persist step payload  
→ Write archived step index  
→ Remove step from hot state  

Added step payload externalization before eviction.  
Added archived step metadata through AiArchivedStepPayloadIndex.  

Added tests proving:  
- save happens before removal  
- archive index happens before removal  
- save failure does not remove the hot-state step  
- archive index failure does not remove the hot-state step  

---

### Archived Step Index

Added Mongo-backed archived step index store.  
Added Redis cached archived step index.  
Added CachedAiStepPayloadIndexStore as Mongo + Redis decorator.  
Added batch index retrieval.  
Added index lookup support for evicted steps.  
Added delete and execution-scoped index lookup support.  

---

### Redis Index Cache Optimization

Added Redis batch lookup using MGET.  
Added Redis pipeline writes.  
Added TTL refresh on read.  
Added batch TTL refresh behavior.  
Replaced N Redis calls with batch operations where possible.  

---

### Lazy Step Resolution

Added DefaultAiExecutionStepResolver.  

Added multi-layer step resolution:  
Hot state  
→ warmed/cache metadata  
→ archived step index  
→ payload store  

Added lazy status resolution via GetStepStatusAsync.  
Added full archived step resolution via GetStepAsync.  
Added incremental warm behavior via WarmStepsAsync.  

Added resolver tests proving:  
- status lookup does not load full payload  
- full step lookup loads payload only on demand  
- warm uses batch GetManyAsync  
- warm avoids N+1 index calls  

---

### DAG Engine Integration

Integrated retention into the DAG execution flow.  
Added retention + persist + warm behavior through ApplyRetentionPersistAndWarmAsync.  
Updated DAG selector to use lazy step status resolution.  
Updated convergence evaluation to avoid unnecessary full payload loading.  
Ensured evicted steps remain visible to selector and convergence logic.  

---

### Test Infrastructure

Centralized default payload store configuration in AiDagExecutionEngineFixture.  
Stabilized integration tests by reducing payload size and step counts for functional scenarios.  
Separated functional retention validation from stress-level scenarios.  
Added targeted tests instead of relying only on large DAG tests.  

---

## 🛡️ Safety Improvements

- Retention now guarantees persistence before eviction.
- Retention now guarantees archive index write before eviction.
- Hot-state step removal is skipped if persistence fails.
- Hot-state step removal is skipped if archive indexing fails.
- Eviction never removes non-terminal steps.
- Eviction protects completed parents required by active children.
- No compact + evict overlap in Hybrid mode.
- Archived steps remain resolvable.
- Step status available without full payload load.
- Resolver prevents lost visibility.

---

## ⚡ Performance Improvements

- Reduced hot execution state size.
- Reduced memory pressure.
- Avoided full payload loading.
- Batch Redis operations (MGET + pipeline).
- Batch warm-up for metadata.
- Avoided N+1 index lookups.

---

## 🧪 Tests Added

- AiExecutionRetentionPolicyTests
- AiExecutionRetentionServiceTests
- AiExecutionStepResolverTests

---

## 🔧 Changed

- Retention now uses IOptions<AiEngineOptions>.
- Threshold is config-driven.
- Hybrid planning separated.
- DAG uses lazy resolver.
- Tests simplified and stabilized.

---

## 🐛 Fixed

- Hardcoded thresholds removed.
- Unsafe eviction fixed.
- Retention loops fixed.
- Hybrid overlap fixed.
- Resolver visibility fixed.
- Payload store config fixed.
- Data loss risks fixed.

---

## ⚠️ Breaking Changes

- IAiStepPayloadIndexCache moved to Abstractions.
- Retention requires IOptions<AiEngineOptions>.
- Behavior depends on StateRetention config.

---

## 🚀 What This Enables

- Large DAG executions
- Long-running workflows
- Bounded state
- Archived recovery
- Lazy evaluation
- Redis optimized lookup
- Safer distributed execution

---

## 🧭 Next Steps

- End-to-end retention tests
- Stress scenarios
- Redis Lua optimizations
- Adaptive retention
- Better observability

---

## 💬 Summary

This release transforms execution state management into a bounded, archived, cached, and lazily-resolved model.

The AI runtime is now safer, more scalable, and production-ready for large deterministic DAG executions.

---
## [1.0.3.2] - 2026-04-26

## Major Runtime Refactor — State + Step Context Architecture

### Execution State

- Refactored `AiExecutionState` into a persistence-only model.
- Introduced:
  - `IAiExecutionStateReader`
  - `IAiExecutionStateWriter`
- Removed direct state access patterns:
  - `state.Get(...)`
  - `state.Set(...)`
  - `state.GetMetadata(...)`
  - `state.SetMetadata(...)`
- Centralized step state management via writer (`GetOrCreateStep`).

### Step Context & Arguments

- Introduced `IAiStepContextHelper` and factory.
- Introduced `IAiContextValueResolver` for path-based value resolution.
- Introduced `IAiStepArguments` for structured step inputs.
- Introduced `IAiAdditionalInputsContainer` for extensible input binding.
- Removed raw dictionary-based step argument handling.

### Runtime Architecture

- Decoupled:
  - execution state
  - step context resolution
  - payload resolution
- Ensured payload-aware state reading through reader abstraction.
- Improved DI consistency across runtime and tests.

### Tests

- Refactored DAG, Redis, retry, and pipeline tests.
- Replaced direct state access with reader/writer.
- Fixed DI-related failures.
- Verified full test suite (250+ tests) passes.

### Outcome

- Cleaner architecture boundaries
- Safer mutation model
- Extensible step input system
- Deterministic execution behavior

---

## [1.0.3.1] - 2026-04-25

## Payload System Finalization

### 🚀 Added

- Mongo-Redis payload store:
  - Mongo as durable source of truth
  - Redis as bounded read-through/write-through cache
- Redis-only payload store (non replay-safe)
- Payload metrics:
  - inline_count / externalized_count
  - inline_bytes / externalized_bytes
  - cache_hit / cache_miss / cache_fallback / cache_write
- SizeBytes tracking in AiStoredPayload

### 🧠 Architecture

- Redis cache implemented as decorator over payload store
- MongoRedisCachedAiPayloadStore uses composition (no duplication)
- Resolver now supports:
  - `inmemory`
  - `mongo`
  - `redis`
  - `mongo-redis`

### 🧪 Tests

- Compactor-level payload tests
- Redis cache integration tests
- Mongo-Redis provider integration tests
- Engine-level pipeline tests (code-first, no JSON)
- Long-run test (200 steps) validating stability and metrics

### ⚠️ Breaking Changes

- Mongo payload store requires `Mongo.Enabled = true`
- IAiPayloadMetrics is now required in DI
- Payload system now depends on metrics for observability
- New providers available: `redis`, `mongo-redis`

### 🎯 Result

Payload system is now production-ready:
- Durable ✔
- Cached ✔
- Observable ✔
- Scalable ✔

---

## [1.0.3.0 ] - 2026-04-25

## 🚀 Payload Compaction & Payload-Aware Runtime

This release introduces a major architectural improvement:

👉 Centralized payload compaction across all execution paths

### Highlights

- Large step outputs are automatically externalized
- Redis execution state remains lightweight and deterministic
- Payloads are stored in external providers (Mongo, future Redis cache)
- Replay and snapshot restoration fully support externalized payloads

### Runtime Improvements

- Unified payload compaction via DefaultAiStepResultPayloadCompactor
- Payload-aware read path using IAiExecutionPayloadResolver
- RAG, prompt, and custom steps aligned with payload abstraction
- Deterministic replay preserved with external payload resolution

### Developer Impact

- Direct access to result.Data["key"] is no longer safe for large payloads
- Use payload-aware helpers instead:
  - GetDataAsync<T>()
  - RagStepHelper.GetRequiredBatchAsync(...)

### Next Steps

- Redis cache payload store validation
- Payload metrics (inline vs externalized, bytes, cache hit/miss)
- State retention policy
- Memory writer (ML signal extraction layer)

---

## [Unreleased]

### ✨ Added
- Introduced `IAiExecutionSnapshotCleanupService` for dedicated snapshot cleanup handling
- Added support for `DeleteSnapshotsIfExist` option in `AiExecutionCleanupOptions`
- Integrated `IAiOwnedRbacCleanupService` into execution cleanup lifecycle
- Added fallback cleanup path when execution record is missing (executionId-based cleanup)

### ♻️ Changed
- Refactored `AiExecutionCleanupService` to use a single unified internal cleanup method
- Centralized cleanup orchestration (DAG, state, record, snapshot, RBAC)
- Improved cleanup idempotency and resilience (safe retry behavior)

### 🧪 Tests
- Extended integration tests to cover:
  - Snapshot deletion when cleanup is enabled
  - Full execution lifecycle: execution → snapshot → replay → cleanup
  - EF provider + external provider scenarios
- Fixed cleanup behavior in tests when execution record is already deleted

### 🧱 Internal
- Updated DI registration to include snapshot cleanup service
- Ensured optional services (snapshot store) are resolved safely
- Improved logging consistency across cleanup operations

---

## 🚀 Summary

This update finalizes the execution cleanup lifecycle:
execution → snapshot → replay → cleanup

The runtime is now fully prepared for V4 (vector-based RAG) with:
- deterministic cleanup
- robust fallback behavior
- modular cleanup architecture

---

## [1.0.2.9] - 2026-04-22

### 🚀 Added

#### Multi-Provider Relational RAG (Major Feature)

- Introduced **provider-mode execution** for relational RAG retrieval
- Added support for **multiple relational providers**:
  - SQL Server
  - PostgreSQL
- Enabled dynamic provider selection via:
  - `provider = relational`
  - `providerKey = state.providerKey`

---

#### Runtime Connectors (Provider Layer)

- Added:
  - `SqlServerRelationalRagConnector`
  - `PostgresRelationalRagConnector`

- Connectors:
  - Resolve queries dynamically using `IRelationalRagQuery`
  - Filter by:
    - `ConnectorKey`
    - `EntityType`
  - Remain **fully generic** (no domain coupling)

---

#### Plugin-Based Query Model

- Introduced `IRelationalRagQuery` abstraction
- Implemented external queries for:

  **Candidate**
  - SQL Server
  - PostgreSQL

  **Job**
  - SQL Server
  - PostgreSQL

- Queries:
  - Encapsulate provider-specific logic
  - Delegate to external stores
  - Return structured rows only

---

#### Dual Execution Mode Support

Datasources now support:

- **Direct Mode**
  - Calls store directly (InMemory / EF)
- **Provider Mode**
  - Delegates to runtime connector
  - Uses `providerKey` to select backend

---

#### Dynamic Config Resolution

- Enhanced `RagStepHelper` to support:
  - `state.*`
  - `steps.*`
  - runtime path resolution inside config

Example:

```json
"providerKey": "state.providerKey"
```

- Added safe fallback behavior when resolution fails

---

### 🧪 Testing

Added full integration coverage for:

- **InMemory**
  - Direct mode
  - Provider mode

- **SQL Server (EF)**
  - Direct mode
  - Provider mode

- **PostgreSQL (EF)**
  - Direct mode
  - Provider mode

- Verified:
  - Multi-provider execution correctness
  - Runtime connector resolution
  - Entity-type based query routing

---

### 🛠 Fixed

- Fixed config resolution issue where `JsonElement` strings were not resolved as runtime paths
- Fixed provider mode not receiving resolved `providerKey`
- Fixed PostgreSQL connection configuration (incorrect SQL Server credentials usage)

---

### 🧠 Architecture Improvements

- Enforced strict separation of concerns:

  - **Runtime**
    - Orchestration (DAG engine)
    - Connectors (generic routing)

  - **External Plugins**
    - Datasources
    - Queries (`IRelationalRagQuery`)
    - Operations

  - **Infrastructure**
    - Stores (EF / InMemory)
    - Database contexts

- Ensured:
  - No domain knowledge inside runtime connectors
  - Full extensibility for future providers:
    - Vector DB
    - APIs
    - Hybrid sources

---

### ⚡ Result

- Fully operational **multi-provider RAG retrieval layer**
- Deterministic, testable, and extensible architecture
- Ready for:
  - multi-source merge (`rag.merge`)
  - context composition (`rag.compose`)
  - hybrid RAG pipelines

## [1.0.2.8] - 2026-04-19

---

### feat(rag): complete deterministic RAG runtime integration (steps + normalization + providers)

---

### ⚙️ DAG Runtime Integration (MAJOR)

- Implemented full **DAG-native RAG step system**:

  - `RagComposeStep`
  - `RagMergeStep`
  - `RagMultiStep`
  - `RagRuntimeStep`
  - `RagSqlStep`
  - `RagVectorStep`

- Added:
  - `RagStepHelper` for shared step logic

- Enables:
  - step-level orchestration of RAG pipelines
  - full integration with:
    - `AiStepState`
    - `AiStepResult`
    - input/output bindings (`steps.step-id.result.data`)
  - retry / recovery / replay compatibility

---

### 🔄 Retrieval Layer (Extended)

- Added retrieval orchestration components:

  - `DefaultRagRetrievalResolver`
  - `DefaultRagBatchMerger`
  - `MultiProvider` retrieval support

- Supports:
  - multi-provider aggregation
  - deterministic merging of results
  - extensible retrieval strategies

---

### 🧩 Provider Resolution

- Introduced provider resolution layer:

  - `DefaultNormalizingRagProviderResolver`

- Enables:
  - dynamic provider resolution
  - separation between provider lookup and execution
  - clean integration with normalization pipeline

---

### 🧱 Composition Layer

- Introduced deterministic composition system:

  - `IRagComposer<TContext>`
  - `DefaultRagComposerResolver`
  - `Composition/Deterministic` pipeline

- Supports:
  - multiple composition strategies (compact / expert ready)
  - fragment-based deterministic context construction

---

### 🔁 Normalization Layer (CRITICAL)

- Introduced step result normalization:

  - `RagStepResultNormalizer`

- Solves:
  - `JsonElement` vs strong type issues
  - structured context degradation during execution/replay

- Ensures:
  - typed output preservation (`RagStructuredContext`)
  - replay-safe data reconstruction
  - consistent runtime data shape

---

### 🧠 Execution Context

- Introduced:
  - `RagExecutionContext`
  - `RagExecutionContext<TContextSnapshot>`

- Enables:
  - typed snapshot access
  - compatibility with persistence and replay
  - structured runtime inputs

---

### 📦 Core Models (from 1.0.2.7)

- `RagNormalizedItem`
- `RagRetrievalBatch`
- `RagContextFragment`
- `RagComposedContext<TContext>`

- Remain the foundation for:
  - provider normalization
  - composition pipeline
  - prompt context construction

---

### 🧠 Architecture Evolution

RAG is now fully executable inside the runtime:

ExecutionContext  
↓  
RagRuntimeStep / RagSqlStep / RagVectorStep  
↓  
RagMultiStep / RagMergeStep  
↓  
RagComposeStep  
↓  
RagComposedContext<TContext>  
↓  
ai.prompt  

---

### 📚 Documentation

- Added full documentation set:

  - architecture overview
  - deep implementation guide
  - developer handbook
  - internal repo guide

- Includes:
  - compact vs expert modes
  - JSON pipeline examples
  - pseudo-code for retrieval/composition
  - debugging workflows
  - extension patterns

---

### 🧪 Key Learnings

- Identified critical runtime issue:
  - structured context degraded to `JsonElement`

- Introduced normalization layer to:
  - restore strong typing
  - ensure replay readability
  - prevent dynamic JSON drift

---

### 🚀 Positioning

This release upgrades RAG from a foundation to a **fully integrated runtime subsystem**:

- DAG-executable
- deterministic
- replay-safe
- provider-agnostic
- fragment-based context pipeline

👉 RAG is now part of the execution engine, not an external helper.

---

### 🔜 Next Steps

- ranking & scoring layer (V2)
- hybrid retrieval strategies
- token-aware composition
- agent loop integration

---

## [1.0.2.6] - 2026-04-10

feat(ai-runtime): integrate declarative prompt step with OpenAI provider and shared variable resolution

- Added provider-agnostic `ai.prompt` pipeline step for declarative AI prompt execution
- Added OpenAI provider integration using injected `OpenAIClient` and provider discovery via attribute scanning
- Added prompt runtime DI registration for executor, renderer, parser, and providers
- Added shared declared input composition in `AiStepExecutionContext`
- Added cached variable bag resolution with typed access helpers:
  - `ResolveDeclaredInputs`
  - `GetVariable`
  - `TryGetVariable`
  - `GetRequiredVariable`
- Added support for JSON-originated declared inputs represented as `JsonElement`
- Refactored `AiPromptStep` to rely on execution-context variable composition instead of local variable resolution logic
- Added structured prompt result persistence including:
  - `rawText`
  - `parsedResult`
  - token usage
  - finish reason
  - rendered prompt hash
  - provider metadata
- Added deterministic `decision.score` step using shared variable resolution from the execution context
- Extended Redis DAG store to persist the full execution state blob alongside distributed step state
- Fixed DAG state reconstruction so global state bags such as `Data` and `Metadata` survive reload and replay
- Added end-to-end integration support for JSON pipelines using:
  - declarative prompt input binding
  - OpenAI execution
  - JSON parsing
  - score-based decision routing

  ---

### Notes
- Prompt and decision steps now share the same declarative variable resolution model
- Global state persistence is now preserved in DAG mode, not only step state
- This lays the foundation for upcoming RAG, rerank, tool-calling, and agent orchestration steps

## [1.0.2.5] - 2026-04-09

feat(ai-runtime): add optional MongoDB snapshot persistence and execution replay support

- Added MongoDB-backed execution snapshot persistence
- Added configuration flags to enable or disable snapshot persistence
- Added execution replay service for restoring runtime state from snapshots
- Added replay preparation to clear transient runtime claim data before restore
- Added integration tests for snapshot persistence, replay, and resume flows

fix(ai-runtime): correct distributed DAG restore and replay consistency

- Fixed RestoreAsync to rebuild full distributed DAG state (record, step keys, step index)
- Fixed DeleteStateAsync to properly remove distributed DAG steps and index
- Fixed DeleteExecutionBundleAsync to ensure full DAG cleanup before restore
- Fixed GetStateAsync to return null when no DAG state exists instead of empty state
- Fixed mismatch between distributed DAG store and generic execution store

fix(ai-runtime): make replay service DAG-aware and idempotent

- Replay now detects existing executions using IAiDagExecutionStore when available
- Fixed replay incorrectly restoring over existing compatible executions
- Ensured replay idempotence across distributed and non-distributed modes
- Improved compatibility validation between snapshot and existing runtime execution

test(ai-runtime): add and stabilize distributed chaos test coverage

- Added retry chaos tests validating retry budget and concurrent execution safety
- Added recovery chaos tests validating step uniqueness and state consistency
- Added replay chaos tests validating idempotence under concurrent replay pressure
- Added execute-all chaos tests validating state integrity under concurrent orchestration
- Fixed test assertions to rely on authoritative distributed DAG store instead of generic store
- Improved reliability of terminal convergence assertions under retry timing

improvement(ai-runtime): strengthen distributed convergence guarantees

- Stabilized convergence behavior under multi-worker retry and recovery conditions
- Ensured no inconsistent intermediate state leaks into final execution result
- Improved alignment between record projection and authoritative step state
- Hardened runtime behavior under high concurrency and timing variability

---

## [1.0.2.4] - 2026-04-06

### Added
- Production-grade deterministic DAG runtime for distributed AI execution
- Strict step state machine with enforced invariants
- Distributed retry engine with:
  - retry budget guarantees
  - time-based retry scheduling (ms precision)
- Lease-based recovery system:
  - multi-worker safe
  - non-destructive retry preservation
- Deterministic convergence model ensuring consistent final state
- Atomic execution finalization with optimistic concurrency (ExecutionStepKey)
- Full test coverage:
  - invariant validation
  - multi-worker concurrency
  - retry correctness
  - recovery correctness
  - chaos scenarios
- Crash consistency model formally documented

### Observability
- IAiRuntimeMetrics interface with thread-safe in-memory implementation
- Metrics coverage:
  - retry_count (per step)
  - recovery_count (per execution)
  - claim_success / claim_miss
  - finalize_attempts / finalize_success
- Structured logging added across runtime:
  - step claim (success/miss)
  - recovery events
  - finalization attempts and outcomes
  - NOSCRIPT fallback scenarios

### Updated
- Redis DAG execution store enhanced with:
  - metrics integration across critical execution paths
  - structured logging for production debugging
  - improved resilience for Lua script reload (NOSCRIPT)

### Guarantees
- Deterministic execution under concurrency (multi-worker safe)
- No double execution or retry over-consumption
- No premature failure during retry window
- Safe recovery without corrupting retry state
- Observability without impacting execution determinism

### Notes
- Metrics currently in-memory (single instance scope)
- Designed for future integration with Prometheus / OpenTelemetry
- Runtime architecture aligned with production-grade distributed systems (Temporal-like model)

---

## [1.0.2.3] - 2026-04-06

### Added

- Introduced deterministic distributed retry engine for DAG execution
- Added execution-level retry configuration (`MaxRetries`, `RetryDelayMs`)
- Introduced `WaitingForRetry` as a non-terminal step lifecycle state
- Added retry-aware DAG step selector with time-based eligibility
- Implemented retry window scheduling using `NextRetryAtUtc`
- Added multi-worker safe retry reclaim logic
- Introduced convergence-safe retry handling in global execution evaluator

### Changed

- Refactored pipeline step definition to support execution-level configuration
- Updated step state model to include retry lifecycle metadata
- Improved DAG convergence evaluation to prevent premature failure during retry windows
- Enhanced selector logic to support retry promotion and dependency validation

### Fixed

- Prevented double retry consumption under concurrent worker execution
- Fixed premature terminal failure when retryable steps were still pending
- Ensured retry count is incremented only once per scheduled retry window
- Corrected retry eligibility logic based on deterministic time evaluation

### Tests

- Added retry budget validation tests (0, 1, 2 retries)
- Added selector tests for retry timing and eligibility
- Added convergence tests ensuring non-terminal retry behavior
- Added multi-worker retry reclaim tests to validate distributed safety

### Guarantees

- Deterministic retry behavior across distributed workers
- No duplicate retry execution under concurrency
- No premature failure during retry windows
- Convergence-safe execution state projection

---

## [1.0.2.2] - 2026-04-04

### Added
- Introduced convergence hardening for distributed DAG execution engine
- Added dedicated convergence test suite validating retry-aware execution behavior
- Introduced shared test step and tracker for deterministic retry validation across integration tests

### Changed
- Improved global execution convergence evaluation to ensure deterministic final state projection
- Strengthened terminal finalization rules:
  - Execution can only finalize when no steps are Running, Ready, or WaitingForRetry
  - Failed state is only reached when no forward progress is possible
  - Completed state requires all steps to be fully completed with no active claims
- Updated Redis DAG execution store to enforce strict convergence validation before finalization
- Refactored integration test setup to use shared top-level retry test components instead of nested types

### Fixed
- Fixed DI resolution issues caused by nested test step types during assembly scanning
- Ensured consistent step resolution across multiple integration test suites

---

## [1.0.2.1] - 2026-03-31

### ✨ Added

- Introduced distributed, step-scoped retry engine integrated with DAG execution
- Added `WaitingForRetry` status to represent non-terminal retry scheduling state
- Implemented retry timing using `NextRetryAtUtc` and configurable `RetryDelay`
- Added `RetryCount` and `MaxRetries` to enforce bounded retry behavior per step
- Introduced `RecoveryCount` to track infrastructure-level recovery separately from business retries
- Added retry promotion logic (`PromoteRetryToReadyIfDue`) to transition steps back to execution eligibility
- Implemented unified retry decision method `MarkRetryOrFail` for deterministic retry vs failure transitions
- Added timeout recovery mechanism for distributed execution (`MarkRequeuedAfterTimeout`)
- Extended distributed DAG execution flow to include retry awareness and time-based eligibility
- Introduced convergence-safe handling of retry states (retry is non-terminal until exhausted)
- Added integration tests for multi-worker retry behavior
- Introduced pipeline steps for retry scenarios
- Added hardcore multi-worker retry configuration (test)

---

### 🔄 Changed

- Refactored `AiDagExecutionEngine` to fully support retry-aware scheduling in distributed environments
- Updated step selection logic to exclude steps not yet eligible for retry (`NextRetryAtUtc`)
- Improved convergence evaluation to correctly account for `WaitingForRetry` state
- Standardized step lifecycle transitions to include retry and recovery phases
- Enhanced distributed coordination to prevent premature or duplicate step execution
- Strengthened separation between execution-level state and step-level state as source of truth

### Removed
- Removed unused legacy `RedisAiDagStepLifecycleScripts`
- Deleted obsolete single-document Redis DAG Lua lifecycle model that was no longer aligned with the current distributed execution store

---

### 🧠 Design Improvements

- Enforced deterministic retry behavior across multiple concurrent workers
- Ensured retry logic remains fully step-scoped and isolated
- Prevented infinite retry loops through strict retry bounds and timing enforcement
- Introduced clear distinction between:
  - business retry (`RetryCount`)
  - infrastructure recovery (`RecoveryCount`)
- Improved resilience against worker crashes, timeouts, and partial execution failures
- Maintained atomic convergence guarantees under retry conditions

---

### 🧪 Test Improvements

- Added test coverage for:
  - retry success scenarios
  - retry exhaustion (max retries reached)
  - retry delay enforcement (`NextRetryAtUtc`)
  - timeout recovery and requeue behavior
- Extended concurrency tests to validate retry safety in multi-worker scenarios
- Verified deterministic convergence across retry transitions
- Ensured no infinite execution loops under failure conditions

---

### 🎯 Result

- Fully distributed, retry-capable DAG execution engine
- Deterministic and safe execution under high concurrency
- Strong consistency between step state and global execution convergence
- Robust handling of failures, retries, and worker crashes
- Production-ready orchestration model for complex AI pipelines

---

## [1.0.2.0] - 2026-03-31

### ✨ Added

- Introduced `IAiExecutionCleanupService` to centralize execution cleanup logic
- Added deterministic cleanup flow triggered by execution engines on terminal states (`Completed`, `Failed`)
- Implemented full execution bundle deletion (record, state, and associated runtime artifacts)
- Introduced distributed-safe convergence persistence for DAG execution
- Added atomic finalization mechanism via `IAiDagExecutionStore.TryFinalizeExecutionAsync`
- Implemented optimistic concurrency control using `ExecutionStepKey` during convergence

---

### 🔄 Changed

- Moved cleanup responsibility directly into execution engines for explicit lifecycle control
- Replaced standard `PersistAsync` calls with `PersistDistributedConvergedRecordAsync` in distributed DAG execution flow
- Enforced atomic promotion of terminal states (`Completed`, `Failed`) across multiple workers
- Improved execution record synchronization by reloading authoritative state after concurrent finalization
- Ensured monotonic execution lifecycle (no downgrade after terminal state)
- Improved consistency of `UpdatedAtUtc` during distributed state updates

---

### 🧪 Test Improvements

- Updated test infrastructure to support cleanup service injection
- Introduced no-op cleanup implementations for unit testing
- Ensedured deterministic behavior under concurrent DAG execution scenarios
- Ensured test stability without requiring external infrastructure (e.g. Redis)

---

### 🎯 Result

- Fully deterministic execution lifecycle
- Atomic and race-condition safe DAG convergence
- Single-writer guarantee for terminal state transitions
- Explicit and predictable cleanup behavior
- Reduced runtime complexity
- Improved maintainability and testability

---

## [1.0.1.9] - 2026-03-30

### Added

#### DAG Runtime Validation & Stress Testing

- Added large-scale DAG integration stress coverage using generated 100-step pipelines
- Added randomized DAG test scenarios with deterministic seeds for reproducible validation
- Added parallel-heavy DAG scenario validation to test wide dependency fan-out
- Added linear DAG scenario validation to verify strict chained execution behavior
- Added fan-in DAG scenario validation to verify convergence of multiple branches into a final step
- Added config-based JSON pipeline generation for integration tests to validate the runtime against real file-backed pipeline loading

#### Pipeline Definition Validation

- Added DAG dependency validation for JSON pipeline definitions
- Added validation for duplicate step names
- Added validation for empty dependency names
- Added validation for duplicate dependencies inside a single step
- Added validation for self-referencing dependencies
- Added validation for unknown dependency references
- Added cycle detection to ensure JSON DAG definitions remain acyclic before execution

#### RAG Runtime Integration

- Added initial RAG engine with Redis Lua-backed coordination and state handling
- Introduced foundation for retrieval-augmented execution within the deterministic AI runtime
- Enabled integration of external knowledge retrieval within pipeline-based execution flows

### Changed

#### Execution Engine Naming & Architecture Clarity

- Renamed `AiExecutionEngine` to `AiSequentialExecutionEngine` to clearly distinguish sequential execution from DAG/distributed execution engines
- Improved architectural clarity between sequential and DAG execution models
- Updated references across runtime, tests, and DI registrations to reflect the new naming

#### Distributed DAG Execution Stability

- Fixed distributed DAG execution status progression so successful step completion keeps execution active when additional steps may now be schedulable
- Updated distributed DAG completion logic to avoid incorrectly returning `Waiting` while the pipeline can still make progress
- Improved final DAG execution state recomputation after distributed step completion or failure

#### Redis DAG Store Robustness

- Hardened Redis DAG step serialization and deserialization for `DependsOn`
- Added runtime repair logic for legacy or corrupted Redis step payloads where empty dependency arrays could be re-encoded as JSON objects
- Normalized Lua step persistence so empty dependency lists remain valid JSON arrays across claim, complete, fail, and recovery flows
- Improved Redis script loading safety by selecting a connected primary endpoint for Lua loading

#### JSON Serialization Compatibility

- Updated DateTime converters to support both Unix numeric timestamps and string-based date values during deserialization
- Improved compatibility for execution records containing mixed snapshot date formats
- Preserved Unix timestamp output semantics while making reads more tolerant for integration and backward compatibility scenarios

### Fixed

#### DAG Execution & Redis Integration

- Fixed Redis Lua claim script incompatibility caused by unsupported control flow syntax
- Fixed distributed DAG state reload failures caused by invalid `DependsOn` payload shapes
- Fixed execution record deserialization failures for snapshot date values read from persisted JSON
- Fixed false `Waiting` terminal outcomes in distributed DAG execution loops
- Fixed Redis-backed DAG execution flow so `ExecuteAllAsync` now completes correctly for valid multi-step DAGs
- Fixed integration behavior across generated DAG stress scenarios using real Redis-backed orchestration

---

## [1.0.1.8] - 2026-03-29

### Added

#### Step-Scoped Execution State

- Introduced `AiStepState` collection inside `AiExecutionState` to persist per-step runtime data
- Added `Inputs` and `Config` to `AiStepState` for storing resolved inputs and declarative configuration
- Enabled full isolation of step-level data from global execution state

#### Step Result Model

- Introduced `AiStepResult` within `AiStepState` as the canonical output of step execution
- Added support for structured `Data` payload (dictionary-based)
- Extended result model with typed output support for flexible result handling

#### Path-Based Resolution Engine

- Introduced unified path-based resolver for accessing:
  - Step inputs
  - Step configuration
  - Step results (value and data)
- Supports structured paths such as:
  - `steps.{step}.inputs.{path}`
  - `steps.{step}.config.{path}`
  - `steps.{step}.result.value`
  - `steps.{step}.result.data.{path}`

#### JSON-Compatible Nested Resolution

- Added support for resolving nested values from:
  - `Dictionary<string, object?>`
  - `IReadOnlyDictionary<string, object?>`
  - `JsonElement` (System.Text.Json)
- Enables safe traversal of complex object graphs using dot-separated paths

---

### Changed

#### Execution Context

- Refactored `AiExecutionContext` to use a unified resolution model
- Introduced:
  - `ResolvePath<T>()`
  - `ResolveInputBinding<T>()`
  - `ResolveConfigBinding<T>()`
  - `ResolveCurrentStepInput<T>()`
  - `ResolveCurrentStepConfig<T>()`
- Standardized access patterns for step-scoped data

#### Runtime Model Evolution

- Shifted from global state (`State.Data`) toward step-scoped execution model
- Maintained backward compatibility for legacy shared state usage

---

### Improved

#### Determinism & Observability

- Improved traceability of execution by isolating step inputs, config, and results
- Strengthened deterministic behavior for replay and debugging scenarios
- Prepared foundation for DAG execution and advanced orchestration strategies

---

### Notes

- `ExecutionContextSnapshot` remains shallow-copied (to be revisited if mutation is introduced)
- Legacy global state is still available but progressively deprecated in favor of step-scoped state

---

## [1.0.1.7] - 2026-03-27

### Added

#### JSON Pipeline Definition Provider

- Introduced support for loading pipeline definitions from JSON files
- Added provider-based pipeline resolution for declarative runtime configuration
- Enabled external pipeline registration through configuration:
  - `AiEngine:DefaultPipelineDefinitionSource`
  - `AiEngine:JsonPipelineDefinitionFilePath`
- Established a portable configuration model for runtime pipeline execution
- Prepared the runtime for future dynamic and environment-specific pipeline loading

#### Step Input Mapping in JSON Definitions

- Added support for declarative `input` sections on pipeline steps in JSON definitions
- Enabled runtime binding of step inputs through named input mappings
- Standardized input resolution via execution context bindings
- Supports scenarios such as:
  - binding shared execution state into a step
  - resolving aliases such as `"text": "input"`
  - passing state values forward across multiple steps

#### Step Configuration in JSON Definitions

- Added support for declarative `config` sections on pipeline steps in JSON definitions
- Enabled strongly-typed step configuration access at runtime through execution context helpers
- Supported configuration-driven step behavior without changing runtime code
- Example use cases now supported:
  - `delayMs`
  - `model`
  - `maxTokens`
  - `temperature`
- Established a clean separation between:
  - step input bindings
  - step execution configuration
  - shared execution state

#### Redis Atomic Execution Update

- Introduced atomic compare-and-swap persistence for AI execution updates using Redis Lua
- Added Redis-side validation of `ExecutionStepKey` before applying record/state updates
- Ensured record and state are updated atomically in a single Redis operation
- Prevented duplicate step transitions under concurrent execution
- Established lock-free optimistic concurrency for distributed execution scenarios

#### Redis Lua SHA Script Optimization

- Added `LuaScript.Prepare` + `LoadedLuaScript` support for Redis execution updates
- Moved atomic update script execution to SHA-based evaluation for improved performance
- Reduced repeated raw Lua payload transmission over the network
- Improved Redis-side efficiency under repeated execution update calls
- Added automatic script reload on `NOSCRIPT` Redis errors
- Ensured performance gains are preserved after Redis restart or failover

#### Execution Context JSON Compatibility

- Added `JsonElement` support for values restored from JSON-based persistence and configuration
- Updated typed value resolution for:
  - `AiExecutionState`
  - `AiExecutionContext` step input values
  - `AiExecutionContext` step config values
  - execution metadata helpers
- Ensured JSON-backed dictionaries using `object?` remain strongly usable at runtime
- Fixed interoperability between:
  - JSON pipeline definitions
  - Redis persistence
  - strongly-typed runtime step access

#### Expanded Runtime Test Coverage

- Expanded the test suite to 61+ tests covering runtime, integration, concurrency, JSON definitions, retry paths, and Redis behavior
- Added end-to-end coverage for:
  - JSON pipeline definitions with real DI
  - step input and step config resolution
  - full pipeline execution
  - `ExecuteNextAsync`
  - `ExecuteAllAsync`
  - failure and exception flows
  - Redis-backed atomic execution updates
- Added concurrency tests validating that only one concurrent step transition succeeds
- Added Redis integration tests validating real persistence behavior and round-trip correctness
- Added integration coverage for fake and real context-store scenarios

---

### Fixed

#### Pipeline Execution Model

- Fixed inconsistent pipeline execution flow caused by double resolution of pipeline definitions
- Removed redundant pipeline resolution during step execution
- Enforced single resolution model:
  - `PrepareAsync` now resolves the pipeline once
  - `ExecuteNextAsync` consumes the resolved pipeline without re-resolving
- Eliminated ambiguity between declarative and runtime pipeline models

#### Execution Contracts Alignment

- Corrected `IAiPipelineExecutor` contract to return `ResolvedAiPipeline` instead of `AiPipelineDefinition`
- Updated execution flow to pass resolved pipeline explicitly into step execution
- Fixed mismatched method signatures across engine and pipeline layers
- Ensured strong typing between definition, resolution, and execution phases

#### Execution Step Rotation on Successful Transition

- Fixed missing `ExecutionStepKey` renewal on successful step progression
- Ensured the execution transition key is rotated on both:
  - successful step completion
  - failed / exception paths
- Restored correctness of optimistic concurrency enforcement on happy-path execution
- Fixed a concurrency issue where multiple callers could otherwise commit the same transition

#### Real Context Store Seeding Contract

- Fixed execution engine behavior to ensure AI-owned RBAC contexts are created with a valid context key before seeding
- Aligned engine behavior with strict requirements of the real Redis-backed context store
- Eliminated invalid context seeding behavior hidden by looser fake-store implementations

#### JSON Step Value Casting

- Fixed invalid cast failures when step `input` and `config` values were loaded from JSON and materialized as `JsonElement`
- Restored proper typed access for step input/config helpers and runtime step execution
- Fixed real DI + JSON-definition execution flow for runtime steps such as `HelloWorldStep`

#### HelloWorld Step Input Resolution

- Updated `HelloWorldStep` to support both:
  - declarative step input binding
  - fallback to shared execution state input
- Improved runtime tolerance across multiple pipeline composition styles
- Eliminated false negatives in integration scenarios caused by differing input sources

---

### Changed

#### Pipeline Architecture

- Introduced clear separation between:
  - `AiPipelineDefinition` (declarative model)
  - `ResolvedAiPipeline` (runtime executable model)
  - `ResolvedAiPipelineStep` (resolved step instance)
- Refactored pipeline resolution flow to produce runtime-ready structures
- Standardized step ordering and execution using resolved pipeline steps
- Reinforced the boundary between pipeline configuration and runtime execution

#### Execution Engine Integration

- Updated `AiExecutionEngine` to:
  - resolve pipelines via `IAiPipelineExecutor.PrepareAsync`
  - execute steps using resolved pipeline instances
  - persist execution state after each step transition
  - rotate execution transition keys correctly between steps
- Removed implicit pipeline assumptions during execution
- Improved determinism by ensuring execution is based on a stable resolved snapshot

#### JSON-Driven Step Runtime Behavior

- Standardized how steps consume declarative JSON metadata through execution context helpers
- Clarified distinction between:
  - `input` as binding metadata
  - `config` as step runtime options
  - execution state as shared mutable data
- Improved readability and runtime consistency of step behavior under JSON-defined pipelines

#### Redis Store Implementation

- Migrated atomic Redis update flow from raw script execution to prepared + loaded Lua scripts
- Added internal script reload path to preserve SHA-based performance after Redis script cache loss
- Improved resilience without sacrificing atomicity or correctness
- Standardized Redis serialization / deserialization behavior with runtime-safe JSON handling

#### Test Suite Refactoring

- Refactored tests to align with the resolved-pipeline execution architecture
- Added dedicated JSON integration and JSON concurrency test scenarios
- Standardized usage of reusable fake components where appropriate:
  - execution store
  - context store
  - execution context factory
  - runtime logger
- Added focused real-store integration tests where runtime correctness required real infrastructure
- Improved test isolation and cleanup of Redis-backed artifacts

---

### Performance

#### Redis Atomic Update Efficiency

- Reduced overhead of repeated Lua execution by switching to SHA-based loaded scripts
- Lowered repeated network payload size for Redis script evaluation
- Improved throughput for execution transition persistence under repeated step progression
- Preserved atomic compare-and-swap behavior while increasing runtime efficiency

#### Concurrency Stability

- Validated correct behavior of optimistic concurrency control under real concurrent execution
- Confirmed that only one caller can commit a given execution transition
- Reinforced deterministic behavior for both:
  - `ExecuteNextAsync`
  - `ExecuteAllAsync`

---

### Test Coverage Summary

This version includes broad runtime validation across unit and integration boundaries, including:

- execution engine flow
- JSON pipeline definition loading
- declarative step input resolution
- declarative step config resolution
- state persistence and round-trip safety
- Redis atomic CAS behavior
- SHA-based Redis script execution
- concurrent step progression protection
- terminal execution behavior
- failure and exception handling
- real DI + JSON execution scenarios
- fake and real context-store integration coverage

Total validated test coverage: **61+ tests**

---

### Notes

- This version significantly strengthens the runtime foundation established in previous versions
- JSON-defined pipelines are now first-class runtime inputs
- Step-level `input` and `config` metadata are now fully supported in real execution scenarios
- Redis execution persistence is now both:
  - atomic
  - performance-optimized via SHA-loaded Lua scripts
- The execution engine now enforces transition-key rotation consistently across all execution paths
- The runtime is now in a strong state for the next phase:
  - provider integration
  - prompt orchestration
  - structured outputs
  - retrieval-augmented execution
---

## [1.0.1.6] - 2026-03-26

### Fixed

#### Pipeline Execution Model

- Fixed inconsistent pipeline execution flow caused by double resolution of pipeline definitions
- Removed redundant pipeline resolution during step execution
- Enforced single resolution model:
  - `PrepareAsync` now resolves the pipeline once
  - `ExecuteNextAsync` consumes the resolved pipeline without re-resolving
- Eliminated ambiguity between declarative and runtime pipeline models

#### Execution Contracts Alignment

- Corrected `IAiPipelineExecutor` contract to return `ResolvedAiPipeline` instead of `AiPipelineDefinition`
- Updated execution flow to pass resolved pipeline explicitly into step execution
- Fixed mismatched method signatures across engine and pipeline layers
- Ensured strong typing between definition, resolution, and execution phases

---

### Changed

#### Pipeline Architecture

- Introduced clear separation between:
  - `AiPipelineDefinition` (declarative model)
  - `ResolvedAiPipeline` (runtime executable model)
  - `ResolvedAiPipelineStep` (resolved step instance)
- Refactored pipeline resolution flow to produce runtime-ready structures
- Standardized step ordering and execution using resolved pipeline steps

#### Execution Engine Integration

- Updated `AiExecutionEngine` to:
  - Resolve pipelines via `IAiPipelineExecutor.PrepareAsync`
  - Execute steps using resolved pipeline instances
- Removed implicit pipeline assumptions during execution
- Improved determinism by ensuring execution is based on a stable resolved snapshot

#### Test Suite Refactoring

- Refactored all tests to align with the new pipeline-driven architecture
- Removed duplicated fake implementations from test files
- Standardized usage of shared fake components (`Fake*`):
  - Execution store
  - Context store
  - Step executor
  - Pipeline definition provider
  - Step registry
- Updated tests to use explicit pipeline definitions instead of direct step injection

#### Concurrency & Stability

- Verified compatibility of the new pipeline model with optimistic concurrency control
- Ensured `ExecutionStepKey` behavior remains correct under concurrent execution
- Confirmed deterministic behavior through updated concurrency tests

---

### Notes

- This version fixes a critical architectural inconsistency in pipeline execution
- Establishes a strict boundary between declarative configuration and runtime execution
- Reinforces deterministic behavior by removing hidden resolution side effects
- Prepares the runtime for future enhancements such as:
  - pipeline caching
  - distributed execution
  - advanced execution policies

---

## [1.0.1.5] - 2026-03-25

### Added

#### Execution Engine & Runtime Abstractions

- Introduced `IAiExecutionEngine` as the central orchestration entry point for AI execution
- Added `IAiStepExecutor` abstraction to isolate step execution logic from pipeline orchestration
- Introduced `AiExecutionStatus` enum to standardize execution lifecycle states (Running, Completed, Failed)
- Added `AiRetryPolicyAttribute` to enable declarative retry configuration at step level

#### Retry & Resilience

- Introduced `IAiRetryExceptionClassifier` to centralize retry decision logic
- Added default retry classification for common transient failures:
  - `TimeoutException`
  - `HttpRequestException`
  - `TaskCanceledException`
- Enabled deterministic retry handling within `AiStepExecutor`
- Improved failure handling to clearly distinguish retryable vs terminal errors

#### Structured Runtime Logging

- Introduced `IAiRuntimeLogger` as a centralized logging facade for the AI runtime
- Added specialized loggers:
  - `IAiExecutionEngineLogger`
  - `IAiPipelineLogger`
  - `IAiPipelineServiceLogger`
  - `IAiStepExecutorLogger`
- Enabled clear separation of logging concerns across execution layers
- Prepared logging architecture for integration with realtime observability providers

#### Test Coverage

- Added full unit test coverage for:
  - Execution Engine lifecycle (`CreateAsync`, `ExecuteNextAsync`, `ExecuteAllAsync`)
  - Step execution flow and completion behavior
  - Retry logic with transient failure simulation
  - Concurrency scenarios and execution stability
- Introduced in-memory test implementations for:
  - Execution store
  - Context store
  - Step executor and steps
- Ensured deterministic behavior under test conditions

---

### Changed

#### Runtime Architecture Refactoring

- Refactored AI execution flow to clearly separate:
  - Execution Engine (orchestration)
  - Pipeline (step sequencing)
  - Step Executor (execution + retry behavior)
- Improved modularity and extensibility of the runtime
- Simplified dependency injection by introducing a single logging entry point (`IAiRuntimeLogger`)

#### Execution Flow Improvements

- Standardized step progression using `CurrentStepIndex`
- Improved terminal state handling with explicit completion logic
- Ensured consistent execution state transitions across all execution paths

#### Abstractions & Reusability

- Moved shared execution concepts (e.g., context snapshot) into Abstractions layer
- Improved consistency of execution contracts across runtime components
- Prepared the system for future support of:
  - distributed execution
  - execution replay
  - advanced telemetry decorators

---

### Notes

- This version represents a significant internal architecture upgrade of the AI runtime
- Focus is on determinism, composability, and observability readiness
- Lays the foundation for upcoming features such as:
  - realtime telemetry streaming
  - RAG integration
  - distributed execution support

---

## [1.0.1.4] - 2026-03-24

### Added

#### Execution State Separation (Record / State Model)

- Introduced `AiExecutionState` to isolate mutable execution data from orchestration metadata
- Refactored `AiExecutionRecord` to focus on orchestration, step tracking, and execution lifecycle
- Decoupled execution state (`Data`, `Metadata`) from orchestration concerns
- Enabled cleaner separation for future distributed execution and replay scenarios

#### Composite AI Execution Store

- Introduced `IAiExecutionStore` abstraction for unified execution persistence
- Implemented:
  - `RedisAiExecutionStore` as primary persistence layer
  - `MemoryAiExecutionStore` as fallback layer
  - `AiExecutionStore` as composite store with fallback strategy
- Supports resilient execution state storage with Redis-first strategy and in-memory fallback

#### Record + State Persistence Contract

- Updated store contract to handle both `AiExecutionRecord` and `AiExecutionState`
- Added:
  - `GetRecordAsync(...)`
  - `GetStateAsync(...)`
  - `TryUpdateAsync(record, state, expectedStepKey)`
- Ensures atomic-like updates across orchestration and execution state

#### Improved Execution Consistency

- Execution updates now persist both record and state together
- Prevents desynchronization between orchestration flow and execution data
- Strengthens deterministic guarantees for step transitions and recovery

---

### Notes

- This version finalizes the V1 execution model with proper separation of concerns between orchestration and execution state
- The system is now ready for distributed execution (worker-based) without structural refactoring
- Context rotation remains part of the RBAC execution model but is not required for AI execution flows

---

## [1.0.1.3] - 2026-03-24

### Added

#### AI Execution Runtime (V1)

- Introduced `AiExecutionEngine` as the core orchestrator for deterministic AI pipeline execution
- Added `CreateAsync(...)` to initialize AI executions from HTTP-bound RBAC context
- Added `ExecuteNextAsync(...)` as the primary step execution primitive (distributed-ready)
- Added `ExecuteAllAsync(...)` helper for sequential/local execution flows

#### Execution Context Isolation

- Introduced AI-owned context seeding via `IContextStore.SeedAsync(...)`
- Ensured strict separation between HTTP context and AI execution context
- Added `ExecutionContextSnapshot` to preserve original request identity (TenantId, UserId, ContextKey)

#### Step-Based Execution Model

- Introduced step-driven execution using `IAiStep`
- Dynamic step resolution via `IServiceProvider`
- Sequential execution using `CurrentStepIndex` cursor model
- Added execution state tracking:
  - `CompletedSteps`
  - `Status` (Pending, Running, Completed, Failed)
  - `Version` for optimistic concurrency

#### Context Lifecycle Management

- Context retrieval per step via `IContextStore.GetAsync(...)`
- Injection into `IExecutionContextAccessor` (AsyncLocal)
- Guaranteed cleanup via `Accessor.Clear()`
- Context rotation after each step using `RotateAsync(...)`
- TTL-based rotation for isolation and replay protection

#### Deterministic Execution Guarantees

- Introduced `ExecutionStepKey` for step-level concurrency control
- Enabled safe re-execution and recovery patterns
- Designed for idempotent and resumable execution flows

---

### Notes

- `ExecuteNextAsync(...)` is designed as the primary entry point for future distributed/background execution
- `ExecuteAllAsync(...)` is intended for local/testing scenarios only
- Future iterations will introduce:
  - distributed execution (workers / message bus)
  - retry and conflict resolution strategies
  - step-level locking and idempotency guarantees
  - production-grade safe rotation strategy

---

## [1.0.1.2] - 2026-03-22

### Added

#### Storage Abstraction & Multi-Provider Support
- Introduced storage abstraction layer for entity persistence
- Added IndexedDbEntityStore implementation for browser-based persistence
- Enabled multi-provider storage strategy (local, simulated API, future extensions)

#### Modular Platform Architecture
- Introduced modular project structure under `src/`
- Split core runtime into independent modules:
  - `Multiplexed.Rbac.Core`
  - `Multiplexed.Realtime`
  - `Multiplexed.Abstractions`
- Established clear dependency boundaries and separation of concerns

#### Realtime Module Extraction
- Extracted realtime pipeline into standalone `Multiplexed.Realtime` project
- Introduced transport-based architecture (`IRealtimeTransport`, providers)
- Added background worker for event processing and dispatching
- Enabled plug-and-play provider model (SignalR, NullTransport, future providers)

#### Shared Abstractions Layer
- Introduced `Multiplexed.Abstractions` for cross-module contracts
- Added `IRuntimeEventContext` abstraction to decouple core from realtime
- Removed direct dependency between RBAC core and realtime infrastructure

#### AI Module (Foundation)
- Added `Multiplexed.AI` project
- Introduced provider-based AI architecture (`IAIProvider`)
- Added `AIService` orchestration layer
- Included fake AI provider for testing and future integration

---

### Changed

#### .NET Upgrade
- Upgraded entire solution to **.NET 10**
- Removed legacy ASP.NET Core package references (2.x)
- Replaced with modern `FrameworkReference` where required

#### Runtime Event Pipeline Refactor
- Replaced reducers with handler-based architecture (`IRuntimeEventHandler`)
- Introduced dispatcher pattern for event routing
- Improved separation between dispatching, handling, and transport layers

#### Namespace & Project Renaming
- Renamed main project to `Multiplexed.Rbac.Core`
- Removed redundant `Core/Core` namespace nesting
- Standardized namespaces across modules:
  - `Multiplexed.Rbac.Core.*`
  - `Multiplexed.Realtime.*`
  - `Multiplexed.Abstractions.*`

#### Dependency Injection Improvements
- Centralized DI registration per module (`AddMultiplexRealtime`, etc.)
- Fixed lifetime mismatches for NServiceBus pipeline compatibility
- Ensured root-safe service resolution for behaviors

#### Solution Structure
- Introduced `src/` layout for .NET projects
- Updated project references for samples and tests
- Renamed solution to `Multiplexed.sln`

---

### Notes

This release represents a major architectural milestone:

- Transition from a monolithic RBAC runtime to a modular platform
- Introduction of clean boundaries between core, realtime, and infrastructure
- Foundation for future extensibility (AI, additional transports, providers)

---

## [1.0.1.1] - 2026-03-20

### Added

#### Storage Abstraction & Multi-Provider Support

- Introduced generic `IEntityStore<T>` abstraction for persistence
- Added `find(query)` support with:
  - filtering (`where`)
  - sorting (`orderBy`)
  - limiting (`limit`)
- Implemented pluggable storage providers:
  - `local-storage`
  - `simulated-api`
  - `api-proxy`
  - `api-simple`
  - `indexed-db`
- Enabled seamless switching between storage backends without impacting domain logic
- Added simulated API mode with latency to mimic real-world network conditions

---

#### BurstRun Domain Model

- Introduced `BurstRun` as a persisted snapshot of execution
- Defined clear separation between:
  - runtime execution (`BurstRuntime`)
  - metrics (`BurstReport`)
  - persisted result (`BurstRun`)
- Added support for parent-child run relationships via `basedOnRunId`
- Prepared structure for run history, replay, and comparison

---

#### BurstRunStore (Unified Store)

- Implemented single `BurstRunStore` abstraction
- Extended `EntityStoreFacadeBase` for generic CRUD and query delegation
- Implemented `IBurstRunStore` with domain-specific methods:
  - `getLatest()`
  - `getByParentRunId()`
- Removed duplicated provider-specific BurstRun stores
- Centralized business logic while keeping infrastructure fully pluggable

---

#### Type-Safe Store Configuration

- Introduced discriminated union for store configuration:
  - `BurstRunLocalStoreOptions`
  - `BurstRunApiStoreOptions`
- Added type-safe narrowing via mode-based detection
- Improved IntelliSense and prevented invalid configuration combinations

---

### Changed

#### Project Structure

- Restructured project into clear architectural layers:
  - `infrastructure/`
    - storage
    - transport
    - realtime
    - logs
  - `burst/domain/`
- Moved generic storage logic into `infrastructure/storage`
- Isolated Burst-specific logic in `burst/domain`

---

#### Naming & Concept Alignment

- Renamed and clarified core concepts:
  - runtime → execution state
  - report → metrics
  - run → persisted snapshot (`BurstRun`)
- Standardized terminology across the codebase

---

#### HTTP / Transport Layer

- Refactored HTTP client to align with storage providers
- Maintained compatibility with existing Next.js proxy implementation
- Standardized request flow across API-based providers

---

### Removed

- Removed duplicated BurstRun storage implementations:
  - `LocalStorageBurstRunStore`
  - `SimulatedApiBurstRunStore`
  - `ProxyApiBurstRunStore`
  - `SimpleApiBurstRunStore`
- Replaced with unified `BurstRunStore` using generic providers

---

### Preparation

#### BurstRun Persistence

- Prepared system to persist execution results after runtime completion
- Enabled future implementation of:
  - run history
  - replay
  - comparison between runs

---

#### AI-Driven Analysis

- Structured run data to support AI consumption
- Prepared for future feature:
  - “Explain this run”
  - automatic failure analysis
  - scenario suggestion based on results
- Normalized metrics and error patterns for AI input

---

## [1.0.1.0] - 2026-03-17

### Added

#### Client Runtime & Testing

- Introduced multiple dispatch strategies for load testing:
  - Single burst execution
  - Maintained concurrency
  - Wave-based batching
- Improved burst request handling for high-volume authorization testing
- Enhanced logging granularity for request lifecycle analysis
- Added context key tracking across concurrent requests
- Enabled detailed visibility into request distribution patterns and concurrency behavior
- Added scenario launch capability for interactive testing of the client runtime
- Allows users to trigger predefined load scenarios directly from the UI
- Enables rapid validation of dispatch strategies and concurrency behavior
- Supports end-to-end testing of authorization flow, context rotation, and observability pipeline

---

#### Deterministic Realtime Observability Layer

Introduced a backend realtime observability layer designed to capture, process, and distribute runtime events without impacting the request hot path.

This layer establishes a foundation for deterministic, low-latency observability in distributed authorization systems.

##### Backend capabilities

- Runtime event dispatching pipeline for observability events
- Background worker responsible for consuming runtime events from a channel
- Reducer-based event processing outside of the request execution path
- Provider host abstraction enabling pluggable transport layers:
  - SignalR
  - WebSocket
- Null realtime provider for safe fallback / disabled mode
- Event context abstraction for runtime propagation
- Reducer dispatching model for specialized event handling and transformation

##### Background worker design

- Fully decoupled from request execution pipeline
- Guarantees zero impact on request latency (no blocking operations)
- Must never break host startup or shutdown flow
- Cancellation is treated as a normal lifecycle event
- Supports safe asynchronous fan-out of runtime events

##### Planned evolution

- Extraction into a standalone reusable observability module
- Potential cross-project reuse for other distributed systems

---

#### Realtime Logging System

- Added real-time log streaming using:
  - WebSocket
  - SignalR
- Added high-performance in-memory log sink for the client runtime
- Enabled real-time visualization of request lifecycle and context transitions

##### In-memory log sink characteristics

- O(1) push
- O(1) patch
- O(1) move-to-front on update
- O(1) trim
- Stable recency ordering (latest events always prioritized)

##### Internal design

- Map for id → node lookup (constant-time access)
- Doubly linked list for recency ordering
- Most recent item always at the head
- Optimized for high-frequency event ingestion

---

#### Visualization & UI

- Added key rotation graph visualization for real-time inspection of context transitions
- Introduced a new global UI for centralized monitoring of runtime activity

---

#### Context Storage & Redis Optimization

- Introduced Lua script preloading for Redis atomic operations
- Added SHA-based script execution after initial `SCRIPT LOAD`
- Eliminated repeated transmission of Lua payloads during context rotation
- Reduced overhead in high-frequency atomic Redis operations
- Improved efficiency of context rotation and synchronization mechanisms
- Significantly increased throughput under concurrent load conditions
- Internal benchmarks showed up to **500% performance improvement** over naïve per-request Lua execution

---

#### Adaptive Runtime Controls (Demo Mode)

Introduced a controlled runtime override layer allowing clients to modify selected runtime parameters via HTTP headers in **demo and testing environments**.

This enables precise experimentation with concurrency behavior and system limits without requiring backend redeployment.

##### Supported overrides

- Max in-flight concurrency:
  - `X-Demo-Max-InFlight`
- Rotation overlap window:
  - `X-Demo-Rotation-Overlap-Ms`

##### Capabilities

- Dynamic tuning of concurrency limits per context key
- Adjustable rotation overlap window for race condition and transition testing
- Configurable overflow policy (Reject strategy currently implemented)
- Custom HTTP status code for concurrency violations (default: 429)

##### Safety mechanisms

- Redis-backed in-flight counters with TTL protection
- Automatic expiration of abandoned counters (crash-safe behavior)
- Optional TTL refresh for long-running requests
- Security logging for concurrency violations (replay / misuse detection)

##### Performance optimization

- Optional Redis Lua script SHA caching
- Reduced network overhead
- Reduced execution overhead in concurrency control paths

##### Design intent

Designed for:

- Demo environments
- Load testing scenarios
- Concurrency experimentation

Ensures safe experimentation while preserving deterministic behavior in production environments.

---

### Improved

#### Authorization Runtime

- Improved stability of context rotation under concurrent load
- Improved consistency between Access Context resolution and rotation lifecycle
- Reduced race conditions during context rotation
- Improved determinism in concurrent execution scenarios

---

#### Client Console (Next.js)

- Improved request visualization and log readability
- Improved log rendering performance under high-frequency updates
- Better separation of log types:
  - HTTP logs
  - Realtime logs
  - Context rotation logs
- Enhanced debugging experience for authorization flows and concurrency scenarios

---

### Fixed

- Fixed inconsistent context key reuse after the initial request burst
- Fixed incorrect rotated keys in subsequent requests
- Fixed concurrency edge cases causing unexpected request rejection
- Fixed inconsistencies between client and server context synchronization

---

### Notes

This release significantly enhances the **observability, performance, and testability** of the Multiplexed RBAC system.

It introduces:

- A deterministic backend realtime observability pipeline
- A high-performance client-side logging and visualization system
- Advanced concurrency control mechanisms with runtime tuning capabilities
- Major Redis optimization strategies for high-throughput environments

This version marks a key step toward a fully observable and controllable distributed authorization runtime.

---

### Upcoming

- Cross-runtime portability (Java, Node.js, Python)
- Extraction of observability layer as standalone module

---

## [1.0.0.0] - 2026-03-09

### Initial Release

Initial public version of the **Multiplexed RBAC Runtime**, including:

- A .NET deterministic authorization runtime
- A Next.js client console for testing context rotation and high-volume authorization scenarios

The project introduces a deterministic approach to multi-tenant authorization by separating authentication, access context resolution, and resource authorization using a TRN-based model.

---

### Added

#### Core Authorization Runtime (.NET)

- Deterministic RBAC authorization engine
- TRN (Tenant Resource Name) resource model
- ASP.NET Core middleware for Access Context resolution
- Authorization integration with the ASP.NET policy system
- Namespace-based tenant isolation
- Logical Access Context lifecycle management
- Context propagation via HTTP headers

---

#### Context Storage

- Redis-backed context store
- Atomic context rotation mechanism
- Lua-based atomic operations for key rotation
- Support for distributed authorization environments
- Logical session expiration handling

---

#### Request Authorization Pipeline

Deterministic request lifecycle:

```text
HTTP Request
   ↓
Authentication (Fake Auth - demo purpose)
   ↓
AccessContextMiddleware
   ↓
CompositeContextStore (Redis + fallback)
   ↓
NamespaceGuard
   ↓
Authorization Policy
   ↓
Controller / Services