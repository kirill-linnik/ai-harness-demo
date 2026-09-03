import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import type { PreviewDto } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { PreviewPage } from "./PreviewPage";

const flowId = "af12aca2-0275-4aea-8684-f21375dc3b15";
const preview: PreviewDto = {
  flowId,
  title: "Refresh Devclub community site design",
  request: "Modernize both sites.",
  repositoryName: "devclub",
  iteration: 1,
  status: "WaitingForFeedback",
  outcomeLabel: "Pull request candidate",
  artifacts: [
    {
      id: "eu",
      label: "devclub.eu",
      url: `/api/previews/${flowId}/artifacts/eu/index.html`
    },
    {
      id: "ee",
      label: "devclub.ee",
      url: `/api/previews/${flowId}/artifacts/ee/index.html`
    }
  ],
  deliveredBy: [],
  generatedAt: "2026-09-03T12:00:00Z"
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function renderPage() {
  const queryClient = new QueryClient();
  vi.spyOn(api, "preview").mockResolvedValue(preview);
  render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MemoryRouter initialEntries={[`/preview/${flowId}`]}>
          <Routes>
            <Route path="/preview/:id" element={<PreviewPage />} />
            <Route path="/factory/:id" element={<div>Execution details</div>} />
          </Routes>
        </MemoryRouter>
      </ToastProvider>
    </QueryClientProvider>
  );
}

describe("PreviewPage", () => {
  it("embeds the delivered artifact and lets the customer approve it", async () => {
    const decide = vi.spyOn(api, "decideFlow").mockResolvedValue({} as never);
    renderPage();

    expect(
      await screen.findByTitle("devclub.eu interactive customer preview")
    ).toHaveAttribute("src", `/api/previews/${flowId}/artifacts/eu/index.html`);
    fireEvent.click(screen.getByRole("button", { name: "Approve and publish" }));

    await waitFor(() => expect(decide).toHaveBeenCalledWith(flowId, true));
    expect(await screen.findByText("Execution details")).toBeInTheDocument();
  });

  it("requires feedback and submits it before requesting changes", async () => {
    const feedback = vi.spyOn(api, "sendFeedback").mockResolvedValue({} as never);
    const decide = vi.spyOn(api, "decideFlow").mockResolvedValue({} as never);
    renderPage();

    await screen.findByTitle("devclub.eu interactive customer preview");
    fireEvent.change(screen.getByLabelText("What should change?"), {
      target: { value: "Increase body text contrast." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Request changes" }));

    await waitFor(() =>
      expect(feedback).toHaveBeenCalledWith(flowId, {
        message: "Increase body text contrast."
      })
    );
    expect(decide).toHaveBeenCalledWith(flowId, false);
  });
});
