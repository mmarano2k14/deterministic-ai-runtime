# Multiplexed AI Python SDK

The Python SDK is an independent client for the portable public execution boundary. It does not import runtime, control-plane, persistence, worker, queue, lease, epoch or journal implementation packages.

The public client supports:

- pipeline publication;
- execution submission;
- execution observation;
- terminal result retrieval;
- cooperative durable cancellation.

The physical transport uses the existing Streamable HTTP MCP boundary. Authentication is transport-owned and is not serialized into publication or execution payloads.

Only observation and result retrieval are eligible for bounded automatic transport retry. Publication, submission and cancellation are never automatically retried by the SDK transport.

Cancelling the Python task waiting for an SDK call cancels only that in-flight client call. Durable execution cancellation is performed explicitly with `cancel_execution(...)`.

```python
from multiplexed_ai_sdk import (
    AiSdkClient,
    AiSdkExecutionSubmissionRequest,
    AiSdkMcpHttpTransport,
)

transport = AiSdkMcpHttpTransport("https://runtime.example/mcp")
client = AiSdkClient(transport)

response = await client.submit_execution(
    AiSdkExecutionSubmissionRequest(publication_ref="publication-ref")
)
```
