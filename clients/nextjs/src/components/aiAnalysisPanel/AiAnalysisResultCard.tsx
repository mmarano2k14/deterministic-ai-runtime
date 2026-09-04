import { JSX } from "react";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisHumanApprovalResult,
  RuntimeAnalysisResult,
  RuntimeAnalysisScenarioExecutionResult,
  RuntimeAnalysisScenarioPolicyValidationResult,
  RuntimeAnalysisVerificationResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiAnalysisObservations } from "./AiAnalysisObservations";
import { AiSuggestedScenarioCard } from "./AiSuggestedScenarioCard";
import { AiScenarioExecutionCard } from "./AiScenarioExecutionCard";
import { AiVerificationCard } from "./AiVerificationCard";

export type AiAnalysisResultCardProps = {
  result: RuntimeAnalysisResult | null;
  policyValidation: RuntimeAnalysisScenarioPolicyValidationResult | null;
  humanApproval: RuntimeAnalysisHumanApprovalResult | null;
  scenarioExecution: RuntimeAnalysisScenarioExecutionResult | null;
  verification: RuntimeAnalysisVerificationResult | null;
  error: string | null;
  isDecidingApproval: boolean;
  approvalError: string | null;
  onApprovalDecision: (
    decision: RuntimeAnalysisHumanApprovalDecision
  ) => void;
  isExecutingScenario: boolean;
  scenarioExecutionError: string | null;
  onExecuteScenario: () => void;
};

export function AiAnalysisResultCard(
  props: AiAnalysisResultCardProps
): JSX.Element {
  const {
    result,
    policyValidation,
    humanApproval,
    scenarioExecution,
    verification,
    error,
    isDecidingApproval,
    approvalError,
    onApprovalDecision,
    isExecutingScenario,
    scenarioExecutionError,
    onExecuteScenario,
  } = props;

  if (error) {
    return (
      <section className={`${"ai-analysis-placeholder"} ${"ai-analysis-snapshot-error"}`}>
        <div className="ai-analysis-placeholder-title">AI analysis error</div>
        <p className="ai-analysis-placeholder-text">{error}</p>
      </section>
    );
  }

  if (!result || !policyValidation || !humanApproval) {
    return (
      <section className="ai-analysis-placeholder">
        <div className="ai-analysis-placeholder-title">AI analysis output</div>
        <p className="ai-analysis-placeholder-text">
          Prepare the context, ask a runtime question, then request a structured
          AI analysis.
        </p>
      </section>
    );
  }

  const approvalBoundaryResolved =
    humanApproval.status !== "Pending";

  const scenarioExecutionStarted =
    scenarioExecution !== null &&
    scenarioExecution.status !== "NotStarted";

  const showPostApprovalRuntime =
    approvalBoundaryResolved &&
    scenarioExecutionStarted;

  return (
    <section className={`${"ai-analysis-section"} ${"ai-analysis-result-section"}`}>
      <div className="ai-analysis-result-header">
        <div>
          <div className="ai-analysis-section-title">AI finding</div>
          <div className="ai-analysis-confidence">
            Confidence {Math.round(result.confidence * 100)}%
          </div>
        </div>

        <div
          className="ai-analysis-severity"
          data-severity={result.severity}
        >
          {result.severity}
        </div>
      </div>

      <div className="ai-analysis-answer">{result.answer}</div>

      <div className="ai-analysis-summary">{result.summary}</div>

      <AiAnalysisObservations observations={result.observations} />

      <AiSuggestedScenarioCard
        scenario={result.suggestedScenario}
        policyValidation={policyValidation}
        humanApproval={humanApproval}
        isDecidingApproval={isDecidingApproval}
        approvalError={approvalError}
        onApprovalDecision={onApprovalDecision}
      />

      {showPostApprovalRuntime && scenarioExecution ? (
        <AiScenarioExecutionCard
          execution={scenarioExecution}
          isExecuting={isExecutingScenario}
          error={scenarioExecutionError}
          onExecute={onExecuteScenario}
        />
      ) : null}

      {showPostApprovalRuntime && verification ? (
        <AiVerificationCard
          verification={verification}
        />
      ) : null}
    </section>
  );
}
