"use client";

import { JSX } from "react";
import type { WaveBatchesConfig } from "@/lib/console/burst/runtime/BurstMachineType";

export type WaveBatchesFieldsProps = {
  disabled: boolean;
  isRunning: boolean;
  config: WaveBatchesConfig;
  onChange: (next: WaveBatchesConfig) => void;
};

/**
 * Fields specific to the "wave-batches" mode.
 * Here, batchSize is explicit and wavePauseMs controls the gap between waves.
 */
export function WaveBatchesFields(props: WaveBatchesFieldsProps): JSX.Element {
  const { disabled, isRunning, config, onChange } = props;

  return (
    <div className="form-grid">
      <div>
        <label>Batch size</label>
        <input
          type="number"
          value={config.batchSize}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onChange({
              ...config,
              batchSize: Number(e.target.value),
            })
          }
        />
      </div>

      <div>
        <label>Wave pause (ms)</label>
        <input
          type="number"
          value={config.wavePauseMs}
          disabled={disabled || isRunning}
          onChange={(e) =>
            onChange({
              ...config,
              wavePauseMs: Number(e.target.value),
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