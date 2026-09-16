# Hosted Worker Isolation

**Status:** Implemented and validated for the selected Linux/amd64 OCI provider boundary.

## Purpose

Hosted worker isolation provides a physical execution boundary for published custom code without creating another scheduler, recovery system, journal, queue, or DAG engine.

The existing hosted-worker supervisor remains authoritative for logical invocation assignment and bounded capacity. The isolated provider is only the physical mechanism used to execute one already-authorized assignment.

```text
AiWorkerInvocationSupervisor
        |
        | one IAiWorkerInvocationTransport
        v
AiWorkerInvocationTransportRouter
        |
        +-- HostRuntime ----------> trusted process provider
        |
        +-- OciImage ------------> OCI sandboxed-container provider
```

Provider selection is derived from the immutable execution/environment descriptor. There is no availability fallback from `SandboxedContainer` to `TrustedProcess`.

## Selected isolated target

```text
Artifact:       OciImage
Isolation:      SandboxedContainer
Network:        DenyAll
Paths:          SealedClosure
Platform:       Linux / amd64
User:           explicit non-root uid:gid
Image:          exact repository@sha256:<OCI manifest digest>
```

The trusted-process provider remains available for explicitly approved trusted execution. It must not report that it satisfies sandbox requirements it cannot enforce.

## Image identity versus tenant material

The OCI image and the tenant publication are separate immutable identities.

```text
controlled worker image
    +
immutable published code
    +
immutable deterministic dependency bundle
    +
frozen invocation input
```

The image provides the controlled worker/runtime environment. A publication does not require building one Docker image per tenant function. Published Python/TypeScript/.NET source or assembly material and supported dependency bundles remain stored and pinned through the existing publication/environment model.

The isolated provider does not host-mount tenant code and does not install packages at runtime. Tenant request/code/dependency bytes are released to the worker only after the running container has passed applied-state attestation.

The following identities remain distinct:

- canonical environment-document SHA-256;
- deterministic package/bundle identities;
- approved host-runtime executable digest;
- OCI image-manifest digest.

## Admission and launch

The isolated provider accepts only the selected finite contract. Admission rejects misleading or unsupported combinations, including mutable image tags, root users, incompatible platform/architecture, trusted-process isolation modes, host networking, and weaker path protection.

The server owns:

- the container-engine executable and approved launch roots;
- the image repository;
- the immutable image digest selected through the publication descriptor;
- the numeric non-root user;
- CPU, memory, PID, and writable-workspace bounds;
- the physical owner scope used for restart cleanup.

The launch requests include the exact digest reference and no implicit pull. The selected profile uses a read-only root filesystem, no host code/dependency mount, no network, dropped capabilities, `no-new-privileges`, PID-1 init/reaping, bounded `/tmp`, and bounded CPU/memory/PID resources.

## Applied-state attestation gate

Correct launch arguments are not treated as proof that isolation was applied.

The transport starts the container, inspects the running state, validates the applied state, and only then writes the tenant invocation request.

```text
immutable execution requirements
        |
        v
pre-launch admission
        |
        v
container start
        |
        v
container-engine inspect
        |
        v
applied-state attestation
        |
        +-- mismatch --> cleanup / quarantine path; no tenant bytes released
        |
        v
send pinned invocation material
```

Attestation verifies the selected boundary, including:

- exact image reference;
- exact non-root numeric user;
- server-owned managed-worker and owner-scope labels;
- `NetworkMode=none`;
- read-only root filesystem;
- non-privileged execution;
- automatic cleanup behavior;
- init/reaping enabled;
- exact memory and matching memory+swap bound;
- exact CPU quota;
- exact PID limit;
- no host bind or unexpected persistent mount;
- no added capabilities and `CapDrop=ALL`;
- `no-new-privileges`;
- only the permitted bounded `/tmp` tmpfs with restrictive options.

A weaker applied state fails closed.

## Lifecycle, cleanup, and quarantine

The isolated provider owns only physical lifecycle concerns:

- container start;
- liveness of the attached execution path;
- cancellation propagation;
- container termination;
- descendant containment through the container boundary;
- cleanup and cleanup verification.

The provider does not decide retry, resume, `Park`, successor selection, publication selection, recovery, or result acceptance.

An attached container-engine client exiting unexpectedly is not sufficient proof that the container stopped. On interrupted paths the provider still performs direct force-removal. If cleanup cannot be confirmed, `AiWorkerProcessCleanupException` flows through the existing supervisor and quarantines the existing shared capacity slot.

No competing capacity scheduler is introduced.

## Restart orphan reconciliation

Every isolated profile carries a server-owned `ContainerOwnerScope`. Launched containers receive managed-worker and owner-scope labels.

The owner scope must be:

1. stable across restart of the same physical runtime host;
2. unique among live runtime hosts sharing the same container engine.

Before the first isolated launch for an engine/scope pair in one transport lifetime, the provider enumerates matching managed containers and force-removes stale instances. Enumeration failure, unexpected identity, or unconfirmed removal fails closed through the existing cleanup/quarantine path.

The owner scope is physical infrastructure identity only. It must not encode tenant, execution, publication, or operation identity.

## Preserved durable authority

Changing the physical provider does not change:

- logical operation identity;
- publication/run pinning;
- RBAC and restored execution ownership;
- durable invocation journal identity;
- assignment lease and epoch;
- accepted-result rules;
- DAG transitions and recovery;
- Child DAG completion/continuation;
- existing shared queue/submission behavior.

The provider returns worker data. The existing journal decides whether that result is authoritative, and the existing DAG path decides how it is applied.

## Tenant and execution relationship

A sandbox is a bounded physical execution environment for an assignment, not a permanent tenant runtime. Tenant identity remains part of the durable invocation/publication context, but the physical container is not the durable tenant authority.

This avoids treating a long-lived per-tenant container as hidden execution state and reduces cross-invocation residue such as `/tmp`, process descendants, and in-memory data.

## Kubernetes boundary

The current isolated provider is an OCI/container-engine implementation for the selected Linux/amd64 target. It is not Docker-in-Docker logic for `KubernetesPool`, and it does not claim that current Kubernetes runtime Pods automatically provide the same hosted-code sandbox.

A future Kubernetes physical provider can materialize the same logical isolation requirement as an ephemeral worker Pod using Kubernetes-native security/resource/network controls. That provider must remain behind the same hosted-worker boundary and preserve the same journal, lease/epoch, result-acceptance, DAG, and recovery authorities.

The implemented public SDK boundary remains provider-agnostic: portable publication/execution contracts describe the required work while server infrastructure decides how hosted isolation is physically supplied. Language-specific SDK clients must preserve that separation.

## Validation boundary

The selected provider has two distinct proof layers:

- deterministic/provider tests proving runtime logic, admission, attestation, failure handling, cleanup/quarantine, reconciliation, and durable-authority preservation;
- explicit real Docker/Linux tests proving selected kernel/cgroup-visible isolation behavior.

These layers must not be conflated. See [Hosted Worker Isolation Validation](hosted-worker-isolation-validation.md).

## Related documentation

- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [Deterministic Dependency Packaging](deterministic-dependency-packaging.md)
- [Testing Strategy](testing-strategy.md)
- [Architecture Overview](architecture-overview.md)
