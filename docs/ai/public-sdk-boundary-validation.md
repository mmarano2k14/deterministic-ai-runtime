# Public SDK Boundary Validation

**Status:** Branch-closure validation completed for the public contract and server boundary. This evidence is scoped to that boundary; the later .NET, TypeScript/JavaScript, and Python SDK libraries have their own closure evidence in [External SDK Libraries Validation](external-sdk-libraries-validation.md).

## Validation scope

The validation boundary covers two separate concerns:

1. the public contract assembly remains portable and independent from engine implementation assemblies;
2. the server adapter maps those public contracts onto existing publication, submission, observation, result, cancellation, authorization, and run-pinning authorities without creating parallel runtime ownership.

## Contract dependency firewall

The public contract validation checks that `Multiplexed.AI.Sdk.Contracts` does not gain accidental engine/infrastructure dependencies.

The branch closure protects against:

- project references from the public contract project;
- package references in the public contract project;
- references to engine/runtime assemblies;
- public exposure of server ownership identities;
- public CLR members that use implementation-oriented types such as `object`, `byte[]`, `Exception`, or `Type` where a portable wire representation is required.

This is a structural boundary, not only a documentation convention.

## Public identity boundary

Validation keeps private placement and ownership identities out of the external contract, including:

- `SharedRunId`;
- `LocalRunId`;
- `RuntimeInstanceId`;
- `WorkerId`;
- claim/lease/epoch material;
- control-plane placement details.

The public execution handle is `executionId`; `idempotencyKey` is a submission key and does not become execution ownership.

## Server-interface boundary

The public server interface is validated to use public SDK contracts and bounded BCL types at its edge. Internal runtime models are mapped explicitly behind that edge rather than leaking into the contract assembly.

The implemented surface includes:

```text
sdk.publish_pipeline
sdk.execution.submit
sdk.execution.observe
sdk.execution.result
sdk.execution.cancel
```

## Existing-authority compatibility

The closure reuses and validates the existing authorities rather than duplicating them:

- publication compilation and immutable publication identity;
- published-run pinning/idempotency;
- authorization and ownership checks;
- queue-first shared submission;
- durable execution state;
- cancellation control.

Repeated compatible submission remains governed by the existing run pin. A conflicting use of the same submission key is not allowed to replace the pinned publication/input.

## Cancellation semantics

Wire-contract validation preserves the distinction between:

```text
cancellation request accepted
```

and:

```text
execution reached terminal Cancelled
```

The public response can therefore show that cancellation was requested while the execution status is still non-terminal.

## Validation commands

The branch closure was exercised through the public-contract tests, public server-boundary tests, existing publication pinning/authorization tests, and the solution build.

Representative commands are:

```text
dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Contracts.Tests\Multiplexed.AI.Sdk.Contracts.Tests.csproj

dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.PublicSdk"

dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Publication.AiPublicationRunPinningTests"

dotnet test .\implementations\dotnet\Tests\Multiplexed.AI.Tests\Multiplexed.AI.Tests.csproj --filter "FullyQualifiedName~Runtime.Publication.AiPublicationAuthorizationTests"

dotnet build .\implementations\dotnet\Multiplexed.sln
```

## What the evidence proves

The validation supports these bounded statements:

- a portable public contract assembly exists independently from engine DLLs;
- publication/pipeline and execution/observation/control wire models are separated from internal runtime models;
- the public server boundary maps onto existing server authorities;
- private runtime ownership identities are not part of the public execution contract;
- submission idempotency reuses immutable run-pinning authority;
- public cancellation does not pretend that request acceptance equals terminal completion;
- the public boundary does not introduce another scheduler, queue, recovery authority, or result-acceptance path.

## What the evidence does not prove

This validation does **not** prove:

- that external SDK packages have been published to public NuGet/npm/Python registries;
- that a CLI is complete;
- that every future public API is backward-compatible forever;
- that replay, ledger, forensics, dashboard, or all control-plane capabilities are already exposed through the SDK boundary;
- that Kubernetes hosted-code sandbox Pods are implemented;
- that the boundary changes the guarantees or limitations of hosted-worker isolation or durable MCP external-effect evidence.

Those remain separate capabilities and validation scopes.

## Related documentation

- [Public SDK Boundary](public-sdk-boundary.md)
- [External SDK Libraries](external-sdk-libraries.md)
- [External SDK Libraries Validation](external-sdk-libraries-validation.md)
- [Developer Experience, API, SDK, and CLI](../product-roadmap/developer-experience-api-sdk-cli.md)
- [Architecture Overview](architecture-overview.md)
- [Testing Strategy](testing-strategy.md)
