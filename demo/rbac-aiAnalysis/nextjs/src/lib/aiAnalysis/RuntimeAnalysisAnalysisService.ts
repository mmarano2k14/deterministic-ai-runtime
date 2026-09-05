import type { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import { RuntimeAnalysisApi } from "./RuntimeAnalysisApi";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisInvestigationMode,
  RuntimeAnalysisPreparedContext,
  RuntimeAnalysisProviderStatus,
  RuntimeAnalysisRuntimeExecutionResult,
  RuntimeAnalysisScenarioExecutionObservation,
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
    investigationMode: RuntimeAnalysisInvestigationMode,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.analyze(
      {
        question,
        investigationMode,
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

  public completeScenarioExecution(
    executionId: string,
    observation: RuntimeAnalysisScenarioExecutionObservation,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.completeScenarioExecution(
      executionId,
      observation,
      signal
    );
  }

  public getExecution(
    rootExecutionId: string,
    rootRunId: string,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.getExecution(
      rootExecutionId,
      rootRunId,
      signal
    );
  }

  public decideChildHumanApproval(
    rootExecutionId: string,
    childExecutionId: string,
    rootRunId: string,
    decision: RuntimeAnalysisHumanApprovalDecision,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.decideChildHumanApproval(
      rootExecutionId,
      childExecutionId,
      rootRunId,
      decision,
      signal
    );
  }

  public completeChildScenarioExecution(
    rootExecutionId: string,
    childExecutionId: string,
    rootRunId: string,
    observation: RuntimeAnalysisScenarioExecutionObservation,
    signal?: AbortSignal
  ): Promise<RuntimeAnalysisRuntimeExecutionResult> {
    return this.api.completeChildScenarioExecution(
      rootExecutionId,
      childExecutionId,
      rootRunId,
      observation,
      signal
    );
  }
}
