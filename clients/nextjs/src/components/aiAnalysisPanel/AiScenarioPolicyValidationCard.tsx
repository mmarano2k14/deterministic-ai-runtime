import { JSX } from "react";
import type {
  RuntimeAnalysisScenarioPolicyRuntimeExecutionResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiScenarioPolicyValidationCardProps = {
  execution: RuntimeAnalysisScenarioPolicyRuntimeExecutionResult;
};

export function AiScenarioPolicyValidationCard(
  props: AiScenarioPolicyValidationCardProps
): JSX.Element {
  const { execution } = props;
  const { result } = execution;

  return (
    <div className={styles.policyValidation}>
      <div className={styles.policyValidationHeader}>
        <div>
          <div className={styles.resultSubheading}>Policy validation</div>
          <div className={styles.policyPipeline}>
            {execution.pipelineName} · {execution.stepName}
          </div>
        </div>

        <div
          className={styles.policyDecisionBadge}
          data-allowed={result.allowed}
        >
          {result.allowed ? "ALLOWED" : "DENIED"}
        </div>
      </div>

      <div className={styles.policyRuntimeIdentity}>
        Execution {shortIdentity(execution.executionId)}
        {" · "}
        {execution.runtimeStatus}
      </div>

      <div className={styles.policyDecisionList}>
        {result.policyDecisions.map((decision) => (
          <div
            key={decision.policyKey}
            className={styles.policyDecision}
            data-allowed={decision.allowed}
          >
            <div className={styles.policyDecisionTopline}>
              <strong>{shortPolicyKey(decision.policyKey)}</strong>
              <span>{decision.allowed ? "Allowed" : "Denied"}</span>
            </div>
            <div className={styles.policyDecisionMessage}>
              {decision.message}
            </div>
          </div>
        ))}
      </div>

      {result.allowed && result.requiresHumanApproval ? (
        <div className={styles.humanApprovalRequired}>
          Policies passed · explicit human approval is still required before
          execution.
        </div>
      ) : null}
    </div>
  );
}

function shortPolicyKey(policyKey: string): string {
  const parts = policyKey.split(".");
  return parts[parts.length - 1] || policyKey;
}

function shortIdentity(value: string): string {
  return value.length <= 14
    ? value
    : `${value.slice(0, 7)}…${value.slice(-6)}`;
}
