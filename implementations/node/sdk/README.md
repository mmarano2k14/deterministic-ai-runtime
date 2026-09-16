# @multiplexed/ai-sdk

External TypeScript client for the portable Multiplexed AI SDK boundary.

The package exposes the same five public operations as the .NET SDK:

- `publishPipeline`
- `submitExecution`
- `observeExecution`
- `getExecutionResult`
- `cancelExecution`

The client serializes only portable SDK contracts. It has no dependency on runtime, control-plane, persistence, worker, lease, epoch, queue or journal implementation types.

## Runtime compatibility

The package targets standard ES2022 APIs and requires Node.js 20 or newer because the pinned MCP client transport requires Node.js `>=20`. There is no artificial Node 22/24 restriction and the SDK does not rely on Node experimental TypeScript execution flags.

## Installation

```bash
npm install @multiplexed/ai-sdk
```

## Example

```ts
import {
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
} from "@multiplexed/ai-sdk";

const transport = new AiSdkMcpHttpTransport(
  new URL("https://runtime.example/mcp"),
  {
    credentialProvider: new AiSdkStaticCredentialProvider({
      scheme: "Bearer",
      value: process.env.MULTIPLEXED_TOKEN!,
    }),
  },
);

const client = new AiSdkClient(transport);
const observation = await client.observeExecution("execution-id");
console.log(observation.status);
```

## Retry behavior

Automatic transport retry is limited to the read-only observation and result operations. Publication, submission and cancellation are never automatically retried by this SDK. Server-side idempotency and runtime execution authorities remain authoritative.

## Cancellation

An `AbortSignal` cancels only the in-flight SDK request. It does not cancel the durable execution. Durable execution cancellation is explicit through `cancelExecution`.

## Authentication

Credentials are supplied by the transport and never serialized into publication, execution or cancellation business payloads.
