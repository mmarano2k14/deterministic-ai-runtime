# Runtime Instance Provider Model

Status: Architecture direction.

This document describes the planned **runtime instance provider model** for the Deterministic AI Runtime control plane.

The goal is to make runtime instance administration and dispatch provider-based, dynamically loadable, and extensible without changing the local runtime queue architecture.

The complete technical reference is currently preserved in:

- [runtime-internals.md](../runtime-internals.md)
- [runtime-control-plane.md](runtime-control-plane.md)
- [mcp-server-control-plane.md](mcp-server-control-plane.md)

---

## Purpose

The runtime now has:

- runtime instance registration
- runtime roles
- Redis-backed runtime registry
- runtime capacity descriptors
- local runtime instance pool
- shared queue
- shared runtime controller
- MCP control-plane adapter

The next step is to make the path between shared queue/admission and runtime instance transport generic.

Today, dispatch is mostly local.

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

---

## Core Principle

Admission decides **which runtime instance** should receive a run.

Providers decide **how to communicate** with that runtime instance.

```text
Admission
    decides WHO

Provider Router
    decides HOW

Provider
    performs the transport-specific operation
```

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

## Runtime Instance Descriptor as Source of Dispatch Metadata

Runtime instances already publish descriptors and capacity information.

The provider model extends this idea.

A runtime instance descriptor should expose metadata such as:

```text
provider.name = local
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

## Scaling Capability

Scaling providers request infrastructure changes.

```csharp
public interface IAiRuntimeInstanceScalingProvider :
    IAiRuntimeInstanceProvider
{
    Task<AiRuntimeScaleOutResult> RequestScaleOutAsync(
        AiRuntimeScaleOutRequest request,
        CancellationToken cancellationToken = default);

    Task<AiRuntimeScaleInResult> RequestScaleInAsync(
        AiRuntimeScaleInRequest request,
        CancellationToken cancellationToken = default);
}
```

Kubernetes is most likely a scaling provider before it is a dispatch provider.

Scaling should remain separate from dispatch.

---

## Provider Router

The provider router resolves providers by name and capability.

Expected responsibilities:

- read provider name from descriptor metadata
- find registered provider by attribute name
- verify requested capability is supported
- throw or return structured failure when provider is missing
- keep shared controller independent from transport details

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

The first provider should be local.

It should preserve the current behavior.

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

It should not change local queue internals.

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
- attaching Kubernetes metadata to descriptors

Kubernetes should not be required to dispatch a run directly.

Dispatch can still happen through:

- Redis command queues
- HTTP
- gRPC

This keeps Kubernetes responsibilities clean.

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

In multi-control-plane setups, snapshots are not enough.

Two control-plane processes may read the same available slot at the same time.

Future admission should use Redis/Lua slot reservations.

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

## Current Implementation Target

The first implementation target should be minimal:

```text
1. AiRuntimeInstanceProviderAttribute
2. IAiRuntimeInstanceProvider
3. IAiRuntimeInstanceDispatchProvider
4. IAiRuntimeInstanceProviderRouter
5. LocalAiRuntimeInstanceProvider
6. Adapter from existing shared run dispatcher to provider router
```

The first implementation must preserve existing behavior.

No local queue behavior should change.

No shared controller behavior should change except delegating dispatch to the provider router.

---

## Future Implementation Targets

After the local provider is stable:

```text
1. Add status provider capability.
2. Add control provider capability.
3. Add Redis command queue provider.
4. Add command consumer in runtime-only host.
5. Add Kubernetes metadata provider.
6. Add Kubernetes scaling provider.
7. Add Redis/Lua slot reservation store.
8. Update admission to use capacity descriptors as primary source of truth.
```

---

## Current Limitations

The provider model is not fully implemented yet.

Current limitations include:

- local dispatch still uses the existing shared runtime instance registry path
- no provider router yet
- no Redis command queue provider yet
- no HTTP/gRPC provider yet
- no Kubernetes provider yet
- no capability negotiation yet
- no Redis/Lua slot reservation yet
- admission still needs to become fully descriptor/capacity-first

---

## Related Documents

- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Queue Control](runtime-queue-control.md)
- [Distributed Execution](distributed-execution.md)
- [Observability and Tracing](observability-tracing.md)
- [Runtime Metrics](runtime-metrics.md)
- [Testing Strategy](testing-strategy.md)

---

## Documentation Rule

This document defines the planned provider-based architecture direction.

The original technical depth remains preserved in:

- [runtime-internals.md](../runtime-internals.md)

Do not remove content from `runtime-internals.md` until the extracted documentation has been reviewed and validated.
