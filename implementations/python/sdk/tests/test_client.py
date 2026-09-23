from __future__ import annotations

import asyncio
import sys
import unittest
from pathlib import Path

SRC_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC_ROOT))

from multiplexed_ai_sdk import (  # noqa: E402
    AI_SDK_OPERATIONS,
    AiSdkClient,
    AiSdkExecutionCancellationRequest,
    AiSdkExecutionControlOperation,
    AiSdkExecutionControlRequest,
    AiSdkExecutionInputSubmissionRequest,
    AiSdkExecutionReplayRequest,
    AiSdkExecutionStatus,
    AiSdkExecutionWatchChannel,
    AiSdkExecutionWatchEventKind,
    AiSdkExecutionWatchRequest,
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


class BlockingWatchTransport:
    def __init__(self, first_response: AiSdkTransportResponse) -> None:
        self.first_response = first_response
        self.requests = []
        self._calls = 0
        self._block = asyncio.Event()

    async def invoke(self, request):
        self.requests.append(request)
        self._calls += 1
        if self._calls == 1:
            return self.first_response
        await self._block.wait()
        raise AssertionError("unreachable")


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

    async def test_watch_execution_exposes_async_iterator_and_advances_stateless_cursor(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-watch",
                "sequence": 10,
                "kind": "Snapshot",
                "occurredAtUtc": "2026-09-22T00:00:00Z",
                "snapshot": {
                    "schemaVersion": 1,
                    "executionId": "exec-watch",
                    "publicationRef": "pub-watch",
                    "pipelineName": "demo",
                    "pipelineVersion": "v1",
                    "status": "Running",
                    "createdAtUtc": "2026-09-22T00:00:00Z",
                    "updatedAtUtc": "2026-09-22T00:00:00Z",
                    "steps": [],
                },
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-watch",
                "sequence": 11,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:01Z",
                "channel": "Steps",
                "eventType": "step.progressed",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Running"},
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-watch",
                "sequence": 12,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:02Z",
                "channel": "Lifecycle",
                "eventType": "execution.terminal",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Completed"},
            }),
        ])
        client = AiSdkClient(transport)

        events = []
        async for event in client.watch_execution(AiSdkExecutionWatchRequest(execution_id="exec-watch")):
            events.append(event)

        self.assertEqual([10, 11, 12], [event.sequence for event in events])
        self.assertEqual(3, len(transport.requests))
        self.assertEqual(
            [AI_SDK_OPERATIONS["watch_execution"]] * 3,
            [request.operation for request in transport.requests],
        )
        self.assertEqual({
            "request": {
                "schemaVersion": 1,
                "executionId": "exec-watch",
                "channels": [],
                "includeInitialSnapshot": True,
            }
        }, transport.requests[0].arguments)
        self.assertEqual({
            "request": {
                "schemaVersion": 1,
                "executionId": "exec-watch",
                "channels": [],
                "afterSequence": 10,
                "includeInitialSnapshot": False,
            }
        }, transport.requests[1].arguments)
        self.assertEqual({
            "request": {
                "schemaVersion": 1,
                "executionId": "exec-watch",
                "channels": [],
                "afterSequence": 11,
                "includeInitialSnapshot": False,
            }
        }, transport.requests[2].arguments)

    async def test_watch_execution_preserves_resume_channels_and_auto_resyncs(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-resume",
                "sequence": 879,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:00Z",
                "channel": "Recovery",
                "eventType": "recovery.resumed",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Running"},
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-resume",
                "kind": "ResyncRequired",
                "occurredAtUtc": "2026-09-22T00:00:01Z",
                "resyncRequired": {
                    "schemaVersion": 1,
                    "reason": "HistoryUnavailable",
                    "requestedAfterSequence": 879,
                    "earliestAvailableSequence": 900,
                    "latestSequence": 901,
                },
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-resume",
                "sequence": 901,
                "kind": "Snapshot",
                "occurredAtUtc": "2026-09-22T00:00:02Z",
                "snapshot": {
                    "schemaVersion": 1,
                    "executionId": "exec-resume",
                    "publicationRef": "pub-watch",
                    "pipelineName": "demo",
                    "pipelineVersion": "v1",
                    "status": "Running",
                    "createdAtUtc": "2026-09-22T00:00:00Z",
                    "updatedAtUtc": "2026-09-22T00:00:02Z",
                    "steps": [],
                },
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-resume",
                "sequence": 902,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:03Z",
                "channel": "Lifecycle",
                "eventType": "watch.test.terminal",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Completed"},
            }),
        ])
        client = AiSdkClient(transport)
        request = AiSdkExecutionWatchRequest(
            execution_id="exec-resume",
            channels=(AiSdkExecutionWatchChannel.RECOVERY,),
            after_sequence=878,
            include_initial_snapshot=False,
        )

        events = [event async for event in client.watch_execution(request)]

        self.assertEqual(4, len(events))
        self.assertIs(AiSdkExecutionWatchEventKind.RESYNC_REQUIRED, events[1].kind)
        self.assertIs(AiSdkExecutionWatchEventKind.SNAPSHOT, events[2].kind)
        self.assertEqual(901, events[2].sequence)
        self.assertEqual(902, events[3].sequence)
        self.assertEqual({
            "request": {
                "schemaVersion": 1,
                "executionId": "exec-resume",
                "channels": ["Recovery"],
                "includeInitialSnapshot": True,
            }
        }, transport.requests[2].arguments)

    async def test_watch_execution_ignores_exact_duplicate_cursor_item(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-duplicate",
                "sequence": 41,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:00Z",
                "channel": "Steps",
                "eventType": "watch.test.duplicate",
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-duplicate",
                "sequence": 42,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:01Z",
                "channel": "Lifecycle",
                "eventType": "watch.test.terminal",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Completed"},
            }),
        ])
        client = AiSdkClient(transport)

        events = [event async for event in client.watch_execution(AiSdkExecutionWatchRequest(
            execution_id="exec-duplicate",
            after_sequence=41,
            include_initial_snapshot=False,
        ))]

        self.assertEqual([42], [event.sequence for event in events])
        self.assertEqual(2, len(transport.requests))
        self.assertEqual(41, transport.requests[0].arguments["request"]["afterSequence"])
        self.assertEqual(41, transport.requests[1].arguments["request"]["afterSequence"])

    async def test_watch_execution_detects_unfiltered_gap_and_reestablishes_snapshot(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-gap",
                "sequence": 42,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:00Z",
                "channel": "Steps",
                "eventType": "watch.test.gapped",
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-gap",
                "sequence": 42,
                "kind": "Snapshot",
                "occurredAtUtc": "2026-09-22T00:00:01Z",
                "snapshot": {
                    "schemaVersion": 1,
                    "executionId": "exec-gap",
                    "publicationRef": "pub-watch",
                    "pipelineName": "demo",
                    "pipelineVersion": "v1",
                    "status": "Running",
                    "createdAtUtc": "2026-09-22T00:00:00Z",
                    "updatedAtUtc": "2026-09-22T00:00:01Z",
                    "steps": [],
                },
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-gap",
                "sequence": 43,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:02Z",
                "channel": "Lifecycle",
                "eventType": "watch.test.terminal",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Completed"},
            }),
        ])
        client = AiSdkClient(transport)

        events = [event async for event in client.watch_execution(AiSdkExecutionWatchRequest(
            execution_id="exec-gap",
            after_sequence=40,
            include_initial_snapshot=False,
        ))]

        self.assertEqual(3, len(events))
        self.assertIs(AiSdkExecutionWatchEventKind.RESYNC_REQUIRED, events[0].kind)
        self.assertEqual("GapDetected", events[0].resync_required.reason.value)
        self.assertEqual(40, events[0].resync_required.requested_after_sequence)
        self.assertIs(AiSdkExecutionWatchEventKind.SNAPSHOT, events[1].kind)
        self.assertEqual(42, events[1].sequence)
        self.assertEqual(43, events[2].sequence)
        self.assertNotIn("afterSequence", transport.requests[1].arguments["request"])
        self.assertTrue(transport.requests[1].arguments["request"]["includeInitialSnapshot"])

    async def test_watch_execution_rejects_regressing_public_sequence(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-regress",
                "sequence": 39,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:00Z",
                "channel": "Lifecycle",
                "eventType": "execution.progressed",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Running"},
            })
        ])
        client = AiSdkClient(transport)
        iterator = client.watch_execution(AiSdkExecutionWatchRequest(
            execution_id="exec-regress",
            after_sequence=40,
            include_initial_snapshot=False,
        ))

        with self.assertRaises(AiSdkException) as caught:
            await anext(iterator)
        self.assertEqual("invalid_response", caught.exception.error.kind)
        self.assertEqual("regressing_watch_sequence", caught.exception.error.code)

    async def test_watch_execution_rejects_mismatched_execution_response(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1,
                "executionId": "exec-other",
                "sequence": 1,
                "kind": "Event",
                "occurredAtUtc": "2026-09-22T00:00:00Z",
                "channel": "Lifecycle",
                "eventType": "execution.progressed",
                "payloadSchemaVersion": 1,
                "payload": {"status": "Running"},
            })
        ])
        client = AiSdkClient(transport)
        iterator = client.watch_execution(AiSdkExecutionWatchRequest(execution_id="exec-requested"))

        with self.assertRaises(AiSdkException) as caught:
            await anext(iterator)
        self.assertEqual("watch_execution_mismatch", caught.exception.error.code)

    def test_watch_execution_validates_channels_before_transport_invocation(self) -> None:
        transport = RecordingTransport([])
        client = AiSdkClient(transport)
        request = AiSdkExecutionWatchRequest(
            execution_id="exec-watch",
            channels=("Internal",),  # type: ignore[arg-type]
        )

        with self.assertRaises(AiSdkException) as caught:
            client.watch_execution(request)
        self.assertEqual("invalid_watch_channel", caught.exception.error.code)
        self.assertEqual([], transport.requests)

    async def test_cancelling_watch_task_stops_iteration_without_durable_cancel(self) -> None:
        transport = BlockingWatchTransport(AiSdkTransportResponse(result={
            "schemaVersion": 1,
            "executionId": "exec-abort",
            "sequence": 1,
            "kind": "Snapshot",
            "occurredAtUtc": "2026-09-22T00:00:00Z",
            "snapshot": {
                "schemaVersion": 1,
                "executionId": "exec-abort",
                "publicationRef": "pub-watch",
                "pipelineName": "demo",
                "pipelineVersion": "v1",
                "status": "Running",
                "createdAtUtc": "2026-09-22T00:00:00Z",
                "updatedAtUtc": "2026-09-22T00:00:00Z",
                "steps": [],
            },
        }))
        client = AiSdkClient(transport)
        iterator = client.watch_execution(AiSdkExecutionWatchRequest(execution_id="exec-abort"))

        first = await anext(iterator)
        self.assertIs(AiSdkExecutionWatchEventKind.SNAPSHOT, first.kind)

        pending = asyncio.create_task(anext(iterator))
        await asyncio.sleep(0)
        pending.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await pending

        self.assertEqual(2, len(transport.requests))
        self.assertTrue(all(
            request.operation == AI_SDK_OPERATIONS["watch_execution"]
            for request in transport.requests
        ))
        self.assertFalse(any(
            request.operation == AI_SDK_OPERATIONS["cancel_execution"]
            for request in transport.requests
        ))

    async def test_pause_resume_input_and_replay_use_public_operation_contracts(self) -> None:
        transport = RecordingTransport([
            AiSdkTransportResponse(result={
                "schemaVersion": 1, "executionId": "execution-1", "operation": "Pause",
                "accepted": True, "acceptedAtUtc": "2026-09-22T00:00:01+00:00"
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1, "executionId": "execution-1", "operation": "Resume",
                "accepted": True, "acceptedAtUtc": "2026-09-22T00:00:02+00:00"
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1, "executionId": "execution-1", "operation": "SubmitInput",
                "accepted": True, "acceptedAtUtc": "2026-09-22T00:00:03+00:00"
            }),
            AiSdkTransportResponse(result={
                "schemaVersion": 1, "executionId": "execution-1", "succeeded": True,
                "deterministic": True, "diagnostics": [],
                "startedAtUtc": "2026-09-22T00:00:04+00:00",
                "completedAtUtc": "2026-09-22T00:00:05+00:00", "durationMs": 1000
            }),
        ])
        client = AiSdkClient(transport)

        pause = await client.pause_execution("execution-1", AiSdkExecutionControlRequest(reason="operator pause"))
        resume = await client.resume_execution("execution-1", AiSdkExecutionControlRequest(reason="operator resume"))
        submitted = await client.submit_execution_input("execution-1", AiSdkExecutionInputSubmissionRequest(
            waiting_key="approval:pricing", input={"approved": True}
        ))
        replay = await client.replay_execution("execution-1", AiSdkExecutionReplayRequest())

        self.assertEqual(AiSdkExecutionControlOperation.PAUSE, pause.operation)
        self.assertEqual(AiSdkExecutionControlOperation.RESUME, resume.operation)
        self.assertEqual(AiSdkExecutionControlOperation.SUBMIT_INPUT, submitted.operation)
        self.assertTrue(replay.succeeded)
        self.assertEqual([
            AI_SDK_OPERATIONS["pause_execution"],
            AI_SDK_OPERATIONS["resume_execution"],
            AI_SDK_OPERATIONS["submit_execution_input"],
            AI_SDK_OPERATIONS["replay_execution"],
        ], [request.operation for request in transport.requests])

    async def test_submit_execution_input_validates_before_transport_invocation(self) -> None:
        transport = RecordingTransport([])
        client = AiSdkClient(transport)

        with self.assertRaises(AiSdkException) as blank_key:
            await client.submit_execution_input("execution-1", AiSdkExecutionInputSubmissionRequest(
                waiting_key=" ", input={"approved": True}
            ))
        self.assertEqual("waiting_key_required", blank_key.exception.error.code)

        with self.assertRaises(AiSdkException) as non_object:
            await client.submit_execution_input("execution-1", AiSdkExecutionInputSubmissionRequest(
                waiting_key="approval:pricing", input=True  # type: ignore[arg-type]
            ))
        self.assertEqual("input_object_required", non_object.exception.error.code)
        self.assertEqual([], transport.requests)

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
