# Hosted Worker Isolation Validation

**Evidence date:** 2026-09-19  
**Selected target:** Linux/amd64 OCI `SandboxedContainer` provider through a Docker-compatible engine.

## Purpose

Isolation validation deliberately separates runtime/provider correctness from real container/kernel enforcement and from the wider public-SDK runtime matrix.

A green simulated or probe-driven test proves that the runtime asks for, inspects, accepts, rejects, cleans up, and quarantines the right states. It does not prove that a real container engine and Linux kernel actually enforce those states.

A green real-engine test proves the selected physical boundary for the exercised image and host. It does not by itself prove scheduler, publication, recovery, or SDK integration.

The current closure therefore uses three complementary layers:

1. deterministic provider/isolation tests;
2. explicit real Docker/Linux tests;
3. the fixture-free 37/37 public-SDK runtime matrix.

## Deterministic provider/isolation suite

The deterministic/provider layer validates runtime-owned behavior such as:

- finite isolation contract and admission refusal;
- immutable OCI digest requirements;
- trusted-process versus isolated-provider routing;
- no `SandboxedContainer -> TrustedProcess` fallback;
- exact launch-plan generation and `--pull=never`;
- production worker runtime identity arguments in the OCI launch plan;
- launch -> inspect -> attest -> request ordering;
- fail-closed behavior for weaker applied state;
- cancellation during startup/inspection;
- engine-client loss and direct cleanup behavior;
- cleanup failure -> existing shared capacity quarantine;
- stable owner-scope labels and restart orphan reconciliation;
- preservation of the existing journal/result-acceptance path;
- preservation of logical operation identity and assignment epoch semantics;
- `PythonWheelBundle`, `NodeLockedBundle`, and `DotNetAssemblyClosure` metadata crossing the isolated transport unchanged;
- lifecycle/durability behavior around cancellation, failure, lease expiry, redispatch, late-result rejection, and accepted-result replay.

Many of these tests use a build-owned controlled container-engine probe. That is intentional: races and negative engine states can be reproduced deterministically without depending on a particular local Docker daemon.

The exact deterministic test count is not used as the live closure claim in this document. The live closure claims below are tied to the explicitly recorded real-engine and matrix results.

## Real Docker/Linux enforcement and worker-execution suite

Final recorded result:

```text
total:     4
passed:    4
failed:    0
skipped:   0
```

The tests are explicitly opt-in and belong to category:

```text
ContainerRealEngine
```

They require an accessible Docker-compatible engine and an exact digest-pinned test image prepared by the repository helper.

### Real applied-boundary proof

`Real_Engine_Applies_The_Selected_Isolation_Boundary` starts a real Linux/amd64 container with the selected profile, inspects it through the engine, then probes the running container itself.

It verifies:

- exact image is locally available before launch;
- applied state passes the same runtime attestation used by the provider;
- runtime UID equals the configured non-root UID;
- effective Linux capability mask is zero;
- `/proc/self/status` reports `NoNewPrivs=1`;
- writing to the root filesystem fails;
- writing to `/tmp` succeeds;
- an outbound HTTP attempt fails;
- only the loopback network interface is visible;
- cgroup memory limit equals the configured value;
- cgroup PID limit equals the configured value;
- cgroup CPU quota/period corresponds to the configured millicores.

This proves materially more than checking command-line flags. The test observes the effective boundary from inside the running container.

### Real production-worker transport proof

`Real_Transport_Executes_The_Production_Python_Worker_In_The_Exact_Isolated_Image` executes the production Python hosted worker through `AiContainerWorkerTransport` inside the exact OCI image.

The proof includes:

```text
exact repository@sha256:<digest>
    -> isolated sibling container
    -> production Python hosted worker
    -> runtime identity arguments
    -> hosted invocation
    -> heartbeat
    -> result value = 42
    -> deterministic cleanup
```

This closes the gap between "the engine can start an isolated container" and "the runtime transport can execute the real hosted-worker protocol inside that container."

### Real active-cancellation proof

`Real_Cancellation_Stops_The_Active_Production_Worker_And_Removes_Its_Container` starts a long-running hosted invocation in the production Python worker, observes active execution/heartbeat, cancels the invocation, and verifies removal of the managed container.

This proves real cancellation propagation and physical cleanup while hosted work is active.

It does not move durable cancellation authority into the provider. The existing control/journal/DAG path remains authoritative.

### Real descendant-containment proof

`Force_Removal_Contains_And_Removes_A_Running_Descendant_Process` starts a container whose shell creates a long-running child process.

The test confirms the descendant is alive inside the container, force-removes the container, and then confirms the container no longer exists. The runtime does not attempt to reconstruct and kill an arbitrary tenant process tree itself; descendant lifecycle remains contained by the container boundary.

## Image preparation

The repository contains the real-engine test image definition and preparation helpers:

```text
implementations/dotnet/Tests/ContainerImages/HostedWorkerIsolation/Dockerfile
implementations/dotnet/Tests/ContainerImages/HostedWorkerIsolation/Prepare-RealEngineTestImage.ps1
implementations/dotnet/Tests/ContainerImages/HostedWorkerIsolation/Prepare-RealEngineTestImage.cmd
```

The image now includes the production Python hosted worker used by the real transport test. Probe-oriented tests may override the entrypoint to inspect the selected kernel/cgroup boundary; that does not replace the production-worker execution proof.

The preparation helper performs mutable acquisition before runtime validation: it builds the image, publishes it through a loopback test registry to obtain an exact OCI manifest digest, preloads the exact digest reference locally, resolves the required runtime identity, and can then execute the real-engine target.

On Windows hosts where repository PowerShell scripts are blocked by execution policy, the CMD launcher uses `-ExecutionPolicy Bypass` only for the child PowerShell process and does not modify user or machine policy.

Example:

```cmd
.\implementations\dotnet\Tests\ContainerImages\HostedWorkerIsolation\Prepare-RealEngineTestImage.cmd -RunTests
```

Manual real-engine run after the environment is prepared:

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "Category=ContainerRealEngine"
```

A green run in which these tests are skipped is not real-engine evidence.

## Public-SDK matrix closure

The isolation provider is also exercised by the fixture-free public-SDK Docker matrix.

Final recorded verifier result:

```text
37/37 scenarios
topology=docker
providers=ProcessHostPool+ContainerIsolationProvider
```

The added isolation-specific scenarios are:

```text
worker-isolation-provider
    -> trusted-process
    -> sandboxed-container

isolation-artifact-selection
    -> HostRuntime
    -> OciImage
```

The verifier also records:

```text
OCI closure profile: sibling worker containers through the host Docker socket; no Docker-in-Docker and no Kubernetes claim.
```

This matrix layer proves provider/artifact selection through the real public SDK/runtime path. It does not replace the kernel-level real-engine assertions above.

## What the three layers mean

| Evidence layer | What it proves |
|---|---|
| Deterministic provider/isolation tests | The runtime selects, validates, refuses, starts, inspects, gates tenant material, cleans up, quarantines, reconciles, and preserves durable authority correctly under the tested scenarios. |
| 4 real Docker/Linux tests | The selected real container engine/Linux boundary applies key non-root, capability, filesystem, network, cgroup, and descendant-containment properties; the exact image executes the production Python hosted worker; active cancellation removes the managed container. |
| 37/37 public-SDK Docker matrix | The external SDK/runtime path remains fixture-free and closes trusted-process vs sandboxed-container plus `HostRuntime` vs `OciImage` selection without changing existing durable authorities. |

These layers are complementary and must not be collapsed into one generic "container isolation passed" claim.

## Bounded claims

The final evidence supports the selected provider/platform boundary only.

It does not certify:

- every Docker-compatible or OCI engine;
- every host operating system, kernel, cgroup mode, or container runtime;
- a Kubernetes ephemeral sandbox-Pod provider;
- `KubernetesPool` parity for the 37-scenario public-SDK matrix;
- universal escape resistance against arbitrary hostile code;
- real-engine TypeScript or .NET hosted-worker execution inside OCI isolation unless separately evidenced;
- all 33 pre-existing `ProcessHostPool` scenarios under `ContainerIsolationProvider`;
- database failover or every runtime-host crash schedule;
- arbitrary external side effects.

The provider remains a physical execution mechanism. Scheduler, publication pinning, durable invocation identity, lease/epoch authority, result acceptance, DAG transitions, cancellation finalization, and recovery remain owned by the existing runtime/control-plane layers.

## Related documentation

- [Hosted Worker Isolation](hosted-worker-isolation.md)
- [Multilanguage Runtime Matrix Validation](multilanguage-runtime-matrix-validation.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [Deterministic Dependency Packaging](deterministic-dependency-packaging.md)
- [Testing Strategy](testing-strategy.md)
