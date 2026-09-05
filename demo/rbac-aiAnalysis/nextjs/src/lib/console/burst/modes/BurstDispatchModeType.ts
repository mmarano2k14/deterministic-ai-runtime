import type { RequestSpec } from "@/lib/infrastructure/transport/http/HttpClientType";
import type { IBurstApi } from "../execution/BurstController";
import type { BurstConfig, BurstEvent } from "../runtime/BurstMachineType";

/**
 * Shared execution context passed to every dispatch mode implementation.
 */
export type BurstDispatchExecutionArgs<TConfig extends BurstConfig = BurstConfig> = {
  api: IBurstApi;
  config: TConfig;
  stopRequested: () => boolean;
  dispatch: (ev: BurstEvent) => void;
  makeRequest: (i: number) => RequestSpec;
  /**
   * Allows a dispatch mode to capture the current context key.
   * Used by wave-batches-staggered to freeze one key per wave.
   */
  getCurrentContextKey?: () => string | undefined;
};

/**
 * Contract implemented by each dispatch mode.
 */
export interface IBurstDispatchMode<TConfig extends BurstConfig = BurstConfig> {
  execute(args: BurstDispatchExecutionArgs<TConfig>): Promise<void>;
}

/**
 * Small utility kept close to execution strategies.
 */
export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}