from dataclasses import dataclass

from ...wire_model import AiSdkWireModel


@dataclass(frozen=True)
class AiSdkPipelineStepExecutionDefinition(AiSdkWireModel):
    max_retries: int
    retry_delay_ms: int
