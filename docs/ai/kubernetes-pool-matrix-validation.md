# KubernetesPool Matrix Validation

**Status:** GREEN - all three KubernetesPool branch scenarios accepted by the closure verifier.

**Documentation update:** 2026-09-21. Validation is based on the recorded successful target-environment runner output. This documentation update does not claim another live execution.

## Purpose and evidence boundary

This document records the bounded KubernetesPool closure: live HTTP command routing, hierarchical runtime/Pod failure recovery, and external Python SDK publication/execution through the public MCP boundary.

The final invocation executed the external SDK scenario and revalidated previously collected routing and recovery evidence. It did not rerun every historical workload. The preserved Docker baseline remains **37/37** scenarios; the combined record is **40 validated scenarios across two topologies: 37 Docker and three KubernetesPool scenarios**. It is not a homogeneous `40/40` topology matrix.

The historical HTTP/gRPC Runtime Pool production profiles and semantic adversarial matrix remain separate proof domains. Their workload counts, logs, hashes, and failure schedules are not replaced or added to this 40-scenario record. See [Runtime Pool Production Validation](runtime-pool-production-validation.md), [Adversarial Runtime Validation Matrix](adversarial-runtime-validation-matrix.md), and [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md).

## Runtime hosting and worker execution are separate dimensions

| Dimension | Values used by the evidence model | Responsibility |
|---|---|---|
| `topology` | `docker`, `kubernetes` | Deployment/execution environment of the scenario. |
| `runtimeProvider` | `ProcessHostPool`, `KubernetesPool` | Runtime-host lifecycle, membership, and capacity. |
| `workerExecutionProvider` | `TrustedProcess`, `ContainerIsolationProvider` | Physical hosted-function execution boundary. |
| Publication artifact | `HostRuntime`, `OciImage` | Pinned environment/artifact used for hosted execution. |

The Docker baseline uses `runtimeProvider=ProcessHostPool`, with the documented `TrustedProcess` and bounded `ContainerIsolationProvider` cases. The Kubernetes external-SDK scenario uses `runtimeProvider=KubernetesPool`, `workerExecutionProvider=TrustedProcess`, and the `HostRuntime` publication artifact.

`KubernetesPool` is not an additional `IAiWorkerInvocationTransport` and does not replace HTTP/gRPC runtime command dispatch. An OCI image containing the Runtime Pool host and production workers is not, by itself, proof of a sandboxed hosted-function container. The validated Python function executes as a trusted worker process inside the Runtime Pool Pod. No Docker socket or Docker-in-Docker worker path is used for this scenario.

## Accepted scenario evidence

| Scenario ID | Boundary | Recorded result |
|---|---|---|
| `feature-runtime-provider-kubernetes-pool-http-command-routing` | Real Kubernetes Pod/Service, in-Pod runtime identities, routed HTTP commands, and cleanup acceptance. | Three runtime instances; three routed commands; deletion `ACCEPTED`. Recorded image: `multiplexed-ai-runtime:k8s-debug-143`. |
| `feature-runtime-provider-kubernetes-pool-hierarchical-recovery` | Existing EventDriven full-failure canary, not a new matrix-owned recovery implementation. | Two runtime-process failures; two Pod failures; eight recovered shared runs; eight ownership transitions; zero violations; parent replay `54/54`; zero lost runs; zero duplicate durable dispatches; warm reuse `PASS`. |
| `feature-runtime-provider-kubernetes-pool-external-sdk-python-worker` | External Python SDK -> public MCP -> published function -> KubernetesPool -> production Python worker -> public result. | `Completed`; uploaded-function marker verified; one Pod, one ready Pod, one Service. Recorded image: `multiplexed-ai-runtime:matrix-sdk`. |

The routing and recovery evidence reuse the existing `HttpKubernetesPoolMcpCommandScenarioTests` and `HttpKubernetesRuntimePoolFullFailureProductionScenarioTests` paths. The recovery runner selects `Http_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario`.

Recorded final output:

```text
3/3 KubernetesPool branch scenarios passed.
Preserved validated baseline: 37/37 Docker scenarios; runtimeProvider=ProcessHostPool.
KubernetesPool closure: live routing + hierarchical recovery + external SDK publication/execution.
Cross-topology validated coverage record: 40 scenarios (37 Docker + 3 Kubernetes).
This is not a homogeneous 40-scenario topology matrix.
GREEN - KUBERNETESPOOL MATRIX CLOSURE PASSED
```

The different image names are part of the evidence scope. This closure does not establish that all three Kubernetes workloads ran on one identical image build.

## External SDK execution proof

```text
External Python SDK client
    -> MCP Streamable HTTP public SDK boundary
    -> immutable publication and pinned environment
    -> QueueFirst submission
    -> existing shared queue / admission / scale-out watcher
    -> existing KubernetesPool Pod and Service lifecycle
    -> RuntimeInstanceOnly child runtime
    -> durable Custom/python invocation adapter
    -> production Python TrustedProcess hosted worker
    -> existing journal / accepted result / DAG continuation
    -> public terminal result Completed
    -> uploaded-function marker VERIFIED
```

A successful Pod probe, accepted queue submission, or successful finalization operation is not sufficient. The client must obtain `Completed`, and the public result must contain the marker embedded in the uploaded function. A finalized execution whose status is `Failed` fails this scenario.

Recorded run summary, combining the catalog and verifier output rather than defining a new evidence schema:

```text
client=python
worker=python
runtimeProvider=KubernetesPool
workerExecutionProvider=TrustedProcess
publicationEnvironment=matrix-kubernetes-python
runtimeVersion=3.12.14
artifact=HostRuntime
terminalStatus=Completed
uploadedFunctionMarker=VERIFIED
pods=1
readyPods=1
services=1
authority=control-plane-reconciliation/runtime-worker-polling
```

The observed Python version is an exact environment identity for this run, not a requirement to hard-code that version in future builds. The runner probes the image and projects the matching reference, version, platform, executable path, and executable SHA-256 into the publication/runtime profiles.

## Host-role and bootstrap contract

The selected local profile runs the external client and control plane on Windows and the Runtime Pool in Minikube. The control plane uses a publication-only environment entry; it does not register or poll a local Windows worker for the Linux Python environment.

| `AiHostedInvocation` setting | Control plane | In-Pod runtime child |
|---|---|---|
| `Enabled` | `true` | `true` |
| `EnableLocalWorkerProfiles` | `false` | `true` |
| `EnableWorkerPolling` | `false` | `true` |
| `EnableDagReconciliation` | `true` | `false` |
| `PublicationOnlyRuntimes` | Advertises the exact remote Python environment. | Execution uses the matching local production worker profile. |

An enabled hosted-invocation host registers `AddAiDurableInvocationDag()` independently of `EnableDagReconciliation`. Runtime children need the Python, TypeScript, and .NET invocation adapters even when the background reconciliation service is disabled. `AddAiDurableInvocationDagReconciliation(...)` remains conditional. Registering adapters does not move reconciliation back into every worker host.

### Snapshot TTL

The matrix bootstrap sets `AiMatrixHarness:ExecutionContextTtlSeconds=3600` on the context before persistence and snapshot creation. The same positive value reaches `AiKubernetesRuntimePoolInPod:SnapshotTtlSeconds` through the existing snapshot/argument path. The manifest exposes `executionContextTtlSeconds`, which the runner validates before starting the external SDK client.

This snapshot property is distinct from a storage expiration or session idle timeout. A context-store expiration does not initialize a missing serialized `TtlSeconds`. A nonpositive snapshot TTL remains invalid; the production validator is not bypassed.

### RBAC project alignment

The generated profile explicitly aligns three values:

```text
AiMatrixHarness:Project=matrix
Multiplexed.Rbac.Core:Project=matrix
AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:Multiplexed.Rbac.Core__Project=matrix
```

`ExecutionContextSnapshot.Project` is ownership data; it does not configure the host's TRN builder. The child must receive the host-owned RBAC project setting as well as the restored context. The runner refuses a mismatch before starting the control plane.

The alignment does not add permissions, grant administrator access, bypass publication authorization, or relax immutable execution-binding checks. It ensures that the same authorized scope is evaluated on both sides of the host boundary.

### Replay-safe payload storage and placement

The profile uses `AiEngine:PayloadStore:Provider=mongo-redis` with replay-safe payloads required. The local control plane and Minikube children use addresses appropriate to their network location while retaining the same durable store/database authority. The child configuration projects `AiEngine__PayloadStore__Provider=mongo-redis` and the existing `host.minikube.internal` MongoDB/Redis endpoints.

Submission remains `QueueFirst`. The existing shared dispatcher requests scale-out when capacity is absent; the existing watcher creates KubernetesPool capacity; normal queue dispatch resumes after capacity becomes selectable. The matrix diagnostic endpoints observe this path rather than creating a second placement authority.

## Runtime image contract

Two image workflows must not be confused:

| Workload | Source authority | Recorded image |
|---|---|---|
| Historical Kubernetes routing/recovery profile | `KubernetesSdkScenarioConstants.RuntimeImage`; host Dockerfile under `implementations/dotnet/src/Multiplexed.AI.McpServer.Host/`. | Routing evidence records `multiplexed-ai-runtime:k8s-debug-143`. |
| External SDK + production hosted workers | `RuntimeImage` parameter of `run-kubernetes-pool-sdk-execution.ps1`; `implementations/matrix/runtime/kubernetes/runtime-pool.Dockerfile`. | `multiplexed-ai-runtime:matrix-sdk`. |

The SDK image includes the MCP host and production .NET, TypeScript, and Python workers. Inclusion of all three workers is not evidence that all three external worker languages were executed in Kubernetes by this scenario.

`runtime_image_probe.py` resolves canonical executable paths inside the image before constructing launch profiles. A symlink alias such as `python3` must not be advertised as an approved launch path when the production path validator requires the real binary. The same probed executable supplies the version/hash used for publication.

The local tagged-image workflow loads the image with `minikube image load <image> --overwrite` and retains `ImagePullPolicy=Never`. This is the workflow evidenced by the recorded successful run. An image rebuild is necessary when host/worker code or files baked into the image change. A generated-profile-only change does not itself require rebuilding those image contents.

### Optional immutable image selection

`AiKubernetesRuntimePoolHost` supports `RuntimeImageRepository` plus `RuntimeImageDigest`, resolved as `repository@sha256:<64-hex>`, and `RequireImmutableRuntimeImage`. Partial repository/digest configuration, ambiguous image authorities, invalid digests, and mutable references under immutable enforcement fail closed.

The execution sidecar accepts `-RuntimeImageRepository`, `-RuntimeImageDigest`, and `-RequireImmutableImage`. The SDK runner instead accepts the complete reference through `-RuntimeImage` together with `-RequireImmutableImage`; it skips its local build for an exact reference. That image must already be available to the local Docker engine for probing and loadable into the target cluster.

The successful tagged-image run is not a live strict-digest validation result. Synthetic strict-image verifier tests and configuration support must remain distinct from an actually executed digest-pinned scenario.

## Run the target-environment scenario

Use the repository root. Prerequisites are the .NET build toolchain, Python, Docker, `kubectl`, Minikube, the `ai-runtime` namespace, the existing Gateway configuration, and reachable Redis/MongoDB endpoints. The historical test image is also required when routing or recovery evidence must be regenerated. See [Local Kubernetes and Minikube Environment](kubernetes-local-environment.md) for the existing environment contract.

Build/load the SDK Runtime Pool image and run the real scenario:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-sdk-execution.ps1
```

Reuse an image that already contains the current host/worker changes:

```powershell
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-sdk-execution.ps1 `
  -SkipImageBuild
```

`-SkipImageBuild` skips only the SDK image build. The runner still loads/probes the image, publishes the local control-plane host, generates and validates the profile, and runs the external client. It is not a request to reuse old SDK execution evidence.

When evidence is absent or intentionally being regenerated, the existing sidecars can be invoked explicitly:

```powershell
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-execution.ps1
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-recovery.ps1
```

Missing lightweight routing evidence is refreshed automatically by the SDK runner using the historical profile. Missing recovery evidence does not trigger an implicit recovery campaign: the SDK execution can run, but final cross-topology closure is left pending until the recovery evidence exists. Do not delete valid evidence merely to rerun the SDK scenario.

Expected bootstrap and client signals include:

```text
RBAC project aligned: context=matrix; control-plane=matrix; runtime-child=matrix
Execution context snapshot TTL=3600s
Publication runtime ready.
Scale-out watcher ready.
PUBLISH succeeded
SUBMIT succeeded
OBSERVE terminal ... status=Completed
RESULT status=Completed
MARKER verified
GREEN - KUBERNETESPOOL EXTERNAL SDK EXECUTION PASSED
GREEN - KUBERNETESPOOL MATRIX CLOSURE PASSED
```

A one-test build/profile-generation summary is not an aggregate result for all bootstrap, adapter, RBAC, or runtime regression tests.

## Evidence files and offline closure verification

The default evidence directory is:

```text
implementations/matrix/evidence/kubernetes/
  feature-runtime-provider-kubernetes-pool-http-command-routing.json
  feature-runtime-provider-kubernetes-pool-hierarchical-recovery.json
  feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.json
  feature-runtime-provider-kubernetes-pool-external-sdk-python-worker.kubernetes.json
```

The SDK verifier also uses `.matrix-kubernetes-sdk/profile.json`. Retain the profile together with its evidence when preserving an auditable run. The closure plan is `implementations/matrix/runtime/kubernetes/kubernetes-pool-closure-v1.json`.

With the default files retained, verify the accumulated evidence without starting a new workload:

```powershell
python .\implementations\matrix\runtime\kubernetes\closure_verifier.py
```

The verifier delegates to the three Kubernetes evidence verifiers and checks that the canonical Docker plan still contains exactly 37 unique scenarios. That check preserves the previously executed Docker baseline; it is not a fresh Docker execution. Revalidation of a retained recovery document is likewise not a new fault-injection campaign.

## Failure diagnostics

The SDK runner retains the local control-plane stdout/stderr and the external SDK stage log from process start. On failure it attempts to collect current-pool Pod descriptions, all still-available Pod logs, Service state, scale-out state, and selected runtime-artifact fingerprints before cleanup.

```text
.matrix-kubernetes-sdk/
  profile.json
  runtime-manifest.json
  runtime-image-identity.json
  runtime-artifacts.local.json
  control-plane.stdout.log
  control-plane.stderr.log
  external-sdk-client.log
  diagnostics/
    <pool-id>/
    <pool-id>-<timestamp>.zip
```

Console output is only a tail. Diagnostics are scoped to the current pool annotation, not stale Pods from unrelated tests. Collection failures are warnings and must not replace the original execution error. Capture the resulting bundle before another run overwrites top-level logs/profile files; keep local diagnostics and credentials out of source commits and public releases.

`runtime_artifact_probe.py` compares SHA-256 for the same selected files in the local image and the running Pod. A match covers only those files, not the whole image. A different Docker/Kubernetes image identifier alone does not establish a cache problem; inspect the actual Pod, arguments, and artifact comparison.

| Symptom | Boundary to inspect |
|---|---|
| `kubernetes-runtime-pool-host-readiness-timeout` | Pod state and complete bootstrap logs; positive `SnapshotTtlSeconds`; canonical executable paths; configured image and store endpoints. The timeout is a final observation, not a diagnosis. |
| `Custom/python` adapter absent; native fallback forbidden | `AddAiDurableInvocationDag()` must run independently of background DAG reconciliation. Do not enable reconciliation in every runtime as a workaround. |
| Active RBAC context does not authorize the publication operation | Context project and both host-owned TRN builders; exact permission scope and pinned execution ownership. Do not bypass authorization. |
| SDK observation timeout after a successful scale-out | Follow shared dispatch, local enqueue, execution creation, step claim, journal/worker activity, and terminal state in the complete control-plane and Pod logs. An empty later queue cycle does not prove that dispatch never happened. |
| Finalization succeeds with execution status `Failed` | Finalization persisted an execution failure. Inspect the step error; this does not satisfy the SDK completion/marker gate. |

## Preserved authorities and non-claims

KubernetesPool retains the existing Kubernetes SDK Pod/Service lifecycle, in-Pod membership, Gateway routing, capacity, replacement, and cleanup authorities. Scheduler, queue, execution ownership, immutable publication/run pinning, durable journal leases/epochs, result acceptance, DAG transitions, recovery, and finalization are not replaced by matrix code.

This closure does not establish:

- the full .NET/TypeScript/Python client-by-worker cross-product on Kubernetes; the external-SDK case is Python-to-Python;
- Kubernetes `ContainerIsolationProvider` execution or an ephemeral sandbox-Pod provider; `TrustedProcess` remains a trusted-execution boundary;
- all 37 Docker scenarios on Kubernetes, or all Docker baseline scenarios under OCI worker isolation;
- a live strict-digest result from tagged runtime images, identical image builds across every scenario, or whole-image identity from selected-file hashes;
- control-plane crash recovery/leader election, multi-control-plane durable ownership, Redis/MongoDB failover, multi-node fault-domain coverage, production cluster packaging, or generic exactly-once external effects from this evidence;
- a fresh execution of every historical test, or a repository commit, push, merge, release, or package publication.

## Implementation and related references

- [Kubernetes matrix runners, plans, and verifiers](../../implementations/matrix/runtime/kubernetes/)
- [SDK Runtime Pool Dockerfile](../../implementations/matrix/runtime/kubernetes/runtime-pool.Dockerfile)
- [External SDK matrix profile](../../implementations/dotnet/Tests/Multiplexed.AI.McpServer.Tests.Integration/Scenarios/Production/Providers/Http/KubernetesPool/HttpKubernetesPoolExternalSdkMatrixProfileTests.cs)
- [Hosted invocation registration](../../implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/HostedInvocationHostRegistration.cs)
- [Matrix context bootstrap](../../implementations/dotnet/src/Multiplexed.AI.McpServer.Host/Bootstrap/MatrixHarnessBootstrapHostedService.cs)
- [Kubernetes Runtime Host Provider](kubernetes-runtime-host-provider.md)
- [Local Kubernetes and Minikube Environment](kubernetes-local-environment.md)
- [Runtime Pool Architecture](runtime-pool-architecture.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Worker Isolation Validation](hosted-worker-isolation-validation.md)
- [External SDK Quickstart](external-sdk-quickstart.md)
