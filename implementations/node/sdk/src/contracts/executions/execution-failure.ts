import type { AiSdkJsonValue } from "../../json/json-value.js";

export interface AiSdkExecutionFailure {
  readonly code: string;
  readonly message: string;
  readonly details: Readonly<Record<string, AiSdkJsonValue>>;
}
