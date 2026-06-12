# Runtime Instance Provider Model

Status: Architecture direction, implemented foundation validated for local dispatch, HTTP pooled runtime scenarios, provider-based scale-out, Redis-backed scale-out request persistence, local runtime scale-out, fulfilled-run requeue, and end-to-end MCP scale-out execution.

This document describes the **runtime instance provider model** for the Deterministic AI Runtime control plane.

The first provider-based hosting layer is now implemented and validated through local and HTTP runtime instance providers, runtime instance visibility, shared queue dispatch, Redis-backed runtime coordination, provider-based scale-out, local runtime instance scaling, and MCP control-plane integration.

The goal is to make runtime instance administration, dispatch, status/control operations, and scale-out provider-based, dynamically loadable, and extensible without changing the local runtime queue architecture.

The complete technical reference is currently preserved in:

- [runtime-control-plane.md](runtime-control-plane.md)
- [mcp-server-control-plane.md](mcp-server-control-plane.md)

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
- end-to-end MCP Redis local scale-out execution validation

The next step is to continue hardening provider capabilities and remote transports while preserving the validated local and HTTP provider paths.

Today, dispatch already flows through provider-style runtime instance resolution for local and HTTP pooled runtime scenarios.

Tomorrow, runtime instances may live behind:

- local in-memory registry
- Redis command queue
- HTTP endpoint
- gRPC endpoint
- Kubernetes pod/service
- external provider
- hosted runtime worker pool

The core architecture should not be rewritten for each transport.

Providers solve this.

The provider model must also preserve the new shared queue pump semantics:

```text
PumpRuntimeInstanceId
    identifies the runtime instance executing a pump cycle

AssignedRuntimeInstanceId
    identifies the runtime instance selected by admission for dispatch
```

These two identities are intentionally separate.

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

The important identities are:

```text
ControlPlaneId
    logical shared control-plane scope used by Redis stores

ControlPlaneHostId
    physical/logical host publishing or owning the control-plane descriptor

RuntimeInstanceId
    dispatchable runtime instance identity, often host-scoped

RuntimeId
    local runtime id inside a host or pool
```

Runtime hosts must not guess the control-plane id when discovery is required.

They should resolve it from the discovery store and reuse the resolved identity consistently for:

- runtime registry entries
- runtime capacity descriptors
- heartbeat updates
- provider routing metadata
- cleanup paths.

Cleanup must not depend on rediscovery after the runtime instance has already been registered or published.

During shutdown, registry unregister and capacity descriptor removal should reuse the known control-plane id for the runtime instance.


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

Scale-out is now modeled as a provider capability.

`IAiRuntimeScaleOutProvider` extends `IAiRuntimeInstanceProvider`.

This means scale-out uses the same provider discovery/routing model as dispatch, status, and control.

This separation protects the architecture.

---

## What Providers Must Not Change

Providers must not replace local runtime queues.

Providers must not bypass deterministic DAG execution.

Providers must not mutate execution state directly.

Providers must not claim DAG steps.

Providers must not become the runtime engine.

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

## Why Providers Are Needed

Without providers, the shared controller can easily become coupled to transport details.

Bad direction:

```text
if local -> use in-memory registry
if redis -> push command
if http -> call endpoint
if kubernetes -> call API
```

That makes the shared controller grow into a transport-specific scheduler.

Better direction:

```text
Shared Controller
    ↓
Admission Decision
    ↓
Runtime Instance Provider Router
    ↓
Provider selected by descriptor metadata
```

The shared controller remains stable.

New transports are added as providers.

---

## Runtime Instance Capacity and Worker Visibility

Runtime instance descriptors are not only transport descriptors.

They are also visibility snapshots used by admission, dashboards, MCP tools, and future autoscaling.

The runtime now exposes worker-aware capacity fields through the runtime instance snapshot path:

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
```

`CanAcceptRun` is now worker-aware.

A runtime instance should only be considered available when it has both run capacity and worker capacity.

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

This matters for provider routing because a provider can only deliver work correctly if the target runtime instance is visible and able to accept work.

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

Runtime instances already publish descriptors and capacity information.

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

Future examples:

```text
provider.name = redis-command-queue
provider.commandQueueKey = ai:runtime:mcp-runtime-1:commands
```

```text
provider.name = http
provider.endpoint = http://mcp-runtime-1.runtime.svc.cluster.local
```

```text
provider.name = grpc
provider.endpoint = grpc://mcp-runtime-1.runtime.svc.cluster.local:5001
```

```text
provider.name = kubernetes
provider.namespace = ai-runtime
provider.podName = runtime-7c9d7f
provider.serviceName = runtime-service
```

The provider router reads the descriptor metadata and resolves the correct provider.

---

## Provider Discovery

Providers should be discovered dynamically using class attributes.

Example:

```csharp
[AiRuntimeInstanceProvider("local")]
public sealed class LocalAiRuntimeInstanceProvider :
    IAiRuntimeInstanceDispatchProvider,
    IAiRuntimeInstanceStatusProvider,
    IAiRuntimeInstanceControlProvider
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

Recommended initial provider names:

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

A local provider may support dispatch, status, and control.

A Kubernetes provider may support discovery and scaling.

A Redis command queue provider may support dispatch and control through commands.

A provider should only implement the capabilities it supports.

A provider can support scale-out without being the dispatch transport for every runtime instance.

For example, the local provider now supports:

```text
dispatch
status
control
scale-out
```

Kubernetes will likely support scale-out before it becomes a direct dispatch provider.

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

A provider may implement capacity directly or delegate to a shared capacity store.

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

There is no separate provider-name property on the scale-out provider.

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

## Provider Router

The provider router resolves providers by name and capability.

Expected responsibilities:

- read provider name from descriptor metadata
- find registered provider by attribute name
- verify requested capability is supported
- throw or return structured failure when provider is missing
- keep shared controller independent from transport details
- allow selectors to request a specific provider capability such as dispatch, status, control, or scale-out

Example usage:

```csharp
var provider = providerRouter.GetRequiredProvider<IAiRuntimeInstanceDispatchProvider>(
    descriptor);

var result = await provider.DispatchRunAsync(
    descriptor,
    request,
    cancellationToken);
```

---

## Provider Resolution Flow

```text
Shared Runtime Controller
    ↓
Admission returns RuntimeInstanceId
    ↓
Load capacity descriptor
    ↓
Read descriptor metadata
    ↓
provider.name = local
    ↓
Provider Router
    ↓
LocalAiRuntimeInstanceProvider
    ↓
DispatchRunAsync
    ↓
Local runtime queue
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

The dispatchable runtime identities are the child runtime instances created by the local runtime instance pool.

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
create local runtime instance
    ↓
register runtime instance / publish capacity
    ↓
mark scale-out request fulfilled
    ↓
requeue shared run for normal pump dispatch
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
Remote runtime instance enqueues local run
```

---

## Local Provider

The first provider is local.

It preserves the current behavior.

```text
Local provider
    uses IAiSharedRuntimeInstanceRegistry
    resolves LocalAiSharedRuntimeInstance
    calls DispatchAsync
    enqueues through IAiRuntimeQueueControlPlane
```

The local provider should support:

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

---

## Scale-Out Request Lifecycle

Scale-out is now implemented as a persisted request lifecycle.

The shared controller does not create runtime instances directly.

The watcher does not dispatch directly.

The normal shared queue pump remains responsible for dispatch after capacity exists.

Validated lifecycle:

```text
SubmitRun
    ↓
DirectDispatch mode
    ↓
Admission sees no runtime capacity
    ↓
Decision = RequestScaleOut
    ↓
SharedRun.Status = ScaleOutRequested
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
runtime instance created/registered/started
    ↓
ScaleOutRequest.Status = Fulfilled
    ↓
AiScaleOutFulfilledRunRequeueService
    ↓
SharedQueueItem.Status = Pending
    ↓
AiSharedQueuePump
    ↓
dispatch-time admission sees new capacity
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
Kubernetes provider creates or expands runtime capacity
    ↓
runtime pod registers/publishes capacity
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
  "correlationId": "correlation-id",
  "requestedBy": "mcp",
  "source": "shared-controller"
}
```

The remote runtime instance would consume commands and enqueue into its local queue.

This keeps cross-pod communication simple and resilient.

---

## HTTP Provider

The HTTP provider can dispatch or control runtime instances through HTTP endpoints.

The current provider-based runtime hosting work includes an HTTP runtime provider foundation validated for `RuntimeInstanceOnly` and `ControlPlaneWithHttpRuntimeInstances` scenarios.

Example metadata:

```text
provider.name = http
provider.endpoint = http://runtime-1.ai-runtime.svc.cluster.local
```

HTTP provider responsibilities may include:

- dispatch run
- get run status
- pause/resume queue
- cancel run
- get queue state

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



This provider is useful when runtime pods expose an HTTP control endpoint.

---

## gRPC Provider

The gRPC provider can perform the same operations as HTTP with a typed contract.

Example metadata:

```text
provider.name = grpc
provider.endpoint = grpc://runtime-1.ai-runtime.svc.cluster.local:5001
```

gRPC may be useful for lower-latency internal cluster communication.

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
Admission re-evaluates the run
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

## Capacity-Aware Admission

Capacity-aware admission should use Redis capacity descriptors as its primary source of truth.

Eligible runtime instances should satisfy:

- role is runtime
- status is ready or acceptable
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

---

## Slot Reservations

Capacity descriptors are snapshots.

The current implementation exposes the required capacity visibility and includes a Redis-backed admission reservation store used by heavy dispatch scenarios.

In multi-control-plane setups, snapshots alone are not enough.

Two control-plane processes may read the same available slot at the same time.

Admission should use Redis-backed reservations to protect selected capacity during dispatch.

Lua-based reservation refinement may still be added later for stronger atomic coordination across more advanced scheduling paths.

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
- duration
- failure reason

Provider observability is essential for Kubernetes and dashboard demos.

---

## Security and Scope

Future provider calls must respect runtime security boundaries.

Potential future concerns:

- tenant authorization
- runtime instance scope
- tool access scope
- admin vs runtime operator permissions
- provider credential isolation
- Kubernetes namespace restrictions
- command queue signing or validation
- MCP tool authorization

This is not fully implemented yet but should be considered in provider design.

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
```

The implementation must continue to preserve existing behavior.

No local queue behavior should change.

No provider should bypass the runtime queue or DAG engine.

Shared controller behavior should remain stable and delegate transport-specific dispatch to provider-capable components.

---

## Future Implementation Targets

After the local and HTTP pooled providers are stable:

```text
1. Complete status provider capability.
2. Complete control provider capability.
3. Add Redis command queue provider.
4. Add command consumer in runtime-only host.
5. Add Kubernetes metadata provider.
6. Add Kubernetes scaling provider using the existing `IAiRuntimeScaleOutProvider` capability model.
7. Add Kubernetes scale-out request handling that creates/expands runtime pods and waits for registration/capacity.
8. Refine Redis/Lua slot reservation paths where stronger atomic coordination is required.
9. Continue hardening admission to use capacity descriptors and reservations as primary scheduling inputs.
```

---

## Current Limitations

The provider model is implemented for the validated local, HTTP dispatch, and local scale-out paths, but it is not complete for all future transports.

Current limitations include:

- provider routing still needs production hardening
- Redis command queue provider is not implemented yet
- gRPC provider is not implemented yet
- Kubernetes provider is not implemented yet
- Kubernetes pod/deployment scale-out is not implemented yet
- capability negotiation is not complete yet
- Lua-based slot reservation refinement is not implemented yet
- admission uses Redis-backed reservation support in validated scenarios but still needs further production hardening for multi-control-plane scheduling
- admission still needs to become fully descriptor/capacity-first for production multi-control-plane scheduling

---

## Validated Test Evidence

The provider model has been validated through MCP integration scenarios and heavy HTTP dispatch scenarios.

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


## Related Documents

- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Distributed Execution](distributed-execution.md)
- [Observability and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Testing Strategy](testing-strategy.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
