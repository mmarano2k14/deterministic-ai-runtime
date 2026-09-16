export const AI_SDK_INVOCATION_KINDS = ["Native", "Custom", "Mcp"] as const;

export type AiSdkInvocationKind = (typeof AI_SDK_INVOCATION_KINDS)[number];
