"use client";

import { JSX } from "react";
import type { WaveBatchesStaggeredConfig } from "@/lib/console/burst/runtime/BurstMachineType";

export type WaveBatchesStaggeredFieldsProps = {
  disabled: boolean;
  isRunning: boolean;
  config: WaveBatchesStaggeredConfig;
  onChange: (next: WaveBatchesStaggeredConfig) => void;
};

/**
 * Fields specific to the "wave-batches-staggered" mode.
 * In this mode:
 * - batchSize controls the wave size
 * - delayMs is the delay between requests inside the same wave
 * - wavePauseMs is the pause between two waves
 */
export function WaveBatchesStaggeredFields(
  props: WaveBatchesStaggeredFieldsProps
): JSX.Element {
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
        <label>Delay between requests (ms)</label>
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