"use client";

import { JSX, useState } from "react";

import { AiAnalysisSidebar } from "@/components/aiAnalysisPanel/AiAnalysisSidebar";
import { BurstPanel } from "@/components/burstPanel/BurstPanel";
import { BurstPanelForm } from "@/components/burstPanel/BurstPanelForm";
import { BurstPanelHelpers } from "@/components/burstPanel/helpers/BurstPanelHelpers";
import { ScenarioPresetsPanel } from "@/components/burstPanel/scenarios/ScenarioPresetsPanel";
import { BurstActions } from "@/components/burstPanel/sections/BurstActions";
import { ContextPanel } from "@/components/contextPanel/ContextPanel";
import { LogsPanel } from "@/components/logsPanel/LogsPanel";
import { RequestPanel } from "@/components/requestPanel/RequestPanel";
import { TargetPanel } from "@/components/targetPanel/TargetPanel";
import { BottomDrawer } from "@/components/ui/BottomDrawer";
import { ConsoleSidePanel } from "@/components/ui/ConsoleSidePanel";
import { ControlSidebarTabs } from "@/components/ui/ControlSidebarTabs";
import { ConsoleStatusBar } from "@/components/ui/status/ConsoleStatusBar";
import { BurstScenarioDefinition } from "@/lib/console/burst/scenarios/BurstScenarioPresetType";
import { useBurstController } from "@/lib/console/burst/useBurstController";
import { InFlightMaxValue } from "@/lib/console/ConsoleType";
import { useConsoleContext } from "@/lib/console/contextProvider/useConsoleContext";
import {
  ConsoleControlTabKey,
  ConsoleSidebarsLayout,
} from "@/lib/console/layout/ConsoleSidebarsLayout";
import layoutStyles from "./DashboardLayout.module.css";

const LOGS_COLLAPSED_HEIGHT = 56;
const WORKSPACE_GAP = 12;

export default function Page(): JSX.Element {
  const { state, actions, dispatch, api } = useConsoleContext();
  const burst = useBurstController({ api, dispatch });

  const [isLogsCollapsed, setIsLogsCollapsed] = useState(false);
  const [logsHeight, setLogsHeight] = useState(320);
  const [sidebars, setSidebars] = useState(() =>
    ConsoleSidebarsLayout.createDefault()
  );

  const isRunning = BurstPanelHelpers.isRunning(burst.model);
  const isAiOpen = !sidebars.aiCollapsed;
  const visibleLogsHeight = isLogsCollapsed
    ? LOGS_COLLAPSED_HEIGHT
    : logsHeight;

  async function handleLaunchScenario(
    scenario: BurstScenarioDefinition
  ): Promise<void> {
    if (scenario.maxInFlight) {
      dispatch({
        type: "maxInFlightChanged",
        maxInFlightValue: scenario.maxInFlight as InFlightMaxValue,
      });
    }

    if (scenario.rotationOverlapMs) {
      dispatch({
        type: "rotationOverlapMsChange",
        rotationOverlapMs: scenario.rotationOverlapMs,
      });
    }

    await burst.actions.start(scenario.burstConfig);
  }

  function handleControlSidebarCollapsedChange(next: boolean): void {
    setSidebars((current) =>
      ConsoleSidebarsLayout.setControlsCollapsed(current, next)
    );
  }

  function handleControlSidebarTabClick(tab: ConsoleControlTabKey): void {
    setSidebars((current) =>
      ConsoleSidebarsLayout.openControlTab(current, tab)
    );
  }

  function handleAiSidebarCollapsedChange(next: boolean): void {
    setSidebars((current) =>
      ConsoleSidebarsLayout.setAiCollapsed(current, next)
    );
  }

  const consoleBodyClassName = [
    "console-body",
    layoutStyles.consoleBody,
    sidebars.controlsCollapsed ? layoutStyles.controlsCollapsed : "",
    isAiOpen ? layoutStyles.aiOpen : "",
  ]
    .filter(Boolean)
    .join(" ");

  const mainStyle = isAiOpen
    ? {
        height: `calc(100% - ${visibleLogsHeight + WORKSPACE_GAP}px)`,
      }
    : undefined;

  return (
    <div className="console-shell">
      <header className="console-header">
        <div className="header-left">
          <TargetPanel
            disabled={state.busy}
            baseUrl={state.baseUrl}
            onTargetChanges={(v) =>
              dispatch({ type: "TargetChanged", baseUrl: v })
            }
          />

          <ConsoleStatusBar
            status={burst.model.state}
            busy={isRunning}
            lastError={state.lastError}
            username={state.demoUserId}
            contextKey={api.contextKey}
            onDismissError={actions.resetError}
          />
        </div>

        <div className="header-right">
          <ContextPanel
            disabled={isRunning}
            demoUserId={state.demoUserId}
            contextKey={api.contextKey}
            maxInFlight={state.maxInFlight}
            rotationOverlapMs={state.rotationOverlapMs}
            onDemoUserIdChange={() => {}}
            onContextKeyChange={() => {}}
            onGetContextClick={actions.getContextKey}
            onMaxInFlightChange={(v) =>
              dispatch({ type: "maxInFlightChanged", maxInFlightValue: v })
            }
            onRotationOverlapMsChange={(v) =>
              dispatch({ type: "rotationOverlapMsChange", rotationOverlapMs: v })
            }
            onClearClick={() =>
              dispatch({ type: "ContextChanged", contextKey: "" })
            }
          />

          <BurstActions
            disabled={isRunning}
            isRunning={isRunning}
            onStart={burst.actions.start}
            onStop={burst.actions.stop}
            onReset={burst.actions.reset}
          />
        </div>
      </header>

      <div
        className="console-content"
        style={{
          paddingBottom: isAiOpen ? "0px" : `${visibleLogsHeight}px`,
        }}
      >
        <div className={consoleBodyClassName}>
          <ConsoleSidePanel
            side="left"
            title="Controls"
            isCollapsed={sidebars.controlsCollapsed}
            onCollapsedChange={handleControlSidebarCollapsedChange}
            collapsedContent={
              <ControlSidebarTabs
                activeTab={sidebars.activeControlTab}
                onTabClick={handleControlSidebarTabClick}
              />
            }
          >
            <ScenarioPresetsPanel
              planKey="read"
              onLaunch={handleLaunchScenario}
            />

            <RequestPanel
              disabled={isRunning}
              invoiceId={state.invoiceId}
              amount={state.amount}
              onInvoiceIdChange={(v) =>
                dispatch({ type: "InvoiceChanged", invoiceId: v })
              }
              onAmountChange={(v) =>
                dispatch({ type: "AmountChanged", amount: v })
              }
              onReadClick={handleLaunchScenario}
              onRefundClick={handleLaunchScenario}
              onClearLogClick={actions.clearLogs}
            />

            <BurstPanelForm
              disabled={state.busy}
              model={burst.model}
              onConfigure={burst.actions.configure}
              onStart={burst.actions.start}
              onStop={burst.actions.stop}
              onReset={burst.actions.reset}
            />
          </ConsoleSidePanel>

          <main className="console-main" style={mainStyle}>
            <BurstPanel
              disabled={state.busy}
              model={burst.model}
              onConfigure={burst.actions.configure}
              onStart={burst.actions.start}
              onStop={burst.actions.stop}
              onReset={burst.actions.reset}
            />
          </main>

          <AiAnalysisSidebar
            isCollapsed={sidebars.aiCollapsed}
            model={burst.model}
            logCount={state.logs.length}
            onCollapsedChange={handleAiSidebarCollapsedChange}
          />
        </div>
      </div>

      <BottomDrawer
        title="Live log"
        isCollapsed={isLogsCollapsed}
        height={logsHeight}
        minHeight={140}
        maxHeight={620}
        collapsedHeight={LOGS_COLLAPSED_HEIGHT}
        className={isAiOpen ? layoutStyles.logsDrawerAiOpen : undefined}
        onCollapsedChange={setIsLogsCollapsed}
        onHeightChange={setLogsHeight}
      >
        <LogsPanel logs={state.logs} onClearClick={actions.clearLogs} />
      </BottomDrawer>
    </div>
  );
}
