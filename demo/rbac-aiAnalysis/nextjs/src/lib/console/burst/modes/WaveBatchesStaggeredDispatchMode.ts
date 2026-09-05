import type { WaveBatchesStaggeredConfig } from "../runtime/BurstMachineType";
import type { BurstDispatchExecutionArgs } from "./BurstDispatchModeType";
import { sleep } from "./BurstDispatchModeType";
import { BurstDispatchModeBase } from "./BurstDispatchModeBase";

/**
 * Sends fixed-size waves, but staggers the requests inside each wave.
 *
 * Important:
 * - The context key must be captured once per wave.
 * - All requests in the same wave must reuse that same key.
 */
export class WaveBatchesStaggeredDispatchMode
  extends BurstDispatchModeBase<WaveBatchesStaggeredConfig>
{
  public async execute(
    args: BurstDispatchExecutionArgs<WaveBatchesStaggeredConfig>
  ): Promise<void> {
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

        /**
         * Capture the context key ONCE for the whole wave.
         * Allow tetsting maximum inflight
         */
        const waveContextKey = this.args.getCurrentContextKey?.();

        const tasks: Promise<void>[] = [];

        for (let i = start; i < end; i++) {
          if (this.shouldStop()) break;

          const indexInWave = i - start;
          const staggerDelayMs = indexInWave * delayMs;

          tasks.push(
            this.executeOne(
              i,
              staggerDelayMs,
              waveContextKey
            )
          );
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
    delayBeforeSendMs: number,
    waveContextKey: string | undefined
  ): Promise<void> {
    this.tickStart(1);

    try {
      if (delayBeforeSendMs > 0) {
        await sleep(delayBeforeSendMs);
      }

      const spec = this.args.makeRequest(index);

      const result = await this.args.api.call(spec, {
        contextKeyOverride: waveContextKey,
      });

      this.dispatchResult(result);
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e);
      this.dispatch({ type: "ResultError", message: msg });
    } finally {
      this.tickComplete(1);
    }
  }
}