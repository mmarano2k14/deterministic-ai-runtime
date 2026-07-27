# Runtime Provider and Transport Model

## Deterministic AI Runtime Platform

This document describes the runtime provider and transport model of the Deterministic AI Runtime Platform.

This is a core part of the platform's distributed execution architecture.

The runtime is not designed to be locked to a single communication model. It is designed so the control plane, MCP server, runtime instances, shared queue, local queues, and workers can communicate through provider-based and transport-aware abstractions.

The key idea is:

> The runtime core should not care whether execution happens locally, over HTTP, over gRPC, through a runtime-instance-only host, through a stable Runtime Pool endpoint, or through a future message-bus transport.

The execution semantics should remain stable.

The transport should be replaceable.

---

## Purpose

The purpose of the runtime provider and transport model is to make distributed AI execution possible without hardcoding one hosting or communication strategy.

Production AI runtime infrastructure may need to support:

- local execution;
- local multi-instance simulation;
- remote runtime instances;
- runtime-instance-only hosts;
- HTTP-based dispatch;
- Kubernetes-style runtime pods;
- implemented gRPC communication;
- future message-bus communication;
- managed hosting;
- dedicated runtime instances;
- tenant-aware runtime capacity;
- MCP-controlled execution.

The provider and transport model allows the platform to evolve from one process to many runtime instances while keeping the deterministic runtime core stable.

---

## Current Foundation

The platform already has important foundations for provider-based runtime hosting and transport abstraction.

These include:

- provider-based runtime hosting direction;
- local runtime provider direction;
- HTTP runtime provider direction;
- runtime-instance-only mode direction;
- control-plane with runtime instances direction;
- shared queue direction;
- local queue per runtime instance;
- shared queue pump direction;
- runtime instance registry direction;
- multiple runtime instances direction;
- multiple workers per runtime instance;
- MCP control-plane foundation;
- run/execution separation;
- admission and dispatch direction;
- capacity-aware dispatch direction;
- decision ledger foundation;
- replay/audit foundation;
- observability direction.

This means provider-based runtime communication is already part of the architecture.

The roadmap is to harden, document, expose, and extend it.

---

## Implemented Runtime Pool Transport Model

The platform now has an opt-in process-host Runtime Pool transport model.

```text
provider selects exact RuntimeInstanceId
    -> stable pool HTTP or gRPC endpoint
    -> exact RouteId
    -> exact child endpoint
```

The stable endpoint does not perform scheduling.

Implemented guarantees:

- exact HTTP and gRPC forwarding;
- no sibling fallback;
- forwarding leases;
- graceful route drain;
- response identity validation;
- targeted child replacement;
- suppression-aware routing;
- compatibility with existing modes.

The existing Kubernetes mode remains one runtime per Pod. Kubernetes Runtime Pool Pods are planned as a separate mode.

See [`runtime-pool-roadmap.md`](runtime-pool-roadmap.md).

---

## Core Architecture

The runtime provider and transport model can be summarized as:

```text
MCP / API / Dashboard
  -> Control Plane
      -> Shared Run Store
      -> Shared Queue
      -> Runtime Instance Registry
          -> Runtime Provider / Transport
              -> Runtime Instance
                  -> Local Queue
                      -> Worker
                          -> Execution Step
```

The runtime provider is the abstraction that allows the control plane to communicate with execution capacity.

The transport is how that communication happens.

The runtime core should remain independent from the transport details.

---

## Separation of Responsibilities

The architecture separates several responsibilities.

| Layer | Responsibility |
|---|---|
| MCP / API / Dashboard | External control and inspection surface. |
| Control Plane | Submit, inspect, replay, pause, resume, cancel, diagnose, dispatch. |
| Shared Queue | Hold submitted runs before assignment or dispatch. |
| Runtime Instance Registry | Track available runtime instances, capacity, heartbeat, and health. |
| Runtime Provider | Communicate with local or remote runtime instances. |
| Transport | Carry runtime commands and responses between processes/services. |
| Runtime Instance | Host local workers and local queue. |
| Local Queue | Hold work assigned to a runtime instance. |
| Worker | Execute claimed steps. |
| Decision Ledger | Record runtime and dispatch decisions. |
| Replay / Audit | Inspect execution history after execution. |

This separation is what makes distributed execution possible.

---

# 1. Runtime Provider

A runtime provider abstracts how work reaches a runtime instance.

A runtime provider can be responsible for:

- submitting assigned work to a runtime instance;
- dispatching a run;
- creating or linking an ExecutionId;
- returning execution status;
- returning runtime health;
- returning queue status;
- supporting cancellation direction;
- supporting diagnostics direction;
- supporting replay/control-plane integration direction;
- reporting errors in a structured way.

The runtime provider should hide whether the target runtime is local or remote.

---

## Provider Contract Direction

A runtime provider contract can expose operations such as:

- dispatch run;
- inspect run;
- inspect execution;
- cancel run or execution;
- inspect runtime instance status;
- inspect local queue status;
- inspect worker status direction;
- inspect diagnostics;
- return provider health.

The exact API can evolve.

The important point is that the control plane should not need to know the internal implementation details of each runtime instance.

---

## Provider Result Model

Provider responses should be structured.

A provider result can include:

- success/failure;
- status;
- error code;
- error message;
- RunId;
- ExecutionId;
- RuntimeInstanceId;
- CorrelationId;
- diagnostic metadata;
- retryable/non-retryable classification direction;
- decision ledger references direction.

Structured provider results are important for:

- MCP responses;
- dashboard views;
- retry behavior;
- diagnostics;
- replay/audit;
- decision ledger events.

---

# 2. Local Runtime Provider

A local runtime provider is used when execution happens in the same process or same host.

This is useful for:

- local development;
- unit tests;
- integration tests;
- simple demos;
- single-instance deployments;
- local multi-instance simulation.

The local provider can dispatch work directly to a runtime instance or local runtime pipeline without remote network communication.

Local provider mode should remain important because it keeps the platform easy to run and test.

---

## Why Local Provider Matters

Not every deployment needs remote runtime instances.

A good platform should support:

```text
simple local execution
```

and

```text
distributed multi-instance execution
```

with the same conceptual model.

The local provider protects developer experience.

It allows users to validate workflows without requiring Kubernetes or remote infrastructure.

---

# 3. HTTP Runtime Provider

The HTTP runtime provider direction is important because it allows runtime instances to run in separate processes or containers.

The control plane can communicate with runtime instances over HTTP.

This supports:

- remote runtime instances;
- runtime-instance-only mode;
- process separation;
- containerized runtime workers;
- integration tests;
- Kubernetes-style deployments;
- managed hosting direction.

HTTP is a practical first distributed transport because it is simple, observable, and easy to test.

---

## HTTP Provider Responsibilities

An HTTP runtime provider can support:

- dispatching assigned runs to a runtime instance;
- querying runtime instance health;
- querying local queue status;
- querying worker capacity direction;
- requesting cancellation direction;
- retrieving execution status direction;
- returning diagnostics;
- propagating correlation identifiers;
- returning structured errors.

The HTTP provider should not change runtime semantics.

It should only change how the control plane communicates with the runtime instance.

---

## HTTP Provider Limitations

HTTP is useful, but it may not be the final or only transport.

Potential limitations:

- request/response behavior may not fit every workflow;
- long-running operations may need async handling;
- backpressure must be explicit;
- network failures must be handled carefully;
- retries must avoid duplicate execution;
- streaming or event-driven scenarios may need another transport later.

This is why the architecture should remain transport-pluggable.

HTTP is a provider, not the entire architecture.

---

# 4. Runtime-Instance-Only Mode

Runtime-instance-only mode is one of the most important distributed hosting modes.

In this mode, a host runs as an execution instance only.

It does not need to act as the full control plane.

It can:

- register itself;
- expose RuntimeInstanceId;
- expose worker capacity;
- receive assigned work;
- manage local queue;
- execute steps through local workers;
- report health;
- report diagnostics;
- participate in replay/audit evidence through state, ledger, and observability.

This mode maps naturally to Kubernetes pods or dedicated runtime services.

---

## Runtime-Instance-Only Flow

A typical flow can be:

```text
Runtime Instance starts
  -> registers RuntimeInstanceId
  -> exposes heartbeat and capacity
  -> waits for assigned work
  -> receives run through provider/transport
  -> enqueues work locally
  -> workers execute steps
  -> runtime state/ledger/observability are updated
```

This makes runtime instances scalable and replaceable.

---

# 5. Control Plane With Runtime Instances

In control-plane-with-runtime-instances mode, one host can expose the control plane and also start runtime instances.

This is useful for:

- demos;
- local multi-instance simulation;
- integration tests;
- development;
- validating shared queue dispatch;
- validating worker capacity;
- validating runtime instance registry behavior.

It allows the platform to simulate distributed behavior before deploying to real Kubernetes or remote services.

---

## Why This Mode Matters

For a single-developer project, this mode is extremely valuable.

It allows the distributed runtime architecture to be tested locally without requiring full production infrastructure.

It can demonstrate:

- multiple runtime instances;
- multiple workers;
- shared queue;
- local queues;
- dispatch;
- replay;
- ledger;
- observability.

This gives strong proof of architecture before full managed hosting.

---

# 6. Shared Queue and Provider Dispatch

The shared queue sits above runtime providers.

A typical dispatch flow can be:

```text
Run submitted
  -> admission policy evaluated
  -> run accepted into shared queue
  -> shared queue pump evaluates runtime instances
  -> runtime instance selected
  -> runtime provider dispatches run
  -> runtime instance local queue receives work
  -> worker executes step
```

The provider model allows the dispatch step to vary.

For example:

- local provider dispatches in-process;
- HTTP provider dispatches over HTTP;
- future gRPC provider dispatches over gRPC;
- future message-bus provider publishes a command.

The control-plane logic should remain conceptually the same.

---

## Dispatch Decisions

Dispatch decisions should be recorded.

The Decision Ledger can record:

- run queued;
- dispatch evaluated;
- runtime instance selected;
- runtime instance skipped;
- capacity unavailable;
- dispatch accepted;
- dispatch failed;
- dispatch retried direction;
- run assigned;
- run rejected;
- queue pressure detected.

This is important because distributed execution must be explainable.

---

# 7. Runtime Instance Registry

The runtime instance registry is the visibility layer for execution capacity.

It can expose:

- RuntimeInstanceId;
- heartbeat;
- health status;
- worker count;
- active workers;
- available workers;
- max concurrent runs;
- local queue depth;
- local queue capacity;
- assigned runs;
- last activity;
- failure status direction.

The control plane uses this information to make dispatch decisions.

MCP and dashboard use it for inspection and diagnostics.

---

## Registry and Provider Relationship

The registry tells the control plane what exists.

The provider tells the control plane how to communicate with it.

```text
Runtime Instance Registry = who is available?
Runtime Provider          = how do I send work there?
Transport                 = how does the message travel?
```

This separation is important.

A runtime instance can be visible in the registry, but the provider may still fail to communicate with it.

That failure should be visible through diagnostics and ledger events.

---

# 8. Transport Abstraction

Transport is the communication mechanism.

The platform should support transport abstraction so that future communication methods can be added without rewriting the runtime core.

Possible transports:

- in-process/local;
- HTTP;
- future gRPC;
- future message bus;
- future NATS;
- future RabbitMQ;
- future Kafka-style event stream;
- future cloud queue;
- future internal managed transport.

The first transport does not need to be the final transport.

The architecture should keep transport replaceable.

---

## Transport Responsibilities

A transport can be responsible for:

- sending commands;
- receiving responses;
- propagating correlation IDs;
- handling timeouts;
- surfacing errors;
- carrying diagnostics;
- supporting cancellation direction;
- supporting health checks;
- supporting backpressure direction;
- integrating with observability.

Transport should not decide runtime semantics.

The runtime and control plane decide semantics.

Transport moves commands and responses safely.

---

## Transport-Agnostic Runtime Semantics

The runtime should preserve the same semantics regardless of transport.

These should not change when moving from local to HTTP or future gRPC:

- run identity;
- execution identity;
- step lifecycle;
- claim behavior;
- retry behavior;
- cancellation behavior;
- finalization behavior;
- decision ledger events;
- replay evidence;
- correlation identifiers;
- retention decisions.

This is the purpose of transport abstraction.

---

# 9. Future gRPC Direction

gRPC can be a future transport direction.

Potential advantages:

- strongly typed contracts;
- efficient service-to-service communication;
- streaming support direction;
- good fit for internal runtime communication;
- clear API contracts;
- Kubernetes-friendly service communication.

Potential use cases:

- control plane to runtime instance dispatch;
- runtime instance status streaming;
- worker diagnostics streaming;
- cancellation propagation;
- execution event streaming direction.

gRPC should be treated as a future transport option, not a required dependency today.

The architecture should allow it later.

---

# 10. Future Message Bus Direction

A message bus transport may be useful for some deployments.

Possible options include:

- RabbitMQ;
- NATS;
- Kafka-style event transport;
- cloud queue services.

Potential advantages:

- decoupled communication;
- buffering;
- asynchronous dispatch;
- event-driven runtime coordination;
- resilience under load;
- distributed delivery semantics.

Potential risks:

- duplicate delivery;
- ordering complexity;
- idempotency requirements;
- harder cancellation semantics;
- more complex operational setup.

Because the runtime already cares about deterministic execution, claim safety, and idempotency, a message-bus transport can be added carefully later.

It should not be rushed.

---

# 11. Redis Is Coordination, Not Transport Lock-In

Redis is important in the platform.

It can support:

- hot state;
- atomic coordination;
- claims;
- shared queue direction;
- concurrency gates;
- runtime coordination;
- admission decisions;
- throttling decisions.

But Redis should not be confused with the entire transport model.

Redis can coordinate state and queues.

Runtime provider/transport abstractions decide how execution commands are delivered to runtime instances.

This separation is important.

```text
Redis = coordination / hot state / queue / atomic decisions
Transport = communication between control plane and runtime instances
Runtime Provider = abstraction over execution delivery
```

This gives the platform flexibility.

---

# 12. MCP and Runtime Providers

MCP is the structured control interface.

MCP can call the control plane.

The control plane can use runtime providers.

Runtime providers can communicate with runtime instances.

The flow can be:

```text
MCP Tool Call
  -> Control Plane
      -> Policy / Admission
      -> Shared Queue
      -> Runtime Provider
      -> Runtime Instance
```

MCP does not need to know whether the runtime instance is local, HTTP, or future gRPC.

MCP should remain above the provider layer.

This keeps the control interface stable.

---

# 13. Dashboard and Runtime Providers

The dashboard should also remain provider-aware but not provider-dependent.

The dashboard can show:

- runtime instances;
- provider type;
- transport type;
- health;
- queue depth;
- worker capacity;
- dispatch status;
- diagnostics;
- last error;
- correlation links.

But the dashboard should not hardcode only one provider type.

This allows the UI to support local, HTTP, future gRPC, and managed hosting modes.

---

# 14. Provider-Based Error Handling

Provider failures must be explicit.

Possible provider errors:

- runtime instance unavailable;
- HTTP timeout;
- dispatch rejected;
- capacity unavailable;
- invalid run payload;
- runtime instance not registered;
- local queue full;
- cancellation failed;
- diagnostics unavailable;
- provider misconfigured;
- transport unavailable.

Errors should be structured.

They should be visible through:

- MCP responses;
- dashboard diagnostics;
- decision ledger events;
- metrics;
- traces;
- logs.

This is critical for production operations.

---

# 15. Provider-Based Observability

Provider operations should emit observability.

Signals can include:

- dispatch attempts;
- dispatch success;
- dispatch failure;
- provider latency;
- transport latency;
- runtime instance response time;
- queue wait time;
- local queue pressure;
- provider error rate;
- cancellation propagation result;
- runtime health check result.

These metrics are important for distributed execution and managed hosting.

---

# 16. Provider-Based Security Direction

Provider communication must eventually be secured.

Future hardening can include:

- authenticated runtime instances;
- authorized dispatch;
- signed runtime instance registration direction;
- tenant-aware dispatch policies;
- encrypted transport;
- mTLS direction;
- token-based provider authentication direction;
- access-controlled MCP operations;
- audit of dispatch and diagnostics access;
- redaction of sensitive payloads.

Provider communication can carry sensitive execution metadata.

It must be treated as part of the security model.

---

# 17. Provider-Based Managed Hosting

Managed hosting depends on provider-based runtime communication.

A managed service may need to support:

- local dev provider;
- remote runtime provider;
- dedicated customer runtime provider;
- Kubernetes runtime provider direction;
- tenant-specific runtime instance pools;
- reserved capacity;
- provider-specific health checks;
- provider-specific metrics;
- provider-specific diagnostics.

The provider model is what makes this possible.

---

# 18. Provider-Based Multi-Tenant Readiness

In a multi-tenant runtime, provider dispatch may depend on tenant context.

Examples:

- tenant A can use shared runtime instances;
- tenant B requires dedicated runtime instances;
- tenant C requires region-specific runtime instances;
- tenant D requires a specific provider/model policy;
- tenant E requires longer retention and audit evidence.

The provider layer should carry enough context for the policy engine and control plane to make safe decisions.

---

# 19. Provider and Transport Testing

Provider and transport behavior must be tested.

Important tests include:

- local provider dispatch;
- HTTP provider dispatch;
- runtime-instance-only mode;
- control-plane with runtime instances;
- dispatch failure;
- runtime instance unavailable;
- local queue full;
- cancellation propagation;
- no double dispatch;
- multi-worker execution;
- replay after remote dispatch;
- ledger events after dispatch;
- correlation propagation.

This is essential because provider-based runtime hosting is infrastructure.

Infrastructure must be tested under failure and concurrency.

---

# Current Foundation Summary

| Area | Status |
|---|---|
| Provider-based runtime hosting | Foundation exists |
| Dynamic runtime provider direction | Foundation exists |
| Local runtime provider direction | Foundation exists |
| HTTP runtime provider direction | Foundation exists |
| Runtime-instance-only mode | Foundation exists |
| Control-plane with runtime instances | Foundation exists |
| Shared queue dispatch | Foundation exists / active direction |
| Shared queue pump | Foundation exists / active direction |
| Local queue per runtime instance | Foundation exists |
| Runtime instance registry | Foundation exists / active direction |
| Multiple runtime instances | Foundation exists / active direction |
| Multiple workers | Foundation exists |
| MCP control-plane integration | Foundation exists |
| Run/execution separation | Foundation exists |
| Admission and dispatch direction | Foundation exists / active direction |
| Capacity-aware dispatch | Foundation exists / active direction |
| Decision ledger integration | Foundation exists |
| Replay/audit integration | Foundation exists |
| Observability integration | Foundation exists |
| Transport abstraction beyond HTTP | Future extension direction |
| gRPC transport | Future extension direction |
| Message bus transport | Future extension direction |
| Provider security hardening | Planned hardening direction |

---

# Productization Roadmap

## Current Delivery Status

| Capability | Status |
|---|---|
| Local provider | Implemented |
| HTTP provider | Implemented |
| gRPC provider | Implemented |
| Process Runtime Host Manager | Implemented |
| Kubernetes Runtime Host Manager | Implemented |
| Process-host Runtime Pool Manager | Implemented |
| Stable HTTP Runtime Pool router | Implemented |
| Stable gRPC Runtime Pool router | Implemented |
| Exact Runtime Pool failure recovery | Implemented |
| Kubernetes Runtime Pool Pod | Planned |
| Message-bus transport | Planned |

## Next Provider and Transport Work

1. add the new Kubernetes Runtime Pool mode;
2. map Pod UID to immutable `HostId`;
3. support Pod-wide failure suppression;
4. persist distributed route, failure, safety, and claim authority;
5. add hierarchical capacity selection;
6. validate Redis Cluster key-slot and failover behavior;
7. continue transport diagnostics and gateway hardening.

