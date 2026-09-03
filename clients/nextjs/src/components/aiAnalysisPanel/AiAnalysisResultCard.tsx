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
import styles from "./AiAnalysisPanel.module.css";

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
      <section className={`${styles.placeholder} ${styles.snapshotError}`}>
        <div className={styles.placeholderTitle}>AI analysis error</div>
        <p className={styles.placeholderText}>{error}</p>
      </section>
    );
  }

  if (!result || !policyValidation || !humanApproval) {
    return (
      <section className={styles.placeholder}>
        <div className={styles.placeholderTitle}>AI analysis output</div>
        <p className={styles.placeholderText}>
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
    <section className={`${styles.section} ${styles.resultSection}`}>
      <div className={styles.resultHeader}>
        <div>
          <div className={styles.sectionTitle}>AI finding</div>
          <div className={styles.confidence}>
            Confidence {Math.round(result.confidence * 100)}%
          </div>
        </div>

        <div
          className={styles.severity}
          data-severity={result.severity}
        >
          {result.severity}
        </div>
      </div>

      <div className={styles.answer}>{result.answer}</div>

      <div className={styles.summary}>{result.summary}</div>

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
