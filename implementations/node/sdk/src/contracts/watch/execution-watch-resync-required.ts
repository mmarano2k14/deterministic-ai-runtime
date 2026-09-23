import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionWatchResyncReason } from "./execution-watch-resync-reason.js";

export interface AiSdkExecutionWatchResyncRequired {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionWatchResyncRequired;
  readonly reason: AiSdkExecutionWatchResyncReason;
  readonly requestedAfterSequence?: number;
  readonly earliestAvailableSequence?: number;
  readonly latestSequence?: number;
  readonly message?: string;
}
