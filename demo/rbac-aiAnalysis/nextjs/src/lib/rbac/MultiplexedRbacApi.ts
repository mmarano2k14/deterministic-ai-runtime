
import { ILogSink, IBusyListener } from "../infrastructure/logs/inMemoryLogType";
import { HttpClient } from "../infrastructure/transport/http/HttpClient";
import { CallResult, HeaderOverride, ProxyError, ProxyResponse, RequestSpec } from "../infrastructure/transport/http/HttpClientType";
import { ClientSession, Rotation } from "./ClientSession";

export class MultiplexedRbacApi {
  private readonly http: HttpClient;
  private readonly session: ClientSession;
  private _baseUrl: string;

  private _log: ILogSink;
  
  private _isBusy = false;
  private _busyListener?: IBusyListener;

  public constructor(baseUrl: string, http: HttpClient, session: ClientSession, logSink: ILogSink) {
    this._baseUrl = baseUrl;
    this.http = http;
    this.session = session;
    this._log = logSink;
  }

  static createInstanceRef(baseUrl: string, logSink: ILogSink): MultiplexedRbacApi {
    return new MultiplexedRbacApi(baseUrl, new HttpClient(), new ClientSession(), logSink);
  }

  // ---- busy ----
  public get isBusy(): boolean {
    return this._isBusy;
  }

  public setBusyListener(listener: IBusyListener): void {
    this._busyListener = listener;
  }

  private setBusy(value: boolean): void {
    this._isBusy = value;
    this._busyListener?.onBusyChange(value);
  }

  public get baseUrl(): string {
    return this._baseUrl;
  }

  public set baseUrl(v: string) {
    this._baseUrl = v;
  }

  get contextKey(): string {
    return this.session.contextKey;
  }
  set contextKey(v: string) {
    this.session.contextKey = v;
  }

  get demoUserId(): string {
    return this.session.demoUserId
  }
  set demoUserId(v: string) {
    this.session.demoUserId = v;
  }

  set maxInFlight(v: string) {
    this.session.maxInFlight = v;
  }

  set rotationOverlapMs(v: string) {
    this.session.rotationOverlapMs = v;
  }

  public async call(spec: RequestSpec, options?: HeaderOverride,  signal?: AbortSignal): Promise<CallResult> {
    const id = this.uid();
    const t = new Date().toISOString();

    try {
        this.setBusy(true);
        
        const headers = this.session.buildHeaders(options);

        this._log.push({
          kind:"http",
          id,
          t,
          name: spec.name,
          method: spec.method,
          path: spec.path,
          baseUrl: this._baseUrl,
          requestHeaders : headers,
          requestBody: spec.body,
        });


        const proxyReq = this.http.buildProxyRequest(this._baseUrl, spec, headers);

        const data = await this.http.sendViaProxy(proxyReq, signal);

        if ("error" in data) {
        this._log.patch(id, {
            error: `${data.error}${data.details ? `: ${data.details}` : ""}`,
        });
        return { kind: "error", error: data as ProxyError };
        }

        const rotation: Rotation | undefined = this.session.applyRotation((data as ProxyResponse).headers);

        this._log.patch(id, {
            url: data.url,
            ok: data.ok,
            status: data.status,
            statusText: data.statusText,
            //responseHeaders: data.headers,
            responseBody: data.body,
            rotation,
        });

        return { kind: "ok", response: data as ProxyResponse, rotation };

    } catch (e: unknown) {
        const msg = e instanceof Error ? e.message : String(e);
        const err: ProxyError = { error: "Network failure", details: msg };
        this._log.patch(id, { error: `${err.error}${err.details ? `: ${err.details}` : ""}` });
        return { kind: "error", error: err };
    } finally {
        this.setBusy(false);
    }
  }

  // ---- endpoints ----

  public async login(username: string, signal?: AbortSignal): Promise<CallResult> {
    const res = await this.call(
      {
        name: "LOGIN",
        method: "POST",
        path: "/demo/login",
        body: { username }
      },
      undefined,
      signal
    );

    if (res.kind === "ok") {
      try {
        const parsed = JSON.parse(res.response.body) as { accessContext?: string };

        if (parsed?.accessContext) {
          this.session.contextKey = parsed.accessContext;
          this.session.demoUserId = username;
        }
      } catch {
        // ignore
      }
    }

    return res;
  }

   public async bootstrap(signal?: AbortSignal): Promise<CallResult> {
    const res = await this.call(
      {
        name: "LOGIN",
        method: "GET",
        path: "/demo/bootstrap",
        body: {  }
      },
      undefined,
      signal
    );

    if (res.kind === "ok") {
      try {
        const parsed = JSON.parse(res.response.body) as { contextKey?: string, userId:string };

        if (parsed?.contextKey) {
          this.session.contextKey = parsed.contextKey;
          this.session.demoUserId = parsed.userId;
        }
      } catch {
        // ignore
      }
    }

    return res;
  }

  public async getContextKey(signal?: AbortSignal): Promise<CallResult> {
    const res = await this.call(
      { name: "Get ContextKey", method: "GET", path: "/demo/context" },
      undefined,
      signal
    );

    if (res.kind === "ok") {
      try {
        const parsed = JSON.parse(res.response.body) as { accessContext?: string };
        if (parsed?.accessContext) this.session.contextKey = parsed.accessContext;
      } catch {
        // ignore
      }
    }

    return res;
  }

  public readInvoice(invoiceId: string, signal?: AbortSignal): Promise<CallResult> {
    return this.call(
      { name: "READ invoice", method: "GET", path: `/billing/${encodeURIComponent(invoiceId)}` },
      undefined,
      signal
    );
  }

  public refundInvoice(invoiceId: string, amount: number, signal?: AbortSignal): Promise<CallResult> {
    return this.call(
      {
        name: "REFUND invoice",
        method: "POST",
        path: `/billing/${encodeURIComponent(invoiceId)}/refund`,
        body: { amount },
      },
      undefined,
      signal
    );
  }

  private uid(): string {
    return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  }
}