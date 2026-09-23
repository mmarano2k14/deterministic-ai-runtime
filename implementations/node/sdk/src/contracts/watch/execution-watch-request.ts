import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionWatchChannel } from "./execution-watch-channel.js";

/** Portable watch request. An empty channels array means all public channels. */
export interface AiSdkExecutionWatchRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.executionWatchRequest;
  readonly executionId: string;
  readonly channels?: readonly AiSdkExecutionWatchChannel[];
  readonly afterSequence?: number;
  readonly includeInitialSnapshot?: boolean;
}
