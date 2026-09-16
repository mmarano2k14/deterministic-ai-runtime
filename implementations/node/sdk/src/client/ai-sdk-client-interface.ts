import type { AiSdkExecutionCancellationRequest } from "../contracts/control/execution-cancellation-request.js";
import type { AiSdkExecutionCancellationResponse } from "../contracts/control/execution-cancellation-response.js";
import type { AiSdkExecutionResult } from "../contracts/executions/execution-result.js";
import type { AiSdkExecutionSubmissionRequest } from "../contracts/executions/execution-submission-request.js";
import type { AiSdkExecutionSubmissionResponse } from "../contracts/executions/execution-submission-response.js";
import type { AiSdkExecutionObservation } from "../contracts/observation/execution-observation.js";
import type { AiSdkPipelinePublicationRequest } from "../contracts/publication/pipeline-publication-request.js";
import type { AiSdkPipelinePublicationResponse } from "../contracts/publication/pipeline-publication-response.js";

export interface AiSdkClientInterface {
  publishPipeline(
    request: AiSdkPipelinePublicationRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkPipelinePublicationResponse>;

  submitExecution(
    request: AiSdkExecutionSubmissionRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionSubmissionResponse>;

  observeExecution(
    executionId: string,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionObservation>;

  getExecutionResult(
    executionId: string,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionResult>;

  cancelExecution(
    executionId: string,
    request: AiSdkExecutionCancellationRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionCancellationResponse>;
}
