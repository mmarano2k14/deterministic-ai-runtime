import { AI_SDK_SCHEMA_VERSIONS } from "../common/schema-versions.js";

export interface AiSdkPipelinePublicationResponse {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.pipelinePublicationResponse;
  readonly publicationRef: string;
  readonly publicationSha256: string;
  readonly pipelineName: string;
  readonly pipelineVersion: string;
}
