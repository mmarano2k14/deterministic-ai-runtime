"use client";

import { JSX } from "react";
import type { SingleBurstConfig } from "@/lib/console/burst/runtime/BurstMachineType";

export type SingleBurstFieldsProps = {
  disabled: boolean;
  isRunning: boolean;
  config: SingleBurstConfig;
  onChange: (next: SingleBurstConfig) => void;
};

/**
 * Fields specific to the "single-burst" mode.
 * All requests are fired immediately, so no concurrency field is needed here.
 */
export function SingleBurstFields(props: SingleBurstFieldsProps): JSX.Element {
  const { disabled, isRunning, config, onChange } = props;

  return (
    <div className="form-grid">
      <div>
        <label>Delay per request (ms)</label>
        <input
          type="number"
          value={config.delayMs}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onChange({
              ...config,
              delayMs: Number(e.target.value),
            })
          }
        />
      </div>
    </div>
  );
}