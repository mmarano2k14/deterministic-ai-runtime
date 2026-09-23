export const AI_SDK_EXECUTION_CONTROL_OPERATIONS = [
  "Pause",
  "Resume",
  "SubmitInput",
] as const;

export type AiSdkExecutionControlOperation =
  (typeof AI_SDK_EXECUTION_CONTROL_OPERATIONS)[number];
