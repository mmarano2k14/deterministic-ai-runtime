from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .invocation_kind import AiSdkInvocationKind


@dataclass(frozen=True)
class AiSdkInvocationDefinition(AiSdkWireModel):
    kind: AiSdkInvocationKind
    implementation_ref: str | None = None
    connection_ref: str | None = None
    tool: str | None = None
