export type DemoConfiguration = Readonly<{
  endpoint: URL;
  token?: string;
  accessContext?: string;
  accessContextHeader: string;
  accessContextEndpoint: URL;
  openAiModel: string;
}>;

export function optional(name: string): string | undefined {
  const value = process.env[name]?.trim();
  return value ? value : undefined;
}

function required(name: string): string {
  const value = optional(name);
  if (value === undefined) {
    throw new Error(`Missing required TypeScript demo configuration '${name}'.`);
  }
  return value;
}

export function loadConfiguration(): DemoConfiguration {
  const endpoint = new URL(required("AI_RUNTIME_ENDPOINT"));
  if (!["http:", "https:"].includes(endpoint.protocol)) {
    throw new Error("AI_RUNTIME_ENDPOINT must be an absolute HTTP or HTTPS URL.");
  }

  const configuredAccessContextEndpoint = optional("AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT");
  const accessContextEndpoint = configuredAccessContextEndpoint === undefined
    ? new URL("/auth/access-context", endpoint)
    : new URL(configuredAccessContextEndpoint);

  if (!["http:", "https:"].includes(accessContextEndpoint.protocol)) {
    throw new Error(
      "AI_RUNTIME_ACCESS_CONTEXT_ENDPOINT must be an absolute HTTP or HTTPS URL.",
    );
  }

  const token = optional("AI_RUNTIME_TOKEN");
  const accessContext = optional("AI_RUNTIME_ACCESS_CONTEXT");

  return {
    endpoint,
    ...(token === undefined ? {} : { token }),
    ...(accessContext === undefined ? {} : { accessContext }),
    accessContextHeader:
      optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER") ?? "X-Access-Context",
    accessContextEndpoint,
    openAiModel: required("OPENAI_MODEL"),
  };
}

export function isSmokeMode(): boolean {
  return process.env.AI_DEMO_SMOKE === "1";
}
