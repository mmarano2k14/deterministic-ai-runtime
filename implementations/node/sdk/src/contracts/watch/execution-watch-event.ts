import type { AiSdkJsonValue } from "../../json/json-value.js";
import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionObservation } from "../observation/execution-observation.js";
import type { AiSdkExecutionWatchChannel } from "./execution-watch-channel.js";
import type { AiSdkExecutionWatchEventKind } from "./execution-watch-event-kind.js";
import type { AiSdkExecutionWatchResyncRequired } from "./execution-watch-resync-required.js";

/**
 * Stable public watch envelope. `sequence` is a public resume cursor and is not a runtime revision,
 * lease or epoch. Event payloads are explicit public projections, never serialized runtime records.
 */
export interface AiSdkExecutionWatchEvent {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionWatchEvent;
  readonly executionId: string;
  readonly sequence?: number;
  readonly kind: AiSdkExecutionWatchEventKind;
  readonly occurredAtUtc: string;
  readonly channel?: AiSdkExecutionWatchChannel;
  readonly eventType?: string;
  readonly payloadSchemaVersion?: number;
  readonly payload?: AiSdkJsonValue;
  readonly snapshot?: AiSdkExecutionObservation;
  readonly resyncRequired?: AiSdkExecutionWatchResyncRequired;
}
