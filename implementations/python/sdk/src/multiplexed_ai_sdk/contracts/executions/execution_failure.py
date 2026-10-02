from __future__ import annotations

from dataclasses import dataclass, field

from ...json_types import AiSdkJsonValue
from ...wire_model import AiSdkWireModel


@dataclass(frozen=True)
class AiSdkExecutionFailure(AiSdkWireModel):
    code: str
    message: str
    details: dict[str, AiSdkJsonValue] = field(default_factory=dict, metadata={"json": True})
