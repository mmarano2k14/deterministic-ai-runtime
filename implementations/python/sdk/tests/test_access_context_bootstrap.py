from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest.mock import patch

SRC_ROOT = Path(__file__).resolve().parents[1] / "src"
sys.path.insert(0, str(SRC_ROOT))

from multiplexed_ai_sdk import (  # noqa: E402
    AiSdkAccessContextBootstrapOptions,
    AiSdkAccessContextBootstrapper,
    AiSdkCredential,
    AiSdkException,
    AiSdkStaticCredentialProvider,
)


class _Headers(dict[str, str]):
    def __init__(self, values: list[tuple[str, str]]) -> None:
        super().__init__(values)
        self._values = values

    def get_list(self, name: str) -> list[str]:
        expected = name.lower()
        return [value for key, value in self._values if key.lower() == expected]


class _Response:
    def __init__(
        self,
        status_code: int,
        headers: list[tuple[str, str]] | None = None,
        reason_phrase: str = "",
    ) -> None:
        self.status_code = status_code
        self.headers = _Headers(headers or [])
        self.reason_phrase = reason_phrase


class _Client:
    def __init__(self, response: _Response | Exception) -> None:
        self.response = response
        self.endpoint: str | None = None
        self.headers = None

    async def __aenter__(self):
        return self

    async def __aexit__(self, exc_type, exc, tb):
        return False

    async def post(self, endpoint: str, *, headers=None):
        self.endpoint = endpoint
        self.headers = headers
        if isinstance(self.response, Exception):
            raise self.response
        return self.response


class AiSdkAccessContextBootstrapTests(unittest.IsolatedAsyncioTestCase):
    async def test_creates_handle_with_bearer_credential(self) -> None:
        client = _Client(_Response(204, [("X-Access-Context", "ctx-created")]))
        options = AiSdkAccessContextBootstrapOptions(
            endpoint="https://runtime.example/auth/access-context",
            credential_provider=AiSdkStaticCredentialProvider(
                AiSdkCredential("Bearer", "token-123")
            ),
        )

        with patch(
            "multiplexed_ai_sdk.access_context_bootstrap._create_http_client",
            return_value=client,
        ):
            result = await AiSdkAccessContextBootstrapper.create(options)

        self.assertEqual("ctx-created", result.access_context)
        self.assertEqual("X-Access-Context", result.header_name)
        self.assertEqual(
            {"Authorization": "Bearer token-123"},
            client.headers,
        )

    async def test_unauthenticated_response_is_normalized(self) -> None:
        client = _Client(_Response(401))
        with patch(
            "multiplexed_ai_sdk.access_context_bootstrap._create_http_client",
            return_value=client,
        ):
            with self.assertRaises(AiSdkException) as caught:
                await AiSdkAccessContextBootstrapper.create(
                    AiSdkAccessContextBootstrapOptions(
                        endpoint="https://runtime.example/auth/access-context"
                    )
                )

        self.assertEqual(
            "access_context_bootstrap_unauthenticated",
            caught.exception.error.code,
        )

    async def test_duplicate_distinct_handles_are_rejected(self) -> None:
        client = _Client(
            _Response(
                200,
                [
                    ("X-Access-Context", "ctx-a"),
                    ("X-Access-Context", "ctx-b"),
                ],
            )
        )
        with patch(
            "multiplexed_ai_sdk.access_context_bootstrap._create_http_client",
            return_value=client,
        ):
            with self.assertRaises(AiSdkException) as caught:
                await AiSdkAccessContextBootstrapper.create(
                    AiSdkAccessContextBootstrapOptions(
                        endpoint="https://runtime.example/auth/access-context"
                    )
                )

        self.assertEqual(
            "access_context_bootstrap_missing_handle",
            caught.exception.error.code,
        )

    async def test_timeout_is_not_retried_and_is_normalized(self) -> None:
        client = _Client(TimeoutError("bootstrap timeout"))
        with patch(
            "multiplexed_ai_sdk.access_context_bootstrap._create_http_client",
            return_value=client,
        ):
            with self.assertRaises(AiSdkException) as caught:
                await AiSdkAccessContextBootstrapper.create(
                    AiSdkAccessContextBootstrapOptions(
                        endpoint="https://runtime.example/auth/access-context"
                    )
                )

        self.assertEqual("access_context_bootstrap_timeout", caught.exception.error.code)

    def test_rejects_invalid_endpoint(self) -> None:
        with self.assertRaises(ValueError):
            AiSdkAccessContextBootstrapOptions(endpoint="file:///tmp/context")


if __name__ == "__main__":
    unittest.main()
