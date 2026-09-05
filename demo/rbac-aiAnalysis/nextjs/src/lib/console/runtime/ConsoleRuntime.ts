
import { MultiplexedRbacApi } from "@/lib/rbac/MultiplexedRbacApi";
import type { TargetPreset } from "@/lib/infrastructure/transport/http/HttpClientType";

import type {
  ConsoleActions,
  ConsoleController,
  ConsoleRealtime,
} from "../ConsoleController";
import type { ConsoleEvent, ConsoleState } from "../ConsoleType";
import type { IConsoleRuntime } from "./IConsoleRuntime";
import { ConsoleRealtimeRuntime } from "./ConsoleRealtimeRuntime";
import type { IConsoleRealtimeRuntime } from "./IConsoleRealtimeRuntime";
import { InMemoryLogSink } from "@/lib/infrastructure/logs/InMemoryLogSink";
import { ConsoleLogEntry, RealtimeLogEntry } from "@/lib/infrastructure/logs/inMemoryLogType";


type ConsoleRuntimeDependencies = {
  presets: TargetPreset[];
  dispatch: (event: ConsoleEvent) => void;
  initialState: ConsoleState;
};

type RunResult = {
  rotatedContextKey?: string;
  status?: number;
  error?: string;
};

/**
 * Framework-agnostic console runtime.
 *
 * Responsibilities:
 * - own API client
 * - own console log sink
 * - orchestrate high-level console actions
 * - coordinate realtime integration through ConsoleRealtimeRuntime
 *
 * No React logic here.
 */
export class ConsoleRuntime implements IConsoleRuntime {
  private readonly dispatchFn: (event: ConsoleEvent) => void;

  private readonly listeners = new Set<() => void>();
  private currentState: ConsoleState;

  private readonly logSink: InMemoryLogSink<ConsoleLogEntry>;
  private readonly apiInstance: MultiplexedRbacApi;
  private readonly realtimeRuntime: IConsoleRealtimeRuntime;

  private readonly actionsObject: ConsoleActions;
  private readonly realtimeObject: ConsoleRealtime;
  private readonly controllerInstance: ConsoleController;

  public static create(deps: ConsoleRuntimeDependencies): ConsoleRuntime {
    return new ConsoleRuntime(deps);
  }

  private constructor(deps: ConsoleRuntimeDependencies) {
    const initialState = deps.initialState;

    this.dispatchFn = deps.dispatch;
    this.currentState = initialState;

    /*
        ---------------------------------------------
        Log sink

        Central in-memory sink used by:
        - HTTP client logging
        - runtime logs received from realtime
    */

    this.logSink = new InMemoryLogSink<ConsoleLogEntry>((items) => {
      this.dispatchFn({ type: "LogsChanged", logs: [...items] });
    });

    /*
        ---------------------------------------------
        API runtime

        Stable API instance used by the console.
    */

    this.apiInstance = MultiplexedRbacApi.createInstanceRef(
      initialState.baseUrl,
      this.logSink
    );

    /*
        ---------------------------------------------
        Realtime runtime

        This sub-runtime owns:
        - transport connection
        - event subscriptions
        - log routing
        - context rotation handling
    */

    this.realtimeRuntime = new ConsoleRealtimeRuntime({
      getBaseUrl: () => this.apiInstance.baseUrl,
      getUserId: () => this.currentState.demoUserId ?? this.apiInstance.demoUserId,
      onContextRotated: (contextKey: string) => {
        this.apiInstance.contextKey = contextKey;

        this.dispatchFn({
          type: "ContextChanged",
          contextKey,
        });
      },
      appendLog: (entry: RealtimeLogEntry) => {
        this.logSink.push(entry);
      },
    });

    this.actionsObject = this.createActions();
    this.realtimeObject = this.createRealtime();
    this.controllerInstance = this.createController();
  }

  /**
   * Public controller facade exposed to the UI layer.
   */
  public get controller(): ConsoleController {
    return this.controllerInstance;
  }

  /**
   * Synchronizes the current runtime view of state with the latest React state.
   */
  public syncState(state: ConsoleState): void {
    this.currentState = state;

    this.apiInstance.baseUrl = state.baseUrl;
    this.apiInstance.demoUserId = state.demoUserId;
    this.apiInstance.maxInFlight = state.maxInFlight;
    this.apiInstance.rotationOverlapMs = state.rotationOverlapMs
    //this.apiInstance.contextKey = state.contextKey;
    // Do not overwrite apiInstance.contextKey here.
    // The rotating context key is managed by the API session itself
    // and may change faster than React state synchronization.

    this.notify();
  }

  /**
   * Releases runtime resources.
   */
  public async dispose(): Promise<void> {
    await this.realtimeRuntime.dispose();
  }

  /**
   * Builds the public controller facade exposed to the UI layer.
   *
   * The facade is created once and remains stable for the lifetime
   * of the runtime.
   */
  private createController(): ConsoleController {
    // eslint-disable-next-line @typescript-eslint/no-this-alias
    const runtime = this;

    return {
      get state(): ConsoleState {
        return runtime.currentState;
      },

      dispatch(event: ConsoleEvent): void {
        runtime.dispatchFn(event);
      },

      get actions(): ConsoleActions {
        return runtime.actionsObject;
      },

      api: runtime.apiInstance,

      realtime: runtime.realtimeObject,

      subscribe(listener: () => void): () => void {
        return runtime.subscribe(listener);
      },
    };
  }

  /**
   * Builds the realtime facade exposed by the controller.
   */
  private createRealtime(): ConsoleRealtime {
    return {
      client: this.realtimeRuntime.client,
      connect: () => this.realtimeRuntime.connect(),
      disconnect: () => this.realtimeRuntime.disconnect(),
      getState: () => this.realtimeRuntime.getState(),
    };
  }

  /**
   * Subscribes to runtime state notifications.
   */
  private subscribe(listener: () => void): () => void {
    this.listeners.add(listener);

    return () => {
      this.listeners.delete(listener);
    };
  }

  /**
   * Notifies listeners that the runtime state view has changed.
   */
  private notify(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }

  /**
   * Builds the high-level console actions exposed to the UI.
   */
  private createActions(): ConsoleActions {
    const run = async (call: () => Promise<RunResult>) => {
      this.dispatchFn({ type: "StartCall" });

      try {
        const result = await call();

        if (
          typeof result.status === "number" &&
          (result.status === 401 || result.status === 403)
        ) {
          this.dispatchFn({
            type: "CallForbiddenOrUnauthorized",
            httpStatus: result.status,
          });
          return;
        }

        if (result.error) {
          this.dispatchFn({
            type: "CallFailed",
            message: result.error,
          });
          return;
        }

        this.dispatchFn({
          type: "CallSucceeded",
          rotatedContextKey: result.rotatedContextKey,
        });
      } catch (e: unknown) {
        const msg = e instanceof Error ? e.message : String(e);

        this.dispatchFn({
          type: "CallFailed",
          message: msg,
        });
      }
    };

    return {
      /**
       * Bootstraps the console runtime from backend session/cookie state.
       */
      bootstrap: async () => {
        this.dispatchFn({ type: "StartCall" });

        try {
          const res = await this.apiInstance.bootstrap();

          if (res.kind === "error") {
            this.dispatchFn({
              type: "CallFailed",
              message: res.error.error,
            });
            return;
          }

          this.dispatchFn({
            type: "CallSucceeded",
            rotatedContextKey: this.apiInstance.contextKey || undefined,
          });

          this.dispatchFn({
            type: "BootStrapSucceeded",
            demoUserId: this.apiInstance.demoUserId,
            contextKey: this.apiInstance.contextKey,
          });

          if(this.apiInstance.demoUserId){
            await this.realtimeRuntime.disconnect();
            await this.realtimeRuntime.connect(this.apiInstance.demoUserId);
          }
          
        } catch (e: unknown) {
          const msg = e instanceof Error ? e.message : String(e);

          this.dispatchFn({
            type: "CallFailed",
            message: msg,
          });
        }
      },

      /**
       * Logs a demo user into the backend runtime.
       */
      login: async (username: string) => {
        this.dispatchFn({ type: "StartCall" });

        try {
          const res = await this.apiInstance.login(username);

          if (res.kind === "error") {
            this.dispatchFn({
              type: "CallFailed",
              message: res.error.error,
            });
            return;
          }

          this.dispatchFn({
            type: "CallSucceeded",
            rotatedContextKey: this.apiInstance.contextKey || undefined,
          });

          this.dispatchFn({
            type: "LoginSucceeded",
            demoUserId: username,
            contextKey: this.apiInstance.contextKey,
          });

          await this.realtimeRuntime.disconnect();
          await this.realtimeRuntime.connect(username);

          return true;
        } catch (e: unknown) {
          const msg = e instanceof Error ? e.message : String(e);

          this.dispatchFn({
            type: "CallFailed",
            message: msg,
          });

          return false;
        }
      },

      /**
       * Requests a fresh context key from the backend runtime.
       */
      getContextKey: async () => {
        await run(async () => {
          const res = await this.apiInstance.getContextKey();

          return {
            status: res.kind === "ok" ? res.response.status : undefined,
            rotatedContextKey: this.apiInstance.contextKey || undefined,
            error: res.kind === "error" ? res.error.error : undefined,
          };
        });
      },

      /**
       * Reads the current invoice selected in the console state.
       */
      readInvoice: async () => {
        const current = this.currentState;

        await run(async () => {
          const res = await this.apiInstance.readInvoice(current.invoiceId);

          return {
            status: res.kind === "ok" ? res.response.status : undefined,
            rotatedContextKey: this.apiInstance.contextKey || undefined,
            error: res.kind === "error" ? res.error.error : undefined,
          };
        });
      },

      /**
       * Refunds the current invoice using the selected amount.
       */
      refundInvoice: async () => {
        const current = this.currentState;

        await run(async () => {
          const res = await this.apiInstance.refundInvoice(
            current.invoiceId,
            current.amount
          );

          return {
            status: res.kind === "ok" ? res.response.status : undefined,
            rotatedContextKey: this.apiInstance.contextKey || undefined,
            error: res.kind === "error" ? res.error.error : undefined,
          };
        });
      },

      /**
       * Clears all in-memory logs displayed in the console.
       */
      clearLogs: () => {
        this.logSink.clear();
        this.dispatchFn({ type: "ClearLogs" });
      },

      /**
       * Clears the current UI/runtime error state.
       */
      resetError: () => {
        this.dispatchFn({ type: "ResetError" });
      },
    };
  }
}