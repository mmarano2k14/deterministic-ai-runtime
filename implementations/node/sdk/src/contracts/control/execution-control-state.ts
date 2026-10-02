import type { AiSdkExecutionControlAction } from "./execution-control-action.js";
import type { AiSdkExecutionControlStatus } from "./execution-control-status.js";

export interface AiSdkExecutionControlState {
  readonly status: AiSdkExecutionControlStatus;
  readonly pendingAction: AiSdkExecutionControlAction;
  readonly reason?: string;
  readonly waitingKey?: string;
  readonly waitingStepName?: string;
  readonly updatedAtUtc: string;
  readonly pauseRequestedAtUtc?: string;
  readonly pausedAtUtc?: string;
  readonly resumeRequestedAtUtc?: string;
  readonly inputReceivedAtUtc?: string;
}
