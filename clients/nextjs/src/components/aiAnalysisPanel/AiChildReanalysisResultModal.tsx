"use client";

import { JSX } from "react";
import type {
  RuntimeAnalysisChildDagRelationResult,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";

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
      className="ai-analysis-ai-result-modal-overlay"
      onClick={onClose}
    >
      <div
        className="ai-analysis-ai-result-modal"
        onClick={(event) => event.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="ai-child-result-modal-title"
      >
        <div className="ai-analysis-ai-result-modal-header">
          <div className="ai-analysis-ai-result-modal-heading">
            <div
              id="ai-child-result-modal-title"
              className="ai-analysis-ai-result-modal-title"
            >
              AI re-analysis result
            </div>

            <div className="ai-analysis-ai-result-modal-subtitle">
              Depth {relation.depth}
              {" · "}
              Confidence {Math.round(reanalysis.confidence * 100)}%
            </div>
          </div>

          <div className="ai-analysis-ai-result-modal-header-actions">
            <span
              className="ai-analysis-ai-result-modal-conclusion"
              data-conclusion={reanalysis.conclusion.toLowerCase()}
            >
              {reanalysis.conclusion.replaceAll("_", " ")}
            </span>

            <button
              type="button"
              className="ai-analysis-ai-result-modal-close-button"
              onClick={onClose}
            >
              Close
            </button>
          </div>
        </div>

        <section className="ai-analysis-ai-result-modal-section">
          <div className="ai-analysis-ai-result-modal-section-title">
            Summary
          </div>
          <div className="ai-analysis-ai-result-modal-summary">
            {reanalysis.summary}
          </div>
        </section>

        <section className="ai-analysis-ai-result-modal-section">
          <div className="ai-analysis-ai-result-modal-section-title">
            Result
          </div>
          <div className="ai-analysis-ai-result-modal-answer">
            {reanalysis.answer}
          </div>
        </section>

        {reanalysis.reasons.length > 0 ? (
          <section className="ai-analysis-ai-result-modal-section">
            <div className="ai-analysis-ai-result-modal-section-title">
              Why
            </div>

            <ul className="ai-analysis-ai-result-modal-reasons">
              {reanalysis.reasons.map((reason, index) => (
                <li key={`${relation.depth}:${index}`}>
                  {reason}
                </li>
              ))}
            </ul>
          </section>
        ) : null}

        <section className="ai-analysis-ai-result-modal-section">
          <div className="ai-analysis-ai-result-modal-section-title">
            Decision
          </div>

          <div className="ai-analysis-ai-result-modal-decision-grid">
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
