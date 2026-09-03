import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  RuntimeAnalysisScenarioPolicyRuntimeExecutionResult,
  RuntimeAnalysisSuggestedScenario,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiScenarioPolicyValidationCard } from "./AiScenarioPolicyValidationCard";
import styles from "./AiAnalysisPanel.module.css";

export type AiSuggestedScenarioCardProps = {
  scenario: RuntimeAnalysisSuggestedScenario;
  isValidating: boolean;
  policyExecution: RuntimeAnalysisScenarioPolicyRuntimeExecutionResult | null;
  policyError: string | null;
  onValidate: () => void;
};

export function AiSuggestedScenarioCard(
  props: AiSuggestedScenarioCardProps
): JSX.Element {
  const {
    scenario,
    isValidating,
    policyExecution,
    policyError,
    onValidate,
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

      <div className={styles.policyAction}>
        <Button
          variant="primary"
          loading={isValidating}
          disabled={isValidating}
          onClick={onValidate}
          title="Run deterministic custom policies inside the runtime before any scenario can be approved"
        >
          {policyExecution ? "Revalidate policies" : "Validate with policies"}
        </Button>
      </div>

      {policyError ? (
        <div className={styles.policyValidationError}>
          {policyError}
        </div>
      ) : null}

      {policyExecution ? (
        <AiScenarioPolicyValidationCard
          execution={policyExecution}
        />
      ) : (
        <div className={styles.policyPending}>
          Proposal only · deterministic policy validation required before
          human approval.
        </div>
      )}
    </div>
  );
}
