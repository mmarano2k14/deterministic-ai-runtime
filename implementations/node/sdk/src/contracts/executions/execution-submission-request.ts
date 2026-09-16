import type { AiSdkJsonValue } from "../../json/json-value.js";
import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";

export interface AiSdkExecutionSubmissionRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.executionSubmissionRequest;
  readonly publicationRef: string;
  readonly idempotencyKey?: string;
  readonly input?: AiSdkJsonValue;
  readonly metadata?: Readonly<Record<string, string>>;
  readonly correlationId?: string;
}
