from dataclasses import dataclass

from ...wire_model import AiSdkWireModel
from .execution_control_action import AiSdkExecutionControlAction
from .execution_control_status import AiSdkExecutionControlStatus


@dataclass(frozen=True)
class AiSdkExecutionControlState(AiSdkWireModel):
    status: AiSdkExecutionControlStatus
    pending_action: AiSdkExecutionControlAction
    updated_at_utc: str
    reason: str | None = None
    waiting_key: str | None = None
    waiting_step_name: str | None = None
    pause_requested_at_utc: str | None = None
    paused_at_utc: str | None = None
    resume_requested_at_utc: str | None = None
    input_received_at_utc: str | None = None
