from __future__ import annotations

import sys
import unittest
from pathlib import Path

SRC_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC_ROOT))

from multiplexed_ai_sdk import (  # noqa: E402
    AI_SDK_OPERATIONS,
    AiSdkClient,
    AiSdkExecutionCancellationRequest,
    AiSdkExecutionStatus,
    AiSdkExecutionSubmissionRequest,
    AiSdkException,
    AiSdkPipelineDefinition,
    AiSdkPipelinePublicationRequest,
    AiSdkTransportResponse,
)


class RecordingTransport:
    def __init__(self, responses: list[AiSdkTransportResponse]) -> None:
        self.responses = list(responses)
        self.requests = []

    async def invoke(self, request):
        self.requests.append(request)
        return self.responses.pop(0)


class AiSdkClientTests(unittest.IsolatedAsyncioTestCase):
    async def test_publish_pipeline_materializes_portable_defaults(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "publicationRef": "publication-1",
                "publicationSha256": "abc",
                "pipelineName": "demo",
                "pipelineVersion": "1",
            })
        ])
        client = AiSdkClient(transport)

        result = await client.publish_pipeline(
            AiSdkPipelinePublicationRequest(definition=AiSdkPipelineDefinition(name="demo"))
        )

        self.assertEqual("publication-1", result.publication_ref)
        request = transport.requests[0]
        self.assertEqual(AI_SDK_OPERATIONS["publish_pipeline"], request.operation)
        payload = request.arguments["request"]
        self.assertEqual(1, payload["schemaVersion"])
        self.assertEqual(1, payload["definition"]["schemaVersion"])
        self.assertEqual("Sequential", payload["definition"]["executionMode"])
        self.assertEqual([], payload["definition"]["steps"])
        self.assertEqual({}, payload["definition"]["config"])
        self.assertEqual([], payload["functions"])
        self.assertNotIn("tenantId", str(payload))
        self.assertNotIn("runtimeInstanceId", str(payload))

    async def test_submit_execution_materializes_schema_and_metadata_defaults(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "execution-1",
                "publicationRef": "publication-1",
                "status": "Pending",
                "acceptedAtUtc": "2026-09-17T00:00:00+00:00",
            })
        ])
        client = AiSdkClient(transport)
        result = await client.submit_execution(
            AiSdkExecutionSubmissionRequest(publication_ref="publication-1")
        )

        self.assertEqual(AiSdkExecutionStatus.PENDING, result.status)
        payload = transport.requests[0].arguments["request"]
        self.assertEqual(1, payload["schemaVersion"])
        self.assertEqual({}, payload["metadata"])
        self.assertNotIn("input", payload)
        self.assertNotIn("idempotencyKey", payload)

    async def test_observe_execution_uses_canonical_operation(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "execution-1",
                "publicationRef": "publication-1",
                "pipelineName": "demo",
                "pipelineVersion": "1",
                "status": "Running",
                "createdAtUtc": "2026-09-17T00:00:00+00:00",
                "updatedAtUtc": "2026-09-17T00:00:01+00:00",
                "steps": [],
            })
        ])
        client = AiSdkClient(transport)
        result = await client.observe_execution("execution-1")
        self.assertEqual(AiSdkExecutionStatus.RUNNING, result.status)
        self.assertEqual(AI_SDK_OPERATIONS["observe_execution"], transport.requests[0].operation)

    async def test_missing_response_schema_fails_closed(self) -> None:
        transport = RecordingTransport([AiSdkTransportResponse(result={"executionId": "execution-1"})])
        client = AiSdkClient(transport)
        with self.assertRaises(AiSdkException) as caught:
            await client.get_execution_result("execution-1")
        self.assertEqual("missing_schema_version", caught.exception.error.code)

    async def test_unsupported_response_schema_fails_closed(self) -> None:
        transport = RecordingTransport([AiSdkTransportResponse(result={"schemaVersion": 2})])
        client = AiSdkClient(transport)
        with self.assertRaises(AiSdkException) as caught:
            await client.get_execution_result("execution-1")
        self.assertEqual("unsupported_schema", caught.exception.error.code)

    async def test_remote_normalized_error_is_raised(self) -> None:
        from multiplexed_ai_sdk import AiSdkError

        transport = RecordingTransport([
            AiSdkTransportResponse(error=AiSdkError(kind="conflict", code="conflict", message="conflict"))
        ])
        client = AiSdkClient(transport)
        with self.assertRaises(AiSdkException) as caught:
            await client.cancel_execution("execution-1", AiSdkExecutionCancellationRequest())
        self.assertEqual("conflict", caught.exception.error.kind)

    async def test_blank_execution_id_fails_before_transport_invocation(self) -> None:
        transport = RecordingTransport([])
        client = AiSdkClient(transport)
        with self.assertRaises(AiSdkException) as caught:
            await client.observe_execution("  ")
        self.assertEqual("execution_id_required", caught.exception.error.code)
        self.assertEqual([], transport.requests)


if __name__ == "__main__":
    unittest.main()
