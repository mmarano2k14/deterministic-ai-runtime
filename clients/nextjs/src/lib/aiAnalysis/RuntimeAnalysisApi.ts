import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import type {
  RuntimeAnalysisAnalyzeRequest,
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisProviderStatus,
  RuntimeAnalysisRuntimeExecutionResult,
  RuntimeAnalysisSnapshot,
  RuntimeAnalysisSnapshotRequest,
} from "./RuntimeAnalysisType";

export class RuntimeAnalysisApi {
  public constructor(
    private readonly rbacApi: MultiplexedRbacApi
  ) {}

  public async getProviderStatus(
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisProviderStatus> {
    const result = await this.rbacApi.call(
      {
        name: "RUNTIME ANALYSIS PROVIDER STATUS",
        method: "GET",
        path: "/runtime-analysis/provider-status",
      },
      undefined,
      signal
    );

    return this.parseResponse<RuntimeAnalysisProviderStatus>(
      result,
      "provider status"
    );
  }

  public async buildSnapshot(
    request: RuntimeAnalysisSnapshotRequest,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisSnapshot> {
    const result = await this.rbacApi.call(
      {
        name: "RUNTIME ANALYSIS SNAPSHOT",
        method: "POST",
        path: "/runtime-analysis/snapshot",
        body: request,
      },
      undefined,
      signal
    );

    return this.parseResponse<RuntimeAnalysisSnapshot>(
      result,
      "snapshot"
    );
  }

  public async analyze(
    request: RuntimeAnalysisAnalyzeRequest,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    const result = await this.rbacApi.call(
      {
        name: "RUNTIME ANALYSIS",
        method: "POST",
        path: "/runtime-analysis/analyze",
        body: request,
      },
      undefined,
      signal
    );

    return this.parseResponse<RuntimeAnalysisRuntimeExecutionResult>(
      result,
      "analysis"
    );
  }

  public async decideHumanApproval(
    executionId: string,
    decision: RuntimeAnalysisHumanApprovalDecision,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    const result = await this.rbacApi.call(
      {
        name: "RUNTIME ANALYSIS HUMAN APPROVAL",
        method: "POST",
        path: `/runtime-analysis/executions/${encodeURIComponent(executionId)}/approval`,
        body: {
          decision,
        },
      },
      undefined,
      signal
    );

    return this.parseResponse<RuntimeAnalysisRuntimeExecutionResult>(
      result,
      "human approval"
    );
  }

  private parseResponse<T>(
    result: Awaited<ReturnType<MultiplexedRbacApi["call"]>>,
    operation: string
  ): T {
    if (result.kind === "error") {
      const responseBody = result.response?.body;
      const details = responseBody || result.error.details;

      throw new Error(
        details
          ? `${result.error.error}: ${details}`
          : result.error.error
      );
    }

    try {
      return JSON.parse(result.response.body) as T;
    } catch {
      throw new Error(
        `Runtime analysis ${operation} endpoint returned invalid JSON.`
      );
    }
  }
}
