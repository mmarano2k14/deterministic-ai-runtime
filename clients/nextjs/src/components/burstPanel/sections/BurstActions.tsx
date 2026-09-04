"use client";

import { JSX } from "react";
import { Button } from "@/components/ui/Button";

export type BurstActionsProps = {
  disabled: boolean;
  isRunning: boolean;
  elapsedMs?: number;
  onStart: () => void;
  onStop: () => void;
  onReset: () => void;
};

/**
 * Action buttons and elapsed time display.
 */
export function BurstActions(props: BurstActionsProps): JSX.Element {
  const { disabled, isRunning, onStart, onStop, onReset } = props;

  return (
    <div className="burst-action">
      <Button disabled={disabled || isRunning} onClick={() => onStart()}>
        Start Burst
      </Button>

      <Button disabled={disabled || !isRunning} onClick={onStop}>
        Stop
      </Button>

      <Button disabled={disabled || isRunning} onClick={onReset}>
        Reset
      </Button>
    </div>
  );
}