# Hosted Worker Isolation Validation

**Evidence date:** 2026-09-16  
**Selected target:** Linux/amd64 OCI `SandboxedContainer` provider through a Docker-compatible engine.

## Purpose

Isolation validation deliberately separates runtime/provider correctness from real container/kernel enforcement.

A green simulated or probe-driven test proves that the runtime asks for, inspects, accepts, rejects, cleans up, and quarantines the right states. It does not prove that a real container engine and Linux kernel actually enforce those states.

The final closure therefore uses two layers.

## Deterministic provider/isolation suite

Final recorded result:

```text
total:     86
passed:    86
failed:    0
skipped:   0
```

Representative command:

```cmd
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Invocation.Workers.Isolation&Category!=ContainerRealEngine"
```

This layer validates runtime-owned behavior such as:

- finite isolation contract and admission refusal;
- immutable OCI digest requirements;
- trusted-process versus isolated-provider routing;
- no `SandboxedContainer -> TrustedProcess` fallback;
- exact launch-plan generation and `--pull=never`;
- launch -> inspect -> attest -> request ordering;
- fail-closed behavior for weaker applied state;
- cancellation during startup/inspection;
- engine-client loss and direct cleanup behavior;
- cleanup failure -> existing shared capacity quarantine;
- stable owner-scope labels and restart orphan reconciliation;
- preservation of the existing journal/result-acceptance path;
- preservation of logical operation identity and assignment epoch semantics;
- `PythonWheelBundle`, `NodeLockedBundle`, and `DotNetAssemblyClosure` metadata crossing the isolated transport unchanged.

Many of these tests use a build-owned controlled container-engine probe. That is intentional: races and negative engine states can be reproduced deterministically without depending on a particular local Docker daemon.

## Real Docker/Linux enforcement suite

Final recorded result:

```text
total:     2
passed:    2
failed:    0
skipped:   0
```

The tests are explicitly opt-in and belong to category:

```text
ContainerRealEngine
```

They require:

```text
MULTIPLEXED_AI_TEST_CONTAINER_ENGINE=<absolute Docker-compatible engine executable>
MULTIPLEXED_AI_TEST_CONTAINER_IMAGE=<repository>@sha256:<exact OCI manifest digest>
```

The exact image must already exist locally. Worker execution does not pull it.

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

### Real descendant-containment proof

`Force_Removal_Contains_And_Removes_A_Running_Descendant_Process` starts a container whose shell creates a long-running child process.

The test confirms the descendant is alive inside the container, force-removes the container, and then confirms the container no longer exists. The runtime does not attempt to reconstruct and kill an arbitrary tenant process tree itself; descendant lifecycle remains contained by the container boundary.

## Fixture preparation

The repository contains a test-only image fixture:

```text
implementations/dotnet/Tests/ContainerImages/HostedWorkerIsolation/Dockerfile
implementations/dotnet/Tests/ContainerImages/HostedWorkerIsolation/Prepare-RealEngineTestImage.ps1
implementations/dotnet/Tests/ContainerImages/HostedWorkerIsolation/Prepare-RealEngineTestImage.cmd
```

The fixture is not a production Python, TypeScript, or .NET worker image. It exists only to provide the shell/utilities used by the kernel/cgroup probes.

The preparation helper performs mutable acquisition before runtime validation: it resolves/builds the fixture, publishes it through a loopback test registry to obtain an exact OCI manifest digest, preloads the exact digest reference locally, and then can run the real-engine target.

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

## What the two layers mean

| Evidence layer | What it proves |
|---|---|
| 86 deterministic provider/isolation tests | The runtime selects, validates, refuses, starts, inspects, gates tenant material, cleans up, quarantines, reconciles, and preserves durable authority correctly under the tested scenarios. |
| 2 real Docker/Linux tests | The selected real container engine/Linux boundary actually applies key non-root, capability, filesystem, network, cgroup, and descendant-containment properties. |

The test counts must not be interpreted as equivalent breadth. One real-engine test contains multiple independent assertions over the effective kernel/container state.

## Bounded claims

The final evidence supports the selected provider/platform boundary only.

It does not certify:

- every Docker-compatible or OCI engine;
- every host operating system, kernel, cgroup mode, or container runtime;
- a Kubernetes ephemeral sandbox-Pod provider;
- universal escape resistance against arbitrary hostile code;
- real-engine Python/TypeScript/.NET package execution inside production language-specific container images;
- database failover or every runtime-host crash schedule;
- arbitrary external side effects.

The deterministic suite proves restart orphan-reconciliation logic and failure behavior. The real-engine suite proves the selected applied boundary and descendant force-removal behavior. Those facts should remain separately stated.

## Related documentation

- [Hosted Worker Isolation](hosted-worker-isolation.md)
- [Hosted Multilanguage Execution](hosted-multilanguage-execution.md)
- [Hosted Multilanguage Validation](hosted-multilanguage-validation.md)
- [Deterministic Dependency Packaging](deterministic-dependency-packaging.md)
- [Testing Strategy](testing-strategy.md)
