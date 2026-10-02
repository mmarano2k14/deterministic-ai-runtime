from __future__ import annotations

from dataclasses import dataclass, field
from typing import Literal

from .json_types import AiSdkJsonObject

AiSdkErrorKind = Literal[
    "transport",
    "authentication",
    "authorization",
    "invalid_request",
    "unsupported_schema",
    "not_found",
    "conflict",
    "result_unavailable",
    "remote_failure",
    "invalid_response",
    "cancelled",
]


@dataclass(frozen=True)
class AiSdkError:
    kind: AiSdkErrorKind
    code: str
    message: str
    retryable: bool = False
    details: AiSdkJsonObject = field(default_factory=dict)


class AiSdkException(Exception):
    def __init__(self, error: AiSdkError) -> None:
        super().__init__(error.message)
        self.error = error
