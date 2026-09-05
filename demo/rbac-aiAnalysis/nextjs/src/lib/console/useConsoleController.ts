"use client";

import { useEffect, useReducer, useState } from "react";

import { ConsoleMachine } from "./ConsoleMachine";
import type { ConsoleController } from "./ConsoleController";
import type { TargetPreset } from "../infrastructure/transport/http/HttpClientType";

import { ConsoleRuntime } from "./runtime/ConsoleRuntime";
import type { IConsoleRuntime } from "./runtime/IConsoleRuntime";

/**
 * React adapter for the framework-agnostic ConsoleRuntime.
 *
 * Responsibilities:
 * - host the React reducer/state machine
 * - create the runtime once
 * - synchronize React state into the runtime
 * - dispose runtime resources on unmount
 *
 * All console business logic stays inside ConsoleRuntime TO AVOID REACT DEPENDENCY.
 */
export function useConsoleController(
  presets: TargetPreset[]
): ConsoleController {
  /*
      ---------------------------------------------
      State machine
      ---------------------------------------------
  */

  const initialState = ConsoleMachine.initial(presets);
  const [state, dispatch] = useReducer(ConsoleMachine.reduce, initialState);

  /*
      ---------------------------------------------
      Stable runtime instance

      The runtime is created once using the initial reducer state.
      From that point on, syncState() keeps the runtime updated.
      ---------------------------------------------
  */

  const [runtime] = useState<IConsoleRuntime>(() =>
    ConsoleRuntime.create({
      presets,
      dispatch,
      initialState,
    })
  );

  /*
      ---------------------------------------------
      Keep runtime synchronized with latest state
      ---------------------------------------------
  */

  useEffect(() => {
    runtime.syncState(state);
  }, [runtime, state]);

  /*
      ---------------------------------------------
      Cleanup runtime resources on unmount
      ---------------------------------------------
  */

  useEffect(() => {
    return () => {
      void runtime.dispose();
    };
  }, [runtime]);

  /*
      ---------------------------------------------
      Expose public controller facade
      ---------------------------------------------
  */

  return runtime.controller;
}