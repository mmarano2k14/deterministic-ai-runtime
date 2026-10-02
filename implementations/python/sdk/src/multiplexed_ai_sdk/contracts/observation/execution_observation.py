from __future__ import annotations

from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from ..executions.execution_status import AiSdkExecutionStatus
from .execution_step_observation import AiSdkExecutionStepObservation


@dataclass(frozen=True)
class AiSdkExecutionObservation(AiSdkWireModel):
    execution_id: str
    publication_ref: str
    pipeline_name: str
    pipeline_version: str
    status: AiSdkExecutionStatus
    created_at_utc: str
    updated_at_utc: str
    steps: tuple[AiSdkExecutionStepObservation, ...]
    schema_version: int = AiSdkSchemaVersions.EXECUTION_OBSERVATION
    completed_at_utc: str | None = None
