"use client";

import { JSX } from "react";
import { Button } from "@/components/ui/Button";
import type {
  AiAnalysisQuickActionKey,
  AiAnalysisScope,
} from "@/lib/aiAnalysis/AiAnalysisType";
import { AiAnalysisUxModel } from "@/lib/aiAnalysis/AiAnalysisUxModel";
import styles from "./AiAnalysisPanel.module.css";

export type AiAnalysisPromptPanelProps = {
  scope: AiAnalysisScope;
  question: string;
  isPreparingContext: boolean;
  isAnalyzing: boolean;
  canAnalyze: boolean;
  providerHint: string;
  onScopeChange: (scope: AiAnalysisScope) => void;
  onQuestionChange: (question: string) => void;
  onPrepareContext: () => void;
  onAnalyze: () => void;
};

export function AiAnalysisPromptPanel(
  props: AiAnalysisPromptPanelProps
): JSX.Element {
  const {
    scope,
    question,
    isPreparingContext,
    isAnalyzing,
    canAnalyze,
    providerHint,
    onScopeChange,
    onQuestionChange,
    onPrepareContext,
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
          onChange={(event) =>
            onScopeChange(event.target.value as AiAnalysisScope)
          }
        >
          {AiAnalysisUxModel.scopes().map((definition) => (
            <option
              key={definition.key}
              value={definition.key}
              disabled={!definition.available}
            >
              {definition.label}
              {!definition.available ? " — coming next" : ""}
            </option>
          ))}
        </select>

        <div className={styles.scopeDescription}>
          {AiAnalysisUxModel.scopeDescription(scope)}
        </div>
      </div>

      <div className={styles.quickActions}>
        {AiAnalysisUxModel.quickActions().map((action) => (
          <Button
            key={action.key}
            onClick={() => handleQuickAction(action.key)}
            title={action.prompt}
          >
            {action.label}
          </Button>
        ))}
      </div>

      <div className={styles.question}>
        <label htmlFor="ai-analysis-question">Ask about this execution</label>

        <textarea
          id="ai-analysis-question"
          value={question}
          onChange={(event) => onQuestionChange(event.target.value)}
          placeholder="Why did latency increase? Which events caused this failure? What scenario should validate the hypothesis?"
        />

        <div className={styles.providerHint}>{providerHint}</div>

        <div className={styles.analysisActions}>
          <Button
            loading={isPreparingContext}
            disabled={!isScopeAvailable || isAnalyzing}
            onClick={onPrepareContext}
            title="Build and validate the bounded runtime analysis snapshot"
          >
            Prepare context
          </Button>

          <Button
            variant="primary"
            loading={isAnalyzing}
            disabled={!canAnalyze}
            onClick={onAnalyze}
            title="Analyze the prepared runtime evidence with the configured AI provider"
          >
            Ask AI
          </Button>
        </div>
      </div>
    </section>
  );
}
