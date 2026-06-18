# Multi-Tenant Control Plane Isolation

## Status

Implemented foundation on branch `feature/multi-tenant-control-plane-isolation`.

This document describes the tenant-aware control-plane and runtime-isolation model added to the deterministic AI runtime. It covers how tenant context flows from MCP to shared runs, admission, registry/capacity filtering, scale-out, dispatch, local runtime execution, and finalization.

The implementation validates Shared, Dedicated, and Hybrid runtime isolation modes with Redis-backed registry/capacity filtering, tenant-aware admission, tenant-scoped scale-out, and background context restoration.

## Core Principle

The durable tenant boundary is `ExecutionContextSnapshot.TenantId`.

`ContextKey` is not the durable tenant boundary. It is useful for RBAC, correlation, debugging, and request-level context resolution, but it must not be used as the primary partition key for runtime isolation.

Runtime metadata may duplicate tenant fields for observability, logs, and operational debugging, but metadata is not the source of truth for tenant isolation when strong typed fields exist.

```text
ExecutionContextSnapshot.TenantId  = durable tenant boundary
ExecutionContextSnapshot.TenantGroupId = optional shared enterprise/group boundary
ContextKey                         = volatile RBAC/correlation/debug context
Metadata                           = observability duplicate only
```

Every asynchronous or distributed hop must either carry or restore the `ExecutionContextSnapshot`.

## Tenant Runtime Settings

Tenant runtime behavior is resolved through tenant runtime settings. The current foundation uses hardcoded settings to prove the model before moving to database/config-backed tenant settings later.

### tenant-a

```text
IsolationMode                  = Dedicated
PreferDedicatedCapacity         = true
AllowSharedFallback             = false
MaxRuntimeInstances             = 3
WorkerCountPerInstance          = 10
MaxConcurrentRunsPerInstance    = 5
LocalQueueCapacity              = 500
RuntimeInstanceIdPrefix         = tenant-a-runtime
```

`tenant-a` must receive dedicated capacity only. It must not fall back to shared capacity.

### tenant-b

```text
IsolationMode                  = Hybrid
PreferDedicatedCapacity         = true
AllowSharedFallback             = true
MaxRuntimeInstances             = 2
WorkerCountPerInstance          = 5
MaxConcurrentRunsPerInstance    = 3
LocalQueueCapacity              = 250
RuntimeInstanceIdPrefix         = tenant-b-runtime
```

`tenant-b` prefers dedicated/hybrid capacity but may use shared capacity when allowed and visible.

### default / unknown / test-tenant

```text
IsolationMode                  = Shared
PreferDedicatedCapacity         = false
AllowSharedFallback             = true
MaxRuntimeInstances             = 1
WorkerCountPerInstance          = 10
MaxConcurrentRunsPerInstance    = 3
RuntimeInstanceIdPrefix         = runtime-instance
```

Default tenants use shared capacity.

## Isolation Modes

### Shared

A shared runtime instance is generic capacity. It is visible to shared tenants by default.

A Hybrid or Dedicated tenant can only see shared capacity if its tenant runtime settings allow shared fallback.

```text
Shared runtime + Shared tenant                 => visible
Shared runtime + Hybrid tenant fallback true   => visible
Shared runtime + Dedicated tenant fallback false => not visible
```

### Dedicated

A dedicated runtime instance belongs to a specific tenant or tenant group.

It is visible only when one of these matches:

```text
descriptor.TenantId      == current TenantId
or
descriptor.TenantGroupId == current TenantGroupId
```

Dedicated capacity is never visible to unrelated tenants.

### Hybrid

A hybrid runtime instance is still owned capacity. It belongs to a specific tenant or tenant group and is visible only to matching tenant context.

Important rule:

```text
AllowSharedFallback on a Hybrid descriptor does not make an unowned Hybrid runtime visible.
```

Hybrid fallback means a Hybrid tenant may use a Shared runtime when allowed. It does not mean every Hybrid runtime is globally shareable.

```text
Hybrid runtime + matching TenantId             => visible
Hybrid runtime + matching TenantGroupId        => visible
Hybrid runtime + unrelated tenant              => not visible
Hybrid runtime without owner                   => not visible
Shared runtime + Hybrid tenant fallback true   => visible
```

## Runtime Instance Identity

Runtime instance identifiers are generated from tenant runtime settings.

```text
Default/shared tenant   => runtime-instance-1
tenant-a Dedicated      => tenant-a-runtime-1
tenant-b Hybrid         => tenant-b-runtime-1
```

The runtime instance prefix is part of the capacity scope. Local scale-out must count capacity matching the tenant/runtime prefix, not the global number of local hosts.

This avoids the bug where existing shared capacity caused dedicated tenant scale-out to incorrectly no-op.

```text
Wrong:
  if target <= allHosts.Count => no-op

Correct:
  if target <= hostsMatching(RuntimeInstanceIdPrefix).Count => no-op
```

## End-to-End Flow

```text
MCP Tool
  ↓
RBAC execution context
  ↓
ExecutionContextSnapshot
  ↓
SharedRuntimeController.SubmitRun
  ↓
SharedRunStore persists AiSharedRunRecord + snapshot
  ↓
AdmissionController
  ↓
Tenant runtime settings
  ↓
Tenant-visible RuntimeInstanceRegistry + CapacityStore
  ↓
AssignToInstance OR RequestScaleOut
  ↓
ScaleOutRequestStore persists tenant runtime fields
  ↓
ScaleOutWatcher / Provider / LocalScaler / future K8s
  ↓
RuntimeInstanceRegistry + CapacityStore updated
  ↓
SharedQueue item claimed by pump
  ↓
SharedQueueDispatcher restores ExecutionContextSnapshot
  ↓
Admission runs again under restored tenant context
  ↓
Shared run dispatched to runtime instance
  ↓
Runtime local queue stores request + ExecutionContextSnapshot
  ↓
BackgroundController restores snapshot
  ↓
DAG execution created
  ↓
Worker executes steps / retry / recovery / convergence
  ↓
Execution-control finalization override
  ↓
Final AiExecutionRecord
  ↓
SharedRun final status
```

## MCP Context Propagation

The MCP server resolves request context through RBAC and maps it into an `ExecutionContextSnapshot`.

The snapshot must be attached to the runtime request before the run is submitted.

```csharp
new AiRuntimePipelineRunRequest
{
    PipelineName = pipelineName,
    ExecutionContextSnapshot = executionContextSnapshot,
    PipelineDefinition = pipelineDefinition,
    Input = input
}
```

The controller request may also carry `TenantId`, but the durable boundary must be the runtime request snapshot.

```csharp
new AiSharedRuntimeControllerRequest
{
    Operation = AiSharedRuntimeControllerOperation.SubmitRun,
    PipelineKey = pipelineName,
    TenantId = tenantId,
    RequestedBy = requestedBy,
    Source = source,
    RunRequest = runtimeRunRequest
}
```

## Shared Run Store

A submitted run is persisted as an `AiSharedRunRecord`.

The record must include the durable execution context snapshot.

```text
AiSharedRunRecord
- SharedRunId
- Status
- RunRequest
- ExecutionContextSnapshot
- LocalRunId
- ExecutionId
- AssignedRuntimeInstanceId
- AdmissionDecision
- PipelineKey
- CorrelationId
- RequestedBy
- Source
- Metadata
- ControlPlaneId
```

The shared run store is the handoff point between MCP/control-plane submission and asynchronous/background dispatch.

Because background dispatch does not have the original HTTP/MCP ambient context, it must restore the context from this persisted snapshot.

## Admission Controller

The admission controller decides whether a run can be assigned to an existing runtime instance or must request scale-out.

Admission is tenant-aware.

It uses:

```text
- current ExecutionContextSnapshot.TenantId
- current ExecutionContextSnapshot.TenantGroupId
- tenant runtime settings
- runtime instance registry
- runtime capacity store
- visibility evaluator
```

Possible outcomes:

```text
AssignToInstance
RequestScaleOut
Deny / Wait / NoCapacity depending policy and mode
```

The selected runtime instance must be visible to the current tenant context.

## Registry and Capacity Visibility

Registry and capacity list operations are filtered by tenant visibility.

This is critical because admission must never consider capacity belonging to another tenant.

### Shared runtime visibility

```text
Shared runtime is visible when:
- current tenant mode is Shared
- or current tenant settings allow shared fallback
```

### Dedicated runtime visibility

```text
Dedicated runtime is visible when:
- TenantId matches
- or TenantGroupId matches
```

### Hybrid runtime visibility

```text
Hybrid runtime is visible when:
- TenantId matches
- or TenantGroupId matches
```

Hybrid fallback is applied by allowing the Hybrid tenant to see Shared runtime capacity. It does not make unrelated Hybrid runtime capacity visible.

## Scale-Out Request Propagation

When admission cannot find tenant-visible capacity, it creates a scale-out request.

The scale-out request must copy tenant runtime settings as strong fields.

```text
AiRuntimeScaleOutRequestRecord
- RequestId
- TenantId
- TenantGroupId
- IsolationMode
- PreferDedicatedCapacity
- AllowSharedFallback
- MaxRuntimeInstances
- RuntimeInstanceIdPrefix
- WorkerCountPerInstance
- MaxConcurrentRunsPerInstance
- LocalQueueCapacity
- RequestedTargetInstanceCount
- Status
- FulfilledRuntimeInstanceId
```

Redis persistence must roundtrip all these fields. Otherwise the watcher/provider can lose tenant scope and create the wrong runtime capacity.

## Local Runtime Scale-Out

The local runtime scaler creates local runtime instance hosts for integration and local provider scenarios.

It must scope capacity by `RuntimeInstanceIdPrefix`.

```text
Default/shared request:
  RuntimeInstanceIdPrefix = runtime-instance
  creates host runtime-instance-1

Dedicated tenant-a request:
  RuntimeInstanceIdPrefix = tenant-a-runtime
  creates host tenant-a-runtime-1

Hybrid tenant-b request:
  RuntimeInstanceIdPrefix = tenant-b-runtime
  creates host tenant-b-runtime-1
```

The scaler must not use global `hosts.Count` to decide whether the tenant target is already satisfied.

Correct behavior:

```text
matchingHosts = hosts where RuntimeInstanceId contains $":{RuntimeInstanceIdPrefix}-"

if targetInstanceCount <= matchingHosts.Count:
    no-op inside this tenant/runtime scope
else:
    create additional matching runtime hosts
```

## Shared Queue Dispatcher Context Restore

The shared queue pump processes queued shared runs asynchronously.

The dispatcher must restore the shared run snapshot before admission and dispatch.

```text
Claim shared queue item
  ↓
Load AiSharedRunRecord
  ↓
Read sharedRun.ExecutionContextSnapshot
  ↓
Restore RBAC ExecutionContext
  ↓
Run tenant-aware admission
  ↓
Reserve selected capacity
  ↓
Dispatch to runtime instance
  ↓
Mark shared run as Dispatched
  ↓
Restore previous context / clear current context
```

This avoids the bug where the background dispatcher had no tenant context and therefore saw zero visible instances.

## Runtime Local Queue Requirements

Direct runtime queued runs now require an `ExecutionContextSnapshot`.

The background controller restores this snapshot before creating or executing the DAG run.

If the snapshot is missing, the runtime should fail fast with a clear error instead of executing under an undefined tenant context.

```text
No execution context snapshot is available for runtime run ...
The shared run must persist ExecutionContextSnapshot in Redis and propagate it to the local runtime queue.
```

This requirement applies to:

```text
- MCP-submitted runs
- shared-controller runs
- scale-out requeued runs
- direct runtime integration tests
- future HTTP/gRPC/Kubernetes providers
```

## DAG Execution and Finalization

Once dispatched to a runtime instance, the normal DAG engine flow applies.

```text
BackgroundController restores ExecutionContextSnapshot
  ↓
AiDagExecutionEngine.CreateAsync
  ↓
Redis DAG execution record/state/steps created
  ↓
Workers call ExecuteNextAsync
  ↓
Steps are claimed atomically
  ↓
Step executes
  ↓
Success / retry / failure / recovery persisted
  ↓
DAG convergence determines terminal state
  ↓
Execution control can override terminal finalization
```

Cancellation has precedence over natural DAG completion.

If work was already claimed and completes successfully after cancellation was requested, durable execution-control state must still be able to persist the final execution as cancelled.

## Validated Test Coverage

The implementation has been validated with 1036 tests passing.

Tenant isolation coverage includes:

```text
- default/shared tenant scale-out creates runtime-instance-1
- tenant-a Dedicated scale-out creates tenant-a-runtime-1
- tenant-b Hybrid scale-out creates tenant-b-runtime-1
- Hybrid tenant can fall back to shared runtime when allowed
- Dedicated tenant does not fall back to shared runtime when disabled
- Redis runtime registry filters by tenant visibility
- Redis capacity store filters by tenant visibility
- admission only considers tenant-visible runtime capacity
- scale-out request persists tenant runtime fields through Redis
- local scaler scopes capacity by RuntimeInstanceIdPrefix
- shared queue dispatcher restores ExecutionContextSnapshot before admission
- direct runtime queued runs require ExecutionContextSnapshot
- Hybrid runtime without tenant owner is not visible
```

## Design Rules

### Do

```text
- Carry ExecutionContextSnapshot across every async/distributed boundary.
- Use ExecutionContextSnapshot.TenantId as the durable tenant boundary.
- Use strong typed fields for tenant runtime settings and scale-out records.
- Duplicate tenant fields into metadata only for observability.
- Filter registry and capacity before admission decisions.
- Scope local scale-out by RuntimeInstanceIdPrefix.
- Treat background workers as contextless until they restore a snapshot.
```

### Do Not

```text
- Do not use ContextKey as durable tenant isolation.
- Do not route tenants based only on metadata when strong fields exist.
- Do not let Hybrid runtime capacity become globally visible because fallback is allowed.
- Do not count global local hosts for tenant-scoped scale-out.
- Do not enqueue direct runtime work without ExecutionContextSnapshot.
- Do not rely on ambient AsyncLocal context inside background dispatch flows.
```

## Future Work

This foundation prepares the runtime for provider-level tenant isolation across:

```text
- HTTP runtime instances
- gRPC runtime instances
- Kubernetes runtime pods
- Mongo-backed tenant settings
- production-grade tenant configuration
- tenant-aware dashboards and observability
```

Planned hardening after Kubernetes demo:

```text
- HTTP/gRPC circuit breakers
- dispatch timeouts
- Redis TIME in Lua scripts
- queue max depth / backpressure
- DLQ store
- Mongo indexes
- MCP rate limiting
- Redis registry TTL
- Redis capacity TTL
- registry self-healing
```

Larger future storage optimizations remain separate:

```text
- step-level DAG storage
- O(1) dependency counters
```

## Related Documents

Recommended docs to cross-link with this document:

```text
docs/ai/architecture-overview.md
docs/ai/runtime-control-plane.md
docs/ai/runtime-discovery-registry-capacity.md
docs/ai/runtime-instance-provider-model.md
docs/ai/mcp-server-control-plane.md
docs/ai/shared-controller-usage.md
docs/ai/shared-queue-pump-and-worker-capacity.md
docs/ai/testing-strategy.md
docs/ai/context-resolution-and-helpers.md
docs/ai/config-driven-runtime.md
```
