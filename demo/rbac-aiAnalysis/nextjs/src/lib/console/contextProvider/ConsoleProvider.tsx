"use client";

import { ReactNode, useEffect } from "react";
import { ConsoleContext } from "./useConsoleContext";
import { TargetPreset } from "@/lib/infrastructure/transport/http/HttpClientType";
import { useConsoleController } from "../useConsoleController";

const PRESETS: TargetPreset[] = [
  { label: ".NET (Kestrel)", baseUrl: "http://localhost:5000" },
  { label: "Java (Spring)", baseUrl: "http://localhost:8080" },
  { label: "Node", baseUrl: "http://localhost:3001" },
];

export function ConsoleProvider({ children }: { children: ReactNode }) {
  const controller = useConsoleController(PRESETS);

  useEffect(() => {
    void controller.actions.bootstrap();
  }, [controller]);

  return (
    <ConsoleContext.Provider value={{ controller }}>
      {children}
    </ConsoleContext.Provider>
  );
}