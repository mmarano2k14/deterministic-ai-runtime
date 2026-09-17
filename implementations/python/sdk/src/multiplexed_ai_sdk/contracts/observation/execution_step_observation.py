from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .execution_step_status import AiSdkExecutionStepStatus


@dataclass(frozen=True)
class AiSdkExecutionStepObservation(AiSdkWireModel):
    name: str
    step_key: str
    status: AiSdkExecutionStepStatus
    started_at_utc: str | None = None
    updated_at_utc: str | None = None
    completed_at_utc: str | None = None
