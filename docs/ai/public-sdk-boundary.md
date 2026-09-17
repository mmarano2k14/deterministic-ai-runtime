# Public SDK Boundary

**Status:** Implemented public contract and server-adapter boundary. Independent .NET, TypeScript/JavaScript, and Python SDK libraries are now implemented on top of this boundary; CLI packaging remains separate.

## Purpose

The public SDK boundary gives external applications a stable, portable way to describe publications, submit executions, observe state, retrieve terminal results, and request cancellation without referencing runtime engine DLLs or private control-plane contracts.

The boundary is deliberately split into two layers:

```text
External application / language SDK
        |
        v
Multiplexed.AI.Sdk.Contracts
        |
        | portable wire models
        v
Authorized public server boundary
        |
        | explicit mapping
        v
Existing publication / queue / execution / control services
        |
        v
Runtime internals
```

The public layer does not become a scheduler, recovery authority, worker authority, or durable state store.

## Independent contract assembly

`Multiplexed.AI.Sdk.Contracts` is the public contract assembly.

Its dependency firewall keeps the public wire surface independent from runtime implementation assemblies:

- no project reference to `Multiplexed.Abstractions`, `Multiplexed.AI`, RBAC, Redis, MongoDB, Kubernetes, gRPC, or other engine/infrastructure assemblies;
- no package reference is required by the contract project;
- the public CLR surface is limited to portable BCL types;
- runtime ownership types are not part of the public contract.

The public contracts are organized by domain:

```text
Multiplexed.AI.Sdk.Contracts/
|-- Boundary/
|-- Common/
|-- Pipelines/
|-- Publication/
|-- Executions/
|-- Observation/
`-- Control/
```

## Portable wire model

The contract is designed for cross-language wire compatibility rather than CLR object reuse.

Important rules include:

- configuration and input payloads use JSON values rather than engine `object` graphs;
- source/dependency file payloads use explicit Base64 wire content rather than public `byte[]` contracts;
- public enums serialize as stable string values;
- schema versions are explicit;
- publication identity is represented by stable public references and hashes;
- server exceptions, stack traces, runtime implementation types, and reflection types are not public wire contracts.

## Publication and pipeline contracts

The public publication model can describe:

- pipeline identity and version;
- native, custom, and MCP invocation declarations;
- language defaults and local execution-language overrides;
- structured step configuration/input;
- nested published call-site identity through definition paths;
- deterministic dependency-package descriptors;
- source and dependency uploads.

Publication remains authoritative on the server. The SDK contract describes desired work; the server validates, compiles, hashes, stores, and pins the accepted publication.

## Execution contract

The public execution surface intentionally uses one durable external execution identity:

```text
publicationRef
      |
      v
submit + optional idempotencyKey
      |
      v
executionId
      |
      +--> observe
      +--> result
      `--> cancel
```

The public contract does not expose private execution ownership or placement identities such as:

- `SharedRunId`;
- `LocalRunId`;
- `RuntimeInstanceId`;
- `WorkerId`;
- claim tokens;
- leases;
- epochs;
- queue partitions;
- preferred-runtime ownership.

Those remain server implementation details.

## Submission idempotency

`idempotencyKey` is a public submission key, not a lease, epoch, worker token, or execution authority.

The server maps it to the existing immutable published-run authority. Compatible repeated submissions converge through the existing run pin; a conflicting publication/input under the same key is rejected rather than silently replacing the original run.

The public boundary uses the existing shared submission path and does not introduce another queue or scheduler.

## Observation and result projection

Observation exposes a public projection of execution and step state. It does not expose worker placement, claims, journal internals, or private control-plane state.

Terminal results use sanitized public failure/result contracts. Internal exceptions and worker diagnostics are not serialized as public implementation objects.

## Cancellation semantics

Cancellation is a request against an existing execution, not proof that the execution is already terminal.

A successful cancellation request may therefore return an execution that is still `Running` while the existing runtime observes and applies the request. The runtime remains authoritative over the eventual terminal `Cancelled` state.

## Server boundary

The server adapter lives outside the contract assembly and explicitly maps public models to existing runtime services.

The implemented public operations are:

```text
sdk.publish_pipeline
sdk.execution.submit
sdk.execution.observe
sdk.execution.result
sdk.execution.cancel
```

The server boundary reuses existing authorities rather than duplicating them:

- immutable publication and compilation;
- immutable run pinning;
- shared queue submission;
- execution state persistence;
- authorization and ownership checks;
- execution cancellation;
- DAG scheduling/recovery/finalization.

Submission uses the existing queue-first/shared-controller route rather than bypassing the runtime's distributed execution path.

## Tenant and authorization boundary

Tenant ownership is resolved and enforced on the server from the authorized request context. Tenant and server-placement identities are not client-selected ownership authorities in the portable execution contracts.

Read, result, submit, cancellation, and publication operations remain capability/RBAC protected by the existing server boundary.

## Relationship to hosted workers

The public SDK boundary is provider-agnostic.

It describes publication and execution requirements, while the server decides whether hosted code executes through an explicitly trusted process or through an available isolated provider. A future Kubernetes hosted-code sandbox provider can materialize the same server requirement without changing the portable SDK contract.

## Relationship to durable MCP effects

The public SDK boundary does not become retry authority for uncertain external effects.

Outbound MCP durable evidence remains a server concern. `Completed`, `NotSent`, `Dispatching`, and `Uncertain` evidence is interpreted by the existing durable-effect layer. Client convenience APIs must not turn uncertainty into blind business-call re-emission.

## What is not delivered by this boundary

This document describes the contract/server boundary itself. The external .NET, TypeScript/JavaScript, and Python clients are implemented as a separate layer on top of it and are documented in [External SDK Libraries](external-sdk-libraries.md).

Still separate from this boundary:

- standalone CLI packaging;
- public NuGet/npm/Python-registry publication;
- broader HTTP/Gateway productization where desired;
- replay/ledger/forensics client surfaces beyond the implemented publication/execution boundary;
- generated API documentation and long-term compatibility/deprecation policy;
- Kubernetes-native hosted sandbox-Pod materialization.

The external clients remain thin wrappers over the stable portable contracts and do not introduce engine-DLL dependencies or runtime execution authority.

## Implementation references

- `implementations/dotnet/src/Multiplexed.AI.Sdk.Contracts/`
- `implementations/dotnet/src/Multiplexed.AI.McpServer/PublicSdk/`

## Related documentation

- [Public SDK Boundary Validation](public-sdk-boundary-validation.md)
- [External SDK Libraries](external-sdk-libraries.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Worker Isolation](hosted-worker-isolation.md)
- [Durable MCP Effect Evidence](durable-mcp-effect-evidence.md)
- [MCP Server Control Plane](mcp-server-control-plane.md)
- [Architecture Overview](architecture-overview.md)
