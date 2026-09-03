import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import { RuntimeAnalysisApi } from "./RuntimeAnalysisApi";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisPreparedContext,
  RuntimeAnalysisProviderStatus,
  RuntimeAnalysisRuntimeExecutionResult,
} from "./RuntimeAnalysisType";

export class RuntimeAnalysisAnalysisService {
  private readonly api: RuntimeAnalysisApi;

  public constructor(rbacApi: MultiplexedRbacApi) {
    this.api = new RuntimeAnalysisApi(rbacApi);
  }

  public getProviderStatus(
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisProviderStatus> {
    return this.api.getProviderStatus(signal);
  }

  public analyze(
    context: RuntimeAnalysisPreparedContext,
    question: string,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.analyze(
      {
        question,
        snapshotRequest: context.request,
      },
      signal
    );
  }

  public decideHumanApproval(
    executionId: string,
    decision: RuntimeAnalysisHumanApprovalDecision,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.decideHumanApproval(
      executionId,
      decision,
      signal
    );
  }
}
