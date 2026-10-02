# Multiplexed AI Python SDK

The Python SDK is an independent client for the portable public execution boundary. It does not import runtime, control-plane, persistence, worker, queue, lease, epoch or journal implementation packages.

The public client supports:

- pipeline publication;
- execution submission;
- execution observation and Watch;
- terminal result retrieval;
- cooperative durable cancellation;
- pause and resume control;
- human or external input submission;
- deterministic replay validation for an existing execution.

The physical transport uses the existing Streamable HTTP MCP boundary. Authentication is transport-owned and is not serialized into publication or execution payloads.

Only safe read operations (observation, Watch and result retrieval) are eligible for bounded automatic transport retry. Publication, submission, cancellation, pause, resume, input submission and replay are never automatically retried by the SDK transport.

Cancelling the Python task waiting for an SDK call cancels only that in-flight client call. Durable execution cancellation is performed explicitly with `cancel_execution(...)`. Pause/resume and input submission delegate to the existing durable execution-control authority. `replay_execution(...)` validates deterministic replay of an existing execution and does not create a new execution.

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
