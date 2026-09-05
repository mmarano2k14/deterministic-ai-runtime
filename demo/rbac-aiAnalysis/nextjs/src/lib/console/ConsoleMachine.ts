import { TargetPreset } from "../infrastructure/transport/http/HttpClientType";
import { ConsoleEvent, ConsoleState } from "./ConsoleType";

export class ConsoleMachine {
  static initial(presets: TargetPreset[]): ConsoleState {
    return {
      status: "Idle",
      baseUrl: presets[0]?.baseUrl ?? "http://localhost:5000",
      demoUserId: "",
      contextKey: "",
      maxInFlight:"5",
      invoiceId: "INV-001",
      amount: 100,
      logs: [],
      lastError: undefined,
      busy: false,
      rotationOverlapMs: "1000"
    };
  }

  static reduce(state: ConsoleState, ev: ConsoleEvent): ConsoleState {
    switch (state.status) {
      // -----------------------------------------------------------
      // IDLE
      // -----------------------------------------------------------
      case "Idle": {
        return ConsoleMachine.reduceIdle(state, ev)
      }
      // -----------------------------------------------------------
      // RUNNING
      // -----------------------------------------------------------
      case "Running": {
        return ConsoleMachine.reduceRunning(state, ev);
      }
      // -----------------------------------------------------------
      // EXPIRED
      // -----------------------------------------------------------
      case "Expired": {
        return ConsoleMachine.reduceExpired(state, ev);
      }
      // -----------------------------------------------------------
      // ERROR
      // -----------------------------------------------------------
      case "Error": {
        return ConsoleMachine.reduceError(state,ev);
      }
    }
  }

  private static reduceIdle(state: ConsoleState, ev: ConsoleEvent) : ConsoleState {
    switch (ev.type) {
        case "TargetChanged":
            return { ...state, baseUrl: ev.baseUrl };
        case "DemoUserChanged":
            return { ...state, demoUserId: ev.demoUserId };
        case "ContextChanged":
            return { ...state, contextKey: ev.contextKey };
        case "InvoiceChanged":
            return { ...state, invoiceId: ev.invoiceId };
        case "AmountChanged":
            return { ...state, amount: ev.amount };
        case "maxInFlightChanged":
            return { ...state, maxInFlight: ev.maxInFlightValue };
        case "rotationOverlapMsChange":
            return { ...state, rotationOverlapMs: ev.rotationOverlapMs };
        case "LogsChanged":
            return { ...state, logs: ev.logs };
        case "ClearLogs":
            return { ...state, logs: [] };
        case "StartCall":
            return { ...state, status: "Running", busy: true, lastError: undefined };
        case "ResetError":
            return { ...state, lastError: undefined };
        case "LoginSucceeded":
            return {...state, demoUserId: ev.demoUserId, contextKey: ev.contextKey };
        case "BootStrapSucceeded":
            return {...state, demoUserId: ev.demoUserId, contextKey: ev.contextKey };
        default:
            return state;
    }
  }

  private static reduceRunning(state: ConsoleState, ev: ConsoleEvent): ConsoleState{
    switch (ev.type) {
        // while running we lock inputs (optional) – ignore changes
        case "TargetChanged":
        case "DemoUserChanged":
        case "ContextChanged":
        case "InvoiceChanged":
        case "AmountChanged":
        return state;

        case "LogsChanged":
            return { ...state, logs: ev.logs };

        case "CallSucceeded": {
        // rotation from API (if any)
        const nextKey = ev.rotatedContextKey ?? state.contextKey;
            return { ...state, status: "Idle", busy: false, contextKey: nextKey };
        }

        case "CallForbiddenOrUnauthorized": {
        // treat 401/403 as "Expired" in demo
            if (ev.httpStatus === 401 || ev.httpStatus === 403) {
                return {
                ...state,
                status: "Expired",
                busy: false,
                lastError: `Session expired (${ev.httpStatus}).`,
                };
            }
            return {
                ...state,
                status: "Error",
                busy: false,
                lastError: `HTTP ${ev.httpStatus}`,
            };
        }

        case "CallFailed":
            return { ...state, status: "Error", busy: false, lastError: ev.message };
        case "LoginSucceeded":
        default:
            return state;
    }
  }

  

  private static reduceExpired(state: ConsoleState, ev: ConsoleEvent): ConsoleState {
    switch (ev.type) {
        case "ContextChanged":
            // user can paste a new contextKey to recover
            return { ...state, contextKey: ev.contextKey, lastError: undefined };
        case "TargetChanged":
            return { ...state, baseUrl: ev.baseUrl };
        case "DemoUserChanged":
            return { ...state, demoUserId: ev.demoUserId };
        case "InvoiceChanged":
            return { ...state, invoiceId: ev.invoiceId };
        case "AmountChanged":
            return { ...state, amount: ev.amount };
        case "maxInFlightChanged":
            return { ...state, maxInFlight: ev.maxInFlightValue };
        case "rotationOverlapMsChange":
            return { ...state, rotationOverlapMs: ev.rotationOverlapMs };
        case "LogsChanged":
            return { ...state, logs: ev.logs };

        case "ClearLogs":
            return { ...state, logs: [] };

        case "StartCall":
            return { ...state, status: "Running", busy: true, lastError: undefined };
        case "LoginSucceeded":
        default:
            return state;
    }
  }

  private static reduceError(state: ConsoleState, ev: ConsoleEvent): ConsoleState {
    switch (ev.type) {
        case "ResetError":
            return { ...state, status: "Idle", lastError: undefined };

        case "TargetChanged":
            return { ...state, baseUrl: ev.baseUrl };
        case "DemoUserChanged":
            return { ...state, demoUserId: ev.demoUserId };
        case "ContextChanged":
            return { ...state, contextKey: ev.contextKey };
        case "InvoiceChanged":
            return { ...state, invoiceId: ev.invoiceId };
        case "AmountChanged":
            return { ...state, amount: ev.amount };
        case "maxInFlightChanged":
            return { ...state, maxInFlight: ev.maxInFlightValue };
        case "rotationOverlapMsChange":
            return { ...state, rotationOverlapMs: ev.rotationOverlapMs };
        case "LogsChanged":
            return { ...state, logs: ev.logs };
        case "ClearLogs":
            return { ...state, logs: [] };

        case "StartCall":
            return { ...state, status: "Running", busy: true, lastError: undefined };
        case "LoginSucceeded":
        default:
            return state;
    }
  }
}