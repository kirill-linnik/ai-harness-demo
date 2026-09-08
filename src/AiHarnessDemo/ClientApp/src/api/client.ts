// Typed fetch wrapper. Mirrors the original wwwroot/app.js `request()` helper:
// - Always sends JSON content type.
// - Always sends the LocalRequestGuard header (see Api/LocalRequestGuard.cs) so that
//   state-changing requests pass the same-origin preflight check.
// - Throws an Error using the ProblemDetails `detail`/`title` on non-2xx responses.
// - Returns null for 204 No Content.

export class ApiError extends Error {
  public readonly extensions: Readonly<Record<string, unknown>>;

  constructor(
    message: string,
    public readonly status: number,
    public readonly payload: unknown
  ) {
    super(message);
    this.name = "ApiError";
    this.extensions = extractProblemExtensions(payload);
  }
}

function extractProblemExtensions(payload: unknown): Readonly<Record<string, unknown>> {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return {};
  const standardProblemFields = new Set([
    "type",
    "title",
    "status",
    "detail",
    "instance",
    "message"
  ]);
  return Object.fromEntries(
    Object.entries(payload).filter(([key]) => !standardProblemFields.has(key))
  );
}

export interface RequestOptions {
  method?: string;
  body?: unknown;
}

export async function request<T>(url: string, options: RequestOptions = {}): Promise<T> {
  const response = await fetch(url, {
    method: options.method ?? "GET",
    headers: {
      "Content-Type": "application/json",
      "X-AI-Harness-Request": "1"
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body)
  });

  if (!response.ok) {
    const responseText = await response.text();
    let message = responseText;
    let payload: unknown = responseText;
    try {
      payload = JSON.parse(responseText) as unknown;
      const problem = payload as { detail?: string; title?: string; message?: string };
      message = problem.detail || problem.title || responseText;
      message = problem.message || message;
    } catch {
      // Plain-text error responses are surfaced as-is.
    }
    throw new ApiError(
      message || `Request failed with ${response.status}.`,
      response.status,
      payload
    );
  }

  if (response.status === 204) {
    return null as T;
  }
  return (await response.json()) as T;
}
