# Runtime Instance Provider Model

Status: Implemented foundation / validated for local dispatch, HTTP pooled runtime scenarios, HTTP dispatch hardening, provider-based scale-out, Redis-backed scale-out request persistence, local runtime scale-out, HTTP runtime scale-out, Runtime Host Manager process-host provisioning, fulfilled-run requeue, end-to-end MCP scale-out execution, durable replay / ledger / trace validation, and tenant-aware runtime isolation across shared, dedicated, and hybrid runtime modes.

This document describes the **runtime instance provider model** for the Deterministic AI Runtime control plane.

The provider model makes runtime instance administration, dispatch, status/control operations, and scale-out provider-based, dynamically extensible, and compatible with the existing local runtime queue architecture.

It also defines how provider dispatch and provider scale-out participate in the multi-tenant control-plane isolation model.

The complete technical reference is currently preserved in:

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)

---

## Purpose

The runtime now has:

- runtime instance registration
- runtime roles
- Redis-backed runtime registry
- Redis-backed runtime capacity store
- Redis-backed control-plane discovery store
- control-plane id resolver
- runtime capacity descriptors
- runtime worker capacity visibility
- local runtime instance pool
- local runtime instance provider
- HTTP runtime instance provider foundation
- pooled HTTP runtime instance hosting
- provider-based scale-out capability
- local runtime instance scaler
- Redis-backed scale-out request store
- scale-out request watcher
- fulfilled scale-out run requeue service
- shared queue
- shared runtime controller
- queue-first submit mode
- shared queue pump and manual drain
- shared queue pump readiness gate
- dispatch-time admission
- Redis-backed admission reservation store
- MCP control-plane adapter
- tenant-aware runtime settings
- tenant-aware admission
- tenant-visible registry filtering
- tenant-visible capacity filtering
- shared, dedicated, and hybrid runtime isolation
- HTTP runtime provider dispatch hardening
- HTTP dispatch timeout, retry, and circuit-breaker handling
- HTTP structured dispatch failure reasons
- HTTP runtime scale-out provider capability
- HTTP runtime scale-out provisioner foundation
- Runtime Host Manager abstraction
- host creation modes for Fixture, Process, Attach, and Kubernetes-oriented provisioning
- process-host HTTP runtime scale-out with real `RuntimeInstanceOnly` host processes
- tenant-aware HTTP scale-out request fulfillment
- HTTP shared/dedicated/hybrid fallback policy validation
- end-to-end MCP Redis local scale-out execution validation
- MCP production runtime scenario framework
- durable replay / ledger / trace validation across process boundaries

The provider model exists so the shared controller and admission layer do not become transport-specific.

Today, dispatch already flows through provider-style runtime instance resolution for local and HTTP pooled runtime scenarios.

Tomorrow, runtime instances may live behind:

- local in-memory registry
- Redis command queue
- HTTP endpoint
- gRPC endpoint
- Kubernetes pod/service
- external provider
- hosted runtime worker pool

The runtime architecture should not be rewritten for each transport.

Providers solve this.

---

## Core Principle

Admission decides **which runtime instance** should receive a run.

Providers decide **how to communicate** with that runtime instance.

For scale-out, admission decides **that more capacity is needed**.

The provider selector decides **which provider can create that capacity**.

```text
Admission
    decides WHO or WHETHER SCALE-OUT IS NEEDED

Provider Router
    decides HOW TO REACH OR SCALE THE TARGET ENVIRONMENT

Provider
    performs the transport-specific operation
```

Scale-out is modeled as a provider capability.

`IAiRuntimeScaleOutProvider` extends `IAiRuntimeInstanceProvider`.

This means scale-out uses the same provider discovery/routing model as dispatch, status, and control.

There is no separate scale-out architecture.

---

## Multi-Tenant Provider Boundary

The provider model must preserve the tenant boundary established by the control plane.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

The following values are useful but must not be treated as the durable tenant boundary:

```text
ContextKey
metadata tenant aliases
provider metadata
transport endpoint
runtime instance id string
```

The provider model must preserve `ExecutionContextSnapshot` across every hop:

```text
MCP request
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
SharedRunRecord
    ↓
Shared queue dispatcher restores context
    ↓
Admission filters tenant-visible registry/capacity
    ↓
Provider dispatch
    ↓
Target runtime local queue
    ↓
Background controller restores context
    ↓
DAG execution
```

Providers may use metadata for diagnostics and routing, but tenant isolation must come from strong runtime settings, registry/capacity descriptors, and the execution context snapshot.

---

## Tenant Runtime Modes

Runtime capacity can be organized by tenant mode.

```text
Shared
    Runtime capacity is shared/default capacity.

Dedicated
    Runtime capacity belongs to one tenant or tenant group.

Hybrid
    Runtime capacity may have dedicated tenant-owned capacity,
    and the tenant may optionally fall back to shared capacity.
```

Validated tenant settings:

```text
tenant-a
    IsolationMode = Dedicated
    PreferDedicatedCapacity = true
    AllowSharedFallback = false
    RuntimeInstanceIdPrefix = tenant-a-runtime
    MaxRuntimeInstances = 3
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 5
    LocalQueueCapacity = 500

tenant-b
    IsolationMode = Hybrid
    PreferDedicatedCapacity = true
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = tenant-b-runtime
    MaxRuntimeInstances = 2
    WorkerCountPerInstance = 5
    MaxConcurrentRunsPerInstance = 3
    LocalQueueCapacity = 250

default / test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    RuntimeInstanceIdPrefix = runtime-instance
    MaxRuntimeInstances = 1
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 3
```

Provider dispatch and scale-out must respect these settings.

---

## Tenant Visibility Rules

Admission, provider routing, runtime registry listing, and capacity listing rely on the same visibility rules.

```text
Shared runtime instance:
    visible to Shared tenants
    visible to Hybrid/Dedicated tenants only if tenant settings allow shared fallback

Dedicated runtime instance:
    visible only if TenantId or TenantGroupId matches

Hybrid runtime instance:
    visible only if TenantId or TenantGroupId matches
    AllowSharedFallback does not make an unowned Hybrid runtime visible
```

Important rule:

```text
Hybrid fallback means a Hybrid tenant may use Shared runtime capacity.

It does not mean every Hybrid runtime instance becomes visible to every Hybrid tenant.
```

This prevents accidental cross-tenant capacity leakage.

---

## Provider Identity vs Tenant Identity

Provider metadata tells the runtime **how** to contact an instance.

Tenant identity tells the runtime **who is allowed** to see and use an instance.

These concerns must remain separate.

Examples:

```text
provider.name = local
provider.transport = in-memory
tenant.id = tenant-a
isolation.mode = Dedicated
```

```text
provider.name = http
provider.endpoint = http://localhost:5001/runtime-instance/commands
tenant.id = tenant-b
isolation.mode = Hybrid
```

The provider router may use `provider.name`.

The visibility evaluator uses tenant isolation fields.

---

## Control-Plane Discovery and Runtime Identity

The provider model relies on a shared logical control-plane identity.

The MCP server can publish the active control-plane discovery descriptor through the Redis control-plane discovery store.

Runtime-only hosts that require discovery can then resolve the MCP-published logical control-plane identity through the control-plane id resolver before registering runtime instances or publishing capacity.

```text
MCP Control Plane
    ↓
Redis Control-Plane Discovery Store
    ↓
ControlPlaneIdResolver
    ↓
RuntimeInstanceOnly Host
    ↓
Runtime Instance Registration
    ↓
Runtime Capacity Publication
```

Important identities:

```text
ControlPlaneId
    logical shared control-plane scope used by Redis stores

ControlPlaneHostId
    physical/logical host publishing or owning the control-plane descriptor

RuntimeInstanceId
    dispatchable runtime instance identity, often host-scoped

RuntimeId
    local runtime id inside a host or pool

WorkerId
    worker identity inside a runtime instance
```

These identities must not be collapsed.

Cleanup must not depend on rediscovery after a runtime instance has already registered or published capacity.

During shutdown, registry unregister and capacity descriptor removal should reuse the known control-plane id for the runtime instance.

---

## Pump Identity vs Assigned Runtime Identity

The provider model must preserve the distinction between pump ownership and dispatch target.

```text
PumpRuntimeInstanceId
    identifies the runtime instance or control-plane worker executing a pump cycle

AssignedRuntimeInstanceId
    identifies the runtime instance selected by admission for dispatch
```

These two identities are intentionally separate.

A pump can claim a shared queue item without being the runtime instance that receives the run.

```text
Shared queue item pending
    ↓
PumpRuntimeInstanceId claims queue work
    ↓
Shared queue dispatcher loads shared run
    ↓
Restores ExecutionContextSnapshot
    ↓
Admission re-evaluates tenant-visible capacity
    ↓
AssignedRuntimeInstanceId is selected
    ↓
Provider router resolves transport
    ↓
Provider dispatches into target runtime local queue
```

Production code must not assume:

```text
PumpRuntimeInstanceId == AssignedRuntimeInstanceId
```

---

## What Providers Must Not Change

Providers must not replace local runtime queues.

Providers must not bypass deterministic DAG execution.

Providers must not mutate execution state directly.

Providers must not claim DAG steps.

Providers must not become the runtime engine.

Providers must not use metadata as the durable tenant boundary.

Providers must not dispatch work to a runtime instance that is not tenant-visible.

The local runtime queue remains the ownership boundary for an executable runtime instance.

```text
Shared Queue
    ↓
Admission
    ↓
Provider Router
    ↓
Provider
    ↓
Runtime Instance Local Queue
    ↓
Workers
    ↓
DAG Execution Engine
```

---

## Runtime Instance Capacity and Worker Visibility

Runtime instance descriptors are not only transport descriptors.

They are visibility snapshots used by admission, dashboards, MCP tools, provider routing, and future autoscaling.

The runtime exposes worker-aware capacity fields through the runtime instance snapshot path:

```text
AiRuntimePipelineBackgroundController
    ↓
AiRuntimePipelineQueueState
    ↓
AiRuntimeInstanceRegistrationHostedService
    ↓
AiRuntimeInstanceCapacityDescriptor
    ↓
IAiRuntimeInstanceRegistry
    ↓
RuntimeInstanceEntry
    ↓
AiRuntimeInstanceSnapshot
```

Important fields:

```text
WorkerCount
ActiveWorkerCount
AvailableWorkerCount
MaxLocalWorkersPerExecution
QueuedRunCount
RunningRunCount
ActiveRunCount
QueueCapacity
MaxConcurrentRuns
AvailableRunSlots
IsQueuePaused
CanAcceptRun
TenantId
TenantGroupId
IsolationMode
PreferDedicatedCapacity
AllowSharedFallback
RuntimeInstanceIdPrefix
```

`CanAcceptRun` is worker-aware.

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

A provider can only deliver work correctly if the target runtime instance is visible, eligible, and able to accept work.

---

## Max Local Workers Per Execution

`MaxLocalWorkersPerExecution` controls how many local workers from one runtime instance may be assigned to one execution.

This is a local runtime policy.

It is not the same as cross-instance execution assistance.

Example:

```text
Distributed.WorkerCount = 30
MaxLocalWorkersPerExecution = 4
```

Result:

```text
The runtime instance owns 30 workers.
One execution can use at most 4 local workers.
The remaining workers stay available for other executions.
```

The effective worker count per execution is resolved from:

```text
min(
  Distributed.WorkerCount,
  MaxLocalWorkersPerExecution,
  AvailableWorkerCount
)
```

This policy is visible through runtime instance snapshots and should be considered by admission and dashboards.

---

## Runtime Instance Descriptor as Source of Dispatch Metadata

Runtime instances publish descriptors and capacity information.

The provider model extends this idea.

A runtime instance descriptor should expose metadata such as:

```text
provider.name = local
```

A runtime instance capacity descriptor should also expose runtime capacity values such as:

```text
worker.count
active.worker.count
available.worker.count
max.workers.per.run
queued.run.count
running.run.count
available.run.slots
can.accept.run
```

Tenant metadata may be duplicated for observability:

```text
tenant.id = tenant-a
tenant.groupId = enterprise
isolation.mode = Dedicated
allow.shared.fallback = false
prefer.dedicated.capacity = true
runtime.instance.id.prefix = tenant-a-runtime
```

However, metadata is not the source of durable tenant isolation when strong fields are available.

Strong fields should be used first.

Metadata is for diagnostics, compatibility, and observability.

---

## Provider Discovery

Providers should be discovered dynamically using class attributes.

Example:

```csharp
[AiRuntimeInstanceProvider("local")]
public sealed class LocalAiRuntimeInstanceProvider :
    IAiRuntimeInstanceDispatchProvider,
    IAiRuntimeInstanceStatusProvider,
    IAiRuntimeInstanceControlProvider,
    IAiRuntimeScaleOutProvider
{
}
```

Another provider:

```csharp
[AiRuntimeInstanceProvider("redis-command-queue")]
public sealed class RedisCommandQueueRuntimeInstanceProvider :
    IAiRuntimeInstanceDispatchProvider,
    IAiRuntimeInstanceStatusProvider,
    IAiRuntimeInstanceControlProvider
{
}
```

The provider name is declared once on the class.

A DI scanner can load all provider types from selected assemblies.

---

## Provider Attribute

Planned attribute:

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class AiRuntimeInstanceProviderAttribute : Attribute
{
    public AiRuntimeInstanceProviderAttribute(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        ProviderName = providerName.Trim();
    }

    public string ProviderName { get; }
}
```

The provider name should be stable and lowercase.

Recommended provider names:

```text
local
redis-command-queue
http
grpc
kubernetes
```

---

## Base Provider Interface

The provider base interface should be small.

```csharp
public interface IAiRuntimeInstanceProvider
{
    bool CanHandle(
        AiRuntimeInstanceCapacityDescriptor descriptor);
}
```

The provider name can come from the class attribute instead of being repeated as a property.

This avoids drift between:

```csharp
[AiRuntimeInstanceProvider("local")]
```

and:

```csharp
ProviderName => "local"
```

---

## Capability-Based Provider Interfaces

Providers should use capabilities instead of one large interface.

A provider should only implement the capabilities it supports.

Capability examples:

```text
dispatch
status
control
capacity
scale-out
```

A local provider may support dispatch, status, control, and scale-out.

A Kubernetes provider may support discovery and scaling before it becomes a direct dispatch provider.

A Redis command queue provider may support dispatch and control through commands.

A provider can support scale-out without being the dispatch transport for every runtime instance.

---

## Dispatch Capability

Dispatch sends a shared run to a selected runtime instance.

```csharp
public interface IAiRuntimeInstanceDispatchProvider :
    IAiRuntimeInstanceProvider
{
    Task<AiRuntimeInstanceDispatchResult> DispatchRunAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        AiRuntimeInstanceDispatchRequest request,
        CancellationToken cancellationToken = default);
}
```

Dispatch should deliver the run to the target runtime instance.

It should not execute DAG steps directly.

It must preserve:

```text
SharedRunId
PipelineKey
RunRequest
ExecutionContextSnapshot
TenantId
CorrelationId
RequestedBy
Source
```

The target runtime queue must receive the original `ExecutionContextSnapshot`.

---

## Status Capability

Status reads runtime-local status.

```csharp
public interface IAiRuntimeInstanceStatusProvider :
    IAiRuntimeInstanceProvider
{
    Task<AiRuntimeInstanceRunStatusResult> GetRunStatusAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        string runId,
        CancellationToken cancellationToken = default);

    Task<AiRuntimeInstanceQueueStatusResult> GetQueueStatusAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
```

Status should work across local, Redis, HTTP, or gRPC providers.

---

## Control Capability

Control sends operational commands to a runtime instance.

```csharp
public interface IAiRuntimeInstanceControlProvider :
    IAiRuntimeInstanceProvider
{
    Task<AiRuntimeInstanceControlResult> PauseQueueAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<AiRuntimeInstanceControlResult> ResumeQueueAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task<AiRuntimeInstanceCancelResult> CancelRunAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        string runId,
        CancellationToken cancellationToken = default);

    Task<AiRuntimeInstanceDrainResult> DrainQueueAsync(
        AiRuntimeInstanceCapacityDescriptor descriptor,
        CancellationToken cancellationToken = default);
}
```

Control operations should use the runtime queue control plane or provider transport.

They should not mutate queue state externally.

---

## Capacity Capability

Capacity providers expose runtime capacity.

```csharp
public interface IAiRuntimeInstanceCapacityProvider :
    IAiRuntimeInstanceProvider
{
    Task<AiRuntimeInstanceCapacityDescriptor?> GetCapacityAsync(
        string runtimeInstanceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<AiRuntimeInstanceCapacityDescriptor>> ListCapacityAsync(
        CancellationToken cancellationToken = default);
}
```

The current Redis capacity store already provides the foundation for this capability.

Capacity listing must apply tenant visibility filtering when a current execution context snapshot is available.

---

## Scale-Out Capability

Scale-out providers request or create runtime capacity.

Current implemented abstraction:

```csharp
public interface IAiRuntimeScaleOutProvider :
    IAiRuntimeInstanceProvider
{
    Task<AiRuntimeScaleOutProviderResult> RequestScaleOutAsync(
        AiRuntimeScaleOutProviderRequest request,
        CancellationToken cancellationToken = default);
}
```

Important rule:

```text
IAiRuntimeScaleOutProvider extends IAiRuntimeInstanceProvider
```

This keeps scale-out inside the existing provider model.

There is no separate scale-out router.

Provider identity still comes from:

```text
[AiRuntimeInstanceProvider("local")]
[AiRuntimeInstanceProvider("http")]
[AiRuntimeInstanceProvider("kubernetes")]
```

Scale-out provider selection is handled by `IAiRuntimeScaleOutProviderSelector`.

The selector reuses `IAiRuntimeInstanceProviderRouter`.

Provider name resolution uses:

```text
AiRuntimeScaleOutProviderRequest.ProviderHint
    -> AiRuntimeInstanceRegistrationOptions.ProviderName
    -> local
```

Current validated local scale-out capability:

```text
provider.name = local
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
LocalAiRuntimeInstanceProvider
    ↓
AiLocalRuntimeInstanceScaler
    ↓
new local runtime instance created/registered/started
```

Kubernetes is most likely a scale-out provider before it is a direct dispatch provider.

Scale-out should remain separate from dispatch, but it should reuse the same provider model.

---

## Tenant-Aware Scale-Out

Scale-out requests carry tenant runtime settings.

The following fields must be preserved from admission to the scale-out request store, watcher, provider request, scaler, runtime registration, and capacity publication:

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

The scale-out provider must create capacity inside the requested tenant runtime scope.

For local scale-out, this means the local scaler must count existing matching hosts by `RuntimeInstanceIdPrefix`, not by global host count.

Correct examples:

```text
default/test-tenant shared
    runtime-instance-1

tenant-a dedicated
    tenant-a-runtime-1

tenant-b hybrid
    tenant-b-runtime-1
```

Incorrect behavior:

```text
A shared runtime already exists.
tenant-a requests dedicated capacity.
Scaler sees hosts.Count == 1 and returns shared runtime.
```

Correct behavior:

```text
A shared runtime already exists.
tenant-a requests dedicated capacity.
Scaler counts only hosts matching tenant-a-runtime.
No matching host exists.
Scaler creates tenant-a-runtime-1.
```

This prevents cross-tenant scale-out leakage.

---

## Provider Router

The provider router resolves providers by name and capability.

Expected responsibilities:

- read provider name from descriptor metadata
- find registered provider by attribute name
- verify requested capability is supported
- throw or return structured failure when provider is missing
- keep shared controller independent from transport details
- allow selectors to request a specific provider capability such as dispatch, status, control, capacity, or scale-out

Example usage:

```csharp
var provider = providerRouter.GetRequiredProvider<IAiRuntimeInstanceDispatchProvider>(
    descriptor);

var result = await provider.DispatchRunAsync(
    descriptor,
    request,
    cancellationToken);
```

The router should not override tenant visibility decisions.

The router should only resolve a provider for a descriptor that admission already selected as tenant-visible and eligible.

---

## Provider Resolution Flow

Shared queue dispatch flow:

```text
Shared Runtime Controller
    ↓
SharedRunRecord persisted with ExecutionContextSnapshot
    ↓
Shared queue item claimed
    ↓
Shared queue dispatcher restores ExecutionContextSnapshot
    ↓
Admission returns AssignedRuntimeInstanceId
    ↓
Load tenant-visible capacity descriptor
    ↓
Read provider metadata
    ↓
provider.name = local/http/grpc/...
    ↓
Provider Router
    ↓
Provider dispatch
    ↓
Target runtime local queue
```

Current HTTP pooled provider flow:

```text
MCP Control Plane
    ↓
Shared Queue Pump / Manual Drain
    ↓
Admission returns AssignedRuntimeInstanceId
    ↓
Load capacity descriptor
    ↓
provider.name = http
provider.endpoint = RuntimeInstanceOnly HTTP host endpoint
    ↓
HTTP Runtime Provider
    ↓
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
runtime-http-* child runtime instance
    ↓
Target runtime local queue
```

In this model, the parent HTTP host is transport and hosting infrastructure.

The dispatchable runtime identities are child runtime instances created by the local runtime instance pool.

```text
HTTP host identity != dispatch target
runtime-http-* child instance == dispatch target
```

Current local scale-out provider resolution flow:

```text
Shared Runtime Controller
    ↓
Admission returns RequestScaleOut
    ↓
Tenant runtime settings attached to admission decision
    ↓
StoreBackedAiRuntimeScaleOutRequestPublisher
    ↓
RedisAiRuntimeScaleOutRequestStore
    ↓
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
resolve provider.name:
        request.ProviderHint
        AiRuntimeInstanceRegistrationOptions.ProviderName
        local
    ↓
IAiRuntimeInstanceProviderRouter.TryGetProvider<IAiRuntimeScaleOutProvider>(...)
    ↓
LocalAiRuntimeInstanceProvider
    ↓
AiLocalRuntimeInstanceScaler
    ↓
create tenant-scoped local runtime instance
    ↓
register runtime instance / publish capacity
    ↓
mark scale-out request fulfilled
    ↓
requeue shared run for normal pump dispatch
```

Current HTTP scale-out provider resolution flow:

```text
Shared Runtime Controller
    ↓
Admission returns RequestScaleOut
    ↓
Tenant runtime settings attached to admission decision
    ↓
StoreBackedAiRuntimeScaleOutRequestPublisher
    ↓
RedisAiRuntimeScaleOutRequestStore
    ↓
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
resolve provider.name:
        request.ProviderHint = http
        AiRuntimeInstanceRegistrationOptions.ProviderName = http
    ↓
IAiRuntimeInstanceProviderRouter.TryGetProvider<IAiRuntimeScaleOutProvider>(...)
    ↓
HttpAiRuntimeInstanceProvider
    ↓
IAiHttpRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
host creation mode:
        Fixture
        Process
        Attach
        Kubernetes
    ↓
ProcessAiRuntimeHostCreationStrategy when HostCreationMode = Process
    ↓
RuntimeInstanceOnly process starts
    ↓
runtime self-registers
    ↓
runtime publishes heartbeat and capacity
    ↓
readiness confirms usable capacity
    ↓
scale-out request fulfilled
    ↓
requeue shared run for normal pump dispatch when applicable
```

The HTTP provider model is now validated beyond metadata-only scale-out.

The process-host path proves that HTTP scale-out can create real executable runtime capacity through the Runtime Host Manager while still preserving the provider boundary.

Metadata publication remains useful for foundation tests and diagnostics, but the production scenario framework validates the stronger path:

```text
HttpAiRuntimeInstanceProvider
    ↓
IAiHttpRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
RuntimeInstanceOnly process starts
    ↓
runtime self-registers
    ↓
runtime publishes capacity
    ↓
readiness confirms usable capacity
    ↓
scale-out request fulfilled
    ↓
HTTP dispatch
    ↓
DAG execution
```

Future Redis command queue flow:

```text
Shared Runtime Controller
    ↓
Admission returns RuntimeInstanceId
    ↓
Load capacity descriptor
    ↓
provider.name = redis-command-queue
provider.commandQueueKey = ai:runtime:mcp-runtime-1:commands
    ↓
Provider Router
    ↓
RedisCommandQueueRuntimeInstanceProvider
    ↓
Push dispatch command to Redis
    ↓
Remote runtime instance consumes command
    ↓
Remote runtime instance enqueues local run with ExecutionContextSnapshot
```

---

## Local Provider

The first provider is local.

It preserves current behavior.

```text
Local provider
    uses IAiSharedRuntimeInstanceRegistry
    resolves LocalAiSharedRuntimeInstance
    calls DispatchAsync
    enqueues through IAiRuntimeQueueControlPlane
```

The local provider supports or prepares:

- dispatch
- run status
- queue status
- pause queue
- resume queue
- cancel local run
- drain queue if supported
- local runtime scale-out when an `IAiLocalRuntimeInstanceScaler` is available

It should not change local queue internals.

Current implementation direction:

- local runtime instances are registered through `IAiSharedRuntimeInstanceRegistry`
- `LocalAiRuntimeInstanceProvider` resolves the target local runtime instance
- dispatch still enters the target runtime instance local queue
- DAG execution remains owned by the runtime engine and local workers
- provider routing does not mutate DAG execution state directly
- `LocalAiRuntimeInstanceProvider` implements `IAiRuntimeScaleOutProvider`
- when scale-out is requested, it delegates capacity creation to `AiLocalRuntimeInstanceScaler`
- when no scaler is registered, it returns a structured rejected provider result

For tenant-aware scale-out, the local provider must pass tenant runtime settings through to the scaler.

---

## Scale-Out Request Lifecycle

Scale-out is implemented as a persisted request lifecycle.

The shared controller does not create runtime instances directly.

The watcher does not dispatch directly.

The normal shared queue pump remains responsible for dispatch after capacity exists.

Validated lifecycle:

```text
SubmitRun
    ↓
DirectDispatch mode
    ↓
Admission sees no tenant-visible runtime capacity
    ↓
Decision = RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
    ↓
Tenant runtime settings copied to scale-out request
    ↓
StoreBackedAiRuntimeScaleOutRequestPublisher
    ↓
RedisAiRuntimeScaleOutRequestStore
    ↓
AiRuntimeScaleOutRequestWatcherHostedService
    ↓
AiRuntimeScaleOutProviderSelector
    ↓
IAiRuntimeScaleOutProvider
    ↓
LocalAiRuntimeInstanceProvider
    ↓
AiLocalRuntimeInstanceScaler
    ↓
tenant-scoped runtime instance created/registered/started
    ↓
ScaleOutRequest.Status = Fulfilled
    ↓
AiScaleOutFulfilledRunRequeueService
    ↓
SharedQueueItem.Status = Pending
    ↓
AiSharedQueuePump
    ↓
dispatch-time admission restores tenant context and sees new capacity
    ↓
Provider dispatch to selected runtime instance
    ↓
LocalRunId / ExecutionId
    ↓
runtime run completed
```

This validates the control loop required by Kubernetes scale-out before introducing Kubernetes pod creation.

Future Kubernetes provider flow should preserve the same boundary:

```text
RequestScaleOut
    ↓
Kubernetes provider creates or expands tenant-scoped runtime capacity
    ↓
runtime pod registers/publishes tenant-aware capacity
    ↓
scale-out request fulfilled
    ↓
shared run requeued
    ↓
pump dispatches normally
```

---

## Redis Command Queue Provider

The Redis command queue provider is the likely first remote provider.

It should send commands to a runtime-specific Redis queue.

Example command queue key:

```text
ai:runtime:{runtimeInstanceId}:commands
```

Dispatch command example:

```json
{
  "type": "dispatch-run",
  "runtimeInstanceId": "mcp-runtime-1",
  "sharedRunId": "shared-run-id",
  "pipelineKey": "pipeline-key",
  "tenantId": "tenant-id",
  "tenantGroupId": "tenant-group-id",
  "correlationId": "correlation-id",
  "requestedBy": "mcp",
  "source": "shared-controller"
}
```

The remote runtime instance would consume commands and enqueue into its local queue with the full `ExecutionContextSnapshot`.

This keeps cross-pod communication simple and resilient.

---

## HTTP Provider

The HTTP provider can dispatch, control, and scale runtime instances through HTTP-oriented runtime infrastructure.

The detailed HTTP-specific reference is documented in [HTTP Runtime Provider](http-runtime-provider.md).

The current HTTP provider has two distinct responsibilities:

```text
1. Dispatch transport
   Sends runtime commands to a selected runtime instance through HTTP.

2. Scale-out provider capability
   Participates in the provider-based scale-out loop by delegating capacity creation
   to IAiHttpRuntimeScaleOutProvisioner.
```

The provider-based runtime hosting work includes an HTTP runtime provider foundation validated for `RuntimeInstanceOnly` and `ControlPlaneWithHttpRuntimeInstances` scenarios.

Example metadata:

```text
provider.name = http
transport.name = http
provider.endpoint = http://runtime-1.ai-runtime.svc.cluster.local
```

HTTP dispatch responsibilities may include:

- dispatch run
- get run status
- pause/resume queue
- cancel run
- get queue state

HTTP dispatch hardening currently includes:

- dispatch timeout handling
- configurable retry support
- retry exhaustion classification
- non-retryable HTTP error classification
- circuit-breaker handling
- provider-unavailable handling
- invalid endpoint handling
- structured failure reasons
- dispatch failure persistence through the shared run store and queue dispatcher

Current HTTP dispatch failure reasons include:

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

HTTP pooled runtime hosting currently validates this shape:

```text
ControlPlaneWithHttpRuntimeInstances
    ↓
HTTP Runtime Provider
    ↓
RuntimeInstanceOnly HTTP Host
    ↓
Local Runtime Instance Pool
    ↓
runtime-http-1
runtime-http-2
runtime-http-3
```

The HTTP runtime host exposes transport endpoints.

The child runtime instances created by the pool expose the real execution capacity and are used as dispatch targets.

```text
HTTP host identity != dispatch target
runtime-http-* child instance == dispatch target
```

HTTP scale-out now validates this stronger process-host shape:

```text
Admission requires more capacity
    ↓
Redis scale-out request
    ↓
Scale-out watcher
    ↓
providerHint = http
    ↓
HttpAiRuntimeInstanceProvider as IAiRuntimeScaleOutProvider
    ↓
IAiHttpRuntimeScaleOutProvisioner
    ↓
IAiRuntimeHostManager
    ↓
HostCreationMode = Process
    ↓
ProcessAiRuntimeHostCreationStrategy
    ↓
real RuntimeInstanceOnly process starts
    ↓
runtime self-registers
    ↓
capacity becomes ready
    ↓
scale-out request fulfilled
    ↓
normal HTTP dispatch
    ↓
DAG execution
```

The HTTP scale-out provisioner still owns effective runtime settings and provider-specific metadata, but host lifecycle is delegated to the Runtime Host Manager.

This preserves the provider boundary:

```text
provider selects and transports
host manager creates or attaches
runtime self-registers
registry / capacity stores expose readiness
dispatcher performs normal HTTP dispatch
```

Host creation modes are intentionally extensible:

```text
Fixture
Process
Attach
Kubernetes
```

Important distinction:

```text
providerHint=http
    means the HTTP provider handles the scale-out request.

transport.name=http
    means the resulting runtime capacity is contacted through HTTP.
```

These values often match for HTTP, but they are not the same concept.

For Kubernetes, they may diverge:

```text
providerHint=kubernetes
transport.name=http or grpc
```

Validated HTTP tenant behavior includes:

```text
Shared tenant
    may use shared HTTP runtime capacity.

Dedicated tenant
    requests dedicated HTTP capacity and does not silently fall back to shared capacity.

Hybrid tenant
    may request dedicated HTTP capacity and may fall back to shared HTTP capacity when allowed.
```

This provider is useful when runtime pods, runtime hosts, or externally managed runtime workers expose an HTTP control endpoint.

---

## Runtime Host Manager

The Runtime Host Manager is the lifecycle boundary used by providers that need to create or attach runtime hosts.

It prevents the provider model from becoming responsible for process, fixture, attach, or Kubernetes mechanics.

Provider responsibility:

```text
select provider
resolve effective tenant runtime settings
prepare provider request
dispatch / status / control through transport
report provider failure reasons
```

Host manager responsibility:

```text
create or attach runtime host
pass runtime identity and tenant context
return endpoint / startup details
allow readiness to be observed through registry and capacity
```

Supported or planned host creation modes:

```text
Fixture
    test-host backed runtime host

Process
    real RuntimeInstanceOnly child process

Attach
    existing runtime endpoint managed outside the current process

Kubernetes
    future pod/service-backed runtime host
```

The process mode is validated by the MCP production scenario framework.

The validated process-host path launches:

```text
Multiplexed.AI.McpServer.Host.dll
```

as:

```text
RuntimeInstanceOnly
```

This proves the provider model can cross a real process boundary without replacing the runtime queue or DAG engine.


---

## gRPC Provider

The gRPC provider can perform the same operations as HTTP with a typed contract.

Example metadata:

```text
provider.name = grpc
provider.endpoint = grpc://runtime-1.ai-runtime.svc.cluster.local:5001
```

gRPC may be useful for lower-latency internal cluster communication.

The gRPC provider must preserve `ExecutionContextSnapshot` in dispatch messages.

---

## Kubernetes Provider

The Kubernetes provider should primarily focus on environment and scaling concerns.

It should be responsible for:

- listing runtime pods
- reading pod labels
- reading pod readiness
- mapping pod metadata to runtime descriptors
- requesting scale-out
- requesting scale-in
- creating or expanding runtime capacity when handling scale-out requests
- applying tenant-aware runtime settings to pod/deployment selection
- attaching Kubernetes metadata to descriptors

Kubernetes should not be required to dispatch a run directly.

Dispatch can still happen through:

- Redis command queues
- HTTP
- gRPC

This keeps Kubernetes responsibilities clean.

---

## Shared Queue Pump and Provider Dispatch

The shared queue pump does not own the final target runtime identity.

It owns the pump cycle.

The dispatch target is selected through admission during drain.

```text
Shared queue item pending
    ↓
PumpRuntimeInstanceId claims queue work
    ↓
Shared queue dispatcher loads shared run
    ↓
Shared queue dispatcher restores ExecutionContextSnapshot
    ↓
Admission re-evaluates the run with tenant visibility
    ↓
AssignedRuntimeInstanceId is selected
    ↓
Provider router resolves transport for assigned instance
    ↓
Provider dispatches into target runtime local queue
```

This is important for provider design.

A runtime instance can execute a pump cycle without necessarily receiving the run itself.

This enables future patterns such as:

- control-plane pod draining queue into remote runtime pods
- one runtime instance assisting dispatch to another runtime instance
- MCP manual drain selecting a target runtime through admission
- Kubernetes control-plane dispatching to HTTP/gRPC/runtime service endpoints

Tests that need deterministic pump-local dispatch should use a fake admission controller assigning the current pump runtime instance id as the dispatch target.

Production code should not assume `PumpRuntimeInstanceId == AssignedRuntimeInstanceId`.

---

## Admission and Provider Separation

Admission should not perform provider-specific dispatch.

Admission should decide:

```text
AssignToInstance(runtimeInstanceId)
QueueGlobally
RequestScaleOut
Reject
```

Provider dispatch should happen after admission.

```text
Admission
    ↓
RuntimeInstanceId
    ↓
Descriptor lookup
    ↓
Provider Router
    ↓
Dispatch Provider
```

This separation keeps admission deterministic and testable.

---

## Capacity-Aware and Tenant-Aware Admission

Capacity-aware admission should use Redis capacity descriptors as its primary source of truth.

Eligible runtime instances should satisfy:

- role is runtime
- status is ready or acceptable
- tenant visibility rules allow access
- queue is not paused unless allowed
- not draining unless allowed
- heartbeat is not stale
- can accept run
- effective available run slots is greater than zero
- available worker count is greater than zero
- max local workers per execution allows the requested execution shape

Recommended ordering:

```csharp
.OrderByDescending(instance => instance.EffectiveAvailableRunSlots)
.ThenByDescending(instance => instance.AvailableWorkerCount)
.ThenBy(instance => instance.RunningRunCount)
.ThenBy(instance => instance.QueuedRunCount)
.ThenByDescending(instance => instance.LastHeartbeatAtUtc)
.ThenBy(instance => instance.RuntimeInstanceId, StringComparer.Ordinal)
```

For tenant-specific admission, visibility filtering happens before provider routing.

---

## Slot Reservations

Capacity descriptors are snapshots.

The current implementation exposes the required capacity visibility and includes a Redis-backed admission reservation store used by heavy dispatch scenarios.

In multi-control-plane setups, snapshots alone are not enough.

Two control-plane processes may read the same available slot at the same time.

Admission should use Redis-backed reservations to protect selected capacity during dispatch.

Expected reservation flow:

```text
Admission selects candidate
    ↓
TryReserveRunSlot(runtimeInstanceId, sharedRunId, ttl)
    ↓
If reserved:
    dispatch
    commit reservation or let heartbeat reflect real running state
    ↓
If dispatch fails:
    release reservation
```

This protects against double assignment when multiple control-plane pods exist.

Lua-based reservation refinement may still be added later for stronger atomic coordination across more advanced scheduling paths.

---

## Provider-Based Runtime Administration

The provider model is not only for dispatch.

It should centralize runtime administration operations:

- dispatch run
- get run status
- get queue status
- cancel run
- pause queue
- resume queue
- drain queue
- list capacity
- request scale-out
- request scale-in
- requeue fulfilled scale-out shared runs through the normal shared queue lifecycle

This prevents future architecture sprawl.

All runtime administration should pass through provider capabilities where appropriate.

---

## Descriptor Metadata Keys

Recommended metadata keys:

| Key | Meaning |
|---|---|
| `provider.name` | Provider used to contact this runtime instance. |
| `provider.transport` | Optional transport hint such as `in-memory`, `redis`, `http`, `grpc`. |
| `provider.endpoint` | HTTP/gRPC endpoint. |
| `provider.commandQueueKey` | Redis command queue key. |
| `provider.namespace` | Kubernetes namespace. |
| `provider.podName` | Kubernetes pod name. |
| `provider.serviceName` | Kubernetes service name. |
| `provider.nodeName` | Kubernetes node name. |
| `provider.region` | Region or deployment zone. |
| `tenant.id` | Diagnostic duplicate of runtime tenant id. |
| `tenant.groupId` | Diagnostic duplicate of runtime tenant group id. |
| `isolation.mode` | Diagnostic duplicate of runtime isolation mode. |
| `allow.shared.fallback` | Diagnostic duplicate of fallback behavior. |
| `prefer.dedicated.capacity` | Diagnostic duplicate of tenant preference. |
| `runtime.instance.id.prefix` | Diagnostic duplicate of runtime prefix. |
| `scaleout.request.id` | Scale-out request id when descriptor/request metadata is tied to a scale-out flow. |
| `scaleout.sharedRunId` | Shared run id that requested scale-out. |
| `scaleout.controlPlaneId` | Control-plane id associated with a scale-out request. |

Initial local descriptors can use:

```text
provider.name = local
```

---

## Dependency Injection

Provider registration should be assembly-scanned.

Planned extension:

```csharp
services.AddAiRuntimeInstanceProvidersFromAssemblies(
    typeof(LocalAiRuntimeInstanceProvider).Assembly);
```

The scanner should:

- find non-abstract classes
- require `AiRuntimeInstanceProviderAttribute`
- require `IAiRuntimeInstanceProvider`
- register implemented capability interfaces
- register provider in a provider registry/router
- prevent duplicate provider names unless explicitly allowed

---

## Duplicate Provider Names

Duplicate provider names should fail fast.

Example invalid state:

```text
[AiRuntimeInstanceProvider("local")]
LocalProviderA

[AiRuntimeInstanceProvider("local")]
LocalProviderB
```

The provider registry should throw during startup.

This prevents ambiguous runtime dispatch.

---

## Error Handling

Provider operations should return structured results.

Errors should include:

- provider name
- runtime instance id
- operation
- success flag
- failure reason
- retryable flag when useful
- tenant id when available
- correlation id when available

Provider errors should be observable through:

- logs
- control-plane observer
- decision ledger
- metrics
- trace timeline

---

## Observability

Provider operations should emit control-plane events.

Events should include:

- operation started
- operation completed
- operation failed
- provider name
- runtime instance id
- run id
- shared run id
- execution id when available
- tenant id when available
- duration
- failure reason

Provider observability is essential for Kubernetes and dashboard demos.

---

## Security and Scope

Future provider calls must respect runtime security boundaries.

Current validated foundation includes tenant-aware runtime visibility and tenant-scoped scale-out.

Future hardening should continue with:

- tenant authorization for external tools
- runtime instance scope
- tool access scope
- admin vs runtime operator permissions
- provider credential isolation
- Kubernetes namespace restrictions
- command queue signing or validation
- MCP tool authorization
- provider-level circuit breakers and dispatch timeouts

Tenant isolation is now a runtime control-plane concern, not only an external authorization concern.

---

## Current Implementation Status

The implementation has moved beyond pure design.

Current completed or validated pieces:

```text
1. Runtime instance registration
2. Runtime instance roles
3. Runtime capacity descriptors
4. Runtime worker capacity visibility
5. In-memory runtime instance registry
6. Redis runtime instance registry
7. Redis runtime instance capacity store
8. Redis control-plane discovery store
9. Control-plane id resolver
10. Local runtime instance provider foundation
11. HTTP runtime instance provider foundation
12. Pooled HTTP runtime instance hosting
13. Shared queue pump
14. Shared queue pump readiness gate
15. Queue-first submit mode
16. Dispatch-time admission
17. Redis admission reservation store
18. MCP control-plane integration
19. Manual shared queue drain through MCP
20. Background pump dispatch through MCP
21. Replay/report/ledger/trace through MCP
22. Redis scale-out request persistence
23. Store-backed scale-out request publisher
24. Scale-out request watcher
25. Scale-out provider selector
26. Local runtime scale-out provider capability
27. Local runtime instance scaler
28. Fulfilled scale-out shared run requeue
29. MCP Redis local scale-out dispatch and execution completion
30. Tenant runtime settings provider foundation
31. Shared/Dedicated/Hybrid runtime isolation
32. Tenant-aware admission
33. Tenant-visible registry filtering
34. Tenant-visible capacity filtering
35. Tenant-aware scale-out request persistence
36. Local scaler scoped by RuntimeInstanceIdPrefix
37. Shared queue dispatcher context restore
38. Runtime local queue ExecutionContextSnapshot requirement
39. HTTP runtime dispatch timeout handling
40. HTTP runtime retry handling
41. HTTP runtime circuit-breaker handling
42. HTTP structured dispatch failure reasons
43. HTTP dispatch failure persistence
44. HTTP runtime scale-out provider capability
45. HTTP runtime scale-out provisioner foundation
46. HTTP tenant-aware registry/capacity publication
47. HTTP shared/dedicated/hybrid scale-out policy validation
48. HTTP dedicated no-shared-fallback validation
49. HTTP hybrid shared-fallback validation
50. Runtime Host Manager abstraction
51. Host creation modes for Fixture, Process, Attach, and Kubernetes-oriented provisioning
52. HTTP scale-out provisioner Host Manager mode
53. ProcessAiRuntimeHostCreationStrategy
54. Real RuntimeInstanceOnly process launch from HTTP scale-out
55. Runtime host readiness validation through registration and capacity
56. Tenant runtime settings precedence in HTTP scale-out provisioning
57. TenantGroupId propagation for scale-out requeue scope matching
58. MCP production runtime scenario framework
59. Focused Dedicated, Shared, and Hybrid process-host scenarios
60. Adversarial multi-tenant Dedicated isolation scenario
61. Mixed-tenant full production validation scenario
62. Durable ledger, trace, replay, replay report, replay ledger, and replay trace validation across process boundaries
```

The implementation must continue to preserve existing behavior.

No local queue behavior should change.

No provider should bypass the runtime queue or DAG engine.

Shared controller behavior should remain stable and delegate transport-specific dispatch to provider-capable components.

---

## Future Implementation Targets

After the local, HTTP pooled, HTTP process-host, HTTP scale-out, and tenant-aware provider foundations are stable:

```text
1. Add RuntimeInstanceHealthReconciler.
2. Mark stale, unhealthy, or circuit-open runtime endpoints as draining or unhealthy.
3. Stop routing new work to unhealthy runtime instances.
4. Request replacement capacity when needed.
5. Finalize shared runtime pooling semantics.
6. Add Hybrid shared fallback process-host validation after shared pooling is explicit.
7. Complete status provider capability.
8. Complete control provider capability.
9. Add Redis command queue provider.
10. Add command consumer in runtime-only host.
11. Add gRPC runtime provider and gRPC dispatch hardening.
12. Add Kubernetes metadata provider.
13. Add Kubernetes scaling provider using the existing IAiRuntimeScaleOutProvider capability model.
14. Add Kubernetes scale-out request handling that creates/expands runtime pods and waits for registration/capacity.
15. Refine Redis/Lua slot reservation paths where stronger atomic coordination is required.
16. Add registry and capacity TTL self-healing.
17. Continue hardening admission to use capacity descriptors and reservations as primary scheduling inputs.
```

---

## Current Limitations

The provider model is implemented for the validated local, HTTP dispatch, local scale-out, and tenant-aware scale-out paths, but it is not complete for all future transports.

Current limitations include:

- final shared runtime pooling semantics are not decided yet
- Shared mode currently validates Shared-mode propagation and execution, not a forced global shared runtime pool
- Hybrid fallback to a shared process-host pool should be tested after shared pooling semantics are finalized
- RuntimeInstanceHealthReconciler is not implemented yet
- circuit-open does not yet automatically mark a runtime unhealthy or request replacement
- Redis command queue provider is not implemented yet
- gRPC provider is not implemented yet
- Kubernetes provider is not implemented yet
- Kubernetes pod/deployment scale-out is not implemented yet
- capability negotiation is not complete yet
- Lua-based slot reservation refinement is not implemented yet
- registry/capacity TTL self-healing is not complete yet
- admission uses Redis-backed reservation support in validated scenarios but still needs further production hardening for multi-control-plane scheduling
- tenant runtime settings are currently foundation/provider-backed and should later become configurable or database-backed

---

## Validated Test Evidence

The provider model has been validated through MCP integration scenarios, heavy HTTP dispatch scenarios, and tenant-aware runtime isolation tests.

Validated behavior includes:

- HTTP provider dispatch through pooled `RuntimeInstanceOnly` runtime hosts.
- Assignment to `runtime-http-*` child runtime instances.
- Manual shared queue draining through MCP.
- Background shared queue pump dispatch through MCP.
- Queue-first submission.
- Runtime run status visibility after dispatch.
- Execution id visibility once execution starts.
- Normal HTTP-provider run completion.
- Larger HTTP-provider pipeline completion.
- Long-running HTTP-provider pause, resume, and cancellation routing.
- Runtime queue cancellation against the assigned child runtime instance.
- Heavy HTTP dispatch with:
  - 50 shared runs
  - 100 steps per run
  - 3 pooled HTTP runtime instances
  - Redis shared run store
  - Redis shared queue
  - Redis admission reservation store.
- HTTP dispatch timeout handling.
- HTTP retry success and retry exhaustion behavior.
- HTTP non-retryable failure classification.
- HTTP circuit-open failure classification.
- HTTP provider-unavailable failure persistence.
- HTTP scale-out request fulfillment through `providerHint = http`.
- HTTP scale-out registry/capacity metadata publication.
- HTTP shared tenant scale-out creates `runtime-instance-*` capacity.
- HTTP dedicated tenant scale-out creates `tenant-a-runtime-*` capacity.
- HTTP hybrid tenant scale-out creates `tenant-b-runtime-*` capacity.
- HTTP dedicated tenant does not fall back to shared HTTP capacity when fallback is disabled.
- HTTP hybrid tenant can fall back to shared HTTP capacity when fallback is allowed.
- Replay, report, ledger, and trace retrieval through MCP for completed shared runs.
- Runtime registry and capacity cleanup during shutdown.
- Discovery-based control-plane id resolution for runtime-only hosts.
- Redis scale-out request persistence.
- Store-backed scale-out request publishing.
- Scale-out watcher request processing.
- Provider-based scale-out selector resolution.
- Local runtime scale-out provider capability.
- Dynamic local runtime instance creation from zero executable runtime capacity.
- Fulfilled scale-out shared run requeue.
- Shared queue pump dispatch after scale-out fulfillment.
- Runtime execution completion after local scale-out.
- Dedicated tenant scale-out creates `tenant-a-runtime-*` capacity.
- Hybrid tenant scale-out creates `tenant-b-runtime-*` capacity.
- Shared/default tenant scale-out creates `runtime-instance-*` capacity.
- Hybrid tenant fallback can dispatch to shared runtime capacity.
- Dedicated tenant does not fall back to shared runtime capacity when fallback is disabled.
- Redis runtime registry filters by tenant visibility.
- Redis runtime capacity store filters by tenant visibility.
- Admission assigns only tenant-visible runtime capacity.
- Scale-out requests preserve tenant runtime settings in Redis.
- Local scaler counts matching tenant runtime prefix, not global host count.
- Shared queue dispatcher restores `ExecutionContextSnapshot` before admission and dispatch.
- Runtime queued runs require `ExecutionContextSnapshot`.
- Visibility evaluator rejects unowned Hybrid runtime instances.
- HTTP provider hardening and tenant-aware HTTP scale-out scenario tests green after the HTTP provider update.
- HTTP process-host scale-out through Runtime Host Manager.
- Real RuntimeInstanceOnly process launch from HTTP scale-out.
- Runtime registration and capacity readiness across process boundaries.
- Focused Dedicated, Shared, and Hybrid process-host production scenarios.
- Adversarial multi-tenant Dedicated isolation process-host scenario.
- Mixed-tenant full production validation scenario with 3 tenants, 12 runs, and 420 DAG steps.
- Durable ledger, trace, replay report, replay ledger, and replay trace validation across process boundaries.
- 1036+ tests green after the tenant-aware runtime isolation update lineage.

---

## Related Documents

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Distributed Execution](distributed-execution.md)
- [Observability and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Testing Strategy](testing-strategy.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)

---

## Documentation Rule

This document describes the runtime instance provider model.

Do not present Redis command queue dispatch, gRPC dispatch, Kubernetes pod scaling, global shared runtime pooling, RuntimeInstanceHealthReconciler behavior, or production dashboard features as completed capabilities until they are implemented and validated.

Provider dispatch and provider scale-out must continue to preserve the runtime boundaries:

```text
Admission decides.
Providers transport or scale.
Runtime Host Manager creates or attaches runtime hosts.
Runtime instances self-register.
Registry/capacity stores expose readiness.
Shared queue owns queued shared run dispatch.
Local runtime queues own RunId.
DAG engine owns ExecutionId.
ExecutionContextSnapshot carries durable tenant context.
```
