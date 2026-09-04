"use client";

import { JSX, useState } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
  RuntimeAnalysisHumanApprovalDecision,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import { AiChildReanalysisResultModal } from "./AiChildReanalysisResultModal";
import { AiScenarioExecutionCard } from "./AiScenarioExecutionCard";
import { AiSuggestedScenarioCard } from "./AiSuggestedScenarioCard";
import { AiVerificationCard } from "./AiVerificationCard";
import styles from "./AiAnalysisPanel.module.css";

export type AiChildReanalysisCardProps = {
  relation: RuntimeAnalysisChildDagRelationResult;
  isDecidingApproval: boolean;
  approvalError: string | null;
  onApprovalDecision: (
    decision: RuntimeAnalysisHumanApprovalDecision
  ) => void;
  isExecutingScenario: boolean;
  scenarioExecutionError: string | null;
  onExecuteScenario: () => void;
};

export function AiChildReanalysisCard(
  props: AiChildReanalysisCardProps
): JSX.Element {
  const {
    relation,
    isDecidingApproval,
    approvalError,
    onApprovalDecision,
    isExecutingScenario,
    scenarioExecutionError,
    onExecuteScenario,
  } = props;

  const reanalysis = relation.reanalysis;
  const investigationMode =
    relation.investigationMode ?? "stop-when-conclusive";
  const continueMode =
    investigationMode === "continue-useful-experiments";

  const [isResultModalOpen, setIsResultModalOpen] =
    useState(false);

  if (!reanalysis) {
    return (
      <div className={styles.childReanalysisPending}>
        <div>
          <div className={styles.childReanalysisWorking}>
            <span className={styles.childReanalysisSpinner} aria-hidden="true">
              <span />
              <span />
              <span />
            </span>
            <strong>AI re-analysis</strong>
          </div>
          <span>Depth {relation.depth}</span>
        </div>
        <p>
          {reanalysisPendingMessage(relation.currentStep)}
        </p>
      </div>
    );
  }

  const validation = relation.policyValidation;
  const approval = relation.humanApproval;
  const scenarioExecution = relation.scenarioExecution;
  const verification = relation.verification;

  return (
    <div className={styles.childReanalysisBlock}>
      <div className={styles.childReanalysisHeader}>
        <div>
          <div className={styles.childReanalysisEyebrow}>
            AI re-analysis · Depth {relation.depth}
          </div>
          <div className={styles.childReanalysisSummary}>
            {reanalysis.summary}
          </div>
        </div>

        <div
          className={styles.childReanalysisConclusion}
          data-conclusion={reanalysis.conclusion.toLowerCase()}
        >
          {reanalysis.conclusion.replaceAll("_", " ")}
        </div>
      </div>

      <div className={styles.childReanalysisMeta}>
        <span>
          Confidence {Math.round(reanalysis.confidence * 100)}%
        </span>
        <span
          className={styles.childReanalysisMode}
          data-mode={continueMode ? "continue" : "stop"}
        >
          {continueMode
            ? "Investigation · continue"
            : "Investigation · stop when conclusive"}
        </span>
        <strong data-continue={reanalysis.shouldContinue}>
          {reanalysis.shouldContinue
            ? "NEXT EXPERIMENT PROPOSED"
            : "NO FURTHER EXPERIMENT"}
        </strong>
      </div>

      <div className={styles.childReanalysisResultAction}>
        <button
          type="button"
          className={styles.childReanalysisViewResultButton}
          onClick={() => setIsResultModalOpen(true)}
        >
          View result
        </button>
      </div>

      {!reanalysis.shouldContinue ? (
        <div className={styles.childReanalysisStop}>
          <strong>
            {continueMode
              ? "Continue mode stopped safely"
              : "Decision loop complete"}
          </strong>
          <p>
            {continueMode
              ? "Even in Continue mode, AI found no materially distinct bounded experiment that would add useful evidence. No deeper child execution will be created."
              : "AI found no materially distinct bounded experiment worth another approved execution. No deeper child execution will be created."}
          </p>
        </div>
      ) : validation && approval ? (
        <AiSuggestedScenarioCard
          scenario={reanalysis.suggestedScenario}
          policyValidation={validation}
          humanApproval={approval}
          isDecidingApproval={isDecidingApproval}
          approvalError={approvalError}
          onApprovalDecision={onApprovalDecision}
        />
      ) : (
        <div className={styles.childReanalysisPending}>
          <div>
            <strong>Deterministic policy</strong>
            <span>evaluating</span>
          </div>
          <p>
            The re-analysis result is available. Deterministic policy is
            deciding whether another experiment may cross the approval
            boundary.
          </p>
        </div>
      )}

      {scenarioExecution && scenarioExecution.status !== "NotStarted" ? (
        <AiScenarioExecutionCard
          execution={scenarioExecution}
          isExecuting={isExecutingScenario}
          error={scenarioExecutionError}
          onExecute={onExecuteScenario}
        />
      ) : null}

      {verification ? (
        <AiVerificationCard verification={verification} />
      ) : null}

      <AiChildReanalysisResultModal
        relation={relation}
        isOpen={isResultModalOpen}
        onClose={() => setIsResultModalOpen(false)}
      />
    </div>
  );
}

function reanalysisPendingMessage(currentStep: string): string {
  const step = currentStep.trim().toLowerCase();

  if (step.includes("re-analyze")) {
    return "AI is comparing the verified experiment with the original hypothesis.";
  }

  if (step.includes("capture")) {
    return "The child is capturing bounded deterministic evidence from the completed experiment.";
  }

  return "The child execution is preparing the next durable decision boundary.";
}
