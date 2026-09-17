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


class AiSdkMcpHttpTransportTests(unittest.IsolatedAsyncioTestCase):
    def test_rejects_non_http_endpoint(self) -> None:
        with self.assertRaises(ValueError):
            AiSdkMcpHttpTransport("file:///tmp/mcp")

    def test_validates_safe_read_attempt_configuration(self) -> None:
        with self.assertRaises(ValueError):
            AiSdkTransportOptions(safe_read_max_attempts=0)

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
