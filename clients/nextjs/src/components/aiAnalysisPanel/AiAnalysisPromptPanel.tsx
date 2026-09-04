"use client";

import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  AiAnalysisQuickActionKey,
  AiAnalysisScope,
} from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisUxModel } from "@/lib/aiAnalysis/AiAnalysisUxModel";
import {
  AiAnalysisActivityIndicator,
  type AiAnalysisActivityLog,
  type AiAnalysisActivityPhase,
} from "./AiAnalysisActivityIndicator";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisPromptPanelProps = {
  scope: AiAnalysisScope;
  question: string;
  isAnalyzing: boolean;
  canAnalyze: boolean;
  providerHint: string;
  activityPhase: AiAnalysisActivityPhase | null;
  activityStartedAt: number | null;
  latestActivityLog: AiAnalysisActivityLog | null;
  onScopeChange: (scope: AiAnalysisScope) => void;
  onQuestionChange: (question: string) => void;
  onAnalyze: () => void;
};

export function AiAnalysisPromptPanel(
  props: AiAnalysisPromptPanelProps
): JSX.Element {
  const {
    scope,
    question,
    isAnalyzing,
    canAnalyze,
    providerHint,
    activityPhase,
    activityStartedAt,
    latestActivityLog,
    onScopeChange,
    onQuestionChange,
    onAnalyze,
  } = props;

  const isScopeAvailable = AiAnalysisUxModel.isScopeAvailable(scope);

  function handleQuickAction(action: AiAnalysisQuickActionKey): void {
    onQuestionChange(AiAnalysisUxModel.promptForAction(action));
  }

  return (
    <section className={styles.section}>
      <div className={styles.sectionHeader}>
        <div className={styles.sectionTitle}>Analyze runtime evidence</div>
      </div>

      <div className={styles.scope}>
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

        <div className={styles.scopeDescription}>
          {AiAnalysisUxModel.scopeDescription(scope)}
        </div>
      </div>

      <div className={styles.quickActions}>
        {AiAnalysisUxModel.quickActions().map((action) => {
          const isSelected = question === action.prompt;

          return (
            <button
              key={action.key}
              type="button"
              className={styles.quickActionButton}
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

      <div className={styles.question}>
        <label htmlFor="ai-analysis-question">Ask about this execution</label>

        <textarea
          id="ai-analysis-question"
          value={question}
          disabled={isAnalyzing}
          onChange={(event) => onQuestionChange(event.target.value)}
          placeholder="Why did latency increase? Which events caused this failure? What scenario should validate the hypothesis?"
        />

        <div className={styles.providerHint}>{providerHint}</div>

        <div className={styles.analysisActions}>
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

        {
          isAnalyzing &&
          activityPhase &&
          activityStartedAt !== null ? (
            <AiAnalysisActivityIndicator
              phase={activityPhase}
              startedAt={activityStartedAt}
              latestLog={latestActivityLog}
            />
          ) : null
        }
      </div>
    </section>
  );
}
