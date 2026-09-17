import { AI_SDK_SCHEMA_VERSIONS } from "../contracts/common/schema-versions.js";
import type { AiSdkExecutionCancellationRequest } from "../contracts/control/execution-cancellation-request.js";
import type { AiSdkExecutionCancellationResponse } from "../contracts/control/execution-cancellation-response.js";
import type { AiSdkExecutionResult } from "../contracts/executions/execution-result.js";
import type { AiSdkExecutionSubmissionRequest } from "../contracts/executions/execution-submission-request.js";
import type { AiSdkExecutionSubmissionResponse } from "../contracts/executions/execution-submission-response.js";
import type { AiSdkExecutionObservation } from "../contracts/observation/execution-observation.js";
import type { AiSdkPipelineStepDefinition } from "../contracts/pipelines/pipeline-step-definition.js";
import type { AiSdkPipelinePublicationRequest } from "../contracts/publication/pipeline-publication-request.js";
import type { AiSdkPipelinePublicationResponse } from "../contracts/publication/pipeline-publication-response.js";
import type { AiSdkPublicationDependencyUpload } from "../contracts/publication/publication-dependency-upload.js";
import type { AiSdkPublicationFunctionUpload } from "../contracts/publication/publication-function-upload.js";
import { AiSdkException } from "../errors/sdk-exception.js";
import { createAiSdkError } from "../errors/sdk-error.js";
import type { AiSdkJsonObject, AiSdkJsonValue } from "../json/json-value.js";
import { isAiSdkJsonObject } from "../json/json-value.js";
import { AI_SDK_OPERATIONS, AI_SDK_PROTOCOL_VERSION } from "../protocol/operations.js";
import type { AiSdkTransport } from "../transport/transport.js";
import type { AiSdkClientInterface } from "./ai-sdk-client-interface.js";

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
