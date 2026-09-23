from __future__ import annotations

from dataclasses import dataclass
from typing import Protocol

from .authentication import AiSdkCredentialProvider
from .errors import AiSdkError
from .json_types import AiSdkJsonObject, AiSdkJsonValue
from .protocol import AI_SDK_PROTOCOL_VERSION


@dataclass(frozen=True)
class AiSdkTransportRequest:
    operation: str
    arguments: AiSdkJsonObject
    protocol_version: int = AI_SDK_PROTOCOL_VERSION


@dataclass(frozen=True)
class AiSdkTransportResponse:
    result: AiSdkJsonValue = None
    error: AiSdkError | None = None

    @property
    def is_success(self) -> bool:
        return self.error is None


@dataclass(frozen=True)
class AiSdkTransportOptions:
    credential_provider: AiSdkCredentialProvider | None = None
    additional_headers: dict[str, str] | None = None
    safe_read_max_attempts: int = 2
    safe_read_retry_delay_seconds: float = 0.1
    connect_timeout_seconds: float = 30.0
    read_timeout_seconds: float = 300.0
    write_timeout_seconds: float = 30.0
    pool_timeout_seconds: float = 30.0

    def __post_init__(self) -> None:
        if self.safe_read_max_attempts < 1:
            raise ValueError("safe_read_max_attempts must be at least 1")
        if self.safe_read_retry_delay_seconds < 0:
            raise ValueError("safe_read_retry_delay_seconds cannot be negative")
        for name, value in (
            ("connect_timeout_seconds", self.connect_timeout_seconds),
            ("read_timeout_seconds", self.read_timeout_seconds),
            ("write_timeout_seconds", self.write_timeout_seconds),
            ("pool_timeout_seconds", self.pool_timeout_seconds),
        ):
            if value <= 0:
                raise ValueError(f"{name} must be greater than zero")
        for name, value in (self.additional_headers or {}).items():
            if (
                not name.strip()
                or not value.strip()
                or name.lower() == "authorization"
                or ":" in name
                or "\r" in name
                or "\n" in name
                or "\r" in value
                or "\n" in value
            ):
                raise ValueError(
                    "Additional transport headers must be non-empty, single-line headers and cannot override Authorization"
                )


class AiSdkTransport(Protocol):
    async def invoke(self, request: AiSdkTransportRequest) -> AiSdkTransportResponse:
        """Cancelling the awaiting task cancels only the in-flight client call."""
        ...
