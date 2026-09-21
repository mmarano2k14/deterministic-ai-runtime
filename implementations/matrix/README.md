# Multilanguage Runtime Matrix

This matrix validates the independently consumable .NET, TypeScript and Python SDKs through the same public MCP boundary and the existing runtime authorities.

## Core matrix

The core matrix contains nine live combinations:

```text
3 external client languages x 3 hosted execution languages
```

Each scenario must prove publication, submission, observation, terminal result and public execution identity. Evidence is written only after the real SDK call path completes.

## Supported topologies

### Docker / ProcessHostPool - canonical reproducible validation

This is the canonical third-party path. The only host prerequisite is Docker with Compose support. .NET, Node, Python, MongoDB, Redis and the runtime are all supplied by containers or container build stages.

From any directory inside the repository on Windows PowerShell:

```powershell
& (Join-Path (git rev-parse --show-toplevel) "implementations\matrix\runtime\docker\run-matrix.ps1")
```

The runner performs a clean build/run, waits for the terminal verifier, prints the client/verifier exit states, reports the core and currently bound feature-verifier results, and writes the complete Docker output to `matrix-full.log` at the repository root. It deliberately does not use Compose `--abort-on-container-exit` / `--exit-code-from`, because the three SDK clients are expected one-shot services and their successful exit must not terminate the runtime before the verifier runs.

Cleanup including persisted test data:

```cmd
docker compose -f .\implementations\matrix\runtime\docker\docker-compose.yml down -v --remove-orphans
```

The stack runs real MongoDB and Redis services, the real MCP runtime host, three external SDK client containers, the real hosted .NET, TypeScript and Python process workers, and the bounded Python sibling-container worker used by the OCI isolation scenarios. Client containers have no engine/runtime project reference and communicate with the runtime only over MCP HTTP.

### Local / ProcessHostPool - developer topology

The local topology exercises the same scenarios and SDK clients but uses local MongoDB, Redis, runtime, .NET, Node and Python installations.

PowerShell orchestration:

```powershell
.\implementations\matrix\runtime\local\run.ps1
```

If MongoDB and Redis are already running on their default local ports:

```powershell
.\implementations\matrix\runtime\local\run.ps1 -InfrastructureAlreadyRunning
```

The local orchestrator publishes the real runtime and .NET worker, stages the same public sample artifacts and production hosted-worker layout used by Docker, starts the runtime with exact installed runtime identities, waits for the matrix manifest, executes the 3 x 3 core matrix, and then executes the eighteen Python-driven feature scenarios plus the three native external-client dependency-firewall checks. Cancellation remains exercised independently by all three native SDK client containers. The OCI isolation scenarios are canonical-Docker-only because their local/CI evidence depends on the bounded host Docker-socket profile.

## Runtime manifest

No environment reference is typed manually. The running host computes the exact executable/loader identities, registers the three approved publication environments and writes a runtime manifest containing:

```text
endpoint
access-context handle
a harness bearer token
exact .NET environment ref
exact TypeScript environment ref
exact Python environment ref
exact container environment refs when OCI isolation is configured
topology
provider
```

The manifest is harness-local evidence and is not a new public runtime API.

## Authentication and RBAC

The matrix keeps the normal MCP authentication/context middleware in the path. An explicit harness-only static bearer scheme and an isolated RBAC context are enabled only when `AiMatrixHarness:Enabled=true`.

Context rotation is disabled only in this harness because the current public SDK transport carries an access-context handle but does not yet negotiate rotated handles across MCP exchanges. This limitation is recorded rather than hidden.

## Provider semantics

Provider evidence is split into two independent dimensions so runtime hosting is not conflated with hosted-worker execution:

```text
runtimeProvider
  ProcessHostPool
  KubernetesPool        # supported runtime topology; separate live matrix closure

workerExecutionProvider
  TrustedProcess
  ContainerIsolationProvider
```

The validated Docker baseline currently uses `runtimeProvider=ProcessHostPool`. Its hosted-worker paths are:

```text
HostRuntime -> TrustedProcess -> ProcessHostPool
OciImage    -> SandboxedContainer -> sibling worker container
```

The OCI path uses the host Docker socket from the runtime container to launch sibling worker containers. It does not use Docker-in-Docker and it is not evidence of production Kubernetes isolation. The exact worker image is prepared as `repository@sha256:<manifest-digest>` and preloaded before the matrix starts; runtime execution uses `--pull=never`. `KubernetesPool` remains the existing runtime-hosting provider and is validated separately rather than being introduced as another `IAiWorkerInvocationTransport`.

### KubernetesPool live execution sidecar

KubernetesPool live execution is validated through the engine's existing Kubernetes SDK Runtime Pool scenario rather than through a second matrix-owned Pod lifecycle. The sidecar runner at `implementations/matrix/runtime/kubernetes/run-kubernetes-pool-execution.ps1` invokes the existing `HttpKubernetesPoolMcpCommandScenarioTests` path, which creates a real Runtime Pool Pod, waits for readiness, routes one command to each planned in-Pod runtime identity through the stable Service endpoint, and requests cleanup through the same Kubernetes SDK lifecycle client.

The sidecar writes machine-readable evidence for `runtimeProvider=KubernetesPool` and verifies it independently from the canonical Docker `37/37` closure. This increment therefore does **not** change the `37/37` executed-coverage total. The live Kubernetes scenario must pass on the target Minikube/Kubernetes environment before a KubernetesPool GREEN claim is made.

When an exact runtime image is already available to the cluster, the runner can supply `RuntimeImageRepository` plus `RuntimeImageDigest` and enable immutable-image verification. Without those parameters, the historical Kubernetes integration-test image remains available for compatibility; the engine path is the same in either case.

### KubernetesPool lifecycle/recovery sidecar

Lifecycle and recovery evidence reuses the existing `HttpKubernetesRuntimePoolFullFailureProductionScenarioTests.Http_KubernetesPool_EventDriven_Canary_Should_Reuse_The_Same_FullFailure_Scenario` production canary. The runner at `implementations/matrix/runtime/kubernetes/run-kubernetes-pool-recovery.ps1` does not create a second recovery harness. It only requests a machine-readable evidence document while the existing engine proves an exact in-Pod runtime-process failure, a distinct busy-Pod failure, replacement capacity, recovery forensics, runtime ownership convergence, parent replay, recursive Child DAG terminal convergence, ledger/trace/lifecycle consistency, and warm-pool reuse.

The recovery sidecar is verified by `recovery_verifier.py` against `kubernetes-pool-recovery-v1.json`. Like the live-execution sidecar, it preserves the canonical Docker `37/37` baseline until final KubernetesPool matrix closure. The target-environment recovery run must succeed before its evidence can participate in final closure.

### KubernetesPool external SDK publication and execution

The final Kubernetes sidecar closes the public-boundary gap rather than adding another Kubernetes engine. `run-kubernetes-pool-sdk-execution.ps1` builds or consumes a Runtime Pool OCI image that contains the existing MCP host plus the production hosted .NET, TypeScript and Python workers, loads that image into Minikube, composes the existing production `KubernetesPool` settings, and starts the real control-plane host.

The control plane advertises an exact Linux Python publication identity through `PublicationOnlyRuntimes` while its durable worker poller is disabled. `RuntimeInstanceOnly` children inside the existing KubernetesPool Pod receive the matching production Python worker profile and own worker polling, while DAG reconciliation remains on the control plane. This separates publication authority from the physical runtime host without adding a Kubernetes-specific worker transport or a second journal/recovery authority.

The external Python SDK then publishes the reusable sample function through the public MCP boundary, submits it against the publication-only Kubernetes runtime identity, waits for terminal completion and requires the unique uploaded-function marker to be present in the public terminal result. A completed status without that marker fails the scenario. Live Kubernetes evidence additionally requires the Runtime Pool Pod, readiness, stable Service and configured Runtime Pool image to be observable for the same pool id.

The scenario is described by `kubernetes-pool-external-sdk-v1.json` and verified by `sdk_verifier.py`. The final runner expects the already-executed live-routing and recovery evidence files to remain under `implementations/matrix/evidence/kubernetes`, so it can close the branch without rerunning the long full-failure canary.

```powershell
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-sdk-execution.ps1
```

Before publication, the runner probes the local runtime image with `runtime_image_probe.py`. The helper resolves executable aliases inside that image with `readlink -e`, then uses the canonical targets for version probes, the Python executable hash and the generated child profiles. In particular, `/usr/local/bin/python3` is a discovery path, not an approved launch-path declaration. The production launch-path guard remains unchanged and continues to reject symbolic links. Node version discovery does not impose a 22/24 version allowlist.

The helper invokes Docker with argument vectors and no shell, preserving Windows PowerShell 5.1 compatibility. Probe results are retained in `.matrix-kubernetes-sdk/runtime-image-identity.json`; all probes use the same inspected local image ID. That local image ID is not treated as a Kubernetes repository manifest digest or as evidence of execution in the cluster.

Only this external-SDK profile disables immediate provider cleanup after a failed readiness check. Before the runner's existing `finally` cleanup, diagnostics capture the exact current pool's Pod descriptions, container logs, Service data and scale-out state under `.matrix-kubernetes-sdk/diagnostics/<pool-id>/`. Historical pools are excluded. These local diagnostic files can contain deployment configuration and should be reviewed before sharing. Readiness timeouts, shared-queue behavior and production cleanup defaults are unchanged.

The matrix bootstrap sets `AiMatrixHarness:ExecutionContextTtlSeconds` explicitly (default `3600`) on the seeded RBAC context before storing it. This is the snapshot TTL copied into durable run/scale-out state and then into `AiKubernetesRuntimePoolInPod:SnapshotTtlSeconds`; it does not change the RBAC context store's session idle timeout or the Kubernetes readiness timeout. A nonpositive configured value fails before the context or client manifest is created. Normal hosts remain unaffected while the harness is disabled.

The SDK manifest records `executionContextTtlSeconds`, and the Kubernetes runner checks that it is a positive integer before starting the external client. The runner continues inspecting the current pool after its first appearance: a `Failed` or `Succeeded` Pod before client completion triggers scoped diagnostics and fails the scenario without waiting solely for the generic scale-out readiness timeout. Neither a positive TTL nor a running Pod counts as execution proof; terminal `Completed` and the uploaded-function marker remain required.

A no-infrastructure C# regression exercises matrix context seeding, JSON persistence through a context-store test double, the real snapshot mapper, the real Kubernetes argument factory, configuration binding, and the unchanged in-Pod validator. It also covers explicit positive TTLs, invalid TTL rejection before seeding, disabled-harness behavior, and continued rejection of nonpositive snapshot TTLs. Run it with:

```powershell
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj -c Debug --filter "FullyQualifiedName~MatrixHarnessBootstrapHostedServiceTests" --logger "console;verbosity=minimal" --nologo
```

After changing the compiled host bootstrap, run the external-SDK runner without `-SkipImageBuild` to rebuild and load the runtime image together with the freshly published local control-plane host. Existing routing/recovery evidence is retained; no automatic rerun of the long recovery canary is introduced.


When only the runner, probe or test-profile settings change, an already-current runtime image can be reused with `-SkipImageBuild`. Changes to runtime or worker code baked into the image still require a rebuild and Minikube reload. Neither the image probe nor the structural tests replace the real uploaded-function marker check required for final closure.

Optional `-RequireImmutableImage` enforcement requires the Runtime Pool image itself to use exact `repository@sha256:<digest>` form; immutable SDK publication is always required by the normal publication path.

### KubernetesPool cross-topology closure

`kubernetes-pool-closure-v1.json` records the closure model explicitly as a preserved **37-scenario Docker baseline plus three independently executed KubernetesPool scenarios**: live command routing, hierarchical failure/recovery convergence, and external SDK publication/execution. `closure_verifier.py` delegates to each existing Kubernetes evidence verifier and checks that the canonical Docker plan still contains exactly 37 unique scenarios.

When all three Kubernetes evidence documents are valid, the coverage record is **40 executed scenarios across two topologies (37 Docker + 3 Kubernetes)**. This is deliberately **not** described as a homogeneous `40/40` topology matrix; the Docker baseline and KubernetesPool sidecars have different scopes. No final KubernetesPool closure claim is made until the external-SDK runner succeeds on the real target cluster and the closure verifier accepts the accumulated evidence.

## Bound feature scenarios

The ProcessHost matrix first binds six publication/dependency feature scenarios without widening execution authority. The Python external SDK is used as the bounded feature driver because the core matrix already proves the public SDK boundary independently for .NET, TypeScript and Python clients. Feature coverage is therefore recorded only for the combinations actually executed.

Publication pinning is exercised once against each hosted worker language. Each scenario submits an execution against one immutable publication, publishes a replacement with the same logical pipeline identity but intentionally failing code, observes the still-running execution after that replacement exists, and then requires the original execution to complete while retaining its original publication reference.

Deterministic dependency packaging is exercised once against each hosted worker language with the language-specific immutable package kind: `DotNetAssemblyClosure`, `NodeLockedBundle`, and `PythonWheelBundle`. The dependency payloads are supplied through the existing publication contract and are materialized by the existing hosted workers; no package manager or runtime package installation is introduced.

The Docker verifier preserves the original nine core scenarios and additionally requires all six bound feature evidence documents. A successful live run must therefore report both `9/9 production-like Docker ProcessHostPool scenarios passed.` and `6/6 publication-pinning/dependency-package ProcessHostPool feature scenarios passed.`. Until that live run is executed, the feature scenarios remain planned/bound evidence rather than a green claim.

The original ProcessHost coverage remains intact for the existing scenarios. Durable recovery and durable journal result acceptance continue to exercise their existing authorities, while the isolation closure adds explicit `TrustedProcess` / `SandboxedContainer` and `HostRuntime` / `OciImage` scenarios without changing scheduler, queue, recovery, journal, continuation, or result-acceptance ownership. Durable running cancellation remains exercised independently by all three external SDK clients.

### Hosted custom policy family scenarios

The production-like process matrix also binds the three hosted custom policy families currently supported by the runtime through the public Python SDK client. The scenarios are deliberately behavioral rather than publication-only:

- `concurrency` runs a Python hosted policy that explicitly allows a native `hello-world` step; custom-to-native policy fallback remains forbidden.
- `retry` runs a TypeScript hosted policy that returns `stop` against the deterministic `fail-once-then-succeed` fixture. The expected execution status is `Failed`; a silent policy drop would instead allow the normal retry path to recover.
- `delegation` runs a .NET hosted policy that returns `deny` at the existing `execution.child-dag` checkpoint. The expected execution status is `Failed`; the child is not dispatched. This policy-denial proof remains distinct from the nested Child DAG execution scenarios below.

These three scenarios reuse immutable publication pinning, the existing hosted worker transport, policy-family adapters, and the existing runtime policy checkpoints. No alternate policy engine or scheduler is introduced.

### Nested Child DAG scenarios

The production-like process matrix additionally binds three depth-two published Child DAG scenarios through the external Python SDK client, one for each hosted worker language. Each root publication contains an inline child DAG, which in turn contains an inline grandchild DAG with one custom hosted `leaf` declaration.

The custom leaf is bound through the immutable publication call site `/invoke-child/invoke-grandchild`. The grandchild default/step language is the worker language under test (`dotnet`, `typescript`, or `python`), while both parent Child DAG transitions continue to use the existing `execution.child-dag` scheduler, child-relation persistence, publication child-run binding, shared-queue continuation, and finalization authorities.

The evidence is intentionally bounded to what the live public path proves: immutable nested publication, root submission, nested child dispatch, hosted grandchild custom declaration execution, parent continuation, terminal observation, and `Completed` root result. It does not claim recovery or failure-injection coverage for nested Child DAGs; those remain separate roadmap targets.

The Docker verifier therefore requires nine core scenarios, six publication/dependency scenarios, three hosted custom-policy scenarios, and three nested Child DAG scenarios before durable MCP effect evidence is added below.

### Durable MCP effect evidence scenarios

The process matrix binds two language-free MCP effect scenarios through the external Python SDK client and the public `Mcp` invocation contract. A matrix-only loopback MCP probe is server-owned and configured through the normal outbound connection catalog; pipeline input never supplies an endpoint, credential or connection revision.

`completed-local-replay` executes a tool that records one physical `tools/call` and returns an explicit MCP tool error. The durable transport persists that confirmed remote result as `Completed`. The DAG then performs its configured logical retry for the same execution/step identity, and the durable fence must replay the stored result locally. The probe must still report exactly one physical call.

`uncertain-blocks-blind-resend` executes a tool that records one physical `tools/call` and then exceeds the server-owned invocation deadline after the dispatch boundary has been crossed. The durable evidence must become `Uncertain`. The DAG performs one configured logical retry, but the durable fence must reject that retry before a second physical emission. The probe must still report exactly one physical call.

Matrix-only diagnostics expose the durable evidence status and retained DAG retry count without exposing endpoint or credential material. These diagnostics are disabled with the matrix harness and are not public runtime APIs. The scenarios prove bounded durable effect evidence behavior only; they do not claim generic exactly-once delivery or automatic reconciliation of uncertain effects.

The Docker verifier now requires nine core scenarios, six publication/dependency scenarios, three hosted custom-policy scenarios, three nested Child DAG scenarios, and two durable MCP effect evidence scenarios. A successful live run must report all five bounded verifier summaries before the matrix runner can return GREEN. The resulting gate is 23/23 executed scenarios.

## Cancellation coverage

The Docker ProcessHostPool matrix exercises `sdk.execution.cancel` independently through the .NET, TypeScript, and Python external SDKs. Each client submits a same-language long-running hosted execution, observes the hosted step in an active state, requests durable cancellation, validates the acknowledgement metadata, and requires public observation/result convergence to `Cancelled`. Cancelling an SDK transport call does not satisfy this target.

## Recovery coverage

The ProcessHostPool matrix binds two recovery paths through the existing runtime execution recovery reconciler. The matrix-only recovery endpoint is diagnostic orchestration only: it marks runtime availability and seeds durable ownership evidence, while the production recovery reconciler, shared queue, shared-run ownership resolver, runtime execution index, execution-control service and DAG store retain mutation authority.

`in-flight-resume` starts a public native DAG execution, waits until its step is actively running, marks the owning runtime unavailable and invokes the production recovery reconciler. The failed local run must become `requeued-for-recovery`, the shared run must be requeued through recovery metadata, and the original durable `ExecutionId` must resume and converge to `Completed`. A new execution identity does not satisfy this scenario.

`local-queued-redispatch` begins from an active public template while its authoritative shared-run dispatch record is still present. The harness copies that `RunRequest`, normalizes it into true not-yet-started local-queued work by clearing the preallocated execution identity/snapshot, seeds one real shared-queue/local-run ownership with no `ExecutionId`, assigns it to an unavailable runtime and invokes the same recovery reconciler. Recovery must requeue that ownership, a healthy runtime must accept the shared run, a new durable execution identity must be created, and that replacement execution must converge to `Completed`.

These scenarios exercise recovery authorities only; they do not claim provider host restart or process-kill ownership. Physical crash/kill coverage remains a separate failure-injection concern.

## Durable journal result-acceptance coverage

Two matrix scenarios use the production Mongo-backed `IAiDurableInvocationStore` through fresh `AiDurableInvocationJournal` instances. They do not call store-specific Mongo methods and do not bypass lease/epoch/result CAS rules.

`accepted-result-replay` prepares one durable operation, acquires epoch-one lease authority, accepts one terminal result, reconstructs a fresh journal over the same durable store, and submits the identical result with the same assignment. The first completion must be `Accepted`; the replay must be `AlreadyAccepted`; the terminal result hash and pending continuation intent must remain unchanged.

`duplicate-delivery-convergence` submits eight concurrent identical result deliveries under the same live lease. Exactly one delivery must become `Accepted`, the remaining seven must converge as `AlreadyAccepted`, no delivery may cross the lease fence as `LeaseRejected`, and the durable record must retain one authoritative terminal result with pending continuation intent.

With these four scenarios, the production-like Docker gate reaches 30 runtime/behavioral scenarios before the exact coverage closure below.

## Exact executed-coverage closure

The dependency-firewall closure retains three external client dependency-firewall scenarios executed independently inside the published .NET client artifact, compiled TypeScript SDK artifact, and Python SDK source/distribution boundary. These checks do not exercise a hosted worker; they prove that the external SDK artifact remains independent from engine/runtime repository dependencies.

The isolation closure adds four executable scenarios through the external Python SDK and the production Python hosted worker:

```text
worker-isolation-provider
  trusted-process      -> matrix-python     -> ProcessHostPool
  sandboxed-container  -> matrix-python-oci -> ContainerIsolationProvider

isolation-artifact-selection
  HostRuntime -> matrix-python     -> TrustedProcess
  OciImage    -> matrix-python-oci -> SandboxedContainer
```

The OCI scenarios publish user code against the immutable `matrix-python-oci` environment. The server-owned catalog resolves that environment to an exact OCI manifest digest and the isolated container transport launches the production hosted worker as a sibling container. The published probe fails unless the isolated execution is non-root, the root filesystem is read-only, and the container has only loopback networking. Tenant publication input cannot select Docker arguments, mutable tags, mounts, network policy, resource limits, or engine configuration.

The canonical Docker verifier now requires **37 executable scenarios**: 9 core + 6 publication/dependency + 3 custom policy + 3 nested Child DAG + 2 durable MCP effect + 3 cancellation + 2 recovery + 2 journal result-acceptance + 3 external client dependency-firewall + 2 worker-isolation-provider + 2 isolation-artifact-selection scenarios. It writes `executed-coverage-closure.json` only after every required evidence document passes validation.

A successful run reports:

```text
Exact executed-coverage closure: 37/37 scenarios; topology=docker; runtimeProvider=ProcessHostPool; workerExecutionProviders=TrustedProcess+ContainerIsolationProvider.
```

The previous baseline comprised 33 executed scenarios and closed at `33/33` under ProcessHostPool. The OCI isolation closure was subsequently executed successfully at `37/37` under the Docker topology and is now the preserved baseline for the KubernetesPool matrix work. The local/CI Docker-socket profile must not be described as production Kubernetes coverage.

## Fixture-free execution artifacts

The matrix does not own worker-function fixtures. External clients publish reusable sample artifacts from `implementations/sdk/samples/published-functions`, while execution is performed by the production hosted-invocation workers under `implementations/dotnet/workers`, `implementations/node/workers`, and `implementations/python/workers`. Durable MCP effect scenarios use the standalone sample MCP server under `implementations/sdk/samples/mcp-effect-server`.


### KubernetesPool post-readiness failure diagnostics

The SDK runner retains both control-plane output streams from process startup in
`.matrix-kubernetes-sdk/control-plane.stdout.log` and `control-plane.stderr.log`.
Both streams are drained concurrently to files using .NET stream-copy tasks; the
control-plane output is no longer inherited solely by the terminal. Runner and
SDK progress remain visible. After a failure, a short control-plane tail is printed
to the console while the complete available files are retained.

Pod log capture requests all available lines (`--tail=-1`) for the exact current
pool, including previous container logs when a restart is recorded. A bounded
collection failure is reported and never replaces the original SDK error. This
cannot recover logs already rotated or removed by the cluster.

On failure, the runner captures Pod diagnostics before stopping the owning host,
then drains and closes the host output streams before creating a ZIP under
`.matrix-kubernetes-sdk/diagnostics/<pool-id>-<timestamp>.zip`. The final console
line `Failure bundle:` identifies that archive. The bundle includes SDK logs,
control-plane logs, the local image identity, scale-out state, resource state,
and available Pod evidence. Full profiles and access-context manifests are not
added to the bundle. Logs can still contain operational or sensitive details and
should be handled accordingly.

`runtime_artifact_probe.py` compares SHA-256 hashes of the same eight explicitly
named runtime/worker files in the already-probed local Docker image and the running
Pod. The result is `match`, `mismatch`, or `unavailable`, with per-file differences.
It is read-only diagnostic evidence, not a replacement execution result or a
whole-image identity assertion. Failure or missing `pods/exec` permission is
reported as `unavailable`, never as a match. Docker index/manifest IDs and CRI
configuration IDs are not directly compared.

These capture changes do not modify engine code, runtime-image contents, image
loading, publication, queue dispatch, admission, scheduling, recovery, or existing
execution deadlines. An already-current runtime image can be reused:

```powershell
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-sdk-execution.ps1 -SkipImageBuild
```

A fulfilled scale-out and a Ready Pod are not evidence that the uploaded function
completed. Final closure still requires the public `Completed` result and the
verified uploaded-function marker.


### Hosted custom DAG adapters on runtime-only hosts

Every enabled `AiHostedInvocation` host registers the existing durable DAG adapter
core through `AddAiDurableInvocationDag()`. This installs the `Custom/python`,
`Custom/typescript`, and `Custom/dotnet` factories independently of the optional
background reconciliation loop. Installing the core does not start that loop.

The Kubernetes profile retains its role split: the control plane enables durable
invocation reconciliation without local worker polling; runtime children enable
local worker profiles and worker polling with `EnableDagReconciliation=false`.
Disabling reconciliation must not remove the adapters required to resolve a
published custom DAG. The enabled/disabled capability boundary, explicit-DAG
requirement, and prohibition on native fallback remain unchanged.

Registration regressions exercise the production host bootstrap and pipeline
resolver without starting infrastructure or invoking worker executables:

```powershell
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.McpServer.Tests.Integration\Multiplexed.AI.McpServer.Tests.Integration.csproj -c Debug --filter "FullyQualifiedName~HostedInvocationHostRegistrationTests" --logger "console;verbosity=minimal" --nologo
```

The host bootstrap is compiled into the runtime image. After this change, rebuild
and load the image by running the SDK scenario without `-SkipImageBuild`:

```powershell
& .\implementations\matrix\runtime\kubernetes\run-kubernetes-pool-sdk-execution.ps1
```

A successful enqueue or Ready Pod still does not prove completion. A custom
adapter failure during `create-execution` can be present in the Pod logs while
the SDK observation expires without a terminal result. Diagnose it from the
retained run logs; do not infer successful worker execution from queue admission.
The final assertion remains `Completed` with the uploaded-function marker verified.

### Kubernetes external-SDK RBAC project alignment

The matrix profile uses one explicit project (`matrix`) for all three configuration
boundaries:

```text
Seeded execution context: AiMatrixHarness:Project
Control-plane TRN builder: Multiplexed.Rbac.Core:Project
Runtime-child TRN builder: AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:Multiplexed.Rbac.Core__Project
```

Snapshot.Project does not configure TrnBuilder. The snapshot carries execution
ownership and exact grants, while the RBAC engine constructs targets using the
host-owned `Multiplexed.Rbac.Core:Project` setting. Without the child setting, its
builder falls back to `rbac-demo`, so an existing exact grant such as
`trn:matrix:default:code:publication:execute` cannot authorize the resulting target.
The existing in-Pod command-line/environment path carries the child setting;
no context permissions are added, no worker identity is synthesized, and no RBAC
or publication guard is bypassed.

The runner validates equality of all three explicit profile values before
starting the control plane and reports `RBAC project aligned`. The parent project
is supplied by the composed profile rather than a separate command-line override.
This checks profile consistency, not live authorization or successful execution.

`HttpKubernetesPoolExternalSdkRbacTests` covers the real bootstrap context,
JSON snapshot restoration, production in-Pod command-line serialization, .NET
environment binding, RBAC registration and publication target authorization.
Missing or foreign host projects, missing execute permission, foreign tenant/group/
control-plane scope, an absent live context and unrelated capabilities remain
denied. A permitted request still fails when its immutable run binding is absent.
These isolated tests do not execute uploaded code or replace the real Kubernetes
scenario.

This adjustment changes the test profile and runner, not runtime-image code.
An image already containing the hosted DAG adapter registration can be reused with
`-SkipImageBuild`. Final success still requires the public SDK result `Completed`
and the uploaded function marker to be verified.
