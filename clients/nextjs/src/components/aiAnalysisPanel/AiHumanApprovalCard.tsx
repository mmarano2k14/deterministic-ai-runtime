import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  RuntimeAnalysisHumanApprovalDecision,
  RuntimeAnalysisHumanApprovalResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiHumanApprovalCardProps = {
  approval: RuntimeAnalysisHumanApprovalResult;
  isDeciding: boolean;
  error: string | null;
  onDecision: (decision: RuntimeAnalysisHumanApprovalDecision) => void;
};

export function AiHumanApprovalCard(
  props: AiHumanApprovalCardProps
): JSX.Element {
  const {
    approval,
    isDeciding,
    error,
    onDecision,
  } = props;

  const pending =
    approval.required && approval.status === "Pending";

  return (
    <div
      className={styles.humanApprovalCard}
      data-status={approval.status}
    >
      <div className={styles.humanApprovalHeader}>
        <div>
          <div className={styles.resultSubheading}>Human approval</div>
          <div className={styles.policyPipeline}>
            Durable external-wait boundary
          </div>
        </div>

        <div className={styles.humanApprovalBadge}>
          {approvalLabel(approval)}
        </div>
      </div>

      <div className={styles.humanApprovalMessage}>
        {approval.message ?? defaultMessage(approval)}
      </div>

      {approval.decidedBy ? (
        <div className={styles.policyRuntimeIdentity}>
          Decision by {approval.decidedBy}
        </div>
      ) : null}

      {pending ? (
        <div className={styles.approvalActions}>
          <Button
            variant="neutral"
            disabled={isDeciding}
            onClick={() => onDecision("reject")}
            title="Reject the AI proposal and resume the same execution to a terminal non-executing state"
          >
            Reject
          </Button>
          <Button
            variant="primary"
            loading={isDeciding}
            disabled={isDeciding}
            onClick={() => onDecision("approve")}
            title="Approve the proposal and continue the same durable ExecutionId"
          >
            Approve &amp; continue
          </Button>
        </div>
      ) : null}

      {error ? (
        <div className={styles.policyValidationError}>
          {error}
        </div>
      ) : null}
    </div>
  );
}

function approvalLabel(
  approval: RuntimeAnalysisHumanApprovalResult
): string {
  if (!approval.required) {
    return "NOT REQUIRED";
  }

  return approval.status.toUpperCase();
}

function defaultMessage(
  approval: RuntimeAnalysisHumanApprovalResult
): string {
  switch (approval.status) {
    case "Pending":
      return "Policies passed. The runtime execution is parked until an explicit human decision is durably recorded.";
    case "Approved":
      return "Approved. The same durable execution resumed through the runtime external-wait continuation path.";
    case "Rejected":
      return "Rejected. The proposal will not execute.";
    case "NotRequired":
      return "No human approval is available for this proposal.";
  }
}
