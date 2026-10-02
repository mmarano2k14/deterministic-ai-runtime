import type { AiSdkPublicationFunctionKind } from "./publication-function-kind.js";

export interface AiSdkPublicationCallSite {
  readonly kind: AiSdkPublicationFunctionKind;
  readonly stepName?: string;
  readonly policyIndex?: number;
  readonly definitionPath?: string;
}
