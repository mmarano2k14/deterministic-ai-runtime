from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path
from typing import get_args

SDK_ROOT = Path(__file__).resolve().parents[1]
SRC_ROOT = SDK_ROOT / "src"
FIXTURE_PATH = SDK_ROOT.parent.parent / "sdk" / "parity" / "fixtures" / "sdk-parity-v1.json"
sys.path.insert(0, str(SRC_ROOT))

from multiplexed_ai_sdk import (  # noqa: E402
    AI_SDK_OPERATIONS,
    AI_SDK_PROTOCOL_VERSION,
    AiSdkClient,
    AiSdkError,
    AiSdkErrorKind,
    AiSdkException,
    AiSdkExecutionCancellationRequest,
    AiSdkExecutionMode,
    AiSdkExecutionStatus,
    AiSdkExecutionStepStatus,
    AiSdkExecutionSubmissionRequest,
    AiSdkInvocationKind,
    AiSdkPipelineDefinition,
    AiSdkPipelinePublicationRequest,
    AiSdkPublicationDependencyPackageKind,
    AiSdkPublicationFunctionKind,
    AiSdkSchemaVersions,
    AiSdkTransportResponse,
)

FIXTURE = json.loads(FIXTURE_PATH.read_text(encoding="utf-8"))


class FixtureTransport:
    def __init__(self, responses: dict) -> None:
        self.responses = responses
        self.requests = []

    async def invoke(self, request):
        self.requests.append(request)
        for name, expected in FIXTURE["requests"].items():
            if expected["operation"] == request.operation:
                return AiSdkTransportResponse(result=self.responses[name])
        raise AssertionError(f"Unexpected operation {request.operation}")


class AiSdkCrossLanguageParityTests(unittest.IsolatedAsyncioTestCase):
    def test_protocol_schema_versions_and_enum_literals_match_shared_fixture(self) -> None:
        self.assertEqual(FIXTURE["protocolVersion"], AI_SDK_PROTOCOL_VERSION)
        self.assertEqual(list(FIXTURE["operations"].values()), list(AI_SDK_OPERATIONS.values()))
        self.assertEqual(FIXTURE["schemaVersions"], {
            "pipelineDefinition": AiSdkSchemaVersions.PIPELINE_DEFINITION,
            "pipelinePublicationRequest": AiSdkSchemaVersions.PIPELINE_PUBLICATION_REQUEST,
            "pipelinePublicationResponse": AiSdkSchemaVersions.PIPELINE_PUBLICATION_RESPONSE,
            "executionSubmissionRequest": AiSdkSchemaVersions.EXECUTION_SUBMISSION_REQUEST,
            "executionSubmissionResponse": AiSdkSchemaVersions.EXECUTION_SUBMISSION_RESPONSE,
            "executionObservation": AiSdkSchemaVersions.EXECUTION_OBSERVATION,
            "executionResult": AiSdkSchemaVersions.EXECUTION_RESULT,
            "executionCancellationRequest": AiSdkSchemaVersions.EXECUTION_CANCELLATION_REQUEST,
            "executionCancellationResponse": AiSdkSchemaVersions.EXECUTION_CANCELLATION_RESPONSE,
        })
        self.assertEqual(FIXTURE["enumValues"]["executionMode"], [item.value for item in AiSdkExecutionMode])
        self.assertEqual(FIXTURE["enumValues"]["invocationKind"], [item.value for item in AiSdkInvocationKind])
        self.assertEqual(
            FIXTURE["enumValues"]["publicationFunctionKind"],
            [item.value for item in AiSdkPublicationFunctionKind],
        )
        self.assertEqual(
            FIXTURE["enumValues"]["publicationDependencyPackageKind"],
            [item.value for item in AiSdkPublicationDependencyPackageKind],
        )
        self.assertEqual(FIXTURE["enumValues"]["executionStatus"], [item.value for item in AiSdkExecutionStatus])
        self.assertEqual(
            FIXTURE["enumValues"]["executionStepStatus"],
            [item.value for item in AiSdkExecutionStepStatus],
        )
        self.assertEqual(FIXTURE["errorKinds"], list(get_args(AiSdkErrorKind)))

    async def test_all_five_request_wire_shapes_match_shared_fixture(self) -> None:
        transport = FixtureTransport(FIXTURE["responses"])
        client = AiSdkClient(transport)

        publication = AiSdkPipelinePublicationRequest.from_wire(FIXTURE["requests"]["publish"]["arguments"]["request"])
        submission = AiSdkExecutionSubmissionRequest.from_wire(FIXTURE["requests"]["submit"]["arguments"]["request"])
        cancellation = AiSdkExecutionCancellationRequest.from_wire(FIXTURE["requests"]["cancel"]["arguments"]["request"])

        publish_response = await client.publish_pipeline(publication)
        submit_response = await client.submit_execution(submission)
        observe_response = await client.observe_execution("exec-parity")
        result_response = await client.get_execution_result("exec-parity")
        cancel_response = await client.cancel_execution("exec-parity", cancellation)

        self.assertEqual("pub-parity", publish_response.publication_ref)
        self.assertEqual(AiSdkExecutionStatus.PENDING, submit_response.status)
        self.assertEqual(AiSdkExecutionStatus.RUNNING, observe_response.status)
        self.assertEqual(AiSdkExecutionStatus.COMPLETED, result_response.status)
        self.assertTrue(cancel_response.cancellation_requested)

        for request, name in zip(transport.requests, ("publish", "submit", "observe", "result", "cancel"), strict=True):
            self.assertEqual(FIXTURE["requests"][name]["operation"], request.operation)
            self.assertEqual(FIXTURE["requests"][name]["arguments"], request.arguments)

    async def test_minimal_defaults_and_omission_semantics_match_shared_fixture(self) -> None:
        transport = FixtureTransport({
            "publish": FIXTURE["responses"]["publish"],
            "submit": FIXTURE["responses"]["submit"],
            "cancel": FIXTURE["responses"]["cancel"],
        })
        client = AiSdkClient(transport)

        await client.publish_pipeline(
            AiSdkPipelinePublicationRequest(definition=AiSdkPipelineDefinition(name="minimal"))
        )
        await client.submit_execution(
            AiSdkExecutionSubmissionRequest(publication_ref="pub-minimal", input=None)
        )
        await client.cancel_execution("exec-minimal", AiSdkExecutionCancellationRequest())

        self.assertEqual(FIXTURE["minimalRequests"]["publish"]["arguments"], transport.requests[0].arguments)
        self.assertEqual(FIXTURE["minimalRequests"]["submit"]["arguments"], transport.requests[1].arguments)
        self.assertEqual(FIXTURE["minimalRequests"]["cancel"]["arguments"], transport.requests[2].arguments)

    def test_response_models_roundtrip_shared_fixture(self) -> None:
        from multiplexed_ai_sdk import (
            AiSdkExecutionCancellationResponse,
            AiSdkExecutionObservation,
            AiSdkExecutionResult,
            AiSdkExecutionSubmissionResponse,
            AiSdkPipelinePublicationResponse,
        )

        cases = (
            (AiSdkPipelinePublicationResponse, "publish"),
            (AiSdkExecutionSubmissionResponse, "submit"),
            (AiSdkExecutionObservation, "observe"),
            (AiSdkExecutionResult, "result"),
            (AiSdkExecutionResult, "failedResult"),
            (AiSdkExecutionCancellationResponse, "cancel"),
        )
        for model_type, name in cases:
            with self.subTest(name=name):
                value = FIXTURE["responses"][name]
                self.assertEqual(value, model_type.from_wire(value).to_wire())

    async def test_normalized_authorization_error_preserves_shared_failure_semantics(self) -> None:
        error = AiSdkError(**FIXTURE["normalizedError"])

        class ErrorTransport:
            async def invoke(self, request):
                return AiSdkTransportResponse(error=error)

        client = AiSdkClient(ErrorTransport())
        with self.assertRaises(AiSdkException) as caught:
            await client.observe_execution("exec-parity")
        self.assertEqual(error, caught.exception.error)


if __name__ == "__main__":
    unittest.main()
