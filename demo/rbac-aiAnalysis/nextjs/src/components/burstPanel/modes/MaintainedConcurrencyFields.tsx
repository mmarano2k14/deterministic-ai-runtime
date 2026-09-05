"use client";

import { JSX } from "react";
import type { MaintainedConcurrencyConfig } from "@/lib/console/burst/runtime/BurstMachineType";

export type MaintainedConcurrencyFieldsProps = {
  disabled: boolean;
  isRunning: boolean;
  config: MaintainedConcurrencyConfig;
  onChange: (next: MaintainedConcurrencyConfig) => void;
};

export function MaintainedConcurrencyFields(
  props: MaintainedConcurrencyFieldsProps
): JSX.Element {
  const { disabled, isRunning, config, onChange } = props;

  return (
    <div className="form-grid">
      <div>
        <label>Concurrency</label>
        <input
          type="number"
          value={config.concurrency}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onChange({
              ...config,
              concurrency: Number(e.target.value),
            })
          }
        />
      </div>

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