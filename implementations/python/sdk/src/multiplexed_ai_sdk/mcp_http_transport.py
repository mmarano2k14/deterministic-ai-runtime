from __future__ import annotations

import asyncio
from urllib.parse import urlparse

from .errors import AiSdkError
from .json_types import AiSdkJsonObject
from .protocol import AI_SDK_OPERATION_RETRY, AI_SDK_OPERATIONS, AI_SDK_PROTOCOL_VERSION
from .transport import AiSdkTransportOptions, AiSdkTransportRequest, AiSdkTransportResponse


class AiSdkMcpHttpTransport:
    def __init__(self, endpoint: str, options: AiSdkTransportOptions | None = None) -> None:
        parsed = urlparse(endpoint)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise ValueError("MCP endpoint must be an absolute HTTP or HTTPS URL")
        self._endpoint = endpoint
        self._options = options or AiSdkTransportOptions()

    async def invoke(self, request: AiSdkTransportRequest) -> AiSdkTransportResponse:
        if request.protocol_version != AI_SDK_PROTOCOL_VERSION:
            return _failure(
                "unsupported_schema",
                "unsupported_protocol",
                f"Unsupported SDK protocol version '{request.protocol_version}'. Expected '{AI_SDK_PROTOCOL_VERSION}'.",
            )
        if request.operation not in AI_SDK_OPERATION_RETRY:
            return _failure(
                "invalid_request",
                "unknown_operation",
                f"Unknown public SDK operation '{request.operation}'.",
            )
        if not isinstance(request.arguments, dict):
            return _failure(
                "invalid_request",
                "invalid_arguments",
                "SDK transport arguments must be a JSON object.",
            )

        safe_read = AI_SDK_OPERATION_RETRY[request.operation] == "safe-read"
        attempts = self._options.safe_read_max_attempts if safe_read else 1

        for attempt in range(1, attempts + 1):
            try:
                return await self._invoke_once(request)
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                retryable_failure = safe_read and _is_retryable_transport_failure(exc)
                if not retryable_failure or attempt >= attempts:
                    return _normalize_transport_failure(exc, retryable_failure)
                if self._options.safe_read_retry_delay_seconds > 0:
                    await asyncio.sleep(self._options.safe_read_retry_delay_seconds)

        return _failure("transport", "transport_failure", "The MCP transport did not produce a response.")

    async def _invoke_once(self, request: AiSdkTransportRequest) -> AiSdkTransportResponse:
        # Imports stay at the physical transport boundary so contract/client imports remain lightweight.
        import httpx2
        from mcp import Client
        from mcp.client.streamable_http import streamable_http_client

        headers = await self._create_headers()
        if isinstance(headers, AiSdkError):
            return AiSdkTransportResponse(error=headers)

        async with httpx2.AsyncClient(headers=headers or None) as http_client:
            transport = streamable_http_client(self._endpoint, http_client=http_client)
            async with Client(transport) as client:
                result = await client.call_tool(request.operation, request.arguments)

        if result.is_error is True:
            return _failure(
                "remote_failure",
                "remote_tool_error",
                f"The remote SDK operation '{request.operation}' returned an error.",
            )
        if not isinstance(result.structured_content, dict):
            return _failure(
                "invalid_response",
                "missing_structured_content",
                f"The remote SDK operation '{request.operation}' did not return an object structured result.",
            )
        return AiSdkTransportResponse(result=result.structured_content)

    async def _create_headers(self) -> dict[str, str] | AiSdkError | None:
        headers = dict(self._options.additional_headers or {})
        provider = self._options.credential_provider
        if provider is None:
            return headers or None
        try:
            credential = await provider.get_credential()
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            return AiSdkError(
                kind="authentication",
                code="credential_provider_failure",
                message="The SDK credential provider failed to supply a transport credential.",
                details={"cause": str(exc)},
            )

        if credential is None:
            return headers or None
        if not credential.scheme.strip() or not credential.value.strip():
            return AiSdkError(
                kind="authentication",
                code="invalid_credential",
                message="The SDK credential provider returned an invalid credential.",
            )
        headers["Authorization"] = f"{credential.scheme} {credential.value}"
        return headers


def _is_retryable_transport_failure(exc: Exception) -> bool:
    status = _status_code(exc)
    if status is not None:
        return status in (408, 429) or status >= 500
    if isinstance(exc, (TimeoutError, ConnectionError, OSError)):
        return True
    return type(exc).__name__ in {
        "ConnectError",
        "NetworkError",
        "ReadError",
        "TimeoutException",
        "TransportError",
        "WriteError",
    }


def _normalize_transport_failure(exc: Exception, retryable: bool) -> AiSdkTransportResponse:
    status = _status_code(exc)
    if status == 401:
        return _failure("authentication", "authentication_failed", "The SDK transport was not authenticated.")
    if status == 403:
        return _failure(
            "authorization",
            "authorization_failed",
            "The SDK transport is not authorized for the requested operation.",
        )
    return AiSdkTransportResponse(
        error=AiSdkError(
            kind="transport",
            code="transport_timeout" if _is_timeout(exc) else "transport_failure",
            message=str(exc) or type(exc).__name__,
            retryable=retryable,
        )
    )


def _status_code(exc: Exception) -> int | None:
    for candidate in (getattr(exc, "status_code", None), getattr(exc, "status", None)):
        if type(candidate) is int:
            return candidate
    response = getattr(exc, "response", None)
    candidate = getattr(response, "status_code", None) if response is not None else None
    return candidate if type(candidate) is int else None


def _is_timeout(exc: Exception) -> bool:
    return isinstance(exc, TimeoutError) or "Timeout" in type(exc).__name__


def _failure(kind: str, code: str, message: str) -> AiSdkTransportResponse:
    return AiSdkTransportResponse(error=AiSdkError(kind=kind, code=code, message=message))  # type: ignore[arg-type]
