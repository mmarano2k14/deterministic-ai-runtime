from __future__ import annotations

from dataclasses import dataclass, field

from ...json_types import AiSdkJsonValue
from ...wire_model import AiSdkWireModel
from .invocation_definition import AiSdkInvocationDefinition
from .pipeline_step_execution_definition import AiSdkPipelineStepExecutionDefinition


@dataclass(frozen=True)
class AiSdkPipelineStepDefinition(AiSdkWireModel):
    name: str
    step_key: str
    order: int
    execution_language: str | None = None
    invocation: AiSdkInvocationDefinition | None = None
    depends_on: tuple[str, ...] = ()
    input: dict[str, AiSdkJsonValue] = field(default_factory=dict, metadata={"json": True})
    config: dict[str, AiSdkJsonValue] = field(default_factory=dict, metadata={"json": True})
    execution: AiSdkPipelineStepExecutionDefinition | None = None
