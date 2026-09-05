import { RequestSpec, HeaderOverride } from "@/lib/infrastructure/transport/http/HttpClientType";
import { ApiCallResult } from "../execution/BurstController";
import { BurstConfig } from "../runtime/BurstMachineType";
import { BurstDispatchExecutionArgs, IBurstDispatchMode } from "./BurstDispatchModeType";

export abstract class BurstDispatchModeBase<TConfig extends BurstConfig>
  implements IBurstDispatchMode<TConfig> {
  private _args?: BurstDispatchExecutionArgs<TConfig>;

  protected get args(): BurstDispatchExecutionArgs<TConfig> {
    if (!this._args) {
      throw new Error("Burst dispatch mode has not been initialized.");
    }

    return this._args;
  }

  protected get dispatch(): BurstDispatchExecutionArgs<TConfig>["dispatch"] {
    return this.args.dispatch;
  }

  protected initialize(args: BurstDispatchExecutionArgs<TConfig>): void {
    this._args = args;
  }

  protected clear(): void {
    this._args = undefined;
  }

  protected dispatchResult(result: ApiCallResult): void {
    if (result.kind === "ok") {
      if (result.status >= 200 && result.status < 300) {
        this.dispatch({ type: "ResultOk", durationMs: result.durationMs });
      } else {
        this.dispatch({
          type: "ResultHttp",
          status: result.status,
          durationMs: result.durationMs,
        });
      }

      return;
    }

    if (typeof result.status === "number") {
      this.dispatch({
        type: "ResultHttp",
        status: result.status,
        durationMs: result.durationMs,
      });
      return;
    }

    this.dispatch({ type: "ResultError", message: result.message });
  }

  protected tickStart(count: number): void {
    this.dispatch({ type: "TickStart", count });
  }

  protected tickComplete(count: number): void {
    this.dispatch({ type: "TickComplete", count });
  }

  protected shouldStop(): boolean {
    return this.args.stopRequested();
  }

  protected async executeRequest(
    spec: RequestSpec,
    options?: HeaderOverride
  ): Promise<void> {
    this.tickStart(1);

    try {
      const result = await this.args.api.call(spec, options);
      this.dispatchResult(result);
    } finally {
      this.tickComplete(1);
    }
  }

  abstract execute(args: BurstDispatchExecutionArgs<TConfig>): Promise<void>;
}