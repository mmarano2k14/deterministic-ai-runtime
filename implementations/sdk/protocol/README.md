# Public SDK protocol foundation

The external SDKs share one transport-neutral client model over the existing MCP public SDK boundary.

The canonical operation names are:

- `sdk.publish_pipeline`
- `sdk.execution.submit`
- `sdk.execution.observe`
- `sdk.execution.watch`
- `sdk.execution.result`
- `sdk.execution.cancel`
- `sdk.execution.pause`
- `sdk.execution.resume`
- `sdk.execution.input.submit`
- `sdk.execution.replay`

`ai-sdk-protocol-v1.json` is the language-neutral protocol manifest used to keep the .NET, TypeScript and Python packages aligned.

## Transport ownership

The SDK transport owns physical connection details and authentication injection. Credentials are not part of publication, execution, control, replay or input payloads. Public request models remain portable and tenant/project/namespace/user ownership continues to come from the authenticated server context and immutable execution admission.

## Serialization

Public documents use JSON, camel-case property names, string enum values and explicit `schemaVersion` fields. Binary publication material remains Base64 text in the fields already defined by the public contracts. Unsupported schema versions fail closed; there is no implicit downgrade. Optional contract fields are omitted when absent. Explicit JSON payload objects may still contain JSON `null` values; a top-level optional execution input of `null` is treated as absent consistently by all SDKs.

## Retry safety

Automatic transport retry is allowed only for safe-read operations: observation, Watch and terminal-result reads. Publication, submission, durable cancellation, pause, resume, human/external input submission and replay are never automatically retried by the SDK transport. Existing server idempotency, control-plane state, replay validation and runtime authorities remain authoritative for business effects.

## Execution control and input

Pause and resume delegate to the existing execution control-plane authority. `sdk.execution.input.submit` submits human or external input against the execution's stable waiting key. The public control state intentionally excludes internal CAS versions, runtime-instance identity and submitted payload persistence details.

Cancelling an in-flight pause, resume, input, replay or Watch transport call only cancels that client invocation. It does not implicitly cancel or roll back the durable execution.

## Replay

`sdk.execution.replay` exposes the existing deterministic replay validation operation for an existing execution. It does not create a new execution and does not expose replay-engine internals, raw ledger entries or trace timelines through the public SDK response.

## Cancellation

Cancelling an in-flight SDK call cancels only that client transport call. It does not cancel a durable execution. Durable execution cancellation is explicit through `sdk.execution.cancel`.

## Errors

Each language SDK normalizes transport and remote failures to the same client error kinds. This error model is an SDK client surface, not a replacement for runtime result/failure contracts and not a new server execution authority.

## Package roots

The package roots are stable:

```text
implementations/
├── sdk/protocol/
├── dotnet/src/Multiplexed.AI.Sdk/
├── node/sdk/
└── python/sdk/
```

Language-specific clients are built inside these roots without moving the protocol, authentication, error or transport boundaries.
