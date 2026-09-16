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


class AiSdkTransport(Protocol):
    async def invoke(self, request: AiSdkTransportRequest) -> AiSdkTransportResponse:
        """Cancel the awaiting task to cancel only the in-flight transport call."""
        ...
