"use client";

import { JSX } from "react";
import { StatusItem } from "./StatusItem";

type Props = {
  status: string;
  busy: boolean;
  lastError?: string | null;
  username?: string;
  contextKey?: string;
  onDismissError?: () => void;
};

export function ConsoleStatusBar({
  status,
  busy,
  lastError,
  username,
  contextKey,
  onDismissError,
}: Props): JSX.Element {
  return (
    <div className="console-status">
      <label>Console State</label>
      <div className="console-status__group">
        <StatusItem label="State" value={status} />
        <StatusItem label="Busy" value={busy ? "true" : "false"} />
        {username && <StatusItem label="User" value={username} />}
        {contextKey && (
          <StatusItem label="Context" value={contextKey} mono />
        )}
      </div>

      {lastError && (
        <div className="console-status__error">
          <span>
            <b>Error:</b> {lastError}
          </span>

          {onDismissError && (
            <button
              className="console-status__dismiss"
              onClick={onDismissError}
              disabled={busy}
            >
              Dismiss
            </button>
          )}
        </div>
      )}
    </div>
  );
}
