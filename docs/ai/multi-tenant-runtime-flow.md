# Multi-Tenant Runtime Flow

Status: Implemented / validated foundation.

This document provides the end-to-end ASCII flow for the multi-tenant runtime control-plane path.

It explains how an MCP or control-plane request becomes a durable runtime execution while preserving RBAC authorization context, `ExecutionContextSnapshot`, tenant boundary, Shared/Dedicated/Hybrid visibility, tenant-aware admission, tenant-aware scale-out, shared queue dispatch, local runtime queue execution, execution control, and observability/audit correlation.

Core invariant:

```text
No background, queued, shared, or distributed runtime run may execute without a durable ExecutionContextSnapshot.
```

---

## Why This Flow Matters

The multi-tenant runtime path crosses several asynchronous and distributed boundaries:

```text
MCP/API request
  -> RBAC context
  -> shared run
  -> Redis/shared queue
  -> background pump
  -> runtime instance provider
  -> local runtime queue
  -> background controller
  -> DAG execution engine
  -> worker loop
```

An ambient in-memory context is not enough across those hops. The runtime therefore persists a durable `ExecutionContextSnapshot` with the run and restores it whenever execution crosses a background or distributed boundary.

---

## End-to-End Runtime Flow


```text
┌──────────────────────────────────────────────────────────────────────────────┐
│                              MCP CLIENT / TOOL                               │
│                                                                              │
│  Example: ReplayMcpTools / SharedRunTools / ExecutionControlTools            │
│                                                                              │
│  Headers / Context:                                                          │
│  - X-Access-Context                                                          │
│  - X-Demo-UserId                                                             │
│  - TenantId / TenantGroupId resolved by RBAC                                 │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         MCP SERVER / CONTROL PLANE                           │
│                                                                              │
│  AddMultiplexedRbacRuntime                                                   │
│  - Authentication                                                            │
│  - Authorization / RequireCapability                                         │
│  - ContextKey resolution                                                     │
│  - TenantId / TenantGroupId resolution                                       │
│  - Namespace / TRN permissions                                               │
│                                                                              │
│  Creates current RBAC ExecutionContext                                       │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    McpRuntimeExecutionContextAccessor                        │
│                                                                              │
│  Current RBAC context stored in AsyncLocal                                   │
│                                                                              │
│  MapToSnapshot()                                                             │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │ ExecutionContextSnapshot                                               │  │
│  │ - ContextKey                                                           │  │
│  │ - Project                                                              │  │
│  │ - UserId                                                               │  │
│  │ - TenantId              ← durable tenant boundary                      │  │
│  │ - TenantGroupId                                                        │  │
│  │ - CurrentNamespace                                                     │  │
│  │ - Namespaces / TRNs                                                    │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    IAiSharedRuntimeController.SubmitRun                      │
│                                                                              │
│  AiSharedRuntimeControllerRequest                                            │
│  - Operation = SubmitRun                                                     │
│  - PipelineKey                                                               │
│  - TenantId                                                                  │
│  - RequestedBy                                                               │
│  - Source                                                                    │
│  - RunRequest                                                                │
│      - PipelineName                                                          │
│      - PipelineDefinition / Json / File                                      │
│      - Input                                                                 │
│      - ExecutionContextSnapshot attached                                     │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              SHARED RUN STORE                                │
│                                                                              │
│  Create AiSharedRunRecord                                                    │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐  │
│  │ AiSharedRunRecord                                                      │  │
│  │ - SharedRunId                                                          │  │
│  │ - Status = Submitted / Queued                                          │  │
│  │ - RunRequest                                                           │  │
│  │ - ExecutionContextSnapshot      ← durable tenant snapshot              │  │
│  │ - PipelineKey                                                          │  │
│  │ - CorrelationId                                                        │  │
│  │ - RequestedBy                                                          │  │
│  │ - Source                                                               │  │
│  │ - Metadata                                                             │  │
│  └────────────────────────────────────────────────────────────────────────┘  │
│                                                                              │
│  Redis / InMemory depending test/runtime mode                                │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                           ADMISSION CONTROLLER                               │
│                                                                              │
│  AiRunAdmissionController.AdmitAsync                                         │
│                                                                              │
│  Loads tenant runtime settings:                                              │
│                                                                              │
│  tenant-a                                                                    │
│  - IsolationMode = Dedicated                                                 │
│  - PreferDedicatedCapacity = true                                            │
│  - AllowSharedFallback = false                                               │
│  - RuntimeInstanceIdPrefix = tenant-a-runtime                                │
│                                                                              │
│  tenant-b                                                                    │
│  - IsolationMode = Hybrid                                                    │
│  - PreferDedicatedCapacity = true                                            │
│  - AllowSharedFallback = true                                                │
│  - RuntimeInstanceIdPrefix = tenant-b-runtime                                │
│                                                                              │
│  default/test-tenant                                                         │
│  - IsolationMode = Shared                                                    │
│  - PreferDedicatedCapacity = false                                           │
│  - AllowSharedFallback = true                                                │
│  - RuntimeInstanceIdPrefix = runtime-instance                                │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                   TENANT-VISIBLE REGISTRY / CAPACITY FILTER                  │
│                                                                              │
│  RedisRuntimeInstanceRegistry.ListAsync                                      │
│  RedisRuntimeInstanceCapacityStore.ListAsync                                 │
│                                                                              │
│  Visibility rules:                                                           │
│                                                                              │
│  Shared runtime:                                                             │
│  - visible to Shared tenants                                                 │
│  - visible to Hybrid/Dedicated only if tenant settings allow shared fallback │
│                                                                              │
│  Dedicated runtime:                                                          │
│  - visible only if TenantId or TenantGroupId matches                         │
│                                                                              │
│  Hybrid runtime:                                                             │
│  - visible only if TenantId or TenantGroupId matches                         │
│  - fallback does NOT make unowned Hybrid runtime visible                     │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
                         ┌────────────────────────────┐
                         │ Capacity available?        │
                         └────────────────────────────┘
                              │                  │
                              │ YES              │ NO
                              ▼                  ▼
┌───────────────────────────────────────┐     ┌────────────────────────────────┐
│ Decision = AssignToInstance           │     │ Decision = RequestScaleOut     │
│                                       │     │                                │
│ - RuntimeInstanceId selected          │     │ Tenant runtime settings copied │
│ - Reservation created                 │     │ into scale-out request         │
│ - SharedRun can be dispatched         │     │                                │
└───────────────────────────────────────┘     └────────────────────────────────┘
                              │                  │
                              │                  ▼
                              │     ┌──────────────────────────────────────────┐
                              │     │       SCALE-OUT REQUEST PUBLISHER        │
                              │     │                                          │
                              │     │ StoreBackedAiRuntimeScaleOutRequest      │
                              │     │                                          │
                              │     │ AiRuntimeScaleOutRequestRecord           │
                              │     │ - TenantId                               │
                              │     │ - TenantGroupId                          │
                              │     │ - IsolationMode                          │
                              │     │ - PreferDedicatedCapacity                │
                              │     │ - AllowSharedFallback                    │
                              │     │ - MaxRuntimeInstances                    │
                              │     │ - RuntimeInstanceIdPrefix                │
                              │     │ - WorkerCountPerInstance                 │
                              │     │ - MaxConcurrentRunsPerInstance           │
                              │     │ - LocalQueueCapacity                     │
                              │     │ - Status = Pending                       │
                              │     └──────────────────────────────────────────┘
                              │                  │
                              │                  ▼
                              │     ┌──────────────────────────────────────────┐
                              │     │         SCALE-OUT WATCHER / PROVIDER     │
                              │     │                                          │
                              │     │ AiRuntimeScaleOutWatcher                 │
                              │     │ → Local / HTTP / gRPC / K8s provider     │
                              │     │                                          │
                              │     │ Local provider example:                  │
                              │     │ AiLocalRuntimeInstanceScaler             │
                              │     │                                          │
                              │     │ Important fix:                           │
                              │     │ - counts matching hosts by prefix        │
                              │     │ - not global hosts.Count                 │
                              │     │                                          │
                              │     │ Examples:                                │
                              │     │ - runtime-instance-1                     │
                              │     │ - tenant-a-runtime-1                     │
                              │     │ - tenant-b-runtime-1                     │
                              │     └──────────────────────────────────────────┘
                              │                  │
                              │                  ▼
                              │     ┌──────────────────────────────────────────┐
                              │     │     RUNTIME INSTANCE REGISTRATION        │
                              │     │                                          │
                              │     │ Runtime instance registers itself:       │
                              │     │ - RuntimeInstanceId                      │
                              │     │ - TenantId / TenantGroupId               │
                              │     │ - IsolationMode                          │
                              │     │ - WorkerCount                            │
                              │     │ - MaxConcurrentRuns                      │
                              │     │ - QueueCapacity                          │
                              │     │ - Heartbeat                              │
                              │     │ - Metadata duplicate for observability   │
                              │     └──────────────────────────────────────────┘
                              │                  │
                              │                  ▼
                              │     ┌──────────────────────────────────────────┐
                              │     │     SCALE-OUT REQUEST FULFILLED          │
                              │     │                                          │
                              │     │ Status = Fulfilled                       │
                              │     │ FulfilledRuntimeInstanceId set           │
                              │     └──────────────────────────────────────────┘
                              │                  │
                              └──────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         SHARED QUEUE / REQUEUE                               │
│                                                                              │
│  SharedRun is queued or requeued after scale-out fulfilled                   │
│                                                                              │
│  AiSharedQueueItem                                                           │
│  - SharedRunId                                                               │
│  - PipelineKey                                                               │
│  - Tenant / context carried via shared run snapshot                          │
│  - Status = Queued                                                           │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         SHARED QUEUE BACKGROUND PUMP                         │
│                                                                              │
│  AiSharedQueueBackgroundService                                              │
│  → AiSharedQueuePump                                                         │
│  → AiSharedQueueDispatcher                                                   │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         AiSharedQueueDispatcher                              │
│                                                                              │
│  1. Load shared queue item                                                   │
│  2. Load AiSharedRunRecord                                                   │
│  3. Restore ExecutionContextSnapshot into RBAC ExecutionContext              │
│                                                                              │
│     This was the critical fix:                                               │
│     background dispatch must restore tenant context before admission.        │
│                                                                              │
│  4. Run admission again with tenant-visible registry/capacity                │
│  5. Reserve selected runtime capacity                                        │
│  6. Dispatch shared run to assigned runtime instance                         │
│  7. Mark shared run as Dispatched                                            │
│  8. Clear/restore previous context                                           │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                     SHARED RUN DISPATCHER / RUNTIME PROVIDER                 │
│                                                                              │
│  RemoteAiSharedRunDispatcher / Local dispatch                                │
│                                                                              │
│  Dispatch target examples:                                                   │
│  - LocalAiSharedRuntimeInstance                                              │
│  - HTTP runtime instance                                                     │
│  - gRPC runtime instance                                                     │
│  - Kubernetes runtime pod                                                    │
│                                                                              │
│  Sends AiRuntimePipelineRunRequest to runtime instance                       │
│  with ExecutionContextSnapshot preserved                                     │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                        RUNTIME INSTANCE LOCAL QUEUE                          │
│                                                                              │
│  AiRuntimePipelineBackgroundController.EnqueueAsync                          │
│                                                                              │
│  AiRuntimeQueuedPipelineRun                                                  │
│  - LocalRunId                                                                │
│  - PipelineName                                                              │
│  - PipelineDefinition / Json                                                 │
│  - Input                                                                     │
│  - ExecutionContextSnapshot      ← required now                              │
│  - AssignedRuntimeInstanceId                                                 │
│                                                                              │
│  If snapshot missing: fail fast                                              │
│  "No execution context snapshot is available..."                             │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                    AiRuntimePipelineBackgroundController                     │
│                                                                              │
│  ProcessQueuedRunAsync                                                       │
│                                                                              │
│  1. Restore ExecutionContextSnapshot                                         │
│  2. Create durable DAG execution                                             │
│  3. Assign ExecutionId                                                       │
│  4. Start worker execution                                                   │
│  5. Observe terminal result                                                  │
│  6. Finalize run                                                             │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         DAG EXECUTION CREATION                               │
│                                                                              │
│  AiDagExecutionEngine.CreateAsync                                            │
│                                                                              │
│  Redis hot state:                                                            │
│                                                                              │
│  ai:execution:record:{executionId}                                           │
│  ai:execution:state:{executionId}                                            │
│  ai:execution:steps:{executionId}                                            │
│  ai:execution:step:{executionId}:{stepName}                                  │
│                                                                              │
│  Record:                                                                     │
│  - ExecutionId                                                               │
│  - PipelineName                                                              │
│  - ExecutionMode = Dag                                                       │
│  - Status = Running                                                          │
│  - CompletedSteps                                                            │
│                                                                              │
│  State:                                                                      │
│  - Steps                                                                     │
│  - Dependencies                                                              │
│  - Claims / leases                                                           │
│  - RetryState                                                                │
│  - RecoveryCount                                                             │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              WORKER LOOP                                     │
│                                                                              │
│  AiRuntimeInstanceWorker                                                     │
│  → AiDagExecutionEngine.ExecuteNextAsync                                     │
│                                                                              │
│  For each cycle:                                                             │
│  1. Load DAG state                                                           │
│  2. Recover timed-out claims                                                 │
│  3. Promote retryable steps if NextRetryAtUtc <= now                         │
│  4. Find ready step                                                          │
│  5. Claim step atomically                                                    │
│  6. Execute IAiStep                                                          │
│  7. Persist step result                                                      │
│  8. Release/clear claim                                                      │
│  9. Update dependencies                                                      │
│  10. Check DAG convergence                                                   │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              STEP EXECUTION                                  │
│                                                                              │
│  IAiStep.ExecuteAsync                                                        │
│                                                                              │
│  Success:                                                                    │
│  - Step Status = Completed                                                   │
│  - Output normalized                                                         │
│  - CompletedSteps updated                                                    │
│                                                                              │
│  Retryable failure:                                                          │
│  - Step Status = WaitingForRetry                                             │
│  - RetryState.RetryCount++                                                   │
│  - RetryState.NextRetryAtUtc set                                             │
│  - Claim cleared                                                             │
│                                                                              │
│  Non-retryable / exhausted:                                                  │
│  - Step Status = Failed                                                      │
│  - Execution converges to Failed                                             │
│                                                                              │
│  Timeout / stale lease:                                                      │
│  - RecoverTimedOutStepsAsync                                                 │
│  - Status back to Ready                                                      │
│  - RecoveryCount++                                                           │
│  - RetryCount unchanged                                                      │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
                         ┌────────────────────────────┐
                         │ DAG terminal?              │
                         └────────────────────────────┘
                              │                  │
                              │ NO               │ YES
                              ▼                  ▼
┌───────────────────────────────────────┐     ┌────────────────────────────────┐
│ Continue worker loop                  │     │ Terminal convergence           │
│                                       │     │                                │
│ - More ready steps                    │     │ Completed / Failed / Cancelled │
│ - Retry windows                       │     │ according to durable state     │
│ - Recovery                            │     │                                │
└───────────────────────────────────────┘     └────────────────────────────────┘
                                                         │
                                                         ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                           EXECUTION CONTROL CHECK                            │
│                                                                              │
│  IAiExecutionControlStore                                                    │
│                                                                              │
│  Before terminal finalization:                                               │
│  - Is execution paused?                                                      │
│  - Is execution cancelled?                                                   │
│  - Is execution waiting for input?                                           │
│                                                                              │
│  Important rule:                                                             │
│  cancellation overrides natural DAG completion                               │
│                                                                              │
│  Example:                                                                    │
│  Step completes successfully after cancel request                            │
│  → DAG may look Completed                                                    │
│  → final persisted status must be Cancelled                                  │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              FINALIZATION                                    │
│                                                                              │
│  Persist final AiExecutionRecord                                             │
│                                                                              │
│  Possible statuses:                                                          │
│  - Completed                                                                 │
│  - Failed                                                                    │
│  - Cancelled                                                                 │
│                                                                              │
│  Final record contains:                                                      │
│  - ExecutionId                                                               │
│  - PipelineName                                                              │
│  - Status                                                                    │
│  - IsTerminal = true                                                         │
│  - CompletedSteps                                                            │
│  - FailureReason / Reason if any                                             │
│                                                                              │
│  Handle.Completion is completed                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                         SHARED RUN FINAL STATUS                              │
│                                                                              │
│  Runtime instance reports back / controller observes final status            │
│                                                                              │
│  AiSharedRunRecord updated:                                                  │
│  - LocalRunId                                                                │
│  - ExecutionId                                                               │
│  - AssignedRuntimeInstanceId                                                 │
│  - Status = Completed / Failed / Cancelled                                   │
│  - FailureReason if any                                                      │
│  - UpdatedAtUtc                                                              │
└──────────────────────────────────────────────────────────────────────────────┘
                                      │
                                      ▼
┌──────────────────────────────────────────────────────────────────────────────┐
│                              OBSERVABILITY                                   │
│                                                                              │
│  Decision ledger:                                                            │
│  - admission decisions                                                       │
│  - scale-out decisions                                                       │
│  - dispatch decisions                                                        │
│  - replay/audit decisions                                                    │
│                                                                              │
│  Tracing timeline:                                                           │
│  - correlation id                                                            │
│  - runtime instance id                                                       │
│  - worker id                                                                 │
│  - execution id                                                              │
│  - run id                                                                    │
│                                                                              │
│  Metadata duplicates tenant info for debugging only.                         │
│  Durable tenant boundary remains ExecutionContextSnapshot.TenantId.          │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## Flow Explanation

### 1. MCP Client / Tool

The flow starts from a control-plane adapter such as MCP tools, API endpoints, CLI commands, dashboard actions, or demo/test runners. For MCP, requests normally include context-bearing headers such as `X-Access-Context` and `X-Demo-UserId`. RBAC resolves the current user, context key, tenant, tenant group, namespaces, and TRN permissions.

### 2. RBAC Execution Context

The MCP server uses RBAC middleware and capability checks such as `RequireCapability(resource, feature, action)`. This produces the current RBAC `ExecutionContext`. That context is valid for the current request flow, but it cannot be assumed to survive Redis persistence, background services, remote runtime providers, or local runtime queues.

### 3. ExecutionContextSnapshot

`ExecutionContextSnapshot` is the durable runtime context. It carries `ContextKey`, `Project`, `UserId`, `TenantId`, `TenantGroupId`, `CurrentNamespace`, and namespace/TRN entries. The durable tenant boundary is `ExecutionContextSnapshot.TenantId`. Metadata may duplicate tenant information for debugging or observability, but routing and isolation must be based on the snapshot, not metadata.

### 4. Shared Runtime Controller

The shared controller receives the run request and persists it as a shared run. The shared run must keep the snapshot attached because it may later be assigned, queued globally, dispatched by a background pump, requeued after scale-out fulfillment, cancelled, or inspected through control-plane tooling.

### 5. Admission Controller

Admission resolves tenant runtime settings and decides whether to assign to a runtime instance, queue globally, request scale-out, or reject. The current foundation includes Dedicated settings for `tenant-a`, Hybrid settings for `tenant-b`, and Shared/default settings for `test-tenant` or unknown tenants.

### 6. Tenant-Visible Registry and Capacity

Registry and capacity listing are tenant-filtered. Shared runtime capacity is visible to Shared/default tenants and to Hybrid/Dedicated tenants only when tenant settings allow shared fallback. Dedicated runtime capacity is visible only when tenant ownership matches. Hybrid runtime capacity is also ownership-bound; fallback does not make an unowned Hybrid runtime visible.

### 7. Scale-Out Request

When no matching capacity is available, admission can request scale-out. The scale-out request must copy tenant runtime settings such as tenant id, tenant group id, isolation mode, fallback policy, runtime instance prefix, worker count, run slots, and queue capacity. This ensures asynchronous scale-out creates the right type of capacity for the right tenant.

### 8. Scale-Out Watcher / Provider

The watcher observes pending scale-out requests and delegates to a provider. The local scaler must count matching hosts by `RuntimeInstanceIdPrefix`, not global host count. This prevents one tenant's runtime instances from satisfying another tenant's scale-out capacity limit.

### 9. Shared Queue and Requeue

When scale-out succeeds, the original shared run is requeued. The queue item references the shared run; the durable tenant context remains on the shared run snapshot.

### 10. Shared Queue Dispatcher

The dispatcher must load the shared run, read the `ExecutionContextSnapshot`, restore it into the current RBAC context, run tenant-aware admission, reserve selected runtime capacity, dispatch the run, and restore or clear the previous context. This is one of the most important tenant-safety restore points.

### 11. Runtime Provider Dispatch

The selected runtime provider receives the runtime pipeline request. Provider examples include local runtime instance, HTTP runtime instance, gRPC runtime instance, and Kubernetes runtime pod. The dispatch payload must preserve `ExecutionContextSnapshot`.

### 12. Runtime Instance Local Queue

The runtime instance queues a local run. The local queued run must contain the snapshot. If it does not, the background controller fails fast with `No execution context snapshot is available...`. This prevents silent tenant-unsafe execution.

### 13. Background Controller Restore

The local background controller restores the snapshot before creating the DAG execution. This ensures downstream runtime services see the correct durable context before DAG creation, execution control checks, claim logic, policy evaluation, ledger/tracing/metrics correlation, and replay snapshot capture.

### 14. DAG Execution and Worker Loop

Once the DAG execution exists, workers operate through deterministic Redis-backed state. The worker loop handles claim recovery, retry promotion, ready step selection, atomic step claim, step execution, result persistence, dependency update, and convergence checks.

### 15. Execution Control

Execution control remains `ExecutionId`-level. It can pause, resume, cancel, wait for input, submit input, block claims, and override finalization when cancellation wins. The tenant boundary still comes from the durable snapshot associated with the execution path.

### 16. Finalization and Shared Run Status

When the DAG reaches a terminal state, the runtime persists the final execution record and completes the local run handle. The shared run is then updated with `LocalRunId`, `ExecutionId`, `AssignedRuntimeInstanceId`, final status, and failure reason when available. This links control-plane lifecycle and durable execution lifecycle without confusing `SharedRunId`, `RunId`, and `ExecutionId`.

### 17. Observability and Audit

Observability can duplicate tenant information in metadata for inspection, but the source of truth remains `ExecutionContextSnapshot.TenantId`. Ledger, tracing, metrics, replay, and audit should correlate shared run id, local run id, execution id, runtime instance id, worker id, tenant id, context key, and correlation id.

---

## How to Read the Flow

The diagram is intentionally long because the runtime path is not a single method call. It is a sequence of control-plane, persistence, queue, provider, local runtime, and worker execution hops.

The most important idea is that the request changes form several times:

```text
RBAC request context
  -> ExecutionContextSnapshot
  -> AiSharedRuntimeControllerRequest
  -> AiSharedRunRecord
  -> AiSharedQueueItem
  -> AiRuntimePipelineRunRequest
  -> AiRuntimeQueuedPipelineRun
  -> AiExecutionRecord / AiExecutionState
```

Each transformation has a different responsibility.

The RBAC request context authorizes the caller and resolves the operational context. The snapshot makes that context durable. The shared run records the global control-plane lifecycle. The shared queue item coordinates delayed or requeued dispatch. The runtime pipeline request crosses the provider boundary. The local queued run belongs to one runtime instance. The execution record and execution state represent the durable DAG lifecycle.

The flow should therefore be read as a set of safety gates. At every gate, the runtime either preserves the durable context or refuses to continue.

---

## Critical Context Boundaries

The most important part of this design is not only that a snapshot exists. It is where the snapshot must be restored.

### Boundary 1 — MCP/API to Shared Run

The adapter begins with an ambient RBAC `ExecutionContext`. That context is valid for the current request only.

Before the run is persisted, the adapter must attach an `ExecutionContextSnapshot` to the runtime request. Once the shared run is written to Redis or memory, future processing must no longer depend on the original request thread or HTTP/MCP call.

### Boundary 2 — Shared Queue Pump to Dispatcher

The shared queue pump is a background component. It can run later, on another task, and without the original user request context.

The dispatcher must therefore:

1. load the shared queue item;
2. load the shared run;
3. read the persisted `ExecutionContextSnapshot`;
4. restore it into the RBAC execution context accessor;
5. run tenant-aware admission;
6. dispatch the run;
7. restore or clear the previous context.

This is the critical protection against tenant-unsafe dispatch.

### Boundary 3 — Runtime Provider to Runtime Instance

Provider dispatch can be local today and HTTP, gRPC, or Kubernetes later. The provider boundary is still a distributed boundary.

The selected provider must preserve the `ExecutionContextSnapshot` when sending the `AiRuntimePipelineRunRequest` to the target runtime instance. The target runtime instance must not reconstruct tenant identity from metadata, runtime instance name, or control-plane id.

### Boundary 4 — Local Queue to Background Controller

The local runtime queue is another asynchronous boundary. A run can be enqueued now and executed later.

The background controller must restore the snapshot before DAG creation. If the snapshot is missing, the runtime fails fast instead of starting a tenant-ambiguous execution.

This is exactly the bug the enterprise demo exposed: the demo had an ambient `DemoExecutionContextAccessor`, but the local queued run did not carry a durable snapshot. The fix was to create the `ExecutionContextSnapshot` from the current RBAC/demo context and attach it to `AiRuntimePipelineRunRequest`.

---

## Why Ambient Context Alone Is Not Enough

`IExecutionContextAccessor.Current` is useful during the current request or current in-process call. It is not a persistence format and it is not a distributed contract.

It can disappear across:

- Redis persistence;
- hosted background services;
- queue pump cycles;
- scale-out watcher cycles;
- provider dispatch;
- HTTP or gRPC calls;
- local runtime queues;
- retry/requeue paths;
- worker execution loops.

That is why the runtime separates three concepts:

| Concept | Role |
|---|---|
| RBAC `ExecutionContext` | Current in-memory authorization and capability context. |
| `ExecutionContextSnapshot` | Durable runtime context persisted with the run. |
| Metadata | Debugging, observability, and correlation only. |

The tenant boundary is never inferred from metadata. Metadata can help humans read logs, but tenant routing and isolation must use `ExecutionContextSnapshot.TenantId`.

---

## Tenant Isolation Semantics

The flow supports three runtime placement modes.

### Shared Runtime Capacity

Shared runtime capacity is the default pool. It is suitable for default tenants, tests, demos, and tenants that do not require dedicated placement.

Shared capacity is visible to:

- Shared/default tenants;
- Hybrid tenants when shared fallback is allowed;
- Dedicated tenants only when their tenant settings explicitly allow shared fallback.

### Dedicated Runtime Capacity

Dedicated runtime capacity belongs to one tenant or tenant group.

Dedicated capacity is visible only when:

```text
runtime.TenantId == request.TenantId
    OR
runtime.TenantGroupId == request.TenantGroupId
```

Free capacity is not enough. Ownership must match.

### Hybrid Runtime Capacity

Hybrid tenants prefer tenant-owned capacity but can fall back to Shared capacity when allowed.

The important rule is:

```text
Hybrid fallback does not make unowned Hybrid runtime capacity visible.
```

Fallback means the tenant may use Shared capacity. It does not turn every Hybrid runtime into a shared runtime.

---

## Admission and Scale-Out Responsibilities

Admission is decisioning, not execution.

The admission controller decides one of these outcomes:

| Decision | Meaning |
|---|---|
| `AssignToInstance` | A tenant-visible runtime instance has suitable capacity. |
| `QueueGlobally` | The run should wait in the shared queue. |
| `RequestScaleOut` | No suitable capacity exists, but policy allows new capacity. |
| `Reject` | No suitable capacity or fallback is available. |

When admission returns `RequestScaleOut`, the scale-out request must copy tenant runtime settings. That includes tenant id, tenant group id, isolation mode, fallback policy, runtime instance prefix, worker count, max concurrent runs, and queue capacity.

The runtime instance prefix is especially important:

```text
runtime-instance-*
tenant-a-runtime-*
tenant-b-runtime-*
```

The local scaler must count matching prefixes only. Counting all hosts globally would allow one tenant's local runtime instances to incorrectly block or satisfy another tenant's scale-out limit.

---

## Shared Queue and Requeue Semantics

The shared queue is a control-plane queue. It is not the DAG execution state and it is not the local runtime queue.

A shared run can enter the queue in two common ways:

1. **Queue-first submission** — the controller intentionally queues first, and the pump dispatches later.
2. **Scale-out fulfillment** — the run first requests scale-out, then gets requeued after capacity is created.

The queue item references the shared run. It should not become a second source of truth for tenant context. The durable tenant context remains attached to the shared run snapshot.

This avoids context drift between queue item, shared run, and runtime request.

---

## Local Runtime Queue Semantics

The local runtime queue belongs to one runtime instance.

At this stage, the run has already passed shared admission and provider dispatch. However, the local queue still must preserve the snapshot because the local background controller executes later.

A local queued run must therefore include:

```text
LocalRunId
PipelineName / PipelineDefinition / Json / File
Input
ExecutionContextSnapshot
AssignedRuntimeInstanceId
```

The background controller must not create an `ExecutionId` from a queued run that has no snapshot.

---

## Execution Control in the Flow

Execution control is `ExecutionId`-level.

It begins after the local background controller creates the durable DAG execution. This is why the runtime distinguishes:

```text
SharedRunId  -> control-plane/global queue lifecycle
RunId        -> runtime instance local queue lifecycle
ExecutionId  -> durable DAG execution lifecycle
```

Pause, resume, cancel, waiting-for-input, submit-input, claim blocking, and cancellation finalization override belong to the `ExecutionId` lifecycle.

Cancellation finalization override is important because cancellation can race with natural completion. If a cancel request exists and a step completes successfully at nearly the same time, finalization must preserve the cancellation intent.

---

## Demo Runner Lesson

The enterprise demo validated the same rule as the distributed path.

The demo had:

```text
DemoExecutionContextAccessor.Current
```

But the local runtime request also needed:

```text
AiRuntimePipelineRunRequest.ExecutionContextSnapshot
```

The corrected demo path is:

```text
DemoExecutionContextAccessor.Current
  -> create ExecutionContextSnapshot
  -> AiRuntimePipelineRunRequest.ExecutionContextSnapshot
  -> AiRuntimeQueuedPipelineRun.ExecutionContextSnapshot
  -> background controller restore
  -> DAG execution creation
```

This proves the rule is not only for MCP. Direct local demo execution, shared queue execution, HTTP dispatch, and future Kubernetes dispatch all need the same durable context invariant.

---

## Failure Modes Prevented

| Failure Mode | Runtime Protection |
|---|---|
| Background dispatch runs with no tenant context | Dispatcher restores `ExecutionContextSnapshot` before admission. |
| Runtime queue creates execution without tenant boundary | Local queue requires snapshot and fails fast when missing. |
| Hybrid tenant sees another tenant's Hybrid runtime | Visibility evaluator requires tenant or tenant-group ownership. |
| Dedicated tenant uses unrelated capacity | Registry/capacity filtering is tenant-aware. |
| Scale-out creates generic capacity instead of tenant capacity | Scale-out request carries tenant runtime settings. |
| One tenant's local hosts satisfy another tenant's scale-out count | Local scaler counts matching runtime prefixes only. |
| Metadata becomes routing source of truth | Tenant routing uses snapshot; metadata remains debug-only. |
| Cancelled execution converges as completed | Finalization checks control state and applies cancellation override. |

---

## Operational Debugging Checkpoints

When debugging this flow, check the following points in order:

1. Does the adapter or demo runner create an `ExecutionContextSnapshot`?
2. Is the snapshot attached to `AiRuntimePipelineRunRequest`?
3. Is the snapshot persisted on `AiSharedRunRecord`?
4. Does the shared queue dispatcher restore the snapshot before admission?
5. Does dispatch preserve the snapshot into the provider request?
6. Does the runtime local queue store the snapshot?
7. Does the background controller restore the snapshot before DAG creation?
8. Does execution finalization preserve control-state override rules?

If one of these checkpoints fails, the runtime should fail loudly instead of executing with an ambiguous tenant boundary.

---

## Identity Summary

| Identity | Meaning |
|---|---|
| `SharedRunId` | Shared control-plane run identity before dispatch. |
| `RunId` / `LocalRunId` | Runtime instance local queue/background-controller run identity. |
| `ExecutionId` | Durable DAG execution identity. |
| `RuntimeInstanceId` | Dispatchable runtime instance identity. |
| `WorkerId` | Runtime worker identity. |
| `TenantId` | Durable tenant boundary from `ExecutionContextSnapshot`. |
| `ContextKey` | Volatile RBAC/correlation/debug context key. |

---

## Non-Negotiable Invariants

1. `ExecutionContextSnapshot.TenantId` is the durable tenant boundary.
2. Metadata may duplicate tenant information but must not become the source of truth for tenant routing.
3. Shared, queued, scaled, local, and background runtime paths must preserve the snapshot.
4. Any background dispatch hop must restore the snapshot before tenant-aware logic.
5. Runtime local queue entries must contain a snapshot.
6. Dedicated and Hybrid runtime instances must not be visible to unrelated tenants.
7. Hybrid fallback allows a Hybrid tenant to use Shared runtime capacity; it does not make unowned Hybrid runtime capacity visible.
8. Scale-out requests must carry tenant runtime settings.
9. Local scale-out must count matching runtime prefixes only.
10. Cancellation finalization override must preserve operator intent even when DAG completion races with cancel.

---

## Validation Evidence

Validated areas include RBAC context to durable snapshot propagation, tenant-aware shared queue dispatch, tenant-aware admission, Shared/Dedicated/Hybrid visibility rules, Redis scale-out request tenant field persistence, local scaler prefix-based counting, runtime local queue snapshot requirement, execution control finalization behavior, enterprise demo direct runtime snapshot propagation, and full test suite validation.

Current branch validation reached:

```text
1036 tests passing
enterprise runtime demo passing
```

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [MCP Server Control Plane](mcp-server-control-plane.md)
- [Shared Controller Usage](shared-controller-usage.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [Execution Control State](execution-control-state.md)
- [Distributed Execution](distributed-execution.md)
- [Testing Strategy](testing-strategy.md)
