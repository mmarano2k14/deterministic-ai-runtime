"use client";

import { JSX } from "react";
import type { BurstRuntime } from "@/lib/console/burst/runtime/BurstMachineType";
import { ConsoleSidePanel } from "@/components/ui/ConsoleSidePanel";
import { AiAnalysisPanel } from "./AiAnalysisPanel";

export type AiAnalysisSidebarProps = {
  isCollapsed: boolean;
  model: BurstRuntime;
  logCount: number;
  onCollapsedChange: (next: boolean) => void;
};

export function AiAnalysisSidebar(props: AiAnalysisSidebarProps): JSX.Element {
  const { isCollapsed, model, logCount, onCollapsedChange } = props;

  return (
    <ConsoleSidePanel
      side="right"
      title="AI Runtime Analysis"
      isCollapsed={isCollapsed}
      onCollapsedChange={onCollapsedChange}
      collapsedContent={
        <button
          type="button"
          className="console-sidebar__mini-tab active"
          onClick={() => onCollapsedChange(false)}
          title="Open AI Runtime Analysis"
          aria-label="Open AI Runtime Analysis"
        >
          AI
        </button>
      }
    >
      <AiAnalysisPanel model={model} logCount={logCount} />
    </ConsoleSidePanel>
  );
}
