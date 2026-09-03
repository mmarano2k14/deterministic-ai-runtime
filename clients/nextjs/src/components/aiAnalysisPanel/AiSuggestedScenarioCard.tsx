import { JSX } from "react";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisHumanApprovalResult,
  RuntimeAnalysisScenarioPolicyValidationResult,
  RuntimeAnalysisSuggestedScenario,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiHumanApprovalCard } from "./AiHumanApprovalCard";
import { AiScenarioPolicyValidationCard } from "./AiScenarioPolicyValidationCard";
import styles from "./AiAnalysisPanel.module.css";

export type AiSuggestedScenarioCardProps = {
  scenario: RuntimeAnalysisSuggestedScenario;
  policyValidation: RuntimeAnalysisScenarioPolicyValidationResult;
  humanApproval: RuntimeAnalysisHumanApprovalResult;
  isDecidingApproval: boolean;
  approvalError: string | null;
  onApprovalDecision: (
    decision: RuntimeAnalysisHumanApprovalDecision
  ) => void;
};

export function AiSuggestedScenarioCard(
  props: AiSuggestedScenarioCardProps
): JSX.Element {
  const {
    scenario,
    policyValidation,
    humanApproval,
    isDecidingApproval,
    approvalError,
    onApprovalDecision,
  } = props;

  return (
    <div className={styles.suggestedScenario}>
      <div className={styles.resultSubheading}>Suggested scenario</div>

      <div className={styles.scenarioName}>{scenario.name}</div>
      <div className={styles.scenarioRationale}>
        {scenario.rationale}
      </div>

      <div className={styles.scenarioGrid}>
        <div>
          <span>Type</span>
          <strong>{scenario.scenarioType}</strong>
        </div>
        <div>
          <span>Requests</span>
          <strong>{scenario.totalRequests}</strong>
        </div>
        <div>
          <span>Max In-Flight</span>
          <strong>{scenario.maxInFlight}</strong>
        </div>
        <div>
          <span>Overlap</span>
          <strong>{scenario.rotationOverlapMs} ms</strong>
        </div>
        <div>
          <span>Concurrency</span>
          <strong>{scenario.concurrency ?? "—"}</strong>
        </div>
        <div>
          <span>Batch size</span>
          <strong>{scenario.batchSize ?? "—"}</strong>
        </div>
      </div>

      <AiScenarioPolicyValidationCard
        validation={policyValidation}
      />

      <AiHumanApprovalCard
        approval={humanApproval}
        isDeciding={isDecidingApproval}
        error={approvalError}
        onDecision={onApprovalDecision}
      />
    </div>
  );
}
