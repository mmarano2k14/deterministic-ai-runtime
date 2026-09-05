import { ConsoleContextAccessor } from "../../ConsoleType";
import type { BurstConfig, BurstEvent, BurstRuntime } from "../runtime/BurstMachineType";
import { BurstPlans } from "../scenarios/BurstPlans";
import type { HeaderKeyRotation, HeaderOverride, RequestSpec } from "@/lib/infrastructure/transport/http/HttpClientType";
import type { IBurstDispatchMode } from "../modes/BurstDispatchModeType";
import { SingleBurstDispatchMode } from "../modes/SingleBurstDispatchMode";
import { MaintainedConcurrencyDispatchMode } from "../modes/MaintainedConcurrencyDispatchMode";
import { WaveBatchesDispatchMode } from "../modes/WaveBatchesDispatchMode";
import { WaveBatchesStaggeredDispatchMode } from "../modes/WaveBatchesStaggeredDispatchMode";
export type ApiCallResult =
  | { kind: "ok"; status: number; durationMs: number; rotation?: HeaderKeyRotation }
  | { kind: "error"; status?: number; durationMs: number; message: string };

export interface IBurstApi {
  call(
    spec: RequestSpec,
    options?: HeaderOverride
  ): Promise<ApiCallResult>;
}

export class BurstController {
  private _stopRequested = false;
  private _consoleContextAccessor: ConsoleContextAccessor;
  constructor(
    private readonly dispatch: (ev: BurstEvent) => void,
    consoleContextAccessor: ConsoleContextAccessor
  ) {
    this._consoleContextAccessor = consoleContextAccessor;
  }

  public configure(cfg: BurstConfig): void {
    this.dispatch({ type: "Configure", config: cfg });
  }

  public reset(): void {
    this._stopRequested = false;
    this.dispatch({ type: "Reset" });
  }

  public stop(): void {
    this._stopRequested = true;
    this.dispatch({ type: "Stop" });
  }
  public async start(
    api: IBurstApi,
    model: BurstRuntime,
    configOverride?: BurstConfig
  ): Promise<void> {
    const st = model.state;
    if (st === "Running" || st === "Stopping") return;

    /**
     * Prefer an explicit config override when provided.
     * This avoids race conditions when configure() has been dispatched
     * but React has not rerendered the model yet.
     */
    const cfg = configOverride ?? model.report?.config;
    if (!cfg) {
      this.dispatch({ type: "Fail", message: "Burst config missing" });
      return;
    }

    this._stopRequested = false;

    /**
     * Capture the execution boundary synchronously before a dispatch mode can
     * create any HTTP log entries. React reducer processing can happen later.
     */
    const startedAt = Date.now();

    // ✅ snapshot explicite
    this.dispatch({ type: "Start", config: cfg, startedAt });

    const plan = BurstPlans.byKey(cfg.planKey);
    const mode = this.createDispatchMode(cfg);
    try {
      await mode.execute({
        api,
        config: cfg,
        stopRequested: () => this._stopRequested,
        dispatch: this.dispatch,
        makeRequest: plan.makeRequest.bind(plan),
        getCurrentContextKey: () => this._consoleContextAccessor.api.contextKey,
      });

      this.dispatch({ type: "Finish" });
    } catch (e: unknown) {
      const msg = e instanceof Error ? e.message : String(e);
      this.dispatch({ type: "Fail", message: msg });
    }
  }
  /**
   * Factory selecting the execution strategy from the explicit dispatch mode.
   */
  private createDispatchMode(cfg: BurstConfig): IBurstDispatchMode {
    switch (cfg.dispatchMode) {
      case "single-burst":
        return new SingleBurstDispatchMode();

      case "maintained-concurrency":
        return new MaintainedConcurrencyDispatchMode();

      case "wave-batches":
        return new WaveBatchesDispatchMode();
      case "wave-batches-staggered":
        return new WaveBatchesStaggeredDispatchMode();
    }
  }
}
