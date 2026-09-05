"use client";

import { JSX } from "react";
import { ConsoleControlTabKey } from "@/lib/console/layout/ConsoleSidebarsLayout";

export type ControlSidebarTabsProps = {
  activeTab: ConsoleControlTabKey;
  onTabClick: (tab: ConsoleControlTabKey) => void;
};

const tabs: Array<{
  key: ConsoleControlTabKey;
  label: string;
  shortLabel: string;
}> = [
  { key: "scenarios", label: "Scenario Presets", shortLabel: "S" },
  { key: "request", label: "Request", shortLabel: "R" },
  { key: "burst", label: "Burst", shortLabel: "B" },
];

export function ControlSidebarTabs(
  props: ControlSidebarTabsProps
): JSX.Element {
  const { activeTab, onTabClick } = props;

  return (
    <>
      {tabs.map((tab) => (
        <button
          key={tab.key}
          type="button"
          className={
            activeTab === tab.key
              ? "console-sidebar__mini-tab active"
              : "console-sidebar__mini-tab"
          }
          onClick={() => onTabClick(tab.key)}
          title={tab.label}
          aria-label={tab.label}
        >
          {tab.shortLabel}
        </button>
      ))}
    </>
  );
}
