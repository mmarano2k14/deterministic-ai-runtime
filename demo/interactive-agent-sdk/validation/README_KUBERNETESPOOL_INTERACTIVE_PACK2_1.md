# KubernetesPool Interactive Validation - Pack 2.1

## Scope

This increment corrects the first live Pack 2 failure without changing Kubernetes lifecycle, scheduler, shared queue, admission, recovery, durable invocation, DAG transition, or result-acceptance authority.

## Root cause

The historical Kubernetes external-SDK profile publishes a flat Python worker scenario and therefore does not opt into Child DAG composition. The interactive-agent pipeline contains `ExecuteChildDagStep`. During `sdk.publish_pipeline`, DI attempted to activate that step while `IAiChildExecutionRelationStore` was not registered.

The existing runtime already owns the required registration behind `AiChildDagComposition:Enabled`.

## Correction

Interactive mode now sets both:

```text
AiChildDagComposition:Enabled=true
AiKubernetesRuntimePoolHost:ChildEnvironmentVariables:AiChildDagComposition__Enabled=true
```

The first enables the existing durable Child DAG services on the control plane. The second projects the same feature into RuntimeInstanceOnly children inside the Runtime Pool Pod.

No new relation store, child scheduler, continuation authority, persistence mechanism, or Kubernetes-specific Child DAG implementation is introduced.

## Diagnostics

Early consumer failure before scale-out is now reported directly. If no dedicated interactive client log exists, the runner prints retained control-plane stdout/stderr tails instead of pointing only to a nonexistent diagnostic file.

## Validation

Run:

```powershell
python .\demo\interactive-agent-sdk\validation\verify_kubernetespool_interactive_launcher.py
python .\demo\interactive-agent-sdk\validation\test_kubernetespool_attestation.py
python .\demo\interactive-agent-sdk\validation\test_replay_readiness.py

python .\demo\interactive-agent-sdk\validation\launch_kubernetespool_matrix.py 2>&1 |
  Tee-Object -FilePath ".\kubernetespool-interactive-pack2-1.log"
```

Do not mark this increment GREEN until the live Kubernetes run succeeds.
