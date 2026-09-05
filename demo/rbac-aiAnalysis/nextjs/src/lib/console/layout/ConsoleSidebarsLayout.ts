export type ConsoleControlTabKey = "scenarios" | "request" | "burst";

export type ConsoleSidebarsState = {
  controlsCollapsed: boolean;
  aiCollapsed: boolean;
  activeControlTab: ConsoleControlTabKey;
};

export class ConsoleSidebarsLayout {
  public static createDefault(): ConsoleSidebarsState {
    return {
      controlsCollapsed: false,
      aiCollapsed: true,
      activeControlTab: "scenarios",
    };
  }

  public static setControlsCollapsed(
    state: ConsoleSidebarsState,
    collapsed: boolean
  ): ConsoleSidebarsState {
    return {
      ...state,
      controlsCollapsed: collapsed,
      aiCollapsed: collapsed ? state.aiCollapsed : true,
    };
  }

  public static openControlTab(
    state: ConsoleSidebarsState,
    tab: ConsoleControlTabKey
  ): ConsoleSidebarsState {
    return {
      ...state,
      activeControlTab: tab,
      controlsCollapsed: false,
      aiCollapsed: true,
    };
  }

  public static setAiCollapsed(
    state: ConsoleSidebarsState,
    collapsed: boolean
  ): ConsoleSidebarsState {
    return {
      ...state,
      aiCollapsed: collapsed,
      controlsCollapsed: collapsed ? state.controlsCollapsed : true,
    };
  }
}
