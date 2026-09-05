import type { SingleBurstConfig } from "../runtime/BurstMachineType";
import type { BurstDispatchExecutionArgs } from "./BurstDispatchModeType";
import { sleep } from "./BurstDispatchModeType";
import { BurstDispatchModeBase } from "./BurstDispatchModeBase";

/**
 * Sends all requests immediately in a single burst.
 * No maintained concurrency, no wave logic.
 */
export class SingleBurstDispatchMode extends BurstDispatchModeBase<SingleBurstConfig> {
  public async execute(args: BurstDispatchExecutionArgs<SingleBurstConfig>): Promise<void> {
    this.initialize(args);

    try {
      const { config, stopRequested } = args;

      const total = Math.max(1, Math.floor(config.total));
      const delayMs = Math.max(0, Math.floor(config.delayMs));

      const tasks: Promise<void>[] = [];

      for (let i = 0; i < total; i++) {
        if (stopRequested()) break;
        tasks.push(this.executeOne(i, delayMs));
      }

      await Promise.all(tasks);
    } finally {
      this.clear();
    }
  }

  private async executeOne(index: number, delayMs: number): Promise<void> {
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