// Typed fetch wrapper. Mirrors the original wwwroot/app.js `request()` helper:
// - Always sends JSON content type.
// - Always sends the LocalRequestGuard header (see Api/LocalRequestGuard.cs) so that
//   state-changing requests pass the same-origin preflight check.
// - Throws an Error using the ProblemDetails `detail`/`title` on non-2xx responses.
// - Returns null for 204 No Content.

export class ApiError extends Error {}

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
    try {
      const problem = JSON.parse(responseText) as { detail?: string; title?: string };
      message = problem.detail || problem.title || responseText;
    } catch {
      // Plain-text error responses are surfaced as-is.
    }
    throw new ApiError(message || `Request failed with ${response.status}.`);
  }

  if (response.status === 204) {
    return null as T;
  }
  return (await response.json()) as T;
}
