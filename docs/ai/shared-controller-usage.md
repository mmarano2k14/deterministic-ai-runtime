# Shared Runtime Controller / Shared Queue Usage

Status: Implemented / validated with Redis shared run store, Redis shared queue, Redis scale-out request persistence, tenant-aware admission, RBAC execution context propagation, local runtime scale-out, fulfilled-run requeue, Shared/Dedicated/Hybrid runtime isolation, HTTP pooled runtime scenarios, gRPC runtime provider scenarios, HTTP/gRPC process-host scale-out, provider-agnostic crash recovery, and end-to-end MCP scale-out execution.

This document shows how to configure and use the AI control-plane shared runtime features.

It covers:

- RBAC integration and durable execution context snapshots
- Tenant-aware shared controller submission
- In-memory shared controller mode
- Redis shared run store
- Redis shared queue
- Redis scale-out request persistence
- Tenant-aware local runtime scale-out
- Fulfilled scale-out run requeue
- Direct assigned-run dispatch
- Global shared queue dispatch
- Queue-first submit mode
- Shared queue pump
- Manual shared queue drain
- Shared queue background service
- Dispatch-time admission
- Shared/Dedicated/Hybrid tenant isolation modes
- Pump identity vs assigned runtime identity
- Runtime worker capacity visibility
- Full distributed host setup
- HTTP and gRPC provider-backed process-host dispatch
- Current architecture summary
- Current limitations
- Future Kubernetes direction

This document complements:

- [Architecture Overview](architecture-overview.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Multi-Tenant Control Plane Isolation](multi-tenant-control-plane-isolation.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [Shared Queue Pump and Worker Capacity](shared-queue-pump-and-worker-capacity.md)
- [MCP Server as Runtime Control Plane](mcp-server-control-plane.md)
- [Testing Strategy](testing-strategy.md)

---

## 1. Core rule: shared runs must carry durable tenant context

Shared runtime orchestration is asynchronous and can cross hosted services, Redis stores, runtime providers, HTTP hosts, and future Kubernetes pods.

Because of that, shared queue dispatch must not rely only on ambient `AsyncLocal` context.

The durable tenant boundary is:

```text
ExecutionContextSnapshot.TenantId
```

Not:

```text
ContextKey
Metadata["tenant"]
```

The rule is:

```text
ContextKey
    = RBAC / correlation / debug context

ExecutionContextSnapshot.TenantId
    = durable tenant boundary

Metadata
    = observability duplicate only
```

Every submitted shared run should preserve an `ExecutionContextSnapshot`.

```text
MCP / API / CLI
    ↓
RBAC ExecutionContext
    ↓
ExecutionContextSnapshot
    ↓
AiSharedRuntimeControllerRequest
    ↓
AiSharedRunRecord
    ↓
Shared queue / scale-out / dispatch
    ↓
Runtime local queue
    ↓
Background controller restores snapshot
    ↓
DAG execution
```

If a direct runtime queued run has no execution context snapshot, the background controller should fail fast rather than silently executing work without tenant context.

---

## 2. RBAC integration

The MCP control plane integrates with RBAC before shared runs are created.

Typical path:

```text
MCP request
    ↓
Authentication
    ↓
RBAC execution context resolution
    ↓
Capability check / RequireCapability(...)
    ↓
McpRuntimeExecutionContextAccessor
    ↓
MapToSnapshot()
    ↓
AiSharedRuntimeController.SubmitRunAsync(...)
```

RBAC context includes:

```text
ContextKey
Project
UserId
TenantId
TenantGroupId
CurrentNamespace
Namespaces / TRNs
```

The snapshot should be persisted with the shared run.

This ensures background workers can restore the correct tenant identity even when the original request scope is gone.

---

## 3. Tenant runtime settings

The current tenant runtime settings foundation supports three modes.

```text
Shared
Dedicated
Hybrid
```

Current hardcoded foundation:

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

default / unknown / test-tenant
    IsolationMode = Shared
    PreferDedicatedCapacity = false
    AllowSharedFallback = true
    MaxRuntimeInstances = 1
    RuntimeInstanceIdPrefix = runtime-instance
    WorkerCountPerInstance = 10
    MaxConcurrentRunsPerInstance = 3
```

These settings are currently provider-backed/hardcoded for the foundation.

They should later become configuration/database-backed tenant settings without changing the control-plane flow.

---

## 4. Tenant visibility rules

Runtime registry and capacity listing are tenant-aware.

Admission should only see runtime instances that are visible for the current tenant context.

Visibility rules:

```text
Shared runtime:
    visible to Shared tenants
    visible to Hybrid/Dedicated tenants only when their tenant settings allow shared fallback

Dedicated runtime:
    visible only when TenantId or TenantGroupId matches

Hybrid runtime:
    visible only when TenantId or TenantGroupId matches
    AllowSharedFallback does not make an unowned Hybrid runtime visible
```

Important:

```text
Hybrid fallback means:
    a Hybrid tenant may use Shared runtime capacity when allowed.

It does not mean:
    an unowned Hybrid runtime is visible to every Hybrid tenant.
```

Examples:

```text
tenant-a Dedicated + tenant-a-runtime-1
    visible

tenant-a Dedicated + runtime-instance-1
    not visible because tenant-a fallback is disabled

tenant-b Hybrid + tenant-b-runtime-1
    visible

tenant-b Hybrid + runtime-instance-1
    visible because tenant-b fallback is enabled

test-tenant Shared + runtime-instance-1
    visible

test-tenant Shared + tenant-a-runtime-1
    not visible

test-tenant Shared + tenant-b-runtime-1
    not visible
```

---

## 5. Basic in-memory setup

Use this mode for local development, unit tests, and single-process demos.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Multiplexed.AI.Runtime.ControlPlane.DI;

var services = new ServiceCollection();

services.AddLogging();

services.AddAiControlPlane(
    configureSharedController: options =>
    {
        options.EnableSubmitRun = true;
        options.EnableGetRun = true;
        options.EnableListRuns = true;
        options.EnableCancelRun = true;
        options.ReturnFailureResultInsteadOfThrowing = true;
        options.MeasureDuration = true;

        // DirectDispatch keeps admission-driven behavior.
        // QueueFirst always creates the shared run and queues it globally first.
        options.SubmitMode = AiSharedRuntimeSubmitMode.DirectDispatch;
    },
    configureSharedQueue: options =>
    {
        options.EnableEnqueue = true;
        options.EnableClaim = true;
        options.EnableComplete = true;
        options.EnableRequeue = true;
        options.EnableCancel = true;
        options.DefaultClaimTtl = TimeSpan.FromSeconds(30);
    },
    configureSharedQueuePump: options =>
    {
        options.Enabled = true;
        options.MaxDispatchesPerCycle = 10;
        options.DefaultClaimTtl = TimeSpan.FromSeconds(30);
        options.StopCycleWhenNoItemAvailable = true;
        options.StopCycleOnDispatchFailure = false;
        options.Source = "local-shared-queue-pump";
    });

var provider = services.BuildServiceProvider();
```

Registered by default:

```text
IAiSharedRunStore                       -> InMemoryAiSharedRunStore
IAiSharedQueue                          -> InMemoryAiSharedQueue
IAiSharedRunDispatcher                  -> LocalAiSharedRunDispatcher
IAiSharedQueueDispatcher                -> AiSharedQueueDispatcher
IAiSharedQueuePump                      -> AiSharedQueuePump
IAiSharedRuntimeController              -> AiSharedRuntimeController
IAiRuntimeScaleOutRequestStore          -> InMemoryAiRuntimeScaleOutRequestStore
IAiRuntimeScaleOutRequestPublisher      -> StoreBackedAiRuntimeScaleOutRequestPublisher
IAiRuntimeScaleOutProviderSelector      -> AiRuntimeScaleOutProviderSelector
IAiScaleOutFulfilledRunRequeueService   -> AiScaleOutFulfilledRunRequeueService
```

---

## 6. Redis setup for distributed shared controller mode

Use Redis when multiple runtime instances, workers, or control-plane hosts need to coordinate shared runs.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.AI.Redis.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using StackExchange.Redis;

var services = new ServiceCollection();

services.AddLogging();

services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect("localhost:6379"));

services.AddAiControlPlane(
    configureSharedController: options =>
    {
        options.EnableSubmitRun = true;
        options.EnableGetRun = true;
        options.EnableListRuns = true;
        options.EnableCancelRun = true;
        options.ReturnFailureResultInsteadOfThrowing = true;
        options.MeasureDuration = true;
        options.SubmitMode = AiSharedRuntimeSubmitMode.QueueFirst;
    },
    configureSharedQueuePump: options =>
    {
        options.Enabled = true;
        options.MaxDispatchesPerCycle = 20;
        options.DefaultClaimTtl = TimeSpan.FromSeconds(30);
        options.StopCycleWhenNoItemAvailable = true;
        options.StopCycleOnDispatchFailure = false;
        options.WorkerId = "runtime-1-pump";
        options.Source = "redis-shared-queue-pump";
    });

// Replace in-memory shared run store with Redis.
services.RemoveAll<IAiSharedRunStore>();
services.AddSingleton<IAiSharedRunStore, RedisAiSharedRunStore>();

services.Configure<RedisAiSharedRunStoreOptions>(options =>
{
    options.KeyPrefix = "ai:shared-runs";
    options.ListScanLimit = 500;
});

// Replace in-memory shared queue with Redis.
services.RemoveAll<IAiSharedQueue>();
services.AddSingleton<IAiSharedQueue, RedisAiSharedQueue>();

services.Configure<RedisAiSharedQueueOptions>(options =>
{
    options.KeyPrefix = "ai:shared-queue";
    options.ListScanLimit = 500;
});

// Replace in-memory scale-out request store with Redis.
services.RemoveAll<IAiRuntimeScaleOutRequestStore>();
services.AddSingleton<IAiRuntimeScaleOutRequestStore, RedisAiRuntimeScaleOutRequestStore>();

services.Configure<RedisAiRuntimeScaleOutRequestStoreOptions>(options =>
{
    options.KeyPrefix = "ai:runtime-scaleout";
    options.ListScanLimit = 500;
});

// The store-backed publisher persists scale-out requests into the configured store.
services.RemoveAll<IAiRuntimeScaleOutRequestPublisher>();
services.AddSingleton<IAiRuntimeScaleOutRequestPublisher, StoreBackedAiRuntimeScaleOutRequestPublisher>();

// The watcher observes pending scale-out requests and delegates to scale-out-capable providers.
services.AddHostedService<AiRuntimeScaleOutRequestWatcherHostedService>();

var provider = services.BuildServiceProvider();
```

Redis-backed services provide:

```text
RedisAiSharedRunStore
  - hash storage per shared run
  - sorted set index
  - Lua atomic create
  - Lua atomic cancel
  - Lua atomic mark-dispatched
  - SHA cache + NOSCRIPT reload
  - durable ExecutionContextSnapshot persistence

RedisAiSharedQueue
  - hash storage per queue item
  - pending sorted set
  - all-items sorted set
  - Lua atomic enqueue
  - Lua atomic claim-next
  - Lua atomic mark-dispatched
  - Lua atomic requeue
  - Lua atomic cancel
  - concurrent claim safety

RedisAiRuntimeScaleOutRequestStore
  - hash storage per scale-out request
  - pending request index
  - lifecycle transitions: Pending, Observed, Fulfilled, Rejected
  - tenant runtime settings persistence
  - watcher-friendly query support
  - Redis-backed coordination for local and future Kubernetes scale-out adapters
```

---

## 7. Submit a tenant-aware run to the shared runtime controller

A tenant-aware shared run should include both:

```text
AiSharedRuntimeControllerRequest.TenantId
RunRequest.ExecutionContextSnapshot.TenantId
```

`TenantId` on the controller request is useful for request-level routing and visibility.

`ExecutionContextSnapshot.TenantId` is the durable boundary that survives Redis, queueing, background dispatch, and runtime provider hops.

```csharp
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;
using Multiplexed.Abstractions.Core.ExecutionContext;

var controller = provider.GetRequiredService<IAiSharedRuntimeController>();

var snapshot = new ExecutionContextSnapshot
{
    ContextKey = "ctx-tenant-a-user-001",
    Project = "ai-runtime-demo",
    UserId = "user-001",
    TenantId = "tenant-a",
    TenantGroupId = "tenant-a",
    CurrentNamespace = "ai-runtime-demo",
    Namespaces = new List<NamespaceEntry>
    {
        new()
        {
            Name = "ai-runtime-demo",
            Trns = new HashSet<string>
            {
                "trn:ai-runtime-demo:shared-run:execution:submit",
                "trn:ai-runtime-demo:shared-run:registry:read",
                "trn:ai-runtime-demo:shared-run:registry:list",
                "trn:ai-runtime-demo:shared-queue:queue:list",
                "trn:ai-runtime-demo:shared-queue:status:read",
                "trn:ai-runtime-demo:shared-queue:pump:drain"
            }
        }
    }
};

var result = await controller.SubmitRunAsync(
    new AiSharedRuntimeControllerRequest
    {
        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
        RequestedSharedRunId = "shared-run-001",
        TenantId = snapshot.TenantId,
        PipelineKey = "document-processing",
        CorrelationId = "correlation-001",
        RequestedBy = "api",
        Source = "example",
        Reason = "Submit document processing workflow.",
        RunRequest = new AiRuntimePipelineRunRequest
        {
            PipelineName = "document-processing",
            ExecutionContextSnapshot = snapshot
        },
        Metadata = new Dictionary<string, string>
        {
            ["tenant.id"] = snapshot.TenantId,
            ["priority"] = "normal",
            ["source"] = "usage-example"
        }
    });

if (result.Success)
{
    Console.WriteLine($"SharedRunId: {result.SharedRunId}");
    Console.WriteLine($"Status: {result.Run?.Status}");
    Console.WriteLine($"AssignedRuntimeInstanceId: {result.AssignedRuntimeInstanceId}");
    Console.WriteLine($"LocalRunId: {result.LocalRunId}");
    Console.WriteLine($"ExecutionId: {result.ExecutionId}");
}
else
{
    Console.WriteLine($"Submit failed: {result.FailureReason}");
}
```

Possible admission results:

```text
AssignToInstance
  -> SharedRunStore.CreateAsync(...)
  -> IAiSharedRunDispatcher.DispatchAsync(...)
  -> SharedRunStore.MarkDispatchedAsync(...)
  -> SharedRun.Status = Dispatched

QueueGlobally
  -> SharedRunStore.CreateAsync(...)
  -> IAiSharedQueue.EnqueueAsync(...)
  -> SharedRun.Status = QueuedGlobally

RequestScaleOut
  -> SharedRunStore.CreateAsync(...)
  -> IAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
  -> Redis scale-out request is persisted with tenant runtime settings
  -> SharedRun.Status = ScaleOutRequested
  -> watcher/provider/scaler create tenant-scoped capacity
  -> fulfilled run is requeued
  -> shared queue pump dispatches normally

Reject
  -> SharedRunStore.CreateAsync(...)
  -> SharedRun.Status = Rejected
```

---

## 8. Queue-first submit mode

Queue-first mode is useful when the control plane should always persist the shared run and place it in the global queue before any runtime instance consumes it.

```csharp
services.Configure<AiSharedRuntimeControllerOptions>(options =>
{
    options.SubmitMode = AiSharedRuntimeSubmitMode.QueueFirst;
});
```

Queue-first flow:

```text
SubmitRunAsync
  -> SharedRunStore.CreateAsync(...)
  -> IAiSharedQueue.EnqueueAsync(...)
  -> SharedRun.Status = QueuedGlobally
  -> run waits for background pump or manual drain
```

Queue-first is different from forcing admission globally.

It is a controller submit mode, not an admission override.

Important:

```text
QueueFirst bypasses submit-time DirectDispatch admission outcomes.
It always creates the shared run and queues it globally first.
Therefore QueueFirst does not produce ScaleOutRequested at submit time.
Use DirectDispatch when the submit path must evaluate admission immediately and request scale-out.
```

Use queue-first when:

- shared runs must always enter the global queue first
- the runtime instance should be selected at dispatch/drain time
- a background pump or MCP manual drain controls dispatch timing
- demos need to show queued work before dispatch
- Kubernetes-style runtime instances should pull work from a shared queue

---

## 9. DirectDispatch scale-out submit mode

DirectDispatch is the mode used when the submit path should immediately ask admission what to do.

```csharp
services.Configure<AiSharedRuntimeControllerOptions>(options =>
{
    options.SubmitMode = AiSharedRuntimeSubmitMode.DirectDispatch;
});
```

DirectDispatch scale-out flow:

```text
SubmitRunAsync
  -> IAiRunAdmissionController
  -> tenant-aware registry/capacity filtering
  -> no eligible runtime capacity
  -> Decision = RequestScaleOut
  -> IAiSharedRunStore.CreateAsync(...)
  -> SharedRun.Status = ScaleOutRequested
  -> IAiRuntimeScaleOutRequestPublisher.PublishAsync(...)
  -> Redis scale-out request is persisted with:
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
  -> AiRuntimeScaleOutRequestWatcherHostedService observes the pending request
  -> AiRuntimeScaleOutProviderSelector resolves a provider-capable scaler
  -> LocalAiRuntimeInstanceProvider delegates to AiLocalRuntimeInstanceScaler
  -> local runtime instance is created, registered, started, and publishes capacity
  -> scale-out request is marked Fulfilled
  -> IAiScaleOutFulfilledRunRequeueService requeues the shared run
  -> IAiSharedQueuePump claims the requeued run
  -> dispatch-time admission sees the new tenant-visible runtime capacity
  -> run is dispatched to the created runtime instance
  -> local run receives an ExecutionId
  -> runtime run reaches a terminal status
```

Use DirectDispatch when:

- the first submission should request scale-out when no tenant-visible capacity exists
- local runtime scale-out needs to be validated end-to-end
- Redis scale-out request persistence should be exercised
- tenant runtime settings must be propagated through the scale-out request
- the fulfilled run should be requeued and consumed by the normal pump
- tests need to prove that a new runtime instance executed the original run

Validated tenant-aware examples:

```text
default / test-tenant
    AssignedRuntimeInstanceId = host-*:runtime-instance-1

tenant-a Dedicated
    AssignedRuntimeInstanceId = host-*:tenant-a-runtime-1
    shared fallback disabled

tenant-b Hybrid
    AssignedRuntimeInstanceId = host-*:tenant-b-runtime-1
    or host-*:runtime-instance-1 when shared fallback is used
```

---

## 10. Provider-backed HTTP and gRPC process-host dispatch

Shared controller and shared queue semantics are transport-neutral.

The shared controller does not know whether the final runtime is contacted through local in-memory dispatch, HTTP, gRPC, or a future Kubernetes-backed transport.

The provider boundary is:

```text
Shared queue dispatcher
  -> restores ExecutionContextSnapshot
  -> dispatch-time tenant-aware admission
  -> capacity descriptor selected
  -> provider.name resolved
  -> provider dispatch
  -> runtime instance local queue
```

Validated provider-backed process-host modes now include:

```text
ControlPlaneWithHttpRuntimeInstances
ControlPlaneWithGrpcRuntimeInstances
```

For HTTP:

```text
provider.name = http
transport.name = http
AiHttpRuntimeScaleOut:Enabled = true
```

For gRPC:

```text
provider.name = grpc
transport.name = grpc
AiGrpcRuntimeScaleOut:Enabled = true
```

Both providers use the same shared queue and shared run lifecycle:

```text
Submit
  -> admission
  -> SharedRun.Status = ScaleOutRequested when no capacity exists
  -> Redis scale-out request
  -> scale-out watcher
  -> provider-specific scale-out provider
  -> Runtime Host Manager
  -> real RuntimeInstanceOnly process
  -> runtime self-registers and publishes capacity
  -> shared run requeued
  -> pump restores ExecutionContextSnapshot
  -> dispatch-time admission selects tenant-visible runtime
  -> provider dispatch
  -> local runtime queue
  -> DAG execution
```

The current gRPC process-host path validates the same shared controller contract through typed gRPC transport.

Important gRPC process-host configuration details:

```text
AiMcpHost:Mode = ControlPlaneWithGrpcRuntimeInstances
AiRuntimeInstanceRegistration:ProviderName = grpc
AiGrpcRuntimeScaleOut:Enabled = true
AiGrpcRuntimeScaleOut:HostCreationMode = Process
RuntimeInstanceOnly transport.name = grpc
Kestrel:EndpointDefaults:Protocols = Http2
```

The gRPC provider uses the Runtime Host Manager and process-host creation path just like HTTP, but dispatches through the gRPC runtime command contract.

Current gRPC readiness note:

```text
AiGrpcRuntimeScaleOut:RequireReadiness = false
```

This is a temporary process-host setting because the old readiness probe was HTTP-specific and probed `/runtime-instance/commands`.

The proper long-term readiness model should be provider-aware, either through:

```text
gRPC-native health/readiness
```

or:

```text
registry/capacity readiness using Status=Ready, CanAcceptRun=true, provider.name=grpc, transport.name=grpc
```

This does not change the shared queue contract. The shared queue still dispatches only after tenant-visible registry and capacity make the runtime eligible.


---

## 11. List shared runs

```csharp
var list = await controller.ListRunsAsync(
    new AiSharedRuntimeControllerRequest
    {
        Operation = AiSharedRuntimeControllerOperation.ListRuns,
        IncludeCancelled = true,
        IncludeCompleted = true,
        IncludeFailed = true
    });

foreach (var run in list.Runs)
{
    Console.WriteLine($"{run.SharedRunId} - {run.Status} - {run.LocalRunId}");
}
```

Tenant-aware listing should use the current restored execution context when filtering is required by the store/control-plane adapter.

---

## 12. Get one shared run

```csharp
var get = await controller.GetRunAsync(
    new AiSharedRuntimeControllerRequest
    {
        Operation = AiSharedRuntimeControllerOperation.GetRun,
        SharedRunId = "shared-run-001"
    });

if (get.Run is not null)
{
    Console.WriteLine($"Status: {get.Run.Status}");
    Console.WriteLine($"TenantId: {get.Run.ExecutionContextSnapshot.TenantId}");
    Console.WriteLine($"Pipeline: {get.Run.RunRequest.PipelineName}");
    Console.WriteLine($"LocalRunId: {get.Run.LocalRunId}");
    Console.WriteLine($"ExecutionId: {get.Run.ExecutionId}");
}
```

---

## 13. Cancel a shared run

```csharp
var cancel = await controller.CancelRunAsync(
    new AiSharedRuntimeControllerRequest
    {
        Operation = AiSharedRuntimeControllerOperation.CancelRun,
        SharedRunId = "shared-run-001",
        Reason = "Operator requested cancellation.",
        RequestedBy = "operator",
        Source = "admin-api"
    });

Console.WriteLine($"Cancelled: {cancel.Success}");
Console.WriteLine($"Status: {cancel.Run?.Status}");
```

Queued shared run cancellation happens at the shared run / shared queue layer.

Running execution cancellation should be bridged to `ExecutionId`-level execution control once an execution id exists.

---

## 14. Manually pump the shared queue

A runtime instance or control-plane adapter can manually ask to claim and dispatch pending shared queue items.

The pump request uses explicit pump identity fields:

```text
PumpRuntimeInstanceId
PumpWorkerId
```

These identify the runtime instance or control-plane worker executing the pump cycle.

They do not necessarily identify the runtime instance that will receive the run.

```csharp
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue.Pump;

var pump = provider.GetRequiredService<IAiSharedQueuePump>();

var pumpResult = await pump.PumpOnceAsync(
    new AiSharedQueuePumpRequest
    {
        PumpRuntimeInstanceId = "runtime-1",
        PumpWorkerId = "runtime-1-shared-queue-pump",
        MaxDispatches = 10,
        ClaimTtl = TimeSpan.FromSeconds(30),
        CorrelationId = Guid.NewGuid().ToString("N"),
        RequestedBy = "system",
        Source = "manual-pump",
        Reason = "Runtime instance has available capacity.",
        Metadata = new Dictionary<string, string>
        {
            ["pump.runtime.instance.id"] = "runtime-1",
            ["mode"] = "manual"
        }
    });

Console.WriteLine($"Pump success: {pumpResult.Success}");
Console.WriteLine($"Attempted: {pumpResult.AttemptedDispatchCount}");
Console.WriteLine($"Succeeded: {pumpResult.SuccessfulDispatchCount}");
Console.WriteLine($"Failed: {pumpResult.FailedDispatchCount}");
Console.WriteLine($"No item: {pumpResult.StoppedBecauseNoItemAvailable}");
```

Pump behavior:

```text
PumpOnceAsync
  -> DispatchNextAsync
  -> ClaimNextAsync from IAiSharedQueue
  -> Get shared run from IAiSharedRunStore
  -> Restore sharedRun.ExecutionContextSnapshot
  -> Re-admit the run for dispatch-time target selection
  -> Dispatch through IAiSharedRunDispatcher
  -> Mark queue item as Dispatched
  -> Mark shared run as Dispatched
  -> Restore previous context / clear temporary context
  -> Repeat until max dispatches or no item
```

Important:

```text
PumpRuntimeInstanceId = instance executing the pump
AssignedRuntimeInstanceId = instance selected by admission for dispatch
```

The pump identity and assigned runtime identity are intentionally separate.

---

## 15. Dispatch-time execution context restore

The shared queue dispatcher must restore the shared run execution context before admission and dispatch.

Why:

```text
Submit-time MCP request context is gone.
Background pump runs later.
Admission filters registry/capacity by current tenant context.
Therefore dispatch must restore the persisted ExecutionContextSnapshot.
```

Dispatch-time flow:

```text
IAiSharedQueueDispatcher
  -> IAiSharedQueue.ClaimNextAsync(...)
  -> IAiSharedRunStore.GetAsync(...)
  -> sharedRun.ExecutionContextSnapshot
  -> restore RBAC ExecutionContext
  -> IAiRunAdmissionController.AdmitAsync(...)
  -> tenant-visible registry/capacity filtering
  -> reserve selected capacity when required
  -> IAiSharedRunDispatcher.DispatchAsync(...)
  -> IAiSharedQueue.MarkDispatchedAsync(...)
  -> IAiSharedRunStore.MarkDispatchedAsync(...)
  -> restore previous context
```

This is required for:

- background pump
- manual MCP drain
- fulfilled scale-out requeue dispatch
- future Kubernetes control-plane pods
- future remote provider dispatch
- multi-control-plane scheduling.

---

## 16. Manual drain while background pump is disabled

Manual drain can be enabled without enabling the hosted background pump.

Required configuration:

```text
AiSharedQueuePump:Enabled = true
AiMcpHost:EnableSharedQueuePump = false
AiSharedQueueBackgroundService:Enabled = false
```

Behavior:

```text
Submit run in QueueFirst mode
  -> SharedRun.Status = QueuedGlobally
  -> SharedQueueItem.Status = Pending

No background pump is running
  -> run remains queued

Manual queue.drain / PumpOnceAsync
  -> claim pending item
  -> restore sharedRun.ExecutionContextSnapshot
  -> dispatch-time admission selects target runtime instance
  -> dispatch to runtime instance
  -> SharedRun.Status = Dispatched
  -> SharedQueueItem.Status = Dispatched
```

This is useful for tests, MCP demos, controlled operator dispatch, and proving that the demo is not dependent on a continuously running background pump.

---

## 17. Enable the background shared queue service

The background service runs the pump continuously.

```csharp
using Multiplexed.AI.Runtime.ControlPlane.DI;

services.AddAiControlPlane();

services.AddAiSharedQueueBackgroundService(options =>
{
    options.Enabled = true;
    options.RuntimeInstanceId = "runtime-1";
    options.WorkerId = "runtime-1-shared-queue-worker";
    options.MaxDispatchesPerCycle = 10;
    options.ClaimTtl = TimeSpan.FromSeconds(30);

    options.IdleDelay = TimeSpan.FromMilliseconds(250);
    options.ActiveDelay = TimeSpan.FromMilliseconds(25);
    options.ErrorDelay = TimeSpan.FromSeconds(2);

    options.RequestedBy = "system";
    options.Source = "shared-queue-background-service";

    options.Metadata = new Dictionary<string, string>
    {
        ["runtimeInstanceId"] = "runtime-1",
        ["component"] = "shared-queue-background-service"
    };
});
```

The hosted service does not contain dispatch logic directly.

```text
AiSharedQueueBackgroundService
  -> IAiSharedQueuePump.PumpOnceAsync(...)
```

The pump owns the cycle logic.

```text
AiSharedQueuePump
  -> IAiSharedQueueDispatcher.DispatchNextAsync(...)
```

The dispatcher owns claim, context restore, admission, dispatch, and state update.

```text
AiSharedQueueDispatcher
  -> IAiSharedQueue.ClaimNextAsync(...)
  -> IAiSharedRunStore.GetAsync(...)
  -> restore ExecutionContextSnapshot
  -> IAiRunAdmissionController.AdmitAsync(...)
  -> IAiSharedRunDispatcher.DispatchAsync(...)
  -> IAiSharedQueue.MarkDispatchedAsync(...)
  -> IAiSharedRunStore.MarkDispatchedAsync(...)
```

---

## 18. Dispatch-time admission

Shared queue dispatch performs admission at drain time.

This means a queued run can be submitted earlier, then assigned later based on the latest visible runtime instance capacity.

```text
Submit time:
  -> run enters shared queue with ExecutionContextSnapshot

Dispatch time:
  -> queue pump claims item
  -> dispatcher reads shared run
  -> dispatcher restores ExecutionContextSnapshot
  -> dispatcher asks admission for target
  -> admission lists tenant-visible registry/capacity
  -> admission selects assigned runtime instance
  -> dispatcher calls IAiSharedRunDispatcher
```

Benefits:

- queued work can wait until capacity exists
- pump identity is not coupled to dispatch target
- runtime target can be selected using current visibility
- local, HTTP, and future Kubernetes runtime providers can share the same queue model
- tenant isolation survives background dispatch.

Current behavior:

- admission uses visible capacity descriptors
- Redis-backed admission reservations protect selected runtime capacity during heavy dispatch scenarios
- registry and capacity listing are tenant-aware
- heartbeat and capacity publication remain the source of runtime visibility
- further Lua refinement can harden multi-control-plane slot reservation semantics.

Future improvement:

```text
admission selects runtime instance
  -> atomically reserve run slot / worker capacity
  -> dispatch
  -> release reservation when run completes, fails, or reservation expires
```

---

## 19. Scale-out fulfilled requeue and dispatch

When a submit-time admission decision requests scale-out, the shared controller does not dispatch the run directly.

The validated scale-out lifecycle is:

```text
SharedRun.Status = ScaleOutRequested
  -> scale-out request persisted in Redis with tenant runtime settings
  -> watcher observes pending request
  -> provider selector resolves a scale-out-capable provider
  -> local provider creates tenant-scoped runtime capacity through AiLocalRuntimeInstanceScaler
  -> scale-out request is marked Fulfilled
  -> IAiScaleOutFulfilledRunRequeueService enqueues the shared run into IAiSharedQueue
  -> shared queue pump claims the requeued item
  -> dispatch-time admission restores tenant context
  -> admission sees the newly registered tenant-visible runtime instance
  -> dispatcher sends the run to the selected runtime instance
  -> shared run and queue item are marked Dispatched
```

The watcher intentionally does not dispatch the run itself.

This keeps responsibilities separated:

```text
AiRuntimeScaleOutRequestWatcherHostedService
  -> observes and fulfills scale-out requests

IAiScaleOutFulfilledRunRequeueService
  -> requeues the shared run after capacity exists

IAiSharedQueuePump / IAiSharedQueueDispatcher
  -> owns claim, context restore, admission, dispatch, and queue item lifecycle
```

This design is important for Kubernetes because the same lifecycle can later be used when the scale-out provider creates pods instead of local runtime instances.

Current tenant-aware local scale-out results:

```text
0 tenant-visible runtime capacity
  -> submit run
  -> scale-out requested
  -> local runtime instance created using tenant RuntimeInstanceIdPrefix
  -> scale-out request fulfilled
  -> shared run requeued
  -> pump dispatches
  -> local runtime executes
  -> runtime status completed
```

---

## 20. Local scaler tenant scope

The local runtime scaler must create capacity inside the tenant runtime scope.

It must not count global host count as if all tenants shared the same runtime pool.

Correct behavior:

```text
default / test-tenant:
    RuntimeInstanceIdPrefix = runtime-instance
    created runtime = host-*:runtime-instance-1

tenant-a Dedicated:
    RuntimeInstanceIdPrefix = tenant-a-runtime
    created runtime = host-*:tenant-a-runtime-1

tenant-b Hybrid:
    RuntimeInstanceIdPrefix = tenant-b-runtime
    created runtime = host-*:tenant-b-runtime-1
```

The scaler should count matching hosts by `RuntimeInstanceIdPrefix`.

It should not use global `hosts.Count` as the capacity count for every tenant.

This prevents a shared runtime such as `runtime-instance-1` from satisfying a dedicated tenant-a scale-out request.

---

## 21. Runtime worker capacity visibility

Runtime instances expose worker capacity through queue state and instance snapshots.

Visible fields include:

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

Capacity path:

```text
AiRuntimePipelineBackgroundController
  -> GetQueueStateAsync()
  -> AiRuntimePipelineQueueState
  -> AiRuntimeInstanceRegistrationHostedService
  -> IAiRuntimeInstanceRegistry
  -> AiRuntimeInstanceSnapshot
  -> MCP / control-plane list instances
```

`CanAcceptRun` reflects both run slots and worker availability.

```text
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

This is important for dashboards, MCP tools, Kubernetes demos, admission visibility, tenant-aware dispatch, and future autoscaling decisions.

---

## 22. Local worker capacity per execution

`MaxLocalWorkersPerExecution` controls how many workers from one runtime instance may work on one execution.

```csharp
services.Configure<AiRuntimePipelineBackgroundControllerOptions>(options =>
{
    options.MaxConcurrentRuns = 4;
    options.QueueCapacity = 100;
    options.MaxLocalWorkersPerExecution = 4;

    options.Distributed.Enabled = true;
    options.Distributed.WorkerCount = 20;
});
```

Example:

```text
Distributed.WorkerCount = 20
MaxLocalWorkersPerExecution = 4

One execution may reserve up to 4 local workers.
The runtime instance still owns 20 workers total.
Other workers remain available for other executions.
```

Effective worker count per execution is resolved from:

```text
min(
  Distributed.WorkerCount,
  MaxLocalWorkersPerExecution,
  AvailableWorkerCount
)
```

If no workers are available, the controller waits for worker capacity instead of immediately failing the run.

This option is local to the runtime instance.

It is not the same as cross-instance execution assistance.

---

## 23. Execution assistance vs local worker capacity

`MaxLocalWorkersPerExecution` is a local runtime instance policy.

`AiExecutionAssistanceOptions` controls cross-instance assistance.

```text
Local worker capacity:
  - one runtime instance
  - one local worker pool
  - limits workers per execution locally

Execution assistance:
  - multiple runtime instances
  - helper runtime instances
  - leases granted to assist an existing execution
```

They are intentionally separate.

The execution assistance candidate should use the effective worker count per execution instead of raw distributed worker count.

This prevents assistance visibility from over-reporting workers when `MaxLocalWorkersPerExecution` caps the actual execution worker group.

---

## 24. Runtime local queue receives snapshot

The final dispatch target is still the selected runtime instance local queue.

The local runtime queue must receive the original `ExecutionContextSnapshot`.

```text
Shared queue dispatcher
  -> restores snapshot for admission
  -> provider dispatch
  -> runtime instance local queue receives AiRuntimePipelineRunRequest
  -> AiRuntimePipelineBackgroundController restores snapshot before execution
  -> DAG execution starts
```

If the snapshot is missing, direct/background runtime execution should fail fast.

This protects the runtime from executing work without tenant context.

---

## 25. Full distributed host example

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.ControlPlane.SharedQueue;
using Multiplexed.AI.Redis.ControlPlane.SharedController;
using Multiplexed.AI.Runtime.ControlPlane.DI;
using Multiplexed.AI.Runtime.ControlPlane.SharedQueue;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect("localhost:6379"));

builder.Services.AddAiControlPlane(
    configureSharedController: options =>
    {
        options.EnableSubmitRun = true;
        options.EnableGetRun = true;
        options.EnableListRuns = true;
        options.EnableCancelRun = true;
        options.ReturnFailureResultInsteadOfThrowing = true;
        options.MeasureDuration = true;
        options.SubmitMode = AiSharedRuntimeSubmitMode.QueueFirst;
    },
    configureSharedQueuePump: options =>
    {
        options.Enabled = true;
        options.MaxDispatchesPerCycle = 20;
        options.DefaultClaimTtl = TimeSpan.FromSeconds(30);
        options.StopCycleWhenNoItemAvailable = true;
        options.StopCycleOnDispatchFailure = false;
        options.WorkerId = "runtime-1-pump";
        options.Source = "runtime-instance";
    });

builder.Services.RemoveAll<IAiSharedRunStore>();
builder.Services.AddSingleton<IAiSharedRunStore, RedisAiSharedRunStore>();

builder.Services.Configure<RedisAiSharedRunStoreOptions>(options =>
{
    options.KeyPrefix = "ai:shared-runs";
    options.ListScanLimit = 500;
});

builder.Services.RemoveAll<IAiSharedQueue>();
builder.Services.AddSingleton<IAiSharedQueue, RedisAiSharedQueue>();

builder.Services.Configure<RedisAiSharedQueueOptions>(options =>
{
    options.KeyPrefix = "ai:shared-queue";
    options.ListScanLimit = 500;
});

builder.Services.AddAiSharedQueueBackgroundService(options =>
{
    options.Enabled = true;
    options.RuntimeInstanceId = "runtime-1";
    options.WorkerId = "runtime-1-shared-queue-worker";
    options.MaxDispatchesPerCycle = 20;
    options.ClaimTtl = TimeSpan.FromSeconds(30);
    options.IdleDelay = TimeSpan.FromMilliseconds(250);
    options.ActiveDelay = TimeSpan.FromMilliseconds(25);
    options.ErrorDelay = TimeSpan.FromSeconds(2);
    options.RequestedBy = "system";
    options.Source = "runtime-instance-background-service";
});

var app = builder.Build();

await app.RunAsync();
```

---

## 26. Current architecture summary

```text
Submit path:

MCP / API / CLI
  -> RBAC ExecutionContext
  -> ExecutionContextSnapshot
  -> IAiSharedRuntimeController
  -> IAiRunAdmissionController
  -> IAiSharedRunStore
  -> IAiSharedQueue
  -> IAiSharedRunDispatcher


Queue dispatch path:

IAiSharedQueuePump
  -> IAiSharedQueueDispatcher
  -> IAiSharedQueue
  -> IAiSharedRunStore
  -> Restore ExecutionContextSnapshot
  -> IAiRunAdmissionController
  -> Tenant-visible registry/capacity filtering
  -> IAiSharedRunDispatcher


Local dispatch path:

IAiSharedRunDispatcher
  -> IAiRuntimeQueueControlPlane
  -> AiRuntimePipelineRunRequest.ExecutionContextSnapshot


Background service path:

AiSharedQueueBackgroundService
  -> IAiSharedQueuePump


Runtime capacity visibility path:

IAiRuntimePipelineBackgroundController
  -> AiRuntimePipelineQueueState
  -> AiRuntimeInstanceRegistrationHostedService
  -> IAiRuntimeInstanceRegistry
  -> AiRuntimeInstanceSnapshot


Tenant-aware scale-out request path:

IAiSharedRuntimeController
  -> IAiRunAdmissionController
  -> IAiRuntimeScaleOutRequestPublisher
  -> IAiRuntimeScaleOutRequestStore
  -> AiRuntimeScaleOutRequestWatcherHostedService
  -> IAiRuntimeScaleOutProviderSelector
  -> IAiRuntimeScaleOutProvider
  -> IAiLocalRuntimeInstanceScaler
  -> IAiScaleOutFulfilledRunRequeueService
  -> IAiSharedQueue


HTTP/gRPC process-host scale-out path:

IAiSharedRuntimeController
  -> IAiRunAdmissionController
  -> IAiRuntimeScaleOutRequestPublisher
  -> Redis scale-out request
  -> AiRuntimeScaleOutRequestWatcherHostedService
  -> IAiRuntimeScaleOutProviderSelector
  -> HttpAiRuntimeInstanceProvider or GrpcAiRuntimeInstanceProvider
  -> HTTP/gRPC scale-out provisioner
  -> IAiRuntimeHostManager
  -> ProcessAiRuntimeHostCreationStrategy
  -> RuntimeInstanceOnly process
  -> runtime self-registration / heartbeat / capacity
  -> IAiScaleOutFulfilledRunRequeueService
  -> IAiSharedQueue
  -> IAiSharedQueuePump
  -> provider dispatch

```

---

## 27. Current limitations

Implemented:

```text
- RBAC-to-ExecutionContextSnapshot propagation foundation
- shared run persistence
- durable shared run ExecutionContextSnapshot storage
- Redis atomic shared run store
- Redis atomic shared queue
- direct dispatch for assigned runs
- queued dispatch through shared queue
- queue-first submit mode
- queue pump
- manual drain
- background service
- local dispatcher V1
- dispatch-time context restore
- dispatch-time admission
- tenant-aware registry filtering
- tenant-aware capacity filtering
- Shared/Dedicated/Hybrid runtime visibility rules
- pump identity / assigned runtime identity separation
- runtime worker capacity visibility
- max local workers per execution
- worker-aware CanAcceptRun
- Redis-backed scale-out request persistence
- tenant runtime settings propagation into scale-out requests
- store-backed scale-out request publisher
- scale-out request watcher
- provider-based scale-out selector
- local runtime scale-out provider
- tenant-scoped local runtime instance scaler
- fulfilled scale-out run requeue
- MCP Redis local scale-out fulfillment
- MCP Redis local scale-out requeue, dispatch, execution, and completion
- HTTP process-host scale-out and dispatch
- gRPC process-host scale-out and dispatch
- ControlPlaneWithGrpcRuntimeInstances host mode
- provider-agnostic shared queue crash recovery validation over HTTP and gRPC
```

Not implemented yet:

```text
- Kubernetes pod creation
- Kubernetes pod creation / deployment scaler adapter
- automatic Kubernetes scaling
- production multi-control-plane leader election
- Redis command queue runtime dispatch
- dashboard UI
- database-backed tenant runtime settings
- production-grade tenant authorization hardening beyond current RBAC foundation
```

---

## 28. Future Kubernetes direction

The next layer should not change the core shared controller design.

Future adapters can be added behind abstractions:

```text
IAiSharedRunDispatcher
  -> LocalAiSharedRunDispatcher
  -> HttpRuntimeInstanceDispatcher
  -> GrpcRuntimeInstanceDispatcher
  -> KubernetesRuntimeInstanceDispatcher

IAiRuntimeScaleOutRequestPublisher
  -> StoreBackedAiRuntimeScaleOutRequestPublisher
  -> Redis-backed scale-out request store
  -> future KubernetesScaleOutPublisher / scaler adapter

IAiRuntimeScaleOutProvider
  -> LocalAiRuntimeInstanceProvider
  -> HttpAiRuntimeInstanceProvider
  -> GrpcAiRuntimeInstanceProvider
  -> future Kubernetes scale-out provider
```

The current system is ready for Kubernetes-style coordination because Redis already coordinates shared work and now also persists tenant-aware scale-out requests:

```text
shared run state
durable execution context snapshot
pending queue state
atomic claim
dispatch ownership
tenant-aware admission
tenant-visible registry/capacity
requeue on failure
concurrent dispatcher safety
scale-out request lifecycle
tenant-scoped runtime capacity creation
```

Runtime instance capacity visibility prepares the control plane for Kubernetes dashboards and autoscaling decisions:

```text
tenant id
tenant group id
isolation mode
worker count
active worker count
available worker count
run slots
queue depth
queue paused state
CanAcceptRun
capacity pressure
```

Future autoscaling should use the existing abstractions:

```text
IAiRuntimeInstanceRegistry
IAiRuntimeInstanceCapacityStore
IAiRunAdmissionController
IAiRuntimeScaleOutRequestPublisher
IAiRuntimeScaleOutRequestStore
IAiRuntimeScaleOutProviderSelector
IAiRuntimeScaleOutProvider
IAiScaleOutFulfilledRunRequeueService
IAiSharedRunDispatcher
```

---

## 29. Validated test evidence

Current validation includes:

```text
HTTP and gRPC shared-controller process-host recovery scenarios passing
```

Tenant-aware shared controller coverage includes:

```text
- default/shared tenant scale-out to runtime-instance-1
- tenant-a Dedicated scale-out to tenant-a-runtime-1
- tenant-a Dedicated no fallback to shared runtime
- tenant-b Hybrid scale-out to tenant-b-runtime-1
- tenant-b Hybrid fallback to runtime-instance-1 when shared capacity is visible
- Redis scale-out request tenant field persistence
- local scaler scoped by RuntimeInstanceIdPrefix
- shared queue dispatcher restores ExecutionContextSnapshot before admission
- direct runtime queued runs require ExecutionContextSnapshot
- Redis registry listing filters by tenant visibility
- Redis capacity listing filters by tenant visibility
- admission assigns only tenant-visible runtime capacity
```

Legacy shared controller behavior remains validated:

```text
- shared run persistence
- Redis shared run store
- Redis shared queue
- Redis atomic queue claim
- direct assigned dispatch
- queue-first submit
- manual queue drain
- background pump dispatch
- missing shared run requeue
- dispatch failure requeue
- Redis local scale-out fulfillment
- fulfilled shared run requeue
- local queue execution completion
- HTTP pooled runtime dispatch
- gRPC process-host dispatch
- HTTP/gRPC provider-backed crash recovery
- heavy HTTP dispatch
```


gRPC shared-controller / process-host evidence includes:

```text
ControlPlaneWithGrpcRuntimeInstances = validated
providerHint = grpc = validated
GrpcAiRuntimeInstanceProvider scale-out capability = validated
Runtime Host Manager process mode = validated
RuntimeInstanceOnly gRPC child process = validated
Kestrel HTTP/2 transport = validated
gRPC dispatch = validated
single tenant real process crash recovery = validated
two tenant crash recovery = validated
two impacted tenants + safe tenant non-impact = validated
strict durable ExecutionId resume = validated
local queued SharedRun redispatch = validated
replay / ledger / trace proof = validated
```

