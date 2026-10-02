import type { ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "./endpoints";
import { queryKeys, useReviewFlowMutation } from "./queries";

const flowId = "11111111-1111-4111-8111-111111111111";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("server projection invalidation", () => {
  it("sends uploads as multipart without overriding the browser boundary or local guard", async () => {
    const photo = new File(["photo"], "speaker.jpg", { type: "image/jpeg" });
    const fetchMock = vi.fn().mockResolvedValue(
      new Response(JSON.stringify({ reply: "Received." }), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      })
    );
    vi.stubGlobal("fetch", fetchMock);

    await api.continueIntake({
      flowId,
      message: "Use this photo.",
      files: [photo]
    });

    const [url, options] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/intake/attachments");
    expect(options.headers).toEqual({ "X-AI-Harness-Request": "1" });
    const form = options.body as FormData;
    expect(form).toBeInstanceOf(FormData);
    expect(form.get("flowId")).toBe(flowId);
    expect(form.get("message")).toBe("Use this photo.");
    expect((form.get("files") as File).name).toBe(photo.name);
  });

  it("invalidates bootstrap, lists, detail, review, settings, and catalog after a review action", async () => {
    vi.spyOn(api, "reviewFlow").mockResolvedValue({
      flowId,
      gateId: "22222222-2222-4222-8222-222222222222",
      intent: "Accept",
      decision: "Accepted",
      status: "Queued",
      iteration: 1,
      publicationStepId: "33333333-3333-4333-8333-333333333333",
      linkedFlowId: null,
      publicationStatus: "Queued",
      message: "Acceptance recorded."
    });
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false }
      }
    });
    const keys = [
      queryKeys.bootstrap,
      queryKeys.flows,
      queryKeys.flow(flowId),
      queryKeys.reviewResult(flowId),
      queryKeys.settings,
      queryKeys.agentCatalog
    ] as const;
    for (const key of keys) queryClient.setQueryData(key, {});
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(() => useReviewFlowMutation(), { wrapper });

    await act(async () => {
      await result.current.mutateAsync({
        flowId,
        body: {
          gateId: "22222222-2222-4222-8222-222222222222",
          intent: "Accept",
          refinement: null
        }
      });
    });

    for (const key of keys) {
      expect(queryClient.getQueryState(key)?.isInvalidated).toBe(true);
    }
  });
});
