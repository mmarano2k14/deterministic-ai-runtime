"use client";

import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  AiAnalysisQuickActionKey,
  AiAnalysisScope,
} from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisUxModel } from "@/lib/aiAnalysis/AiAnalysisUxModel";
import type {
  RuntimeAnalysisInvestigationMode,
} from "@/lib/aiAnalysis/RuntimeAnalysisType";

export type AiAnalysisPromptPanelProps = {
  scope: AiAnalysisScope;
  investigationMode: RuntimeAnalysisInvestigationMode;
  investigationModeLocked: boolean;
  question: string;
  isAnalyzing: boolean;
  canAnalyze: boolean;
  providerHint: string;
  onScopeChange: (scope: AiAnalysisScope) => void;
  onInvestigationModeChange: (
    mode: RuntimeAnalysisInvestigationMode
  ) => void;
  onQuestionChange: (question: string) => void;
  onAnalyze: () => void;
};

export function AiAnalysisPromptPanel(
  props: AiAnalysisPromptPanelProps
): JSX.Element {
  const {
    scope,
    investigationMode,
    investigationModeLocked,
    question,
    isAnalyzing,
    canAnalyze,
    providerHint,
    onScopeChange,
    onInvestigationModeChange,
    onQuestionChange,
    onAnalyze,
  } = props;

  const isScopeAvailable = AiAnalysisUxModel.isScopeAvailable(scope);

  function handleQuickAction(action: AiAnalysisQuickActionKey): void {
    onQuestionChange(AiAnalysisUxModel.promptForAction(action));
  }

  return (
    <section className="ai-analysis-section">
      <div className="ai-analysis-section-header">
        <div className="ai-analysis-section-title">Analyze runtime evidence</div>
      </div>

      <div className="ai-analysis-scope">
        <label htmlFor="ai-analysis-scope">Scope</label>
        <select
          id="ai-analysis-scope"
          value={scope}
          disabled={isAnalyzing}
          onChange={(event) =>
            onScopeChange(event.target.value as AiAnalysisScope)
          }
        >
          {AiAnalysisUxModel.scopes()
            .filter((definition) => definition.available)
            .map((definition) => (
              <option key={definition.key} value={definition.key}>
                {definition.label}
              </option>
            ))}
        </select>

        <div className="ai-analysis-scope-description">
          {AiAnalysisUxModel.scopeDescription(scope)}
        </div>
      </div>

      <div className="ai-analysis-scope">
        <label htmlFor="ai-analysis-investigation-mode">
          Investigation mode
        </label>
        <select
          id="ai-analysis-investigation-mode"
          value={investigationMode}
          disabled={isAnalyzing || investigationModeLocked}
          onChange={(event) =>
            onInvestigationModeChange(
              event.target.value as RuntimeAnalysisInvestigationMode
            )
          }
        >
          <option value="stop-when-conclusive">
            Stop when conclusion is strong
          </option>
          <option value="continue-useful-experiments">
            Continue with another useful experiment
          </option>
        </select>

        <div className="ai-analysis-scope-description">
          {investigationMode === "continue-useful-experiments"
            ? "AI actively seeks one materially different bounded follow-up after each verified child. Every follow-up still requires deterministic policy + human approval. Maximum approved depth: 5."
            : "AI may close the decision loop when deterministic evidence is conclusive. Select Continue before Ask AI when you want to exercise multiple approval-driven children."}
          {investigationModeLocked
            ? " Mode locked for this durable analysis chain."
            : ""}
        </div>
      </div>

      <div className="ai-analysis-quick-actions">
        {AiAnalysisUxModel.quickActions().map((action) => {
          const isSelected = question === action.prompt;

          return (
            <button
              key={action.key}
              type="button"
              className="ai-analysis-quick-action-button"
              data-selected={isSelected}
              aria-pressed={isSelected}
              disabled={isAnalyzing}
              onClick={() => handleQuickAction(action.key)}
              title={action.prompt}
            >
              {action.label}
            </button>
          );
        })}
      </div>

      <div className="ai-analysis-question">
        <label htmlFor="ai-analysis-question">Ask about this execution</label>

        <textarea
          id="ai-analysis-question"
          value={question}
          disabled={isAnalyzing}
          onChange={(event) => onQuestionChange(event.target.value)}
          placeholder="Why did latency increase? Which events caused this failure? What scenario should validate the hypothesis?"
        />

        <div className="ai-analysis-provider-hint">{providerHint}</div>

        <div className="ai-analysis-analysis-actions">
          <Button
            variant="primary"
            loading={isAnalyzing}
            disabled={!canAnalyze || !isScopeAvailable}
            onClick={onAnalyze}
            title="Prepare the bounded runtime context and analyze it with the configured AI provider"
          >
            {isAnalyzing ? "Working…" : "Ask AI"}
          </Button>
        </div>

      </div>
    </section>
  );
}
