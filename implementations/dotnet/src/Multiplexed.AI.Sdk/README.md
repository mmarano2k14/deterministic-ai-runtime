# Multiplexed.AI.Sdk

`Multiplexed.AI.Sdk` is the external .NET client for the portable Multiplexed AI public SDK boundary.

It depends on `Multiplexed.AI.Sdk.Contracts` for wire models and uses the MCP Streamable HTTP transport to invoke the public SDK operations. It does not reference runtime, control-plane, persistence, worker, queue, recovery, lease or epoch assemblies.

## Client

```csharp
using Multiplexed.AI.Sdk;
using Multiplexed.AI.Sdk.Authentication;
using Multiplexed.AI.Sdk.Transport;

var transport = new AiSdkMcpHttpTransport(
    new Uri("https://runtime.example/mcp"),
    new AiSdkTransportOptions
    {
        CredentialProvider = new AiSdkStaticCredentialProvider(
            new AiSdkCredential("Bearer", token))
    });

IAiSdkClient client = new AiSdkClient(transport);
```

The client exposes typed operations for pipeline publication, execution submission, execution observation and Watch, terminal result retrieval, cooperative cancellation, pause/resume control, human or external input submission, and deterministic replay validation.

Automatic transport retry is restricted to safe read operations: observation, Watch and result retrieval. Publication, submission, cancellation, pause, resume, input submission and replay are never automatically retried by the SDK transport.

Cancelling a .NET `CancellationToken` cancels only the in-flight SDK call. Durable execution cancellation requires `CancelExecutionAsync`. Pause/resume and input submission delegate to the existing durable execution-control authority. `ReplayExecutionAsync` performs deterministic replay validation for an existing execution and does not create a new execution.
