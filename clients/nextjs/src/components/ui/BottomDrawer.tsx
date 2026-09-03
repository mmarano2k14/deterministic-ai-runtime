"use client";

import React, { JSX, ReactNode, useEffect, useRef } from "react";

export type BottomDrawerProps = {
  title: string;
  children: ReactNode;
  isCollapsed: boolean;
  height: number;
  minHeight?: number;
  maxHeight?: number;
  collapsedHeight?: number;
  className?: string;
  onCollapsedChange: (next: boolean) => void;
  onHeightChange: (next: number) => void;
};

export function BottomDrawer(props: BottomDrawerProps): JSX.Element {
  const {
    title,
    children,
    isCollapsed,
    height,
    minHeight = 140,
    maxHeight = 640,
    collapsedHeight = 56,
    className,
    onCollapsedChange,
    onHeightChange,
  } = props;

  const dragStateRef = useRef<{
    startY: number;
    startHeight: number;
  } | null>(null);

  useEffect(() => {
    const handlePointerMove = (event: PointerEvent): void => {
      const dragState = dragStateRef.current;
      if (!dragState) {
        return;
      }

      const deltaY = dragState.startY - event.clientY;
      const nextHeight = dragState.startHeight + deltaY;
      const clampedHeight = Math.max(minHeight, Math.min(maxHeight, nextHeight));

      onHeightChange(clampedHeight);
    };

    const handlePointerUp = (): void => {
      dragStateRef.current = null;
      document.body.style.userSelect = "";
      document.body.style.cursor = "";
    };

    window.addEventListener("pointermove", handlePointerMove);
    window.addEventListener("pointerup", handlePointerUp);

    return () => {
      window.removeEventListener("pointermove", handlePointerMove);
      window.removeEventListener("pointerup", handlePointerUp);
    };
  }, [maxHeight, minHeight, onHeightChange]);

  function handleResizeStart(
    event: React.PointerEvent<HTMLButtonElement>
  ): void {
    if (isCollapsed) {
      return;
    }

    dragStateRef.current = {
      startY: event.clientY,
      startHeight: height,
    };

    document.body.style.userSelect = "none";
    document.body.style.cursor = "ns-resize";
  }

  const rootClassName = [
    "bottom-drawer",
    isCollapsed ? "bottom-drawer--collapsed" : "",
    className ?? "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <section
      className={rootClassName}
      style={{
        height: isCollapsed ? `${collapsedHeight}px` : `${height}px`,
      }}
    >
      <button
        type="button"
        className="bottom-drawer__resize-handle"
        onPointerDown={handleResizeStart}
        aria-label="Resize bottom drawer"
      />

      <div className="bottom-drawer__header">
        <div className="bottom-drawer__title">{title}</div>

        <button
          type="button"
          className="bottom-drawer__toggle"
          onClick={() => onCollapsedChange(!isCollapsed)}
          aria-expanded={!isCollapsed}
        >
          {isCollapsed ? "▲" : "▼"}
        </button>
      </div>

      <div className="bottom-drawer__content">{!isCollapsed && children}</div>
    </section>
  );
}
