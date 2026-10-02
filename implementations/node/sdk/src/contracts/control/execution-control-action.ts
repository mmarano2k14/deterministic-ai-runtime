export const AI_SDK_EXECUTION_CONTROL_ACTIONS = [
  "None",
  "Pause",
  "Resume",
  "Cancel",
  "WaitForInput",
  "SubmitInput",
] as const;

export type AiSdkExecutionControlAction =
  (typeof AI_SDK_EXECUTION_CONTROL_ACTIONS)[number];
