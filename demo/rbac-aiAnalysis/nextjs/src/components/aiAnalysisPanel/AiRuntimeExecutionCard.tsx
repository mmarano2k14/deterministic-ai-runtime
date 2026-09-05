import { JSX } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisRuntimeExecutionResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiChildDagEvidenceCard } from "./AiChildDagEvidenceCard";

export type AiRuntimeExecutionCardProps = {
  execution: RuntimeAnalysisRuntimeExecutionResult | null;
  decidingChildExecutionId: string | null;
  childApprovalError: string | null;
  onChildApprovalDecision: (
    relation: RuntimeAnalysisChildDagRelationResult,
    decision: RuntimeAnalysisHumanApprovalDecision
  ) => void;
  executingChildExecutionId: string | null;
  childScenarioExecutionError: string | null;
  onExecuteChildScenario: (
    relation: RuntimeAnalysisChildDagRelationResult
  ) => void;
};

export function AiRuntimeExecutionCard(
  props: AiRuntimeExecutionCardProps
): JSX.Element | null {
  const {
    execution,
    decidingChildExecutionId,
    childApprovalError,
    onChildApprovalDecision,
    executingChildExecutionId,
    childScenarioExecutionError,
    onExecuteChildScenario,
  } = props;

  if (!execution) {
    return null;
  }

  const runtimeStatus =
    typeof execution.runtimeStatus === "string" &&
    execution.runtimeStatus.trim().length > 0
      ? execution.runtimeStatus.trim()
      : "Unknown";

  return (
    <section className={`${"ai-analysis-section"} ${"ai-analysis-runtime-execution"}`}>
      <div className="ai-analysis-section-header">
        <div className="ai-analysis-section-title">Runtime DAG</div>
        <div
          className="ai-analysis-runtime-status"
          data-status={runtimeStatus.toLowerCase()}
        >
          {runtimeStatus}
        </div>
      </div>

      <div className="ai-analysis-runtime-identity-grid">
        <div>
          <span>Pipeline</span>
          <strong>{execution.pipelineName}</strong>
        </div>
        <div>
          <span>Current step</span>
          <strong>{execution.stepName}</strong>
        </div>
        <div>
          <span>Initial RunId</span>
          <strong title={execution.runId}>
            {execution.runId}
          </strong>
        </div>
        <div>
          <span>ExecutionId</span>
          <strong title={execution.executionId}>
            {execution.executionId}
          </strong>
        </div>
        {execution.continuationRunId ? (
          <div>
            <span>Continuation RunId</span>
            <strong title={execution.continuationRunId}>
              {execution.continuationRunId}
            </strong>
          </div>
        ) : null}
      </div>

      <AiChildDagEvidenceCard
        childDag={execution.childDag}
        rootExecutionId={execution.executionId}
        decidingChildExecutionId={decidingChildExecutionId}
        childApprovalError={childApprovalError}
        onChildApprovalDecision={onChildApprovalDecision}
        executingChildExecutionId={executingChildExecutionId}
        childScenarioExecutionError={childScenarioExecutionError}
        onExecuteChildScenario={onExecuteChildScenario}
      />
    </section>
  );
}
