import { AI_SDK_SCHEMA_VERSIONS } from "../contracts/common/schema-versions.js";
import type { AiSdkExecutionCancellationRequest } from "../contracts/control/execution-cancellation-request.js";
import type { AiSdkExecutionCancellationResponse } from "../contracts/control/execution-cancellation-response.js";
import type { AiSdkExecutionControlRequest } from "../contracts/control/execution-control-request.js";
import type { AiSdkExecutionControlResponse } from "../contracts/control/execution-control-response.js";
import type { AiSdkExecutionInputSubmissionRequest } from "../contracts/control/execution-input-submission-request.js";
import type { AiSdkExecutionResult } from "../contracts/executions/execution-result.js";
import type { AiSdkExecutionSubmissionRequest } from "../contracts/executions/execution-submission-request.js";
import type { AiSdkExecutionSubmissionResponse } from "../contracts/executions/execution-submission-response.js";
import type { AiSdkExecutionObservation } from "../contracts/observation/execution-observation.js";
import { AI_SDK_EXECUTION_WATCH_CHANNELS } from "../contracts/watch/execution-watch-channel.js";
import type { AiSdkExecutionWatchEvent } from "../contracts/watch/execution-watch-event.js";
import type { AiSdkExecutionWatchRequest } from "../contracts/watch/execution-watch-request.js";
import type { AiSdkPipelineStepDefinition } from "../contracts/pipelines/pipeline-step-definition.js";
import type { AiSdkPipelinePublicationRequest } from "../contracts/publication/pipeline-publication-request.js";
import type { AiSdkPipelinePublicationResponse } from "../contracts/publication/pipeline-publication-response.js";
import type { AiSdkExecutionReplayRequest } from "../contracts/replay/execution-replay-request.js";
import type { AiSdkExecutionReplayResponse } from "../contracts/replay/execution-replay-response.js";
import type { AiSdkPublicationDependencyUpload } from "../contracts/publication/publication-dependency-upload.js";
import type { AiSdkPublicationFunctionUpload } from "../contracts/publication/publication-function-upload.js";
import { AiSdkException } from "../errors/sdk-exception.js";
import { createAiSdkError } from "../errors/sdk-error.js";
import type { AiSdkJsonObject, AiSdkJsonValue } from "../json/json-value.js";
import { isAiSdkJsonObject } from "../json/json-value.js";
import { AI_SDK_OPERATIONS, AI_SDK_PROTOCOL_VERSION } from "../protocol/operations.js";
import type { AiSdkTransport } from "../transport/transport.js";
import type { AiSdkClientInterface } from "./ai-sdk-client-interface.js";

const MAX_CONSECUTIVE_DUPLICATE_WATCH_ITEMS = 3;

export class AiSdkClient implements AiSdkClientInterface {
  readonly #transport: AiSdkTransport;

  public constructor(transport: AiSdkTransport) {
    this.#transport = transport;
  }

  public async publishPipeline(
    request: AiSdkPipelinePublicationRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkPipelinePublicationResponse> {
    const normalized = normalizePublicationRequest(request);
    validateSchemaVersion(
      "pipeline publication request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.pipelinePublicationRequest,
    );

    return this.#invoke<AiSdkPipelinePublicationResponse>(
      AI_SDK_OPERATIONS.publishPipeline,
      { request: normalized as unknown as AiSdkJsonValue },
      AI_SDK_SCHEMA_VERSIONS.pipelinePublicationResponse,
      signal,
    );
  }

  public async submitExecution(
    request: AiSdkExecutionSubmissionRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionSubmissionResponse> {
    const normalized = normalizeSubmissionRequest(request);
    validateSchemaVersion(
      "execution submission request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionSubmissionRequest,
    );

    return this.#invoke<AiSdkExecutionSubmissionResponse>(
      AI_SDK_OPERATIONS.submitExecution,
      { request: normalized as unknown as AiSdkJsonValue },
      AI_SDK_SCHEMA_VERSIONS.executionSubmissionResponse,
      signal,
    );
  }

  public async observeExecution(
    executionId: string,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionObservation> {
    validateExecutionId(executionId);
    return this.#invoke<AiSdkExecutionObservation>(
      AI_SDK_OPERATIONS.observeExecution,
      { executionId },
      AI_SDK_SCHEMA_VERSIONS.executionObservation,
      signal,
    );
  }

  public watchExecution(
    request: AiSdkExecutionWatchRequest,
    signal?: AbortSignal,
  ): AsyncIterable<AiSdkExecutionWatchEvent> {
    const normalized = normalizeWatchRequest(request);
    validateSchemaVersion(
      "execution watch request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionWatchRequest,
    );
    validateExecutionId(normalized.executionId);
    validateWatchChannels(normalized.channels);

    return this.#watchExecutionCore(normalized, signal);
  }

  public async getExecutionResult(
    executionId: string,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionResult> {
    validateExecutionId(executionId);
    return this.#invoke<AiSdkExecutionResult>(
      AI_SDK_OPERATIONS.getExecutionResult,
      { executionId },
      AI_SDK_SCHEMA_VERSIONS.executionResult,
      signal,
    );
  }

  public async cancelExecution(
    executionId: string,
    request: AiSdkExecutionCancellationRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionCancellationResponse> {
    validateExecutionId(executionId);
    const normalized = normalizeCancellationRequest(request);
    validateSchemaVersion(
      "execution cancellation request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionCancellationRequest,
    );

    return this.#invoke<AiSdkExecutionCancellationResponse>(
      AI_SDK_OPERATIONS.cancelExecution,
      {
        executionId,
        request: normalized as unknown as AiSdkJsonValue,
      },
      AI_SDK_SCHEMA_VERSIONS.executionCancellationResponse,
      signal,
    );
  }

  public async pauseExecution(
    executionId: string,
    request: AiSdkExecutionControlRequest = {},
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionControlResponse> {
    validateExecutionId(executionId);
    const normalized = normalizeControlRequest(request);
    validateSchemaVersion(
      "execution control request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionControlRequest,
    );
    return this.#invoke<AiSdkExecutionControlResponse>(
      AI_SDK_OPERATIONS.pauseExecution,
      { executionId, request: normalized },
      AI_SDK_SCHEMA_VERSIONS.executionControlResponse,
      signal,
    );
  }

  public async resumeExecution(
    executionId: string,
    request: AiSdkExecutionControlRequest = {},
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionControlResponse> {
    validateExecutionId(executionId);
    const normalized = normalizeControlRequest(request);
    validateSchemaVersion(
      "execution control request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionControlRequest,
    );
    return this.#invoke<AiSdkExecutionControlResponse>(
      AI_SDK_OPERATIONS.resumeExecution,
      { executionId, request: normalized },
      AI_SDK_SCHEMA_VERSIONS.executionControlResponse,
      signal,
    );
  }

  public async submitExecutionInput(
    executionId: string,
    request: AiSdkExecutionInputSubmissionRequest,
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionControlResponse> {
    validateExecutionId(executionId);
    if (typeof request.waitingKey !== "string" || request.waitingKey.trim().length === 0) {
      throw invalidRequest("waiting_key_required", "A non-empty waitingKey is required.");
    }
    const normalized = normalizeInputSubmissionRequest(request);
    validateSchemaVersion(
      "execution input submission request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionInputSubmissionRequest,
    );
    return this.#invoke<AiSdkExecutionControlResponse>(
      AI_SDK_OPERATIONS.submitExecutionInput,
      { executionId, request: normalized },
      AI_SDK_SCHEMA_VERSIONS.executionControlResponse,
      signal,
    );
  }

  public async replayExecution(
    executionId: string,
    request: AiSdkExecutionReplayRequest = {},
    signal?: AbortSignal,
  ): Promise<AiSdkExecutionReplayResponse> {
    validateExecutionId(executionId);
    const normalized = normalizeReplayRequest(request);
    validateSchemaVersion(
      "execution replay request",
      normalized.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionReplayRequest,
    );
    return this.#invoke<AiSdkExecutionReplayResponse>(
      AI_SDK_OPERATIONS.replayExecution,
      { executionId, request: normalized },
      AI_SDK_SCHEMA_VERSIONS.executionReplayResponse,
      signal,
    );
  }

  async *#watchExecutionCore(
    request: NormalizedWatchRequest,
    signal?: AbortSignal,
  ): AsyncGenerator<AiSdkExecutionWatchEvent, void, void> {
    let afterSequence = request.afterSequence;
    let includeInitialSnapshot = request.includeInitialSnapshot;
    let awaitingResyncSnapshot = false;
    let consecutiveDuplicates = 0;

    while (true) {
      signal?.throwIfAborted();

      const nextRequest: NormalizedWatchRequest = {
        schemaVersion: request.schemaVersion,
        executionId: request.executionId,
        channels: request.channels,
        ...(afterSequence === undefined ? {} : { afterSequence }),
        includeInitialSnapshot,
      };

      const item = await this.#invoke<AiSdkExecutionWatchEvent>(
        AI_SDK_OPERATIONS.watchExecution,
        { request: toWatchRequestJson(nextRequest) },
        AI_SDK_SCHEMA_VERSIONS.executionWatchEvent,
        signal,
      );

      validateWatchEvent(nextRequest, item);

      if (item.kind === "ResyncRequired") {
        if (awaitingResyncSnapshot) {
          throw invalidWatchResponse(
            "watch_resync_loop",
            "The execution Watch server requested another resynchronization before returning the authoritative snapshot.",
          );
        }

        yield item;
        afterSequence = undefined;
        includeInitialSnapshot = true;
        awaitingResyncSnapshot = true;
        consecutiveDuplicates = 0;
        continue;
      }

      if (awaitingResyncSnapshot && item.kind !== "Snapshot") {
        throw invalidWatchResponse(
          "watch_resync_snapshot_required",
          "The execution Watch server did not return the authoritative snapshot required to complete resynchronization.",
        );
      }

      if (afterSequence !== undefined && item.sequence !== undefined) {
        if (item.sequence === afterSequence) {
          consecutiveDuplicates += 1;
          if (consecutiveDuplicates > MAX_CONSECUTIVE_DUPLICATE_WATCH_ITEMS) {
            throw invalidWatchResponse(
              "duplicate_watch_sequence_loop",
              "The execution Watch server repeatedly returned the already-consumed public sequence.",
            );
          }
          continue;
        }

        if (request.channels.length === 0 && item.sequence > afterSequence + 1) {
          yield createGapDetectedResync(
            request.executionId,
            afterSequence,
            item.sequence,
            item.occurredAtUtc,
          );
          afterSequence = undefined;
          includeInitialSnapshot = true;
          awaitingResyncSnapshot = true;
          consecutiveDuplicates = 0;
          continue;
        }
      }

      consecutiveDuplicates = 0;
      yield item;

      if (awaitingResyncSnapshot) {
        awaitingResyncSnapshot = false;
      }

      if (isTerminalWatchItem(item)) {
        return;
      }

      afterSequence = item.sequence;
      includeInitialSnapshot = false;
    }
  }

  async #invoke<TResponse extends { readonly schemaVersion: number }>(
    operation: (typeof AI_SDK_OPERATIONS)[keyof typeof AI_SDK_OPERATIONS],
    args: AiSdkJsonObject,
    expectedResponseSchemaVersion: number,
    signal?: AbortSignal,
  ): Promise<TResponse> {
    signal?.throwIfAborted();

    const response = await this.#transport.invoke(
      {
        protocolVersion: AI_SDK_PROTOCOL_VERSION,
        operation,
        arguments: args,
      },
      signal,
    );

    if (response.error !== undefined) {
      throw new AiSdkException(response.error);
    }

    if (!isAiSdkJsonObject(response.result)) {
      throw new AiSdkException(
        createAiSdkError(
          "invalid_response",
          "invalid_response_document",
          `The '${operation}' operation did not return a JSON object.`,
        ),
      );
    }

    const schemaVersion = response.result.schemaVersion;
    if (typeof schemaVersion !== "number") {
      throw new AiSdkException(
        createAiSdkError(
          "invalid_response",
          "missing_schema_version",
          `The '${operation}' response does not contain a numeric schemaVersion.`,
        ),
      );
    }

    validateSchemaVersion(`${operation} response`, schemaVersion, expectedResponseSchemaVersion);
    return response.result as unknown as TResponse;
  }
}

type VersionedJsonObject = AiSdkJsonObject & { readonly schemaVersion: number };

function normalizePublicationRequest(request: AiSdkPipelinePublicationRequest): VersionedJsonObject {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("publication_request_required", "A pipeline publication request is required.");
  }

  const definition = request.definition;
  if (definition === null || typeof definition !== "object") {
    throw invalidRequest("pipeline_definition_required", "A pipeline definition is required.");
  }

  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.pipelinePublicationRequest,
    definition: {
      schemaVersion: definition.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.pipelineDefinition,
      name: definition.name,
      ...(definition.version === undefined ? {} : { version: definition.version }),
      ...(definition.executionLanguage === undefined
        ? {}
        : { executionLanguage: definition.executionLanguage }),
      executionMode: definition.executionMode ?? "Sequential",
      steps: (definition.steps ?? []).map(normalizePipelineStep),
      config: definition.config ?? {},
    },
    functions: (request.functions ?? []).map(normalizePublicationFunction),
  };
}

function normalizePipelineStep(step: AiSdkPipelineStepDefinition): AiSdkJsonObject {
  return {
    name: step.name,
    stepKey: step.stepKey,
    ...(step.executionLanguage === undefined
      ? {}
      : { executionLanguage: step.executionLanguage }),
    ...(step.invocation === undefined
      ? {}
      : { invocation: step.invocation as unknown as AiSdkJsonValue }),
    order: step.order,
    dependsOn: step.dependsOn ?? [],
    input: step.input ?? {},
    config: step.config ?? {},
    ...(step.execution === undefined
      ? {}
      : { execution: step.execution as unknown as AiSdkJsonValue }),
  };
}

function normalizePublicationFunction(upload: AiSdkPublicationFunctionUpload): AiSdkJsonObject {
  return {
    site: upload.site as unknown as AiSdkJsonValue,
    environmentRef: upload.environmentRef,
    entryPointPath: upload.entryPointPath,
    entryPointSymbol: upload.entryPointSymbol,
    sources: (upload.sources ?? []).map(normalizeFileUpload),
    dependencies: (upload.dependencies ?? []).map(normalizeDependencyUpload),
  };
}

function normalizeDependencyUpload(upload: AiSdkPublicationDependencyUpload): AiSdkJsonObject {
  return {
    name: upload.name,
    version: upload.version,
    files: (upload.files ?? []).map(normalizeFileUpload),
    ...(upload.package === undefined
      ? {}
      : { package: upload.package as unknown as AiSdkJsonValue }),
  };
}

function normalizeFileUpload(upload: { readonly path: string; readonly contentBase64: string }): AiSdkJsonObject {
  return {
    path: upload.path,
    contentBase64: upload.contentBase64,
  };
}

function normalizeSubmissionRequest(request: AiSdkExecutionSubmissionRequest): VersionedJsonObject {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("submission_request_required", "An execution submission request is required.");
  }

  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.executionSubmissionRequest,
    publicationRef: request.publicationRef,
    ...(request.idempotencyKey === undefined ? {} : { idempotencyKey: request.idempotencyKey }),
    ...(request.input == null ? {} : { input: request.input }),
    metadata: request.metadata ?? {},
    ...(request.correlationId === undefined ? {} : { correlationId: request.correlationId }),
  };
}

function normalizeCancellationRequest(request: AiSdkExecutionCancellationRequest): VersionedJsonObject {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("cancellation_request_required", "An execution cancellation request is required.");
  }

  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.executionCancellationRequest,
    ...(request.reason === undefined ? {} : { reason: request.reason }),
    ...(request.correlationId === undefined ? {} : { correlationId: request.correlationId }),
  };
}

function normalizeControlRequest(request: AiSdkExecutionControlRequest): VersionedJsonObject {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("control_request_required", "An execution control request is required.");
  }
  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.executionControlRequest,
    ...(request.reason === undefined ? {} : { reason: request.reason }),
    ...(request.correlationId === undefined ? {} : { correlationId: request.correlationId }),
  };
}

function normalizeInputSubmissionRequest(request: AiSdkExecutionInputSubmissionRequest): VersionedJsonObject {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("input_submission_request_required", "An execution input submission request is required.");
  }
  if (!isAiSdkJsonObject(request.input)) {
    throw invalidRequest("input_object_required", "Execution input must be a JSON object.");
  }
  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.executionInputSubmissionRequest,
    waitingKey: request.waitingKey,
    ...(request.waitingStepName === undefined ? {} : { waitingStepName: request.waitingStepName }),
    input: request.input,
    ...(request.reason === undefined ? {} : { reason: request.reason }),
    ...(request.correlationId === undefined ? {} : { correlationId: request.correlationId }),
  };
}

function normalizeReplayRequest(request: AiSdkExecutionReplayRequest): VersionedJsonObject {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("replay_request_required", "An execution replay request is required.");
  }
  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.executionReplayRequest,
    strictDeterminism: request.strictDeterminism ?? true,
    includeDiagnostics: request.includeDiagnostics ?? true,
    ...(request.reason === undefined ? {} : { reason: request.reason }),
    ...(request.correlationId === undefined ? {} : { correlationId: request.correlationId }),
  };
}

type NormalizedWatchRequest = {
  readonly schemaVersion: typeof AI_SDK_SCHEMA_VERSIONS.executionWatchRequest;
  readonly executionId: string;
  readonly channels: readonly (typeof AI_SDK_EXECUTION_WATCH_CHANNELS)[number][];
  readonly afterSequence?: number;
  readonly includeInitialSnapshot: boolean;
};

function normalizeWatchRequest(request: AiSdkExecutionWatchRequest): NormalizedWatchRequest {
  if (request === null || typeof request !== "object") {
    throw invalidRequest("watch_request_required", "An execution Watch request is required.");
  }

  return {
    schemaVersion: request.schemaVersion ?? AI_SDK_SCHEMA_VERSIONS.executionWatchRequest,
    executionId: request.executionId,
    channels: request.channels ?? [],
    ...(request.afterSequence === undefined ? {} : { afterSequence: request.afterSequence }),
    includeInitialSnapshot: request.includeInitialSnapshot ?? true,
  };
}

function toWatchRequestJson(request: NormalizedWatchRequest): AiSdkJsonObject {
  return {
    schemaVersion: request.schemaVersion,
    executionId: request.executionId,
    channels: request.channels,
    ...(request.afterSequence === undefined ? {} : { afterSequence: request.afterSequence }),
    includeInitialSnapshot: request.includeInitialSnapshot,
  };
}

function validateWatchChannels(channels: readonly string[]): void {
  for (const channel of channels) {
    if (!AI_SDK_EXECUTION_WATCH_CHANNELS.some((known) => known === channel)) {
      throw invalidRequest(
        "invalid_watch_channel",
        `Unknown execution Watch channel '${channel}'.`,
      );
    }
  }
}

function validateWatchEvent(
  request: NormalizedWatchRequest,
  item: AiSdkExecutionWatchEvent,
): void {
  if (item.executionId !== request.executionId) {
    throw invalidWatchResponse(
      "watch_execution_mismatch",
      "The execution Watch response does not belong to the requested execution.",
    );
  }

  if (item.kind === "Snapshot") {
    if (item.snapshot === undefined || item.sequence === undefined) {
      throw invalidWatchResponse(
        "invalid_watch_snapshot",
        "An execution Watch snapshot requires both snapshot and sequence values.",
      );
    }
    validateSchemaVersion(
      "execution Watch snapshot",
      item.snapshot.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionObservation,
    );
  } else if (item.kind === "Event") {
    if (
      item.sequence === undefined ||
      item.channel === undefined ||
      !AI_SDK_EXECUTION_WATCH_CHANNELS.some((known) => known === item.channel) ||
      typeof item.eventType !== "string" ||
      item.eventType.trim().length === 0
    ) {
      throw invalidWatchResponse(
        "invalid_watch_event",
        "An execution Watch event requires sequence, known channel and eventType values.",
      );
    }
  } else if (item.kind === "ResyncRequired") {
    if (item.resyncRequired === undefined) {
      throw invalidWatchResponse(
        "invalid_watch_resync",
        "A ResyncRequired Watch item requires a resyncRequired document.",
      );
    }
    validateSchemaVersion(
      "execution Watch resync document",
      item.resyncRequired.schemaVersion,
      AI_SDK_SCHEMA_VERSIONS.executionWatchResyncRequired,
    );
    return;
  } else {
    throw invalidWatchResponse(
      "unknown_watch_kind",
      `Unknown execution Watch item kind '${String(item.kind)}'.`,
    );
  }

  if (!Number.isInteger(item.sequence) || (item.sequence ?? -1) < 0) {
    throw invalidWatchResponse(
      "invalid_watch_sequence",
      "Execution Watch sequence values must be non-negative integers.",
    );
  }

  if (request.afterSequence !== undefined && item.sequence! < request.afterSequence) {
    throw invalidWatchResponse(
      "regressing_watch_sequence",
      "The execution Watch response regressed behind the requested public sequence.",
    );
  }
}

function createGapDetectedResync(
  executionId: string,
  requestedAfterSequence: number,
  observedSequence: number,
  occurredAtUtc: string,
): AiSdkExecutionWatchEvent {
  return {
    schemaVersion: AI_SDK_SCHEMA_VERSIONS.executionWatchEvent,
    executionId,
    kind: "ResyncRequired",
    occurredAtUtc,
    resyncRequired: {
      schemaVersion: AI_SDK_SCHEMA_VERSIONS.executionWatchResyncRequired,
      reason: "GapDetected",
      requestedAfterSequence,
      earliestAvailableSequence: requestedAfterSequence + 1,
      latestSequence: observedSequence,
      message: "A gap was detected in the unfiltered public execution Watch stream.",
    },
  };
}

function isTerminalWatchItem(item: AiSdkExecutionWatchEvent): boolean {
  if (item.snapshot !== undefined) {
    return isTerminalStatus(item.snapshot.status);
  }

  if (
    item.kind !== "Event" ||
    item.channel !== "Lifecycle" ||
    !isAiSdkJsonObject(item.payload)
  ) {
    return false;
  }

  return isTerminalStatus(item.payload.status);
}

function isTerminalStatus(value: unknown): boolean {
  return value === "Completed" || value === "Failed" || value === "Cancelled";
}

function invalidWatchResponse(code: string, message: string): AiSdkException {
  return new AiSdkException(createAiSdkError("invalid_response", code, message));
}

function validateExecutionId(executionId: string): void {
  if (executionId.trim().length === 0) {
    throw invalidRequest("execution_id_required", "A non-empty executionId is required.");
  }
}

function validateSchemaVersion(document: string, actual: number, expected: number): void {
  if (actual === expected) {
    return;
  }

  throw new AiSdkException(
    createAiSdkError(
      "unsupported_schema",
      "unsupported_schema",
      `Unsupported ${document} schemaVersion '${actual}'. Expected '${expected}'.`,
    ),
  );
}

function invalidRequest(code: string, message: string): AiSdkException {
  return new AiSdkException(createAiSdkError("invalid_request", code, message));
}
