export type HttpMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export type ProxyRequest = {
  baseUrl: string;
  path: string;
  method: HttpMethod;
  body?: unknown;
  headers?: Record<string, string>;
};

export type ProxyResponse = {
  ok: boolean;
  status: number;
  statusText: string;
  url: string;
  headers: Record<string, string>;
  body: string;
};

export type ProxyError = {
  error: string;
  details?: string;
};

export type TargetPreset = {
  label: string;
  baseUrl: string;
};

export type RequestSpec = {
  name: string;
  method: HttpMethod;
  path: string;
  body?: unknown;
  headers?: Record<string, string>;
};

export type HeaderKeyRotation = {
  from: string;
  to: string;
};

export type HeaderOverride = {
  contextKeyOverride?: string;
};

export type CallResult =
  | { kind: "ok"; response: ProxyResponse; rotation?: HeaderKeyRotation }
  | { kind: "error"; error: ProxyError; response?: ProxyResponse };