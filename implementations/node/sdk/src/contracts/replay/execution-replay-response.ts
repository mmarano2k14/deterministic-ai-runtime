import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";

export interface AiSdkExecutionReplayResponse {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionReplayResponse;
  readonly executionId: string;
  readonly succeeded: boolean;
  readonly deterministic?: boolean;
  readonly message?: string;
  readonly diagnostics: readonly string[];
  readonly failureReason?: string;
  readonly correlationId?: string;
  readonly startedAtUtc: string;
  readonly completedAtUtc: string;
  readonly durationMs: number;
}
