"use client";

import { JSX, ReactNode } from "react";

export type ConsoleSidePanelSide = "left" | "right";

export type ConsoleSidePanelProps = {
  side: ConsoleSidePanelSide;
  title: string;
  isCollapsed: boolean;
  collapsedContent?: ReactNode;
  children: ReactNode;
  onCollapsedChange: (next: boolean) => void;
};

export function ConsoleSidePanel(props: ConsoleSidePanelProps): JSX.Element {
  const {
    side,
    title,
    isCollapsed,
    collapsedContent,
    children,
    onCollapsedChange,
  } = props;

  const panelClassName = [
    "console-sidebar",
    `console-side-panel--${side}`,
    isCollapsed ? "console-sidebar--collapsed" : "",
  ]
    .filter(Boolean)
    .join(" ");

  const toggleGlyph = getToggleGlyph(side, isCollapsed);

  return (
    <aside className={panelClassName}>
      <div className="console-sidebar__header">
        {!isCollapsed && <div className="console-sidebar__title">{title}</div>}
        <button
          type="button"
          className="console-sidebar__toggle"
          onClick={() => onCollapsedChange(!isCollapsed)}
          aria-expanded={!isCollapsed}
          aria-label={`${isCollapsed ? "Open" : "Close"} ${title}`}
          title={`${isCollapsed ? "Open" : "Close"} ${title}`}
        >
          {toggleGlyph}
        </button>
      </div>

      {isCollapsed ? (
        <div className="console-sidebar__collapsed-tabs">{collapsedContent}</div>
      ) : (
        <div className="console-sidebar__content">{children}</div>
      )}
    </aside>
  );
}

function getToggleGlyph(
  side: ConsoleSidePanelSide,
  isCollapsed: boolean
): string {
  if (side === "left") {
    return isCollapsed ? "▶" : "◀";
  }

  return isCollapsed ? "◀" : "▶";
}
