# Public SDK protocol foundation

The external SDKs share one transport-neutral client model over the existing MCP public SDK tools.

The canonical operation names are:

- `sdk.publish_pipeline`
- `sdk.execution.submit`
- `sdk.execution.observe`
- `sdk.execution.result`
- `sdk.execution.cancel`

`ai-sdk-protocol-v1.json` is the language-neutral protocol manifest used to keep the .NET, TypeScript and Python packages aligned.

## Transport ownership

The SDK transport owns physical connection details and authentication injection. Credentials are not part of publication, execution or cancellation payloads. The public request models therefore remain portable and tenant identity continues to come from the authenticated server context.

## Serialization

Public documents use JSON, camel-case property names, string enum values and explicit `schemaVersion` fields. Binary publication material remains Base64 text in the fields already defined by the public contracts. Unsupported schema versions fail closed; there is no implicit downgrade. Optional contract fields are omitted when absent. Explicit JSON payload objects may still contain JSON `null` values; a top-level optional execution input of `null` is treated as absent consistently by all SDKs.

## Retry safety

Automatic transport retry is allowed only for the read-only observation and result operations. Publication, submission and cancellation are not automatically retried by the SDK transport. Existing server idempotency and runtime authorities remain the only business-operation authorities.

## Cancellation

Cancelling an in-flight SDK call cancels only that client transport call. It does not cancel a durable execution. Durable execution cancellation is explicit through `sdk.execution.cancel`.

## Errors

Each language SDK normalizes transport and remote failures to the same client error kinds. This error model is an SDK client surface, not a replacement for runtime result/failure contracts and not a new server execution authority.

## Package roots

The package roots are stable from this point forward:

```text
implementations/
├── sdk/protocol/
├── dotnet/src/Multiplexed.AI.Sdk/
├── node/sdk/
└── python/sdk/
```

Language-specific clients are built inside these roots without moving the protocol, authentication, error or transport boundaries.
