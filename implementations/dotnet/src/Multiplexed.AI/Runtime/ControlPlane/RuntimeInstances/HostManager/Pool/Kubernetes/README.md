# Kubernetes Runtime Pool

## Step 5 Status

Step 5 is complete when the focused compatibility and routing gates are green.

```text
5A  Identity and topology contract                         complete
5B  Runtime-owned Pod and bootstrap specification          complete
5C  Kubernetes SDK Pod/Service and host strategy           complete
5D  Real in-Pod Process Pool bootstrap and readiness       complete
5E  HTTP ProcessPool and KubernetesPool MCP proofs         complete
5F  gRPC ProcessPool and KubernetesPool MCP proofs         complete
5G  Compatibility gates, documentation, and closure        complete after validation
```

Pod deletion, Pod-wide capacity suppression, recovery, and replacement are not
part of Step 5. They remain Step 6.

## Compatibility Boundary

The Kubernetes Runtime Pool is additive.

```text
AiRuntimeHostCreationMode.Kubernetes = 2
    existing one RuntimeInstanceOnly process per Pod and Service
    KubernetesAiRuntimeHostCreationStrategy

AiRuntimeHostCreationMode.KubernetesPool = 4
    one Runtime Pool Manager per Pod
    several independently registered RuntimeInstanceOnly child processes
    KubernetesAiRuntimePoolHostCreationStrategy
```

The existing `Kubernetes = 2` behavior must not be redirected through the pool
strategy and must not require Runtime Pool options.

The Runtime Pool path is disabled by default and is registered only through the
dedicated opt-in dependency-injection extension.

## Identity Model

Identity is first-class and must not be collapsed into transport or Pod identity.

```text
PoolId
    logical pool ownership

PodRequestId
    one provisioning request

PodUid / HostId
    exact Kubernetes Pod ownership after creation

RuntimeInstanceId
    independent dispatchable child identity
```

One Pod owns several child runtimes:

```text
PodUid = HostId
    +-- RuntimeInstanceId A1
    +-- RuntimeInstanceId A2
    +-- RuntimeInstanceId A3
```

`HostId` is the exact Kubernetes `metadata.uid`. The Pod name is not used as an
authoritative host identity.

Every command remains targeted to one exact `RuntimeInstanceId`. A stable pool
endpoint must never silently select a healthy sibling when the requested child
identity is unavailable.

## Metadata Rule

Metadata is diagnostic duplication only.

Correctness, routing, lifecycle, membership, draining, capacity selection, and
recovery must use typed first-class fields such as:

```text
PoolId
HostId
PodUid
RuntimeInstanceId
Status
```

A later failure proof must not depend on parsing arbitrary metadata to recover
Pod membership.

## Pod Topology

```text
Kubernetes Runtime Pool Pod
    +-- parent Runtime Pool Manager
    +-- stable transport endpoint :8080
    +-- HTTP readiness endpoint   :8081
    +-- child A1 transport        :18080
    +-- child A2 transport        :18081
    +-- child A3 transport        :18082
```

For clear-text transport:

```text
HTTP pool
    stable endpoint :8080 = HTTP/1

gRPC pool
    stable endpoint :8080 = HTTP/2

readiness
    endpoint :8081 = HTTP/1
```

The stable Kubernetes Service exposes only the stable parent transport. Child
ports remain internal to the Pod and remain bound to exact child identities.

## Validated Proof Matrix

```text
HTTP ProcessPool
    one stable Kestrel endpoint
    three real external child processes
    exact command routing to all three RuntimeInstanceId values

HTTP KubernetesPool
    one real Pod
    one stable Service
    three real in-Pod child processes
    exact command routing to all three RuntimeInstanceId values

gRPC ProcessPool
    one stable HTTP/2 Kestrel endpoint
    three real external child processes
    exact command routing to all three RuntimeInstanceId values

gRPC KubernetesPool
    one real Pod
    one stable HTTP/2 Service endpoint
    one separate HTTP/1 readiness endpoint
    three real in-Pod child processes
    exact command routing to all three RuntimeInstanceId values
```

All four proofs require no-fallback behavior: the response identity must equal
the requested child identity.

## Step 5 Closure Gates

Run the focused closure script from the repository root:

```powershell
.\tools\Validate-RuntimePoolStep5.ps1
```

To validate only non-Kubernetes gates:

```powershell
.\tools\Validate-RuntimePoolStep5.ps1 -SkipKubernetes
```

The full closure requires:

```text
runtime host build                                      green
focused Runtime Pool and legacy compatibility unit gate green
HTTP and gRPC ProcessPool exact-routing proofs          green
legacy Kubernetes integration compatibility gate       green
in-Pod Kubernetes Runtime Pool readiness proof          green
HTTP and gRPC KubernetesPool exact-routing proofs       green
```

No P20, P30, or P35 scenario is part of Step 5 closure.

## Explicitly Deferred to Step 6

Step 5 does not claim proof for:

```text
Pod deletion
PodUid-wide membership enumeration after failure
atomic suppression of all children from a deleted Pod
assigned-work recovery from a failed Pod
replacement Pod creation
new child RuntimeInstanceId registration after replacement
stale-route rejection after Pod loss
```

Those invariants must be introduced and validated separately in Step 6.
