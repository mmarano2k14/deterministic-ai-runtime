# Kubernetes Runtime Host Provider

Status: Implemented and validated for both the historical one-runtime-per-Pod Kubernetes mode and the additive KubernetesPool mode with several independent runtime processes per Pod, HTTP/gRPC transport preservation, layered readiness, child replacement, full Pod failure recovery, bounded capacity, warm reuse, replay, ledger, lifecycle, and forensics evidence.

This document is the canonical architecture reference for Kubernetes-hosted runtime instances in the Deterministic AI Runtime.

Related documents:

- [Architecture Overview](architecture-overview.md)
- [Runtime Instance Provider Model](runtime-instance-provider-model.md)
- [Runtime Control Plane](runtime-control-plane.md)
- [Runtime Discovery, Registry, and Capacity](runtime-discovery-registry-capacity.md)
- [HTTP Runtime Provider](http-runtime-provider.md)
- [gRPC Runtime Provider](grpc-runtime-provider.md)
- [MCP Production Runtime Scenario Framework](mcp-production-runtime-scenario-framework.md)
- [Provider-Agnostic Process-Host Recovery](provider-agnostic-process-host-recovery.md)
- [Multi-Tenant Runtime Crash Isolation](multi-tenant-runtime-crash-isolation.md)
- [Testing Strategy](testing-strategy.md)
- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Runtime Pool Production Validation](runtime-pool-production-validation.md)

---

## Purpose

The Kubernetes runtime host provider makes Kubernetes a **host lifecycle mechanism** for runtime instances.

It does not replace the HTTP or gRPC runtime providers.

The architecture deliberately separates three decisions:

```text
Admission
    decides whether existing tenant-visible capacity can accept the run
    or whether scale-out is required

Runtime provider
    decides how runtime commands are transported
    HTTP or gRPC

Runtime Host Manager / Kubernetes strategy
    decides how the runtime host is created, exposed, observed, and terminated
```

The resulting invariant is:

```text
Kubernetes owns host lifecycle.
HTTP or gRPC owns runtime command transport.
The runtime instance owns its local queue and DAG execution.
The control plane owns registry/capacity publication and recovery coordination.
```

This boundary allows the same shared queue, admission, dispatch, recovery, replay, ledger, and forensics model to operate when the runtime is:

- a local in-process runtime;
- a child operating-system process;
- a Kubernetes Pod;
- or a future external host implementation.

---

## Non-Goals

The Kubernetes host provider does not:

- execute DAG steps;
- decide tenant admission;
- select HTTP versus gRPC;
- own shared queue dispatch;
- own provider retry or circuit-breaker behavior;
- mark scale-out requests fulfilled directly;
- own assigned-work recovery;
- replace runtime health reconciliation;
- replace `ExecutionContextSnapshot` tenant isolation;
- make Kubernetes labels the source of tenant authorization.

Kubernetes metadata is operational evidence and a resource-selection mechanism. Tenant visibility remains enforced by the runtime registry, capacity store, execution context, and isolation evaluator.

---

## High-Level Architecture

```text
MCP / Control Plane
        |
        v
Shared Run Admission
        |
        | RequestScaleOut
        v
Redis Scale-Out Request Store
        |
        v
Scale-Out Request Watcher
        |
        v
Runtime Scale-Out Provider Selector
        |
        +-----------------------------+
        |                             |
        v                             v
HTTP Scale-Out Provisioner       gRPC Scale-Out Provisioner
        |                             |
        +-------------+---------------+
                      |
                      v
             IAiRuntimeHostManager
                      |
                      v
      KubernetesAiRuntimeHostCreationStrategy
                      |
          +-----------+------------+
          |                        |
          v                        v
Pod/Service lifecycle        Transport exposure
Fake or Kubernetes SDK       Service / NodePort /
                             kubectl port-forward /
                             Gateway API
          |                        |
          +-----------+------------+
                      |
                      v
          Kubernetes host readiness
                      |
                      v
           Runtime command readiness
                      |
                      v
 KubernetesAiRuntimeInstancePublisher
          |                        |
          v                        v
Runtime Instance Registry     Capacity Store
          |                        |
          +-----------+------------+
                      |
                      v
          Normal shared queue dispatch
                      |
                      v
             HTTP or gRPC provider
                      |
                      v
          RuntimeInstanceOnly Pod
                      |
                      v
             Local runtime queue
                      |
                      v
                 DAG engine
```

The Kubernetes strategy returns a normal `AiRuntimeHostStartResult`. The upstream HTTP or gRPC scale-out provisioner and watcher continue to own provider-scale-out completion.

---

## Component Map

| Component | Responsibility |
|---|---|
| `AiKubernetesRuntimeHostOptions` | Configures Kubernetes lifecycle, image, namespace, readiness, Service, NodePort, port-forward, and Gateway behavior. |
| `AiKubernetesRuntimePodMetadataBuilder` | Creates deterministic Pod names, labels, and annotations from the host start request. |
| `AiKubernetesRuntimePodSpecBuilder` | Builds a provider-neutral `RuntimeInstanceOnly` Pod specification and environment. |
| `AiKubernetesRuntimePodSpec` | Keeps the core lifecycle model independent from Kubernetes SDK types. |
| `IAiKubernetesRuntimeHostClient` | Abstracts create, host-readiness, and delete operations. |
| `FakeAiKubernetesRuntimeHostClient` | Simulates lifecycle deterministically in memory; it does not create a routable endpoint. |
| `KubernetesSdkAiKubernetesRuntimeHostClient` | Creates and deletes real Pods/Services and validates readiness through the Kubernetes SDK. |
| `AiKubernetesSdkClient` | Thin SDK adapter around Kubernetes core and Gateway API operations. |
| `KubernetesAiRuntimeHostCreationStrategy` | Coordinates convergence, lifecycle, readiness, transport exposure, publication, and termination. |
| `KubernetesAiRuntimeInstancePublisher` | Publishes and removes runtime registry/capacity records after readiness. |
| `KubernetesSdkAiKubernetesRuntimeGatewayManager` | Creates or validates shared Gateway infrastructure and per-runtime routes. |
| `KubectlAiKubernetesGatewayTransportEndpointManager` | Resolves one process-wide local endpoint to the shared Gateway data-plane Service. |
| `AiKubernetesGatewayResourceFactory` | Builds GatewayClass, Gateway, HTTPRoute, and GRPCRoute resources. |

Dependency injection is registered through `AddAiKubernetesRuntimeHostProvider(...)`. It binds the `AiKubernetesRuntimeHost` configuration section by default, selects Fake or Kubernetes SDK client mode, registers Gateway services, registers the runtime instance publisher, and adds the Kubernetes strategy as an `IAiRuntimeHostCreationStrategy`.

---

## Start Lifecycle

`KubernetesAiRuntimeHostCreationStrategy.StartAsync` is the main lifecycle coordinator.

### 1. Validate lifecycle configuration

The strategy rejects startup when Kubernetes hosting is disabled or required settings are missing:

- namespace;
- runtime image;
- container name.

The failure is returned as a structured host-start rejection rather than as a successful but unusable runtime.

### 2. Serialize lifecycle by RuntimeInstanceId

A per-runtime semaphore ensures that concurrent scale-out requests for the same logical runtime cannot independently create, publish, or delete the same host.

```text
RuntimeInstanceId
    -> one lifecycle gate
    -> one convergent start/kill sequence
```

This gate covers both direct endpoint and shared-Gateway paths.

### 3. Converge duplicate starts

The strategy maintains:

- a cache of successful start results;
- the Pod specification associated with each runtime instance;
- active port-forward processes;
- per-runtime port-forward lifecycle gates.

When a duplicate start arrives, the strategy revalidates the existing Pod and, for shared-Gateway gRPC, the routed command path. If the existing host remains usable, the new request returns a converged start result instead of creating a duplicate Pod.

The converged result includes metadata such as:

```text
kubernetes.creation.converged = True
kubernetes.creation.convergence.source = runtime-host-lifecycle-cache
```

A stale convergence entry is invalidated before normal creation resumes.

### 4. Build a provider-neutral Pod specification

The Pod specification builder converts `AiRuntimeHostStartRequest` into `AiKubernetesRuntimePodSpec`.

The resulting model contains:

- namespace and deterministic Pod name;
- runtime image and image pull policy;
- container name and port;
- optional service account;
- transport name;
- labels and annotations;
- runtime environment variables.

Kubernetes SDK objects are not exposed to the Host Manager contract.

### 5. Create Pod and optional Service

`IAiKubernetesRuntimeHostClient.CreateRuntimeHostAsync` creates lifecycle resources.

In Kubernetes SDK mode:

- the Pod is created;
- a per-runtime Service is created when configured;
- `AlreadyExists` is treated as convergence only after resource identity is validated;
- unrelated existing resources are not silently adopted.

The create result records whether the current invocation actually created the resources. This ownership evidence controls failure cleanup.

### 6. Wait for Kubernetes host readiness

The host client waits for the Pod to become usable at the Kubernetes layer.

This proves the container lifecycle and Pod readiness state. It does not by itself prove that HTTP or gRPC commands can reach the expected runtime.

### 7. Resolve the transport endpoint

The strategy chooses one transport exposure path:

- direct Service-derived endpoint;
- NodePort endpoint;
- per-runtime `kubectl port-forward`;
- shared Kubernetes Gateway plus per-runtime HTTPRoute or GRPCRoute.

The resolved endpoint remains associated with the original runtime provider:

```text
provider.name = http or grpc
transport.name = http or grpc
host.provider = kubernetes
host.creation.mode = Kubernetes
```

### 8. Wait for runtime command readiness

When `RequireRuntimeReadiness` is enabled, the generic runtime readiness waiter validates the runtime command boundary using the resolved provider and transport metadata.

For shared-Gateway gRPC, the strategy performs an additional route-aware probe through the published Gateway endpoint. It sends the runtime routing header and requires a queue-status response from the expected runtime instance.

### 9. Publish registry and capacity

Only after host readiness, endpoint resolution, and required command readiness succeed does `KubernetesAiRuntimeInstancePublisher` write:

- `AiRuntimeInstanceRegistration`;
- `AiRuntimeInstanceCapacityDescriptor`.

The published descriptor is tenant-aware and includes:

- runtime instance id;
- control-plane id;
- provider and transport;
- Kubernetes namespace, Pod, Service, and node metadata when available;
- tenant id and tenant group id;
- isolation mode and fallback policy;
- worker count;
- maximum concurrent runs;
- local queue capacity;
- transport endpoint;
- ready/accepting capacity state.

### 10. Return to the provider-scale-out flow

The Kubernetes strategy returns the started host result. The HTTP or gRPC provisioner and the scale-out watcher then continue their normal flow and mark the durable request fulfilled.

---

## Pod Identity and Metadata

The metadata builder creates deterministic, Kubernetes-safe identities.

Pod names combine a readable prefix with a stable hash so that:

- long runtime ids do not exceed Kubernetes limits;
- similar prefixes do not collide after truncation;
- repeated requests for the same runtime converge on the same name.

Labels identify the resource for operational selection:

```text
app.kubernetes.io/name
app.kubernetes.io/component
control-plane-id
runtime-instance-id
provider
transport
host-provider
```

Tenant and tenant-group labels are added when available.

Annotations preserve richer metadata that does not need to satisfy label-value constraints:

```text
host.provider
host.creation.mode
hostCreation.strategy
kubernetes.namespace
kubernetes.pod.name
provider.name
transport.name
transport.endpoint
tenant.id
tenant.group.id
runtime.isolationMode
```

Custom labels and annotations from configuration are merged into the generated metadata.

---

## RuntimeInstanceOnly Pod Configuration

The Pod runs the same runtime host executable in `RuntimeInstanceOnly` mode.

The builder configures the runtime to:

- listen on `0.0.0.0` inside the container;
- disable control-plane-only MCP tools;
- disable the shared queue pump inside the runtime Pod;
- run one local runtime instance identity per Pod;
- configure worker count, concurrent run slots, and local queue capacity from the host start request;
- enable distributed execution workers;
- preserve `ControlPlaneId`, tenant, tenant group, provider, transport, and isolation metadata;
- use HTTP/2 Kestrel settings for gRPC transport;
- use control-plane discovery rather than publishing a second control-plane identity;
- enable replay-safe Mongo/Redis payload storage settings required by production scenarios.

A critical design choice is:

```text
AiRuntimeInstanceRegistration__Enabled = false
```

The Pod does not publish optimistic registry/capacity state before the control plane proves that the Kubernetes host and required command transport are ready. Publication is centralized in `KubernetesAiRuntimeInstancePublisher`.

Configured environment variables are added only when they do not override values derived from the runtime host request. The request remains authoritative for identity, tenant isolation, provider, transport, and capacity.

---

## Kubernetes Client Modes

### Fake

`FakeAiKubernetesRuntimeHostClient` is an in-memory lifecycle simulator.

It is useful for:

- Host Manager mode selection tests;
- deterministic create/readiness/delete behavior;
- metadata propagation tests;
- failure and convergence tests that do not require a cluster.

It does not create:

- a real Pod;
- a real Service;
- a routable NodePort;
- a usable `kubectl port-forward` target;
- a real HTTP or gRPC runtime process.

A Fake-client test is therefore not transport-readiness evidence. Tests requiring a reachable endpoint must use Kubernetes SDK mode or an explicit transport test fixture.

### KubernetesSdk

`KubernetesSdkAiKubernetesRuntimeHostClient` creates real cluster resources through the Kubernetes .NET SDK abstraction.

It owns:

- Pod create/read;
- Service create/read;
- readiness polling;
- delete requests;
- exact resource disappearance validation;
- identity validation when resources already exist.

The SDK client keeps Kubernetes API details below `IAiKubernetesRuntimeHostClient`, so the main strategy remains testable and provider-neutral.

---

## Readiness Model

Kubernetes hosting uses layered readiness. No single signal is sufficient.

| Layer | Question | Owner |
|---|---|---|
| Resource creation | Did the Pod/Service request succeed or converge safely? | Kubernetes host client |
| Pod readiness | Is the exact Pod running and ready? | Kubernetes host client |
| Endpoint exposure | Is there a unique endpoint for the runtime transport? | Kubernetes strategy / Gateway endpoint manager |
| Runtime command readiness | Can the selected HTTP or gRPC provider reach the runtime command service? | Generic runtime readiness waiter |
| Gateway route readiness | Does the shared Gateway route reach the exact selected runtime? | Kubernetes strategy gRPC route probe |
| Registry/capacity publication | Is the runtime now safe to admit and dispatch work to? | Kubernetes runtime instance publisher |

The order is intentional:

```text
Pod Running
    is not enough

Pod Ready
    is not enough

Service exists
    is not enough

Gateway Programmed
    is not enough

Runtime command path works
    then publish capacity
```

This prevents admission from seeing a runtime that is alive at the orchestrator layer but unusable at the command-transport layer.

---

## Transport Exposure Modes

### Direct Service or NodePort

When Gateway mode is disabled, the strategy resolves a per-runtime endpoint from the Service metadata, NodePort settings, or request metadata.

`UseServicePerRuntime = true` preserves one routable Service identity per runtime instance.

When NodePort publication is enabled, `NodePortHost` identifies the host that the control-plane process can reach.

This mode is appropriate when every runtime should expose a distinct endpoint.

### Per-Runtime kubectl Port-Forward

`UsePortForwardTransportEndpoint` publishes a local endpoint such as:

```text
http://127.0.0.1:<allocated-port>
```

The strategy:

- allocates or uses the configured local port;
- starts `kubectl port-forward` against the runtime Service;
- waits until the local socket becomes reachable;
- reuses an existing live tunnel for convergent starts;
- stops the tunnel on kill, owned failure cleanup, or strategy disposal.

Port-forward is an operational bridge for local control-plane processes. It is not a cluster-native production ingress model.

### Shared Kubernetes Gateway

`UseGatewayTransportEndpoint = true` routes all runtime traffic through one shared Gateway endpoint.

The Gateway manager can:

- create or validate a GatewayClass;
- create or validate the shared Gateway;
- wait for accepted/programmed listener state;
- discover the controller-managed data-plane Service;
- create a runtime-specific HTTPRoute or GRPCRoute;
- delete the runtime route on host termination.

Routing is selected by a stable runtime header, defaulting to:

```text
x-ai-runtime-instance-id: <RuntimeInstanceId>
```

Each route forwards to the target runtime Service and container port.

```text
Control Plane
    |
    | one shared endpoint
    v
Kubernetes Gateway
    |
    | x-ai-runtime-instance-id
    +--------------------+--------------------+
    |                    |                    |
    v                    v                    v
HTTPRoute/GRPCRoute A  Route B              Route C
    |                    |                    |
    v                    v                    v
Runtime Service A      Service B            Service C
    |                    |                    |
    v                    v                    v
Runtime Pod A          Pod B                Pod C
```

Gateway API CRDs and a matching controller deployment are external prerequisites. Dynamic creation manages GatewayClass/Gateway/route resources; it does not install CRDs or deploy the controller.

---

## HTTP and gRPC Boundaries

Kubernetes does not become a third runtime command protocol.

### HTTP-hosted Kubernetes runtime

```text
Host provider = kubernetes
Runtime provider = http
Transport = http
Route kind = HTTPRoute when Gateway mode is used
```

HTTP retry, timeout, circuit breaker, structured failure reasons, and dispatch semantics remain in the HTTP provider.

### gRPC-hosted Kubernetes runtime

```text
Host provider = kubernetes
Runtime provider = grpc
Transport = grpc over HTTP/2
Route kind = GRPCRoute when Gateway mode is used
```

gRPC command dispatch and provider-specific readiness remain in the gRPC provider and readiness path. The Pod builder configures Kestrel for HTTP/2.

The same `RuntimeInstanceId` must remain visible through:

- admission decision;
- scale-out request;
- host start request;
- Kubernetes labels/annotations;
- transport routing header;
- registry/capacity descriptor;
- local runtime queue;
- recovery evidence.

---

## Registry and Capacity Publication

Unlike the process-host path, the Kubernetes strategy intentionally owns the first control-plane publication after readiness.

The publisher writes a runtime role descriptor with initial ready capacity:

```text
Role = Runtime
Status = Ready
CanAcceptRun = true
AvailableWorkerCount = WorkerCountPerInstance
AvailableRunSlots = MaxConcurrentRunsPerInstance
```

Publication metadata preserves both provider and host identity:

```text
provider.name = grpc or http
transport.name = grpc or http
host.provider = kubernetes
host.creation.mode = Kubernetes
kubernetes.namespace = ...
kubernetes.pod.name = ...
kubernetes.service.name = ...
```

This separation is essential:

```text
Provider metadata answers: how do I send commands?
Host metadata answers: where and how is the runtime hosted?
Tenant metadata answers: who may see and use this capacity?
```

---

## Failure Ownership and Cleanup

Resource cleanup is ownership-aware.

When creation fails after a resource was newly created by the current invocation, the strategy may delete the owned Pod/Service when `DeleteResourcesOnFailure` is enabled.

When a duplicate request converges on a host created by another invocation, it must not delete shared live resources or an existing reused tunnel.

Structured failures distinguish lifecycle boundaries, including:

- configuration missing;
- Pod specification failure;
- host creation failure;
- Kubernetes readiness timeout;
- transport endpoint resolution failure;
- port-forward startup failure;
- runtime command readiness failure;
- Gateway readiness or route failure;
- publication failure.

A rejected host start does not become executable capacity.

---

## Kill, Crash, and Recovery Boundary

`KubernetesAiRuntimeHostCreationStrategy` also implements runtime host process control so production recovery scenarios can terminate a Kubernetes runtime through the same lifecycle abstraction used for process hosts.

Kill flow:

```text
Acquire RuntimeInstanceId lifecycle gate
    -> remove convergence cache
    -> stop direct port-forward
    -> load exact cached Pod specification
    -> delete Service and Pod
    -> wait for exact Pod UID disappearance
    -> delete runtime Gateway route best effort
    -> remove registry capacity
    -> unregister runtime instance
```

The exact Pod UID boundary matters because a new Pod may reuse the same name. Recovery must prove that the failed Pod incarnation can no longer write after termination.

Kubernetes termination does not itself recover work.

The existing recovery split remains:

```text
RuntimeInstanceHealthReconciler
    suppresses unsafe capacity

AiRuntimeExecutionRecoveryReconciler
    enumerates work assigned to the failed runtime
    resumes in-flight ExecutionId work
    redispatches local-queued SharedRunId work

Runtime Host Manager / provider scale-out
    creates replacement capacity
```

The same recovery semantics therefore apply to child processes and Pods without moving business recovery logic into Kubernetes code.

---

## Multi-Tenant Isolation

The host start request carries the durable execution context and effective tenant runtime settings into Kubernetes creation:

- `TenantId`;
- `TenantGroupId`;
- isolation mode;
- `PreferDedicatedCapacity`;
- `AllowSharedFallback`;
- maximum runtime instances;
- runtime instance id prefix;
- worker count;
- maximum concurrent runs;
- local queue capacity.

These values are copied into Pod environment, labels/annotations, registry metadata, and capacity metadata.

The authoritative visibility rule remains in the control-plane registry/capacity layer:

- Dedicated capacity is visible only to its owner scope;
- Hybrid capacity is owner-scoped but may fall back to Shared capacity when allowed;
- Shared capacity is visible to compatible shared/fallback scopes.

A namespace alone is not treated as the tenant boundary. Kubernetes namespace strategy can complement isolation, but it does not replace runtime tenant metadata and execution-context enforcement.

---

## Configuration Reference

The default configuration section is:

```text
AiKubernetesRuntimeHost
```

### Core lifecycle

| Setting | Meaning |
|---|---|
| `Enabled` | Enables Kubernetes host creation. |
| `ClientMode` | Selects `Fake` or `KubernetesSdk`. |
| `Namespace` | Namespace for runtime Pods and per-runtime Services. |
| `RuntimeImage` | RuntimeInstanceOnly container image. |
| `ContainerName` | Runtime container name. |
| `ServiceAccountName` | Optional Pod service account. |
| `PodNamePrefix` | Prefix for deterministic Pod names. |
| `ImagePullPolicy` | `IfNotPresent`, `Always`, or `Never`. |
| `UseServicePerRuntime` | Creates a dedicated Service for each runtime. |
| `StartupTimeout` | Host creation/start timeout. |
| `ReadinessPollInterval` | Kubernetes and runtime readiness polling interval. |
| `ReadinessTimeout` | Runtime command readiness timeout. |
| `RequireRuntimeReadiness` | Requires provider/transport readiness before publication. |
| `DeleteResourcesOnFailure` | Deletes resources created by the failed invocation. |
| `Labels` / `Annotations` | Additional Kubernetes metadata. |
| `EnvironmentVariables` | Additional non-authoritative Pod environment variables. |

### Transport

| Setting | Meaning |
|---|---|
| `TransportName` | Default runtime transport, normally `http` or `grpc`. |
| `ContainerPort` | Runtime transport port inside the Pod. |
| `NodePortHost` | Reachable host used when publishing NodePort endpoints. |
| `PublishNodePortTransportEndpoint` | Allows NodePort endpoint publication. |
| `UsePortForwardTransportEndpoint` | Publishes a local per-runtime kubectl port-forward endpoint. |
| `PortForwardLocalPort` | Fixed local port, or `0` for automatic allocation. |
| `KubectlPath` | kubectl executable path. |
| `PortForwardStartupTimeout` | Time allowed for the local forwarded socket to become reachable. |

### Shared Gateway

| Setting | Meaning |
|---|---|
| `UseGatewayTransportEndpoint` | Uses one shared Gateway endpoint instead of direct per-runtime exposure. |
| `GatewayName` | Shared Gateway name. |
| `GatewayClassName` | GatewayClass used by the Gateway. |
| `GatewayControllerName` | Expected controller name. |
| `CreateGatewayClassWhenMissing` | Creates the GatewayClass resource when absent. |
| `GatewayListenerName` | Listener name. |
| `GatewayPort` | Listener/data-plane port. |
| `GatewayServiceName` | Optional explicit backing Service name. |
| `GatewayServiceNamespace` | Optional explicit backing Service namespace. |
| `GatewayRouteHeaderName` | Header used to select the target runtime. |
| `CreateGatewayWhenMissing` | Creates the Gateway resource when absent. |
| `RequireGatewayProgrammed` | Requires the Gateway Programmed condition. |
| `GatewayReadinessTimeout` | Gateway and route readiness timeout. |
| `GatewayReadinessPollInterval` | Gateway polling interval. |

---

## Cluster Prerequisites

Kubernetes SDK mode requires control-plane credentials with the permissions needed by enabled features.

Core lifecycle permissions normally include:

```text
pods: get, list, create, delete
services: get, list, create, delete
```

Gateway mode additionally requires the relevant Gateway API permissions:

```text
gatewayclasses: get, create when dynamic creation is enabled
gateways: get, create
httproutes: get, create, update, delete
grpcroutes: get, create, update, delete
services: list/get across the configured discovery scope
```

Operational prerequisites:

- the runtime image must be available to cluster nodes;
- Redis/Mongo endpoints used by the runtime Pod must be reachable from the cluster;
- DNS/network policies must allow the runtime to access required stores;
- the control plane must be able to reach the selected NodePort, port-forward, or Gateway endpoint;
- gRPC paths must preserve HTTP/2;
- Gateway API CRDs and a compatible controller must exist before Gateway mode can become ready;
- `kubectl` must be available when a port-forward endpoint manager is used.

---

## KubernetesPool Mode

KubernetesPool is an additive host mode and does not change the historical Kubernetes semantics.

```text
Kubernetes
    -> one RuntimeInstanceOnly runtime per Pod/Service

KubernetesPool
    -> one Pod failure boundary
    -> several independent child runtime processes
```

Inside a KubernetesPool Pod, every child owns an independent `RuntimeInstanceId`. The Pod UID identifies the physical failure boundary, not the execution identity.

The in-Pod pool manager is responsible for child process lifecycle and bounded membership. Kubernetes remains responsible for the outer Pod lifecycle. HTTP or gRPC remains responsible for runtime command transport.

### Child failure

A child can be killed after durable DAG progress while:

- the Pod remains alive;
- four configured siblings remain alive in the validated five-runtime topology;
- the affected run resumes with the same `ExecutionId`;
- replacement child membership restores the configured runtime count.

### Pod failure

A distinct fully busy Pod can be force-deleted after child recovery has converged.

The control plane then:

- identifies the exact five-runtime failed membership in the validated topology;
- preserves the other two Pods;
- creates replacement Pod capacity;
- recovers exactly five affected runs;
- returns to three Pods and fifteen active runtimes.

### Warm reuse

The final production scenarios run two complete cycles without intermediate cleanup. The second cycle starts with `ColdStart=false`, reusing the repaired Pod and runtime topology produced by the first cycle.

### Capacity observation

Historical snapshots for an intentionally failed child are not counted as active capacity during replacement convergence. Active capacity is measured from valid, selectable runtime membership rather than from raw historical registry visibility.

See [Runtime Pool Production Validation](runtime-pool-production-validation.md).

---

## Testing Model

Kubernetes tests should be classified by the boundary they prove.

### Pure builder and metadata tests

Validate:

- deterministic Pod naming;
- label-value limits and stable hashes;
- environment propagation;
- tenant/isolation metadata;
- gRPC Kestrel HTTP/2 configuration;
- custom environment variables cannot override request identity.

### Fake lifecycle tests

Validate:

- DI client selection;
- strategy mode selection;
- lifecycle convergence;
- structured failures;
- ownership-aware cleanup;
- publication metadata with a test endpoint.

They do not prove a live Kubernetes or transport boundary.

### Kubernetes SDK lifecycle tests

Validate:

- real Pod and Service creation;
- Pod readiness;
- AlreadyExists identity convergence;
- exact Pod UID deletion;
- registry/capacity publication and removal.

### Transport tests

Validate separately for HTTP and gRPC:

- endpoint resolution;
- per-runtime port-forward reachability;
- HTTP/2 gRPC command readiness;
- shared-Gateway route selection by runtime header;
- route reaches the exact runtime instance.

### Production crash recovery tests

Validate:

- a real Pod executes DAG work;
- the failed Pod reaches expected progress before termination;
- kill returns only after the old Pod UID disappears;
- unsafe capacity is suppressed;
- replacement capacity is created;
- in-flight work resumes with the same `ExecutionId`;
- local queued work is redispatched through the same `SharedRunId`;
- unrelated tenant work is not recovered or interrupted;
- replay, ledger, trace, and forensics evidence remains coherent.

See [Testing Strategy](testing-strategy.md) for the complete test taxonomy.

---

## Troubleshooting Boundaries

### Pod is Running but scale-out is not fulfilled

Inspect readiness in order:

```text
Pod Ready?
Service endpoint available?
Resolved transport endpoint unique and reachable?
HTTP/gRPC command readiness successful?
Gateway route accepted/programmed?
Registry and capacity publication completed?
```

Do not treat `Pod.Status.Phase = Running` as proof of command readiness.

### Scale-out request remains Observed

The watcher has claimed the request but host creation or readiness has not completed. Inspect the host-strategy structured failure and Kubernetes/Gateway readiness logs.

### Port-forward endpoint does not become reachable

Verify:

- client mode is Kubernetes SDK rather than Fake;
- the Service exists;
- kubectl can access the same cluster/namespace;
- the Service selector resolves the target Pod;
- the container listens on the configured port;
- no local port conflict exists.

### gRPC endpoint exists but commands fail

Verify:

- Kestrel uses HTTP/2;
- plaintext HTTP/2 is supported by the client path;
- the Gateway controller supports GRPCRoute when Gateway mode is enabled;
- the runtime routing header is forwarded;
- the route backend port matches the runtime Service port.

### Runtime disappeared but work did not recover

Kubernetes deletion is only the lifecycle boundary. Inspect:

- health reconciler unsafe-instance detection;
- assigned run/execution index;
- recovery reconciler classification;
- replacement runtime selection;
- resume/redispatch metadata;
- recovery forensics timeline.

---

## Architecture Invariants

The Kubernetes implementation must preserve these invariants:

```text
1. Kubernetes is a host lifecycle provider, not a command provider.
2. HTTP/gRPC provider identity survives Kubernetes hosting unchanged.
3. One RuntimeInstanceId converges through one lifecycle gate.
4. Existing unrelated Kubernetes resources are never silently adopted.
5. A Pod is not published as capacity before required command readiness succeeds.
6. Failure cleanup deletes only resources owned by the failing invocation.
7. Fake client mode is not live transport evidence.
8. RuntimeInstanceOnly Pods do not run the control-plane shared queue pump.
9. Tenant/isolation settings flow from ExecutionContextSnapshot and admission into Pod and capacity metadata.
10. Kill proves exact old Pod UID disappearance before recovery proceeds.
11. Kubernetes termination does not own work recovery.
12. Shared Gateway routing must select the exact runtime instance.
13. Registry/capacity removal accompanies successful host termination.
14. Normal shared queue dispatch remains the path after scale-out fulfillment.
```

---

## Source Map

The implementation is centered under:

```text
implementations/dotnet/src/Multiplexed.AI/Runtime/ControlPlane/
  RuntimeInstances/HostManager/Strategy/Kubernetes/
```

Main areas:

```text
KubernetesAiRuntimeHostCreationStrategy.cs
AiKubernetesRuntimeHostOptions.cs
AiKubernetesRuntimePodSpecBuilder.cs
AiKubernetesRuntimePodMetadataBuilder.cs

Client/
  IAiKubernetesRuntimeHostClient.cs
  FakeAiKubernetesRuntimeHostClient.cs
  KubernetesSdkAiKubernetesRuntimeHostClient.cs
  AiKubernetesSdkClient.cs

Publisher/
  KubernetesAiRuntimeInstancePublisher.cs

Gateway/
  AiKubernetesGatewayResourceFactory.cs
  KubernetesSdkAiKubernetesRuntimeGatewayManager.cs
  Transport/KubectlAiKubernetesGatewayTransportEndpointManager.cs
```

Dependency injection is registered by:

```text
Runtime/ControlPlane/DI/AiKubernetesRuntimeHostServiceCollectionExtensions.cs
```

---

## Documentation Rule

Do not describe Kubernetes runtime hosting as a replacement for HTTP or gRPC providers.

The implemented architecture is:

```text
Kubernetes host lifecycle
    + HTTP or gRPC command transport
    + layered readiness
    + control-plane registry/capacity publication
    + provider-agnostic health and execution recovery
```

Keep cluster prerequisites and validation scope explicit. Fake lifecycle tests, SDK resource tests, transport-readiness tests, Gateway tests, and production crash-recovery tests prove different boundaries and must not be presented as interchangeable evidence.
