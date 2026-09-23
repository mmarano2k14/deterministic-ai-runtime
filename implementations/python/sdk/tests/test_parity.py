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
    AiSdkExecutionControlAction,
    AiSdkExecutionControlOperation,
    AiSdkExecutionControlRequest,
    AiSdkExecutionControlResponse,
    AiSdkExecutionControlStatus,
    AiSdkExecutionInputSubmissionRequest,
    AiSdkExecutionMode,
    AiSdkExecutionReplayRequest,
    AiSdkExecutionReplayResponse,
    AiSdkExecutionStatus,
    AiSdkExecutionStepStatus,
    AiSdkExecutionSubmissionRequest,
    AiSdkExecutionWatchChannel,
    AiSdkExecutionWatchEvent,
    AiSdkExecutionWatchEventKind,
    AiSdkExecutionWatchRequest,
    AiSdkExecutionWatchResyncReason,
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
            "executionWatchRequest": AiSdkSchemaVersions.EXECUTION_WATCH_REQUEST,
            "executionWatchEvent": AiSdkSchemaVersions.EXECUTION_WATCH_EVENT,
            "executionWatchResyncRequired": AiSdkSchemaVersions.EXECUTION_WATCH_RESYNC_REQUIRED,
            "executionControlRequest": AiSdkSchemaVersions.EXECUTION_CONTROL_REQUEST,
            "executionControlResponse": AiSdkSchemaVersions.EXECUTION_CONTROL_RESPONSE,
            "executionInputSubmissionRequest": AiSdkSchemaVersions.EXECUTION_INPUT_SUBMISSION_REQUEST,
            "executionReplayRequest": AiSdkSchemaVersions.EXECUTION_REPLAY_REQUEST,
            "executionReplayResponse": AiSdkSchemaVersions.EXECUTION_REPLAY_RESPONSE,
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
        self.assertEqual(
            FIXTURE["enumValues"]["executionWatchChannel"],
            [item.value for item in AiSdkExecutionWatchChannel],
        )
        self.assertEqual(
            FIXTURE["enumValues"]["executionWatchEventKind"],
            [item.value for item in AiSdkExecutionWatchEventKind],
        )
        self.assertEqual(
            FIXTURE["enumValues"]["executionWatchResyncReason"],
            [item.value for item in AiSdkExecutionWatchResyncReason],
        )
        self.assertEqual(
            FIXTURE["enumValues"]["executionControlOperation"],
            [item.value for item in AiSdkExecutionControlOperation],
        )
        self.assertEqual(
            FIXTURE["enumValues"]["executionControlStatus"],
            [item.value for item in AiSdkExecutionControlStatus],
        )
        self.assertEqual(
            FIXTURE["enumValues"]["executionControlAction"],
            [item.value for item in AiSdkExecutionControlAction],
        )
        self.assertEqual(FIXTURE["errorKinds"], list(get_args(AiSdkErrorKind)))

    async def test_all_public_non_streaming_request_wire_shapes_match_shared_fixture(self) -> None:
        transport = FixtureTransport(FIXTURE["responses"])
        client = AiSdkClient(transport)

        publication = AiSdkPipelinePublicationRequest.from_wire(FIXTURE["requests"]["publish"]["arguments"]["request"])
        submission = AiSdkExecutionSubmissionRequest.from_wire(FIXTURE["requests"]["submit"]["arguments"]["request"])
        cancellation = AiSdkExecutionCancellationRequest.from_wire(FIXTURE["requests"]["cancel"]["arguments"]["request"])
        pause = AiSdkExecutionControlRequest.from_wire(FIXTURE["requests"]["pause"]["arguments"]["request"])
        resume = AiSdkExecutionControlRequest.from_wire(FIXTURE["requests"]["resume"]["arguments"]["request"])
        submit_input = AiSdkExecutionInputSubmissionRequest.from_wire(FIXTURE["requests"]["submitInput"]["arguments"]["request"])
        replay = AiSdkExecutionReplayRequest.from_wire(FIXTURE["requests"]["replay"]["arguments"]["request"])

        publish_response = await client.publish_pipeline(publication)
        submit_response = await client.submit_execution(submission)
        observe_response = await client.observe_execution("exec-parity")
        result_response = await client.get_execution_result("exec-parity")
        cancel_response = await client.cancel_execution("exec-parity", cancellation)
        pause_response = await client.pause_execution("exec-parity", pause)
        resume_response = await client.resume_execution("exec-parity", resume)
        input_response = await client.submit_execution_input("exec-parity", submit_input)
        replay_response = await client.replay_execution("exec-parity", replay)

        self.assertEqual("pub-parity", publish_response.publication_ref)
        self.assertEqual(AiSdkExecutionStatus.PENDING, submit_response.status)
        self.assertEqual(AiSdkExecutionStatus.RUNNING, observe_response.status)
        self.assertEqual(AiSdkExecutionStatus.COMPLETED, result_response.status)
        self.assertTrue(cancel_response.cancellation_requested)
        self.assertEqual(AiSdkExecutionControlOperation.PAUSE, pause_response.operation)
        self.assertEqual(AiSdkExecutionControlOperation.RESUME, resume_response.operation)
        self.assertEqual(AiSdkExecutionControlOperation.SUBMIT_INPUT, input_response.operation)
        self.assertTrue(replay_response.succeeded)

        for request, name in zip(
            transport.requests,
            ("publish", "submit", "observe", "result", "cancel", "pause", "resume", "submitInput", "replay"),
            strict=True,
        ):
            self.assertEqual(FIXTURE["requests"][name]["operation"], request.operation)
            self.assertEqual(FIXTURE["requests"][name]["arguments"], request.arguments)

    async def test_minimal_defaults_and_omission_semantics_match_shared_fixture(self) -> None:
        transport = FixtureTransport({
            "publish": FIXTURE["responses"]["publish"],
            "submit": FIXTURE["responses"]["submit"],
            "cancel": FIXTURE["responses"]["cancel"],
            "pause": FIXTURE["responses"]["pause"],
            "resume": FIXTURE["responses"]["resume"],
            "replay": FIXTURE["responses"]["replay"],
        })
        client = AiSdkClient(transport)

        await client.publish_pipeline(
            AiSdkPipelinePublicationRequest(definition=AiSdkPipelineDefinition(name="minimal"))
        )
        await client.submit_execution(
            AiSdkExecutionSubmissionRequest(publication_ref="pub-minimal", input=None)
        )
        await client.cancel_execution("exec-minimal", AiSdkExecutionCancellationRequest())
        await client.pause_execution("exec-minimal")
        await client.resume_execution("exec-minimal")
        await client.replay_execution("exec-minimal")

        names = ("publish", "submit", "cancel", "pause", "resume", "replay")
        for request, name in zip(transport.requests, names, strict=True):
            self.assertEqual(FIXTURE["minimalRequests"][name]["arguments"], request.arguments)

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
            (AiSdkExecutionControlResponse, "pause"),
            (AiSdkExecutionControlResponse, "resume"),
            (AiSdkExecutionControlResponse, "submitInput"),
            (AiSdkExecutionReplayResponse, "replay"),
        )
        for model_type, name in cases:
            with self.subTest(name=name):
                value = FIXTURE["responses"][name]
                self.assertEqual(value, model_type.from_wire(value).to_wire())

    def test_watch_contracts_roundtrip_shared_fixture(self) -> None:
        watch = FIXTURE["watchContracts"]
        cases = (
            (AiSdkExecutionWatchRequest, "request"),
            (AiSdkExecutionWatchEvent, "snapshot"),
            (AiSdkExecutionWatchEvent, "event"),
            (AiSdkExecutionWatchEvent, "resyncRequired"),
        )
        for model_type, name in cases:
            with self.subTest(name=name):
                value = watch[name]
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
