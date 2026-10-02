from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Any, Mapping
from urllib.parse import urlparse

from .authentication import AiSdkCredential, AiSdkCredentialProvider
from .errors import AiSdkError, AiSdkException

_DEFAULT_ACCESS_CONTEXT_HEADER = "X-Access-Context"
_DEFAULT_TIMEOUT_SECONDS = 30.0


@dataclass(frozen=True)
class AiSdkAccessContextBootstrapOptions:
    """Configuration for one explicit access-context bootstrap request."""

    endpoint: str
    credential_provider: AiSdkCredentialProvider | None = None
    access_context_header_name: str = _DEFAULT_ACCESS_CONTEXT_HEADER
    timeout_seconds: float = _DEFAULT_TIMEOUT_SECONDS

    def __post_init__(self) -> None:
        parsed = urlparse(self.endpoint)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc:
            raise ValueError(
                "The access-context bootstrap endpoint must be an absolute HTTP or HTTPS URL."
            )

        header_name = self.access_context_header_name.strip()
        if (
            not header_name
            or ":" in header_name
            or "\r" in header_name
            or "\n" in header_name
            or any(ord(character) < 32 or ord(character) == 127 for character in header_name)
        ):
            raise ValueError(
                "access_context_header_name must be a valid non-empty HTTP header name."
            )

        if self.timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be greater than zero")


@dataclass(frozen=True)
class AiSdkAccessContextBootstrapResult:
    access_context: str
    header_name: str


class AiSdkAccessContextBootstrapper:
    """Creates the initial runtime access-context handle over authenticated HTTP.

    The POST is deliberately explicit and is never retried automatically because
    access-context creation changes server state. Subsequent handle rotation is owned
    by the physical SDK transport.
    """

    @staticmethod
    async def create(
        options: AiSdkAccessContextBootstrapOptions,
    ) -> AiSdkAccessContextBootstrapResult:
        if options is None:
            raise ValueError("options is required")

        credential = await _get_credential(options.credential_provider)
        headers: dict[str, str] = {}
        if credential is not None:
            headers["Authorization"] = _format_authorization(credential)

        try:
            async with _create_http_client(options.timeout_seconds) as client:
                response = await client.post(options.endpoint, headers=headers or None)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            if _is_timeout(exc):
                raise _sdk_exception(
                    "transport",
                    "access_context_bootstrap_timeout",
                    "The access-context bootstrap request timed out.",
                    exc,
                ) from exc
            raise _sdk_exception(
                "transport",
                "access_context_bootstrap_transport_failure",
                str(exc) or type(exc).__name__,
                exc,
            ) from exc

        status_code = int(getattr(response, "status_code", 0))
        if status_code == 401:
            raise _sdk_exception(
                "authentication",
                "access_context_bootstrap_unauthenticated",
                "The access-context bootstrap request was not authenticated.",
            )
        if status_code == 403:
            raise _sdk_exception(
                "authorization",
                "access_context_bootstrap_forbidden",
                "The authenticated identity is not allowed to create an access context.",
            )
        if status_code < 200 or status_code >= 300:
            reason = getattr(response, "reason_phrase", "")
            suffix = f" ({reason})" if isinstance(reason, str) and reason.strip() else ""
            raise _sdk_exception(
                "remote_failure",
                "access_context_bootstrap_failed",
                f"The access-context bootstrap endpoint returned HTTP {status_code}{suffix}.",
            )

        header_name = options.access_context_header_name.strip()
        values = _header_values(getattr(response, "headers", {}), header_name)
        normalized = tuple(dict.fromkeys(value.strip() for value in values if _valid_header_value(value)))

        if len(normalized) != 1:
            raise _sdk_exception(
                "invalid_response",
                "access_context_bootstrap_missing_handle",
                f"The access-context bootstrap response did not contain one unambiguous '{header_name}' header.",
            )

        return AiSdkAccessContextBootstrapResult(
            access_context=normalized[0],
            header_name=header_name,
        )


async def _get_credential(
    provider: AiSdkCredentialProvider | None,
) -> AiSdkCredential | None:
    if provider is None:
        return None

    try:
        return await provider.get_credential()
    except asyncio.CancelledError:
        raise
    except Exception as exc:
        raise _sdk_exception(
            "authentication",
            "credential_provider_failure",
            "The SDK credential provider failed to supply a bootstrap credential.",
            exc,
        ) from exc


def _format_authorization(credential: AiSdkCredential) -> str:
    scheme = credential.scheme.strip()
    value = credential.value.strip()

    if (
        not scheme
        or not value
        or any(character.isspace() or character == ":" or ord(character) < 32 or ord(character) == 127 for character in scheme)
        or "\r" in value
        or "\n" in value
    ):
        raise _sdk_exception(
            "authentication",
            "invalid_credential",
            "The SDK credential provider returned an invalid bootstrap credential.",
        )

    return f"{scheme} {value}"


def _header_values(headers: Mapping[str, str] | Any, name: str) -> tuple[str, ...]:
    get_list = getattr(headers, "get_list", None)
    if callable(get_list):
        raw = get_list(name)
        if isinstance(raw, list):
            return tuple(value for value in raw if isinstance(value, str))

    expected = name.lower()
    values = [
        value
        for header_name, value in getattr(headers, "items", lambda: ())()
        if isinstance(header_name, str)
        and header_name.lower() == expected
        and isinstance(value, str)
    ]
    return tuple(values)


def _valid_header_value(value: str | None) -> bool:
    return (
        isinstance(value, str)
        and bool(value.strip())
        and "\r" not in value
        and "\n" not in value
    )


def _is_timeout(exc: Exception) -> bool:
    return isinstance(exc, TimeoutError) or "Timeout" in type(exc).__name__


def _create_http_client(timeout_seconds: float) -> Any:
    import httpx2

    return httpx2.AsyncClient(timeout=timeout_seconds)


def _sdk_exception(
    kind: str,
    code: str,
    message: str,
    cause: Exception | None = None,
) -> AiSdkException:
    details = {} if cause is None else {"cause": str(cause) or type(cause).__name__}
    return AiSdkException(
        AiSdkError(
            kind=kind,  # type: ignore[arg-type]
            code=code,
            message=message,
            retryable=False,
            details=details,
        )
    )
