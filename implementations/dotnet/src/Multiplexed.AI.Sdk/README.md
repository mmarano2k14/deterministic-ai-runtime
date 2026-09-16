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

The client exposes typed operations for pipeline publication, execution submission, execution observation, terminal result retrieval and cooperative cancellation.

Automatic transport retry is restricted to observation and result retrieval. Publication, submission and cancellation are never automatically retried by the SDK transport.

Cancelling a .NET `CancellationToken` cancels only the in-flight SDK call. Durable execution cancellation requires `CancelExecutionAsync`.
