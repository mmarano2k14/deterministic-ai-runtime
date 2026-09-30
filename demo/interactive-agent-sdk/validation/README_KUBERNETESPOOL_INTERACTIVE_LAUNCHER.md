# KubernetesPool Interactive SDK Launcher

This validation layer reuses the existing production KubernetesPool runner and Kubernetes lifecycle authority. It does not create a second Pod lifecycle, scheduler, shared queue, admission path, recovery authority, DAG engine, or result-acceptance path.

## Scope

The opt-in runner mode executes the existing interactive-agent public SDK scenario with all three external clients:

```text
.NET / TypeScript / Python external SDK
    -> real JWT authentication
    -> POST /auth/access-context
    -> X-Access-Context
    -> public MCP Streamable HTTP boundary
    -> existing QueueFirst / admission / KubernetesPool scale-out
    -> real Runtime Pool Pod + RuntimeInstanceOnly children
    -> server-owned OpenAI provider key
    -> interactive DAG + Child DAG + human input
    -> terminal result
    -> bounded replay-readiness handling
    -> deterministic replay validation
```

## Existing Kubernetes authority

The implementation calls:

```text
implementations/matrix/runtime/kubernetes/run-kubernetes-pool-sdk-execution.ps1
```

with `-InteractiveAgent`. The existing runner continues to own image preparation, profile composition, control-plane startup, scale-out observation, exact-pool Pod/Service inspection, failure diagnostics, and cleanup.

Historical behavior remains unchanged when `-InteractiveAgent` is omitted.

## Authentication

Interactive mode keeps the matrix diagnostics/bootstrap surface but opts the public boundary into standalone JWT authentication:

```text
AiMatrixHarness:UseStandaloneAuthentication=true
AiMcpAuthentication:Enabled=true
```

The issuer, audience, and signing key are supplied through the control-plane process environment. The real signing key is not persisted into the generated Kubernetes profile.

The JWT scope is intentionally pinned to the existing Kubernetes external-SDK profile:

```text
project=matrix
namespace=default
tenant=matrix-tenant
tenantGroup=matrix-group
```

## OpenAI key ownership

The historical Kubernetes production profile contains a non-secret placeholder because its original workload does not require a real OpenAI provider request.

Interactive mode removes the `AiKubernetesRuntimePoolHost:OpenAiApiKey` placeholder from the generated command-line settings before the control plane is started, then supplies the real value through:

```text
OPENAI_API_KEY
OpenAI__ApiKey
AiKubernetesRuntimePoolHost__OpenAiApiKey
```

The existing KubernetesPool host options project that value into the Runtime Pool Pod and child runtime environment. The external SDK consumer environment removes `OPENAI_API_KEY` and JWT signing secrets before launching a client.

Kubectl diagnostics redact the provider key if it appears in Runtime Pool command-line arguments.

## Live topology attestation

The KubernetesPool topology is not accepted from CLI labels or a profile declaration alone.

`attest_kubernetespool.py` inspects the live Kubernetes namespace and requires the exact generated pool id to have:

```text
>= 1 pool Pod
>= 1 Running + Ready pool Pod
>= 1 pool Service
>= 1 runtime instance from the pool annotation
expected Runtime Pool image
non-empty Pod and Service identities
```

The attestation authority is:

```text
kubernetes-cluster-inspection
```

The first SDK starts before attestation so that its real QueueFirst submission can trigger KubernetesPool creation. The runner polls for the exact new pool, records the live attestation when it becomes Ready, and refreshes the attestation before the second and third SDK executions.

## Validation

Static/unit checks:

```powershell
python .\demo\interactive-agent-sdk\validation\verify_kubernetespool_interactive_launcher.py
python .\demo\interactive-agent-sdk\validation\test_kubernetespool_attestation.py
python .\demo\interactive-agent-sdk\validation\test_replay_readiness.py
```

Live target-environment run:

```powershell
python .\demo\interactive-agent-sdk\validation\launch_kubernetespool_matrix.py 2>&1 |
Tee-Object -FilePath ".\kubernetespool-interactive.log"
```

Use `--skip-image-build` only if the current Minikube image already contains the required current runtime code.

Expected closure for this pack is:

```text
[kubernetes-sdk-matrix] PASS: interactive KubernetesPool/HTTP 3/3
[kubernetes-launcher] PASS: KubernetesPool/HTTP 3/3
```

No GREEN claim is made until that live run succeeds on the target environment.
