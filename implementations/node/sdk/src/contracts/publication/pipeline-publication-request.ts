import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";
import type { AiSdkPipelineDefinition } from "../pipelines/pipeline-definition.js";
import type { AiSdkPublicationFunctionUpload } from "./publication-function-upload.js";

export interface AiSdkPipelinePublicationRequest {
  readonly schemaVersion?: typeof AI_SDK_SCHEMA_VERSIONS.pipelinePublicationRequest;
  readonly definition: AiSdkPipelineDefinition;
  readonly functions?: readonly AiSdkPublicationFunctionUpload[];
}
