import axios, { AxiosInstance } from "axios";
import {
  CallResult,
  HeaderKeyRotation,
  HeaderOverride,
  ProxyError,
  ProxyRequest,
  ProxyResponse,
  RequestSpec,
} from "./HttpClientType";

export class HttpClient {
  private readonly ax: AxiosInstance;

  public constructor() {
    this.ax = axios.create({
      baseURL: "",
      timeout: 30_000,
      withCredentials: true,
      headers: {
        "content-type": "application/json",
      },
    });
  }

  public buildProxyRequest(
    baseUrl: string,
    spec: RequestSpec,
    headers: Record<string, string>
  ): ProxyRequest {
    return {
      baseUrl,
      path: spec.path,
      method: spec.method,
      headers,
      body: spec.body,
    };
  }

  public async sendViaProxy(
    req: ProxyRequest,
    signal?: AbortSignal
  ): Promise<ProxyResponse | ProxyError> {
    try {
      const response = await this.ax.post<ProxyResponse | ProxyError>(
        "/api/proxy",
        req,
        { signal }
      );

      return response.data;
    } catch (e: unknown) {
      const msg = axios.isAxiosError(e)
        ? e.message
        : e instanceof Error
          ? e.message
          : String(e);

      const error: ProxyError = {
        error: signal?.aborted ? "Aborted" : "Network failure",
        details: msg,
      };

      return error;
    }
  }

  // Higher-level helper for existing runtime usage
  public async call(
    baseUrl: string,
    spec: RequestSpec,
    options?: {
      signal?: AbortSignal;
      headers?: Record<string, string>;
      headerOverride?: HeaderOverride;
    }
  ): Promise<CallResult> {
    const headers = this.mergeHeaders(
      spec.headers,
      options?.headers,
      options?.headerOverride
    );

    const request = this.buildProxyRequest(baseUrl, spec, headers);
    const result = await this.sendViaProxy(request, options?.signal);

    if (this.isProxyError(result)) {
      return {
        kind: "error",
        error: result,
      };
    }

    if (!result.ok) {
      return {
        kind: "error",
        error: {
          error: result.statusText || "Proxy request failed",
          details: result.body,
        },
        response: result,
      };
    }

    return {
      kind: "ok",
      response: result,
      rotation: this.extractRotation(result),
    };
  }

  private mergeHeaders(
    specHeaders?: Record<string, string>,
    extraHeaders?: Record<string, string>,
    headerOverride?: HeaderOverride
  ): Record<string, string> {
    const headers: Record<string, string> = {
      ...(specHeaders ?? {}),
      ...(extraHeaders ?? {}),
    };

    if (headerOverride?.contextKeyOverride) {
      headers["x-access-context"] = headerOverride.contextKeyOverride;
    }

    return headers;
  }

  private extractRotation(response: ProxyResponse): HeaderKeyRotation | undefined {
    const rotatedTo = response.headers["x-access-context"];

    if (!rotatedTo) {
      return undefined;
    }

    return {
      from: "",
      to: rotatedTo,
    };
  }

  private isProxyError(value: ProxyResponse | ProxyError): value is ProxyError {
    return "error" in value;
  }
}