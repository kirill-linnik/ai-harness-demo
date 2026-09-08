import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, request } from "./client";

function jsonResponse(body: unknown, init: ResponseInit = {}) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
    ...init
  });
}

describe("request", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("always sends the JSON content type and local-request guard header", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => jsonResponse({ ok: true }));
    vi.stubGlobal("fetch", fetchMock);

    await request("/api/bootstrap");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [, options] = fetchMock.mock.calls[0];
    expect(options?.method).toBe("GET");
    expect(options?.headers).toMatchObject({
      "Content-Type": "application/json",
      "X-AI-Harness-Request": "1"
    });
  });

  it("serializes the body and returns the parsed JSON payload", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => jsonResponse({ enabled: true }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await request<{ enabled: boolean }>("/api/agents/account-manager", {
      method: "PUT",
      body: { enabled: true }
    });

    expect(result).toEqual({ enabled: true });
    const [, options] = fetchMock.mock.calls[0];
    expect(options?.body).toBe(JSON.stringify({ enabled: true }));
  });

  it("returns null for a 204 No Content response", async () => {
    const fetchMock = vi.fn(async () => new Response(null, { status: 204 }));
    vi.stubGlobal("fetch", fetchMock);

    const result = await request("/api/feedback/1");
    expect(result).toBeNull();
  });

  it("throws an ApiError using the ProblemDetails detail on failure", async () => {
    const fetchMock = vi.fn(
      async () => jsonResponse({ detail: "Repository path is required.", title: "Bad Request" }, { status: 400 })
    );
    vi.stubGlobal("fetch", fetchMock);

    await expect(request("/api/settings", { method: "PUT", body: {} })).rejects.toThrow(
      "Repository path is required."
    );
  });

  it("exposes top-level ProblemDetails extensions", async () => {
    const fetchMock = vi.fn(async () =>
      jsonResponse(
        {
          title: "The request conflicts with the current flow state.",
          status: 409,
          detail: "The flow was saved.",
          flowId: "11111111-1111-1111-1111-111111111111",
          flowStatus: "Intake",
          retryMessage: "Open the saved flow and retry."
        },
        { status: 409 }
      )
    );
    vi.stubGlobal("fetch", fetchMock);

    const error = await request("/api/intake", {
      method: "POST",
      body: { message: "sensitive request" }
    }).catch(value => value);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).extensions).toEqual({
      flowId: "11111111-1111-1111-1111-111111111111",
      flowStatus: "Intake",
      retryMessage: "Open the saved flow and retry."
    });
  });

  it("surfaces a plain-text error body", async () => {
    const fetchMock = vi.fn(
      async () => new Response("Internal failure", { status: 500, headers: { "Content-Type": "text/plain" } })
    );
    vi.stubGlobal("fetch", fetchMock);

    await expect(request("/api/settings")).rejects.toThrow(
      new ApiError("Internal failure", 500, "Internal failure")
    );
  });
});
