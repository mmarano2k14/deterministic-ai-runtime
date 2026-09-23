import {
  AiSdkClient,
  AiSdkMcpHttpTransport,
  AiSdkStaticCredentialProvider,
} from "@multiplexed/ai-sdk";

type DemoConfiguration = Readonly<{
  endpoint: URL;
  environmentRef: string;
  token?: string;
  accessContext?: string;
  accessContextHeader: string;
  openAiKeyConfigured: boolean;
  openAiModelConfigured: boolean;
}>;

function optional(name: string): string | undefined {
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

function loadConfiguration(): DemoConfiguration {
  const endpoint = new URL(required("AI_RUNTIME_ENDPOINT"));
  if (!["http:", "https:"].includes(endpoint.protocol)) {
    throw new Error("AI_RUNTIME_ENDPOINT must be an absolute HTTP or HTTPS URL.");
  }

  const token = optional("AI_RUNTIME_TOKEN");
  const accessContext = optional("AI_RUNTIME_ACCESS_CONTEXT");

  return {
    endpoint,
    environmentRef: required("AI_RUNTIME_TYPESCRIPT_ENVIRONMENT_REF"),
    ...(token === undefined ? {} : { token }),
    ...(accessContext === undefined ? {} : { accessContext }),
    accessContextHeader:
      optional("AI_RUNTIME_ACCESS_CONTEXT_HEADER") ?? "X-Access-Context",
    openAiKeyConfigured: optional("OPENAI_API_KEY") !== undefined,
    openAiModelConfigured: optional("OPENAI_MODEL") !== undefined,
  };
}

const config = loadConfiguration();

const transportOptions = {
  ...(config.token === undefined
    ? {}
    : {
        credentialProvider: new AiSdkStaticCredentialProvider({
          scheme: "Bearer",
          value: config.token,
        }),
      }),
  ...(config.accessContext === undefined
    ? {}
    : {
        additionalHeaders: {
          [config.accessContextHeader]: config.accessContext,
        },
      }),
};

const client = new AiSdkClient(
  new AiSdkMcpHttpTransport(config.endpoint, transportOptions),
);

void client;

console.log("Interactive Agent SDK Demo");
console.log("SDK: TypeScript");
console.log(`Runtime endpoint: ${config.endpoint.toString()}`);
console.log(`Environment ref: ${config.environmentRef}`);
console.log(`Bearer token configured: ${config.token !== undefined}`);
console.log(`Access context configured: ${config.accessContext !== undefined}`);
console.log(`OpenAI key configured: ${config.openAiKeyConfigured}`);
console.log(`OpenAI model configured: ${config.openAiModelConfigured}`);
console.log();
console.log("External SDK consumer initialized.");
console.log("No runtime request is sent by the scaffold increment.");
