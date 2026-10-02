from __future__ import annotations

import sys
import unittest
from pathlib import Path

SRC_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC_ROOT))

from multiplexed_ai_sdk import (  # noqa: E402
    AI_SDK_OPERATIONS,
    AiSdkMcpHttpTransport,
    AiSdkTransportOptions,
    AiSdkTransportRequest,
    AiSdkTransportResponse,
)
from multiplexed_ai_sdk.mcp_http_transport import (  # noqa: E402
    _AiSdkAccessContextState,
    _create_http_timeout,
    _is_retryable_transport_failure,
    _normalize_transport_failure,
    _remote_tool_error_details,
)


class FlakyTransport(AiSdkMcpHttpTransport):
    def __init__(self, operation_failures: int, options: AiSdkTransportOptions | None = None) -> None:
        super().__init__("https://runtime.example/mcp", options)
        self.operation_failures = operation_failures
        self.attempts = 0

    async def _invoke_once(self, request):
        self.attempts += 1
        if self.attempts <= self.operation_failures:
            raise TimeoutError("timeout")
        return AiSdkTransportResponse(result={"schemaVersion": 1})


class _TextContent:
    def __init__(self, text: str) -> None:
        self.text = text


class _ToolErrorResult:
    def __init__(self) -> None:
        self.content = [_TextContent("InvalidOperationException: exact runtime environment mismatch")]
        self.structured_content = {"code": "publication_failed", "retryable": False}


class _CapturedTimeout:
    def __init__(self, **kwargs) -> None:
        self.kwargs = kwargs


class _FakeHttpx2:
    Timeout = _CapturedTimeout


class _FakeExceptionGroup(Exception):
    def __init__(self, *exceptions: Exception) -> None:
        super().__init__("unhandled errors in a TaskGroup")
        self.exceptions = exceptions


class _ReadTimeout(Exception):
    pass


class AiSdkMcpHttpTransportTests(unittest.IsolatedAsyncioTestCase):
    def test_rejects_non_http_endpoint(self) -> None:
        with self.assertRaises(ValueError):
            AiSdkMcpHttpTransport("file:///tmp/mcp")

    def test_validates_safe_read_attempt_configuration(self) -> None:
        with self.assertRaises(ValueError):
            AiSdkTransportOptions(safe_read_max_attempts=0)

    def test_validates_http_timeout_configuration(self) -> None:
        for field in (
            "connect_timeout_seconds",
            "read_timeout_seconds",
            "write_timeout_seconds",
            "pool_timeout_seconds",
        ):
            with self.subTest(field=field):
                with self.assertRaises(ValueError):
                    AiSdkTransportOptions(**{field: 0})

    def test_streamable_http_timeout_defaults_preserve_long_reads(self) -> None:
        timeout = _create_http_timeout(_FakeHttpx2, AiSdkTransportOptions())
        self.assertEqual(
            {
                "connect": 30.0,
                "read": 300.0,
                "write": 30.0,
                "pool": 30.0,
            },
            timeout.kwargs,
        )

    def test_streamable_http_timeout_values_are_configurable(self) -> None:
        options = AiSdkTransportOptions(
            connect_timeout_seconds=11.0,
            read_timeout_seconds=222.0,
            write_timeout_seconds=12.0,
            pool_timeout_seconds=13.0,
        )
        timeout = _create_http_timeout(_FakeHttpx2, options)
        self.assertEqual(
            {"connect": 11.0, "read": 222.0, "write": 12.0, "pool": 13.0},
            timeout.kwargs,
        )

    def test_access_context_state_reuses_latest_rotated_handle(self) -> None:
        state = _AiSdkAccessContextState(
            "X-Access-Context",
            {"x-access-context": "ctx-initial"},
        )

        first_headers: dict[str, str] = {}
        state.apply(first_headers)
        self.assertEqual("ctx-initial", first_headers["X-Access-Context"])

        state.observe({"X-Access-Context": "ctx-rotated"})

        second_headers: dict[str, str] = {}
        state.apply(second_headers)
        self.assertEqual("ctx-rotated", second_headers["X-Access-Context"])
        self.assertEqual("ctx-rotated", state.current)

    def test_access_context_state_supports_custom_header_name(self) -> None:
        state = _AiSdkAccessContextState(
            "X-Custom-Context",
            {"x-custom-context": "ctx-custom"},
        )

        headers: dict[str, str] = {}
        state.apply(headers)

        self.assertEqual("ctx-custom", headers["X-Custom-Context"])

    def test_task_group_timeout_is_classified_as_retryable_timeout(self) -> None:
        grouped = _FakeExceptionGroup(_ReadTimeout("watch read timed out"))
        self.assertTrue(_is_retryable_transport_failure(grouped))

        response = _normalize_transport_failure(grouped, retryable=True)
        self.assertFalse(response.is_success)
        self.assertEqual("transport_timeout", response.error.code)
        self.assertTrue(response.error.retryable)
        self.assertEqual("watch read timed out", response.error.message)
        self.assertEqual("_ReadTimeout", response.error.details["exceptionType"])
        self.assertEqual("_FakeExceptionGroup", response.error.details["exceptionGroupType"])

    def test_remote_tool_error_preserves_same_call_diagnostics(self) -> None:
        details = _remote_tool_error_details(_ToolErrorResult())
        self.assertEqual(
            ["InvalidOperationException: exact runtime environment mismatch"],
            details["remoteContent"],
        )
        self.assertEqual(
            {"code": "publication_failed", "retryable": False},
            details["remoteStructuredContent"],
        )

    async def test_unsupported_protocol_fails_before_io(self) -> None:
        transport = FlakyTransport(0)
        response = await transport.invoke(
            AiSdkTransportRequest(
                operation=AI_SDK_OPERATIONS["observe_execution"],
                arguments={"executionId": "execution-1"},
                protocol_version=99,
            )
        )
        self.assertFalse(response.is_success)
        self.assertEqual("unsupported_protocol", response.error.code)
        self.assertEqual(0, transport.attempts)

    async def test_safe_read_retries_transport_timeout(self) -> None:
        transport = FlakyTransport(
            1,
            AiSdkTransportOptions(safe_read_max_attempts=2, safe_read_retry_delay_seconds=0),
        )
        response = await transport.invoke(
            AiSdkTransportRequest(
                operation=AI_SDK_OPERATIONS["observe_execution"],
                arguments={"executionId": "execution-1"},
            )
        )
        self.assertTrue(response.is_success)
        self.assertEqual(2, transport.attempts)

    async def test_watch_is_a_safe_read_transport_operation(self) -> None:
        transport = FlakyTransport(
            1,
            AiSdkTransportOptions(safe_read_max_attempts=2, safe_read_retry_delay_seconds=0),
        )
        response = await transport.invoke(
            AiSdkTransportRequest(
                operation=AI_SDK_OPERATIONS["watch_execution"],
                arguments={"request": {"schemaVersion": 1, "executionId": "execution-1"}},
            )
        )
        self.assertTrue(response.is_success)
        self.assertEqual(2, transport.attempts)

    async def test_execution_control_and_replay_are_never_automatically_retried(self) -> None:
        for operation in (
            AI_SDK_OPERATIONS["pause_execution"],
            AI_SDK_OPERATIONS["resume_execution"],
            AI_SDK_OPERATIONS["submit_execution_input"],
            AI_SDK_OPERATIONS["replay_execution"],
        ):
            with self.subTest(operation=operation):
                transport = FlakyTransport(
                    1,
                    AiSdkTransportOptions(safe_read_max_attempts=5, safe_read_retry_delay_seconds=0),
                )
                response = await transport.invoke(
                    AiSdkTransportRequest(operation=operation, arguments={"executionId": "execution-1", "request": {}})
                )
                self.assertFalse(response.is_success)
                self.assertEqual(1, transport.attempts)

    async def test_submit_is_never_automatically_retried(self) -> None:
        transport = FlakyTransport(
            1,
            AiSdkTransportOptions(safe_read_max_attempts=5, safe_read_retry_delay_seconds=0),
        )
        response = await transport.invoke(
            AiSdkTransportRequest(
                operation=AI_SDK_OPERATIONS["submit_execution"],
                arguments={"request": {}},
            )
        )
        self.assertFalse(response.is_success)
        self.assertEqual(1, transport.attempts)


if __name__ == "__main__":
    unittest.main()
