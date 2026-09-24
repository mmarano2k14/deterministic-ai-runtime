from __future__ import annotations

import asyncio
from collections.abc import Iterable
from typing import Any, Mapping, MutableMapping
from urllib.parse import urlparse

from .errors import AiSdkError
from .json_types import AiSdkJsonObject
from .protocol import AI_SDK_OPERATION_RETRY, AI_SDK_PROTOCOL_VERSION
from .transport import AiSdkTransportOptions, AiSdkTransportRequest, AiSdkTransportResponse


class AiSdkMcpHttpTransport:
    def __init__(self, endpoint: str, options: AiSdkTransportOptions | None = None) -> None:
        parsed = urlparse(endpoint)
        if parsed.scheme not in ("http", "https") or not parsed.netloc:
            raise ValueError("MCP endpoint must be an absolute HTTP or HTTPS URL")
        self._endpoint = endpoint
        self._options = options or AiSdkTransportOptions()
        self._access_context = _AiSdkAccessContextState(
            self._options.access_context_header_name,
            self._options.additional_headers,
        )

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

        timeout = _create_http_timeout(httpx2, self._options)
        async with httpx2.AsyncClient(
            headers=headers or None,
            timeout=timeout,
            event_hooks={
                "request": [self._on_http_request],
                "response": [self._on_http_response],
            },
        ) as http_client:
            transport = streamable_http_client(self._endpoint, http_client=http_client)
            async with Client(transport) as client:
                result = await client.call_tool(request.operation, request.arguments)

        if result.is_error is True:
            return _failure(
                "remote_failure",
                "remote_tool_error",
                f"The remote SDK operation '{request.operation}' returned an error.",
                details=_remote_tool_error_details(result),
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
        self._access_context.apply(headers)
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

    async def _on_http_request(self, request: Any) -> None:
        self._access_context.apply(request.headers)

    async def _on_http_response(self, response: Any) -> None:
        self._access_context.observe(response.headers)



class _AiSdkAccessContextState:
    def __init__(
        self,
        header_name: str | None,
        initial_headers: Mapping[str, str] | None = None,
    ) -> None:
        self._header_name = None if header_name is None else header_name.strip()
        self._current: str | None = None

        if self._header_name is None or initial_headers is None:
            return

        expected = self._header_name.lower()
        for name, value in initial_headers.items():
            if name.lower() == expected and self._valid_value(value):
                self._current = value.strip()
                break

    @property
    def current(self) -> str | None:
        return self._current

    def apply(self, headers: MutableMapping[str, str]) -> None:
        if self._header_name is None or self._current is None:
            return

        expected = self._header_name.lower()
        for name in tuple(headers.keys()):
            if name.lower() == expected and name != self._header_name:
                del headers[name]

        headers[self._header_name] = self._current

    def observe(self, headers: Mapping[str, str]) -> None:
        if self._header_name is None:
            return

        expected = self._header_name.lower()
        values = [
            value.strip()
            for name, value in headers.items()
            if name.lower() == expected and self._valid_value(value)
        ]

        if len(set(values)) == 1 and values:
            self._current = values[0]

    @staticmethod
    def _valid_value(value: str | None) -> bool:
        return (
            isinstance(value, str)
            and bool(value.strip())
            and "\r" not in value
            and "\n" not in value
        )



def _create_http_timeout(httpx2_module: Any, options: AiSdkTransportOptions) -> object:
    # MCP's Streamable HTTP client uses a deliberately long read timeout because a
    # server may keep a solicited response stream open while work is in progress.
    # Supplying our own AsyncClient for auth/headers means we must preserve that
    # transport characteristic explicitly instead of inheriting the HTTP client's
    # much shorter generic defaults.
    return httpx2_module.Timeout(
        connect=options.connect_timeout_seconds,
        read=options.read_timeout_seconds,
        write=options.write_timeout_seconds,
        pool=options.pool_timeout_seconds,
    )


def _exception_leaves(exc: Exception) -> Iterable[Exception]:
    nested = getattr(exc, "exceptions", None)
    if isinstance(nested, (tuple, list)) and nested:
        emitted = False
        for child in nested:
            if isinstance(child, Exception):
                emitted = True
                yield from _exception_leaves(child)
        if emitted:
            return
    yield exc


def _primary_transport_exception(exc: Exception) -> Exception:
    leaves = tuple(_exception_leaves(exc))
    for leaf in leaves:
        if _status_code(leaf) is not None or _is_timeout(leaf) or _is_network_exception(leaf):
            return leaf
    return leaves[0] if leaves else exc


def _is_retryable_transport_failure(exc: Exception) -> bool:
    for leaf in _exception_leaves(exc):
        status = _status_code(leaf)
        if status is not None:
            if status in (408, 429) or status >= 500:
                return True
            continue
        if _is_network_exception(leaf):
            return True
    return False


def _is_network_exception(exc: Exception) -> bool:
    if _is_timeout(exc) or isinstance(exc, (ConnectionError, OSError)):
        return True
    return type(exc).__name__ in {
        "ConnectError",
        "NetworkError",
        "ReadError",
        "ReadTimeout",
        "TimeoutException",
        "TransportError",
        "WriteError",
        "WriteTimeout",
        "PoolTimeout",
        "ConnectTimeout",
    }


def _normalize_transport_failure(exc: Exception, retryable: bool) -> AiSdkTransportResponse:
    primary = _primary_transport_exception(exc)
    status = _status_code(primary)
    if status == 401:
        return _failure("authentication", "authentication_failed", "The SDK transport was not authenticated.")
    if status == 403:
        return _failure(
            "authorization",
            "authorization_failed",
            "The SDK transport is not authorized for the requested operation.",
        )

    message = str(primary) or type(primary).__name__
    details: AiSdkJsonObject = {"exceptionType": type(primary).__name__}
    if primary is not exc:
        details["exceptionGroupType"] = type(exc).__name__

    return AiSdkTransportResponse(
        error=AiSdkError(
            kind="transport",
            code="transport_timeout" if _is_timeout(primary) else "transport_failure",
            message=message,
            retryable=retryable,
            details=details,
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


def _remote_tool_error_details(result: object) -> AiSdkJsonObject:
    # MCP tool errors are already normalized by the server into the same CallToolResult.
    # Preserve bounded textual diagnostics from that exact response instead of issuing a
    # second non-idempotent tool call solely to discover why publication/submission failed.
    details: AiSdkJsonObject = {}
    messages: list[str] = []
    for item in getattr(result, "content", None) or []:
        text = getattr(item, "text", None)
        if text is None and isinstance(item, dict):
            text = item.get("text")
        if isinstance(text, str) and text.strip():
            messages.append(text[:4096])
        if len(messages) >= 8:
            break
    if messages:
        details["remoteContent"] = messages

    structured = getattr(result, "structured_content", None)
    if isinstance(structured, dict):
        # Keep only ordinary JSON values and bound the rendered payload. The server owns
        # this document; transport diagnostics must never turn into an unbounded echo.
        try:
            import json

            rendered = json.dumps(structured, ensure_ascii=False, separators=(",", ":"), default=str)
            if len(rendered) <= 8192:
                decoded = json.loads(rendered)
                if isinstance(decoded, dict):
                    details["remoteStructuredContent"] = decoded
        except (TypeError, ValueError):
            pass
    return details


def _failure(
    kind: str,
    code: str,
    message: str,
    *,
    details: AiSdkJsonObject | None = None,
) -> AiSdkTransportResponse:
    return AiSdkTransportResponse(
        error=AiSdkError(kind=kind, code=code, message=message, details=details or {})
    )  # type: ignore[arg-type]
