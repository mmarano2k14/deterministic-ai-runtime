import { HttpMethod, ProxyError, ProxyRequest, ProxyResponse } from "@/lib/infrastructure/transport/http/HttpClientType";
import { NextResponse } from "next/server";


function safeJoin(baseUrl: string, path: string): string {
  const b = baseUrl.replace(/\/+$/, "");
  const p = path.startsWith("/") ? path : `/${path}`;
  return `${b}${p}`;
}

function isHttpMethod(x: unknown): x is HttpMethod {
  return x === "GET" || x === "POST" || x === "PUT" || x === "PATCH" || x === "DELETE";
}

export async function POST(req: Request) {
  const data = (await req.json()) as ProxyRequest;

  if (!data?.baseUrl || !data?.path || !data?.method || !isHttpMethod(data.method)) {
    return NextResponse.json<ProxyError>(
      { error: "Missing/invalid baseUrl/path/method" },
      { status: 400 }
    );
  }

  const url = safeJoin(data.baseUrl, data.path);

  const incomingCookie = req.headers.get("cookie");

  const headers: Record<string, string> = {
    "content-type": "application/json",
    ...(data.headers ?? {}),
  };

  if (incomingCookie) {
    headers["cookie"] = incomingCookie;
  }

  const method = data.method.toUpperCase() as HttpMethod;

  const init: RequestInit = {
    method,
    headers,
    body: ["POST", "PUT", "PATCH"].includes(method)
      ? JSON.stringify(data.body ?? {})
      : undefined,
    cache: "no-store",
  };

  try {
    const res = await fetch(url, init);
    const text = await res.text();

    const outHeaders: Record<string, string> = {};
    for (const h of ["x-access-context", "content-type"]) {
      const v = res.headers.get(h);
      if (v) outHeaders[h] = v;
    }

    const payload: ProxyResponse = {
      ok: res.ok,
      status: res.status,
      statusText: res.statusText,
      url,
      headers: outHeaders,
      body: text,
    };

    const nextRes = NextResponse.json<ProxyResponse>(payload, { status: 200 });

    const setCookie = res.headers.get("set-cookie");
    if (setCookie) {
      nextRes.headers.set("set-cookie", setCookie);
    }

    return nextRes;
  } catch (e: unknown) {
    const msg = e instanceof Error ? e.message : String(e);
    return NextResponse.json<ProxyError>(
      { error: "Network failure", details: msg },
      { status: 502 }
    );
  }
}