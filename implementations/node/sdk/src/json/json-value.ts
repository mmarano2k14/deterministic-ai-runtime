export type AiSdkJsonScalar = null | boolean | number | string;

export type AiSdkJsonValue =
  | AiSdkJsonScalar
  | readonly AiSdkJsonValue[]
  | { readonly [key: string]: AiSdkJsonValue };

export type AiSdkJsonObject = { readonly [key: string]: AiSdkJsonValue };

export function isAiSdkJsonObject(value: unknown): value is AiSdkJsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
