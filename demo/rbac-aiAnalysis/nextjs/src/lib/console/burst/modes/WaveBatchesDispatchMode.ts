import type { WaveBatchesConfig } from "../runtime/BurstMachineType";
import type { BurstDispatchExecutionArgs } from "./BurstDispatchModeType";
import { sleep } from "./BurstDispatchModeType";
import { BurstDispatchModeBase } from "./BurstDispatchModeBase";

/**
 * Sends fixed-size batches.
 * Each wave is fully awaited before the next one starts.
 */
export class WaveBatchesDispatchMode extends BurstDispatchModeBase<WaveBatchesConfig> {
  public async execute(args: BurstDispatchExecutionArgs<WaveBatchesConfig>): Promise<void> {
    this.initialize(args);

    try {
      const { config } = args;

      const total = Math.max(1, Math.floor(config.total));
      const batchSize = Math.max(1, Math.floor(config.batchSize));
      const delayMs = Math.max(0, Math.floor(config.delayMs));
      const wavePauseMs = Math.max(0, Math.floor(config.wavePauseMs));

      for (let start = 0; start < total; start += batchSize) {
        if (this.shouldStop()) return;

        const end = Math.min(start + batchSize, total);
        const tasks: Promise<void>[] = [];

        for (let i = start; i < end; i++) {
          if (this.shouldStop()) break;
          tasks.push(this.executeOne(i, delayMs));
        }

        await Promise.all(tasks);

        const hasMore = end < total;
        if (hasMore && wavePauseMs > 0 && !this.shouldStop()) {
          await sleep(wavePauseMs);
        }
      }
    } finally {
      this.clear();
    }
  }

  private async executeOne(
    index: number,
    delayMs: number
  ): Promise<void> {
    this.tickStart(1);

    try {
      if (delayMs > 0) {
        await sleep(delayMs);
      }

      const spec = this.args.makeRequest(index);
      const result = await this.args.api.call(spec);

      this.dispatchResult(result);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e);
      this.dispatch({ type: "ResultError", message: msg });
    } finally {
      this.tickComplete(1);
    }
  }
}