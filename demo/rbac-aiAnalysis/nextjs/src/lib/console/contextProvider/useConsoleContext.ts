"use client";

import { createContext, useContext, useSyncExternalStore } from "react";
import { ConsoleController } from "../ConsoleController";

type ConsoleContextType = {
  controller: ConsoleController;
};

export const ConsoleContext = createContext<ConsoleContextType | null>(null);

export function useConsoleContext() {
  const ctx = useContext(ConsoleContext);

  if (!ctx) {
    throw new Error("useConsole must be used inside ConsoleProvider");
  }

  const controller = ctx.controller;

  const state = useSyncExternalStore(
    controller.subscribe,
    () => controller.state,
    () => controller.state
  );

  return {
    controller,
    state,
    dispatch: controller.dispatch,
    actions: controller.actions,
    api: controller.api,
  };
}