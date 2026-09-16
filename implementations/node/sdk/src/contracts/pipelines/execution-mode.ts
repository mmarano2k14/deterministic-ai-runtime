export const AI_SDK_EXECUTION_MODES = ["Sequential", "Dag"] as const;

export type AiSdkExecutionMode = (typeof AI_SDK_EXECUTION_MODES)[number];
