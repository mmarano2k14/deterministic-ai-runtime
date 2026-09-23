import type { AiSdkExecutionCancellationRequest } from "../contracts/control/execution-cancellation-request.js";
import type { AiSdkExecutionCancellationResponse } from "../contracts/control/execution-cancellation-response.js";
import type { AiSdkExecutionControlRequest } from "../contracts/control/execution-control-request.js";
import type { AiSdkExecutionControlResponse } from "../contracts/control/execution-control-response.js";
import type { AiSdkExecutionInputSubmissionRequest } from "../contracts/control/execution-input-submission-request.js";
import type { AiSdkExecutionResult } from "../contracts/executions/execution-result.js";
import type { AiSdkExecutionSubmissionRequest } from "../contracts/executions/execution-submission-request.js";
import type { AiSdkExecutionSubmissionResponse } from "../contracts/executions/execution-submission-response.js";
import type { AiSdkExecutionObservation } from "../contracts/observation/execution-observation.js";
import type { AiSdkExecutionWatchEvent } from "../contracts/watch/execution-watch-event.js";
import type { AiSdkExecutionWatchRequest } from "../contracts/watch/execution-watch-request.js";
import type { AiSdkPipelinePublicationRequest } from "../contracts/publication/pipeline-publication-request.js";
import type { AiSdkPipelinePublicationResponse } from "../contracts/publication/pipeline-publication-response.js";
import type { AiSdkExecutionReplayRequest } from "../contracts/replay/execution-replay-request.js";
import type { AiSdkExecutionReplayResponse } from "../contracts/replay/execution-replay-response.js";

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

  watchExecution(
    request: AiSdkExecutionWatchRequest,
    signal?: AbortSignal,
  ): AsyncIterable<AiSdkExecutionWatchEvent>;

  getExecutionResult(
    executionId: string,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionResult>;

  cancelExecution(
    executionId: string,
    request: AiSdkExecutionCancellationRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionCancellationResponse>;

  pauseExecution(
    executionId: string,
    request?: AiSdkExecutionControlRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionControlResponse>;

  resumeExecution(
    executionId: string,
    request?: AiSdkExecutionControlRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionControlResponse>;

  submitExecutionInput(
    executionId: string,
    request: AiSdkExecutionInputSubmissionRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionControlResponse>;

  replayExecution(
    executionId: string,
    request?: AiSdkExecutionReplayRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionReplayResponse>;
}
