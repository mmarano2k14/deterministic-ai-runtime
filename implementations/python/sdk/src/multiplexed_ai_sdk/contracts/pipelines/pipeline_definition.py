from __future__ import annotations

from dataclasses import dataclass, field

from ...json_types import AiSdkJsonValue
from ...wire_model import AiSdkWireModel
from ..common.schema_versions import AiSdkSchemaVersions
from .execution_mode import AiSdkExecutionMode
from .pipeline_step_definition import AiSdkPipelineStepDefinition


@dataclass(frozen=True)
class AiSdkPipelineDefinition(AiSdkWireModel):
    name: str
    schema_version: int = AiSdkSchemaVersions.PIPELINE_DEFINITION
    version: str | None = None
    execution_language: str | None = None
    execution_mode: AiSdkExecutionMode = AiSdkExecutionMode.SEQUENTIAL
    steps: tuple[AiSdkPipelineStepDefinition, ...] = ()
    config: dict[str, AiSdkJsonValue] = field(default_factory=dict, metadata={"json": True})
