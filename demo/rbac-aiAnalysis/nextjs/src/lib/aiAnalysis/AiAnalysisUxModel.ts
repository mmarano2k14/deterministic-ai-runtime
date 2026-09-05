import {
  AiAnalysisQuickActionDefinition,
  AiAnalysisQuickActionKey,
  AiAnalysisScope,
  AiAnalysisScopeDefinition,
  AiAnalysisStatus,
  AiAnalysisStatusDefinition,
} from "./AiAnalysisType";

export class AiAnalysisUxModel {
  private static readonly scopeDefinitions: AiAnalysisScopeDefinition[] = [
    {
      key: "current-run",
      label: "Current scenario / run",
      description:
        "Analyze the current scenario metrics and the logs captured around this run.",
      available: true,
    },
    {
      key: "current-execution",
      label: "Current DAG / execution",
      description:
        "Available after runtime DAG execution selection and lifecycle correlation are connected.",
      available: false,
    },
    {
      key: "selected-logs",
      label: "Selected logs",
      description:
        "Available after explicit Live log selection is connected to the analysis workspace.",
      available: false,
    },
    {
      key: "last-30s",
      label: "Last 30 seconds",
      description:
        "Analyze a bounded rolling window from the current live observability stream.",
      available: true,
    },
  ];

  private static readonly quickActionDefinitions: AiAnalysisQuickActionDefinition[] =
    [
      {
        key: "analyze",
        label: "Analyze",
        prompt:
          "Analyze the current execution behavior and summarize the most important findings.",
      },
      {
        key: "explain-failures",
        label: "Explain failures",
        prompt:
          "Explain the observed failures and identify whether they come from authorization, concurrency, policy, DAG execution, or infrastructure behavior.",
      },
      {
        key: "find-anomaly",
        label: "Find anomaly",
        prompt:
          "Find unusual behavior or timing anomalies in the current execution evidence and explain the most likely cause.",
      },
      {
        key: "suggest-scenario",
        label: "Suggest test",
        prompt:
          "Suggest one bounded deterministic scenario that would validate the most important hypothesis from this execution.",
      },
    ];

  private static readonly statusDefinitions: AiAnalysisStatusDefinition[] = [
    {
      key: "provider-pending",
      label: "Provider pending",
      description:
        "The AI provider exists but is not configured with server-side credentials.",
    },
    {
      key: "ready",
      label: "Ready",
      description:
        "The AI provider is configured and ready for runtime analysis.",
    },
    {
      key: "analyzing",
      label: "Analyzing",
      description: "Structured runtime analysis is in progress.",
    },
    {
      key: "finding-available",
      label: "Finding available",
      description: "A structured AI finding is available for review.",
    },
  ];

  public static scopes(): readonly AiAnalysisScopeDefinition[] {
    return this.scopeDefinitions;
  }

  public static quickActions(): readonly AiAnalysisQuickActionDefinition[] {
    return this.quickActionDefinitions;
  }

  public static scopeDescription(scope: AiAnalysisScope): string {
    return (
      this.scopeDefinitions.find((definition) => definition.key === scope)
        ?.description ?? ""
    );
  }

  public static isScopeAvailable(scope: AiAnalysisScope): boolean {
    return (
      this.scopeDefinitions.find((definition) => definition.key === scope)
        ?.available ?? false
    );
  }

  public static promptForAction(action: AiAnalysisQuickActionKey): string {
    return (
      this.quickActionDefinitions.find((definition) => definition.key === action)
        ?.prompt ?? ""
    );
  }

  public static status(status: AiAnalysisStatus): AiAnalysisStatusDefinition {
    return (
      this.statusDefinitions.find((definition) => definition.key === status) ??
      this.statusDefinitions[0]
    );
  }
}
