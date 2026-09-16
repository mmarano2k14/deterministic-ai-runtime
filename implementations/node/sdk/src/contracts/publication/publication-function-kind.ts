export const AI_SDK_PUBLICATION_FUNCTION_KINDS = [
  "Step",
  "ConcurrencyPolicy",
  "RetryPolicy",
  "DelegationPolicy",
] as const;

export type AiSdkPublicationFunctionKind =
  (typeof AI_SDK_PUBLICATION_FUNCTION_KINDS)[number];
