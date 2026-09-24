# .NET SDK Access-Context Bootstrap and Interactive Demo Authentication

## Scope

Connect the external .NET SDK consumer to the standalone JWT/access-context host boundary without matrix infrastructure or manually pre-seeded RBAC context handles.

## Public SDK Changes

- Added `AiSdkAccessContextBootstrapOptions`.
- Added `AiSdkAccessContextBootstrapResult`.
- Added `AiSdkAccessContextBootstrapper`.
- Added explicit authenticated `POST` support for creating the first access-context handle.
- Normalized bootstrap authentication, authorization, transport, timeout, remote-failure, and invalid-response errors through existing SDK error types.
- Prohibited automatic retry of the state-changing bootstrap request.
- Added support for caller-provided `HttpClient` instances for proxy/mTLS/testing scenarios.
- Added targeted bootstrap tests.
- Updated the .NET SDK README.

## Demo Changes

- The .NET interactive agent now requires a JWT bearer token.
- If `AI_RUNTIME_ACCESS_CONTEXT` is absent, the consumer automatically calls the protected access-context endpoint.
- The returned handle initializes the existing MCP transport.
- Subsequent access-context rotation remains owned by the already validated transport rotation mechanism.
- Added `AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT`.
- Updated launcher validation for the standalone authenticated .NET slice.
- Added a local-development HS256 JWT issuer utility implemented with the Python standard library only.
- Updated demo documentation and environment examples.

## Security Boundary

The bootstrap client never supplies tenant, project, namespace, or TRN capabilities.

The protected host endpoint derives those values from the validated JWT.

The local token utility is development tooling only. It creates a cryptographically signed JWT that is validated by the normal standalone `JwtBearer` host. It does not install or invoke a fake authentication handler.

Production consumers must obtain the bearer token from their trusted identity provider and must never receive the issuer signing key.

## Local Demo Capabilities

The local token grants only the capabilities required by the current interactive agent:

```text
code/publication/publish
code/publication/read
code/publication/execute
shared-run/execution/submit
execution/control/read
execution/control/cancel
execution/control/pause
execution/control/resume
execution/control/input
replay/execution/run
```

TRNs are emitted using the configured project and namespace.

## Compatibility

No MCP tool schema changes.

No runtime scheduler, recovery, replay, execution, publication, Child DAG, persistence, or RBAC-engine changes.

No matrix dependency.

TypeScript and Python demo consumers remain scaffold-only.

## Validation

### SDK unit tests

```powershell
dotnet test `
  .\implementations\dotnet\Tests\Multiplexed.AI.Sdk.Tests\Multiplexed.AI.Sdk.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~AiSdkAccessContextBootstrapperTests|FullyQualifiedName~AiSdkAccessContextRotationTests" `
  --logger "console;verbosity=minimal" `
  --nologo
```

### Demo package/build smoke

```powershell
.\demo\interactive-agent-sdk\scripts\bootstrap.cmd
```

### Target E2E

With the standalone JWT host running:

```powershell
$env:AI_RUNTIME_TOKEN = python .\demo\interactive-agent-sdk\scripts\create-local-jwt.py
$env:AI_RUNTIME_ENDPOINT="http://localhost:8081/mcp"
$env:OPENAI_MODEL="gpt-5.4"

.\demo\interactive-agent-sdk\run.cmd
```

The complete E2E remains pending target execution.
