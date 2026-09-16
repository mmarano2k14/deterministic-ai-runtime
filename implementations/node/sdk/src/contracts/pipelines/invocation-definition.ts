import type { AiSdkInvocationKind } from "./invocation-kind.js";

export interface AiSdkInvocationDefinition {
  readonly kind: AiSdkInvocationKind;
  readonly implementationRef?: string;
  readonly connectionRef?: string;
  readonly tool?: string;
}
