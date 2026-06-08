# Shared Runtime Controller / Shared Queue Usage

This document shows how to configure and use the AI control plane shared runtime features.

It covers:

- In-memory shared controller mode
- Redis shared run store
- Redis shared queue
- Direct assigned-run dispatch
- Global shared queue dispatch
- Queue-first submit mode
- Shared queue pump
- Manual shared queue drain
- Shared queue background service
- Dispatch-time admission
- Pump identity vs assigned runtime identity
- Runtime worker capacity visibility
- Full distributed host setup
- Current architecture summary
- Current limitations
- Future Kubernetes direction

---

## 1. Basic in-memory setup

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

        // Optional:
        // DirectDispatch keeps the classic behavior.
        // QueueFirst always creates the shared run and queues it globally first.
        options.SubmitMode = AiSharedRuntimeControllerSubmitMode.DirectDispatch;
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

```txt
IAiSharedRunStore          -> InMemoryAiSharedRunStore
IAiSharedQueue             -> InMemoryAiSharedQueue
IAiSharedRunDispatcher     -> LocalAiSharedRunDispatcher
IAiSharedQueueDispatcher   -> AiSharedQueueDispatcher
IAiSharedQueuePump         -> AiSharedQueuePump
IAiSharedRuntimeController -> AiSharedRuntimeController
```

---

## 2. Redis setup for distributed shared controller mode

Use Redis when multiple runtime instances or workers need to coordinate shared runs.

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
        options.SubmitMode = AiSharedRuntimeControllerSubmitMode.QueueFirst;
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

var provider = services.BuildServiceProvider();
```

Redis-backed services provide:

```txt
RedisAiSharedRunStore
  - hash storage per shared run
  - sorted set index
  - Lua atomic create
  - Lua atomic cancel
  - Lua atomic mark-dispatched
  - SHA cache + NOSCRIPT reload

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
```

---

## 3. Submit a run to the shared runtime controller

```csharp
using Multiplexed.Abstractions.AI.ControlPlane.SharedController;
using Multiplexed.Abstractions.AI.Runtime.Execution.Instance.Worker;

var controller = provider.GetRequiredService<IAiSharedRuntimeController>();

var result = await controller.SubmitRunAsync(
    new AiSharedRuntimeControllerRequest
    {
        Operation = AiSharedRuntimeControllerOperation.SubmitRun,
        RequestedSharedRunId = "shared-run-001",
        RunRequest = new AiRuntimePipelineRunRequest
        {
            PipelineName = "document-processing"
        },
        TenantId = "tenant-a",
        PipelineKey = "document-processing",
        CorrelationId = "correlation-001",
        RequestedBy = "api",
        Source = "example",
        Reason = "Submit document processing workflow.",
        Metadata = new Dictionary<string, string>
        {
            ["tenant"] = "tenant-a",
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

```txt
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
  -> SharedRun.Status = ScaleOutRequested

Reject
  -> SharedRunStore.CreateAsync(...)
  -> SharedRun.Status = Rejected
```

---

## 4. Queue-first submit mode

Queue-first mode is useful when the control plane should always persist the shared run and place it in the global queue before any runtime instance consumes it.

```csharp
services.Configure<AiSharedRuntimeControllerOptions>(options =>
{
    options.SubmitMode = AiSharedRuntimeControllerSubmitMode.QueueFirst;
});
```

Queue-first flow:

```txt
SubmitRunAsync
  -> SharedRunStore.CreateAsync(...)
  -> IAiSharedQueue.EnqueueAsync(...)
  -> SharedRun.Status = QueuedGlobally
  -> run waits for background pump or manual drain
```

Queue-first is different from forcing admission globally. It is a controller submit mode, not an admission override.

Use queue-first when:

- shared runs must always enter the global queue first
- the runtime instance should be selected at dispatch/drain time
- a background pump or MCP manual drain controls dispatch timing
- demos need to show queued work before dispatch
- Kubernetes-style runtime instances should pull work from a shared queue

---

## 5. List shared runs

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

---

## 6. Get one shared run

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
    Console.WriteLine($"Pipeline: {get.Run.RunRequest.PipelineName}");
    Console.WriteLine($"LocalRunId: {get.Run.LocalRunId}");
    Console.WriteLine($"ExecutionId: {get.Run.ExecutionId}");
}
```

---

## 7. Cancel a shared run

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

---

## 8. Manually pump the shared queue

A runtime instance can manually ask to claim and dispatch pending shared queue items.

The pump request now uses explicit pump identity fields:

- `PumpRuntimeInstanceId`
- `PumpWorkerId`

These identify the runtime instance and worker executing the pump cycle. They do not necessarily identify the runtime instance that will receive the run.

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

```txt
PumpOnceAsync
  -> DispatchNextAsync
  -> ClaimNextAsync from IAiSharedQueue
  -> Get shared run from IAiSharedRunStore
  -> Re-admit the run for dispatch-time target selection
  -> Dispatch through IAiSharedRunDispatcher
  -> Mark queue item as Dispatched
  -> Mark shared run as Dispatched
  -> Repeat until max dispatches or no item
```

Important:

```txt
PumpRuntimeInstanceId = instance executing the pump
AssignedRuntimeInstanceId = instance selected by admission for dispatch
```

The pump identity and assigned runtime identity are intentionally separate.

---

## 9. Manual drain while background pump is disabled

Manual drain can be enabled without enabling the hosted background pump.

Required configuration:

```txt
AiSharedQueuePump:Enabled = true
AiMcpHost:EnableSharedQueuePump = false
AiSharedQueueBackgroundService:Enabled = false
```

Behavior:

```txt
Submit run in QueueFirst mode
  -> SharedRun.Status = QueuedGlobally
  -> SharedQueueItem.Status = Pending

No background pump is running
  -> run remains queued

Manual queue.drain / PumpOnceAsync
  -> claim pending item
  -> dispatch-time admission selects target runtime instance
  -> dispatch to runtime instance
  -> SharedRun.Status = Dispatched
  -> SharedQueueItem.Status = Dispatched
```

This is useful for tests, MCP demos, controlled operator dispatch, and proving that the demo is not dependent on a continuously running background pump.

---

## 10. Enable the background shared queue service

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

```txt
AiSharedQueueBackgroundService
  -> IAiSharedQueuePump.PumpOnceAsync(...)
```

The pump owns the cycle logic.

```txt
AiSharedQueuePump
  -> IAiSharedQueueDispatcher.DispatchNextAsync(...)
```

The dispatcher owns claim, admission, dispatch, and state update.

```txt
AiSharedQueueDispatcher
  -> IAiSharedQueue.ClaimNextAsync(...)
  -> IAiSharedRunStore.GetAsync(...)
  -> IAiRunAdmissionController.AdmitAsync(...)
  -> IAiSharedRunDispatcher.DispatchAsync(...)
  -> IAiSharedQueue.MarkDispatchedAsync(...)
  -> IAiSharedRunStore.MarkDispatchedAsync(...)
```

---

## 11. Dispatch-time admission

Shared queue dispatch now performs admission at drain time.

This means a queued run can be submitted earlier, then assigned later based on the latest visible runtime instance capacity.

```txt
Submit time:
  -> run enters shared queue

Dispatch time:
  -> queue pump claims item
  -> dispatcher reads shared run
  -> dispatcher asks admission for target
  -> admission selects assigned runtime instance
  -> dispatcher calls IAiSharedRunDispatcher
```

This keeps queue ownership and runtime target selection separate.

Benefits:

- queued work can wait until capacity exists
- pump identity is not coupled to dispatch target
- runtime target can be selected using current visibility
- local, HTTP, and future Kubernetes runtime providers can share the same queue model

Current limitation:

- admission uses visible capacity
- target capacity is not yet atomically reserved during admission
- if many dispatches happen faster than heartbeat/capacity updates, a deterministic admission strategy can hotspot on the same instance

Future improvement:

```txt
admission selects runtime instance
  -> atomically reserve run slot / worker capacity
  -> dispatch
  -> release reservation when run completes or fails
```

---

## 12. Runtime worker capacity visibility

Runtime instances now expose worker capacity through queue state and instance snapshots.

Visible fields include:

```txt
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

```txt
AiRuntimePipelineBackgroundController
  -> GetQueueStateAsync()
  -> AiRuntimePipelineQueueState
  -> AiRuntimeInstanceRegistrationHostedService
  -> AiRuntimeInstanceCapacityDescriptor
  -> IAiRuntimeInstanceRegistry
  -> RuntimeInstanceEntry
  -> AiRuntimeInstanceSnapshot
  -> MCP / control-plane list instances
```

`CanAcceptRun` now reflects both run slots and worker availability.

```txt
CanAcceptRun = queue not paused
            + queue capacity available
            + run slot available
            + worker available
```

This is important for dashboards, MCP tools, Kubernetes demos, admission visibility, and future autoscaling decisions.

---

## 13. Local worker capacity per execution

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

```txt
Distributed.WorkerCount = 20
MaxLocalWorkersPerExecution = 4

One execution may reserve up to 4 local workers.
The runtime instance still owns 20 workers total.
Other workers remain available for other executions.
```

Effective worker count per execution is resolved from:

```txt
min(
  Distributed.WorkerCount,
  MaxLocalWorkersPerExecution,
  AvailableWorkerCount
)
```

If no workers are available, the controller waits for worker capacity instead of immediately failing the run.

This option is local to the runtime instance. It is not the same as cross-instance execution assistance.

---

## 14. Execution assistance vs local worker capacity

`MaxLocalWorkersPerExecution` is a local runtime instance policy.

`AiExecutionAssistanceOptions` controls cross-instance assistance.

```txt
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

The execution assistance candidate now uses the effective worker count per execution instead of raw distributed worker count. This prevents assistance visibility from over-reporting workers when `MaxLocalWorkersPerExecution` caps the actual execution worker group.

---

## 15. Full distributed host example

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
        options.SubmitMode = AiSharedRuntimeControllerSubmitMode.QueueFirst;
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

## 16. Current architecture summary

```txt
Submit path:

IAiSharedRuntimeController
  -> IAiRunAdmissionController
  -> IAiSharedRunStore
  -> IAiSharedQueue
  -> IAiSharedRunDispatcher


Queue dispatch path:

IAiSharedQueuePump
  -> IAiSharedQueueDispatcher
  -> IAiSharedQueue
  -> IAiSharedRunStore
  -> IAiRunAdmissionController
  -> IAiSharedRunDispatcher


Local dispatch path:

IAiSharedRunDispatcher
  -> IAiRuntimeQueueControlPlane


Background service path:

AiSharedQueueBackgroundService
  -> IAiSharedQueuePump


Runtime capacity visibility path:

IAiRuntimePipelineBackgroundController
  -> AiRuntimePipelineQueueState
  -> AiRuntimeInstanceRegistrationHostedService
  -> IAiRuntimeInstanceRegistry
  -> AiRuntimeInstanceSnapshot
```

---

## 17. Current limitations

Implemented:

```txt
- shared run persistence
- Redis atomic shared run store
- Redis atomic shared queue
- direct dispatch for assigned runs
- queued dispatch through shared queue
- queue-first submit mode
- queue pump
- manual drain
- background service
- local dispatcher V1
- dispatch-time admission
- pump identity / assigned runtime identity separation
- runtime worker capacity visibility
- max local workers per execution
- worker-aware CanAcceptRun
```

Not implemented yet:

```txt
- Kubernetes pod creation
- distributed runtime instance API dispatch
- automatic scaling
- production admission capacity reservation
- scale-out request publisher
- dashboard UI
```

---

## 18. Future Kubernetes direction

The next layer should not change the core shared controller design.

Future adapters can be added behind abstractions:

```txt
IAiSharedRunDispatcher
  -> LocalAiSharedRunDispatcher
  -> HttpRuntimeInstanceDispatcher
  -> KubernetesRuntimeInstanceDispatcher

IAiRuntimeScaleOutRequestPublisher
  -> NoopScaleOutPublisher
  -> RedisScaleOutPublisher
  -> KubernetesScaleOutPublisher
```

The current system is ready for Kubernetes-style coordination because Redis already coordinates:

```txt
shared run state
pending queue state
atomic claim
dispatch ownership
requeue on failure
concurrent dispatcher safety
```

Runtime instance capacity visibility prepares the control plane for Kubernetes dashboards and autoscaling decisions:

```txt
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

```txt
IAiRuntimeInstanceRegistry
IAiRuntimeInstanceCapacityStore
IAiRunAdmissionController
IAiRuntimeScaleOutRequestPublisher
IAiSharedRunDispatcher
```
