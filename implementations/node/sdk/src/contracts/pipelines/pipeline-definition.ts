import type { AiSdkJsonValue } from "../../json/json-value.js";
import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkExecutionMode } from "./execution-mode.js";
import type { AiSdkPipelineStepDefinition } from "./pipeline-step-definition.js";

export interface AiSdkPipelineDefinition {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.pipelineDefinition;
  readonly name: string;
  readonly version?: string;
  readonly executionLanguage?: string;
  readonly executionMode?: AiSdkExecutionMode;
  readonly steps?: readonly AiSdkPipelineStepDefinition[];
  readonly config?: Readonly<Record<string, AiSdkJsonValue>>;
}
