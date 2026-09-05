import type { MaintainedConcurrencyConfig } from "../runtime/BurstMachineType";
import type { BurstDispatchExecutionArgs } from "./BurstDispatchModeType";
import { sleep } from "./BurstDispatchModeType";
import { BurstDispatchModeBase } from "./BurstDispatchModeBase";

/**
 * Keeps N workers active until the configured total is reached.
 */
export class MaintainedConcurrencyDispatchMode
  extends BurstDispatchModeBase<MaintainedConcurrencyConfig>
{
  public async execute(
    args: BurstDispatchExecutionArgs<MaintainedConcurrencyConfig>
  ): Promise<void> {
    this.initialize(args);

    try {
      const { config } = args;

      const total = Math.max(1, Math.floor(config.total));
      const concurrency = Math.max(1, Math.floor(config.concurrency));
      const delayMs = Math.max(0, Math.floor(config.delayMs));

      let nextIndex = 0;

      const workers: Promise<void>[] = [];
      for (let w = 0; w < concurrency; w++) {
        workers.push(
          this.workerLoop(
            total,
            delayMs,
            () => nextIndex++
          )
        );
      }

      await Promise.all(workers);
    } finally {
      this.clear();
    }
  }

  private async workerLoop(
    total: number,
    delayMs: number,
    next: () => number
  ): Promise<void> {
    while (true) {
      if (this.shouldStop()) return;

      const i = next();
      if (i >= total) return;

      this.tickStart(1);

      try {
        if (delayMs > 0) {
          await sleep(delayMs);
        }

        const spec = this.args.makeRequest(i);
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
}