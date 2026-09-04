"use client";

import { JSX } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";
import styles from "./AiAnalysisPanel.module.css";

export type AiChildReanalysisResultModalProps = {
  relation: RuntimeAnalysisChildDagRelationResult;
  isOpen: boolean;
  onClose: () => void;
};

export function AiChildReanalysisResultModal(
  props: AiChildReanalysisResultModalProps
): JSX.Element | null {
  const {
    relation,
    isOpen,
    onClose,
  } = props;

  const reanalysis = relation.reanalysis;

  if (!isOpen || !reanalysis) {
    return null;
  }

  const investigationMode =
    relation.investigationMode ?? "stop-when-conclusive";

  return (
    <div
      className={styles.aiResultModalOverlay}
      onClick={onClose}
    >
      <div
        className={styles.aiResultModal}
        onClick={(event) => event.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="ai-child-result-modal-title"
      >
        <div className={styles.aiResultModalHeader}>
          <div className={styles.aiResultModalHeading}>
            <div
              id="ai-child-result-modal-title"
              className={styles.aiResultModalTitle}
            >
              AI re-analysis result
            </div>

            <div className={styles.aiResultModalSubtitle}>
              Depth {relation.depth}
              {" · "}
              Confidence {Math.round(reanalysis.confidence * 100)}%
            </div>
          </div>

          <div className={styles.aiResultModalHeaderActions}>
            <span
              className={styles.aiResultModalConclusion}
              data-conclusion={reanalysis.conclusion.toLowerCase()}
            >
              {reanalysis.conclusion.replaceAll("_", " ")}
            </span>

            <button
              type="button"
              className={styles.aiResultModalCloseButton}
              onClick={onClose}
            >
              Close
            </button>
          </div>
        </div>

        <section className={styles.aiResultModalSection}>
          <div className={styles.aiResultModalSectionTitle}>
            Summary
          </div>
          <div className={styles.aiResultModalSummary}>
            {reanalysis.summary}
          </div>
        </section>

        <section className={styles.aiResultModalSection}>
          <div className={styles.aiResultModalSectionTitle}>
            Result
          </div>
          <div className={styles.aiResultModalAnswer}>
            {reanalysis.answer}
          </div>
        </section>

        {reanalysis.reasons.length > 0 ? (
          <section className={styles.aiResultModalSection}>
            <div className={styles.aiResultModalSectionTitle}>
              Why
            </div>

            <ul className={styles.aiResultModalReasons}>
              {reanalysis.reasons.map((reason, index) => (
                <li key={`${relation.depth}:${index}`}>
                  {reason}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        <section className={styles.aiResultModalSection}>
          <div className={styles.aiResultModalSectionTitle}>
            Decision
          </div>

          <div className={styles.aiResultModalDecisionGrid}>
            <div>
              <span>Investigation mode</span>
              <strong>
                {investigationMode === "continue-useful-experiments"
                  ? "Continue with another useful experiment"
                  : "Stop when conclusion is strong"}
              </strong>
            </div>

            <div>
              <span>Next experiment</span>
              <strong>
                {reanalysis.shouldContinue
                  ? "Proposed"
                  : "Not required"}
              </strong>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
}
