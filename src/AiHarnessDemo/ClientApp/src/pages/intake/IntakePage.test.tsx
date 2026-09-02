import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import { queryKeys } from "../../api/queries";
import type { BootstrapDto, IntakeResponse } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { IntakePage } from "./IntakePage";

const bootstrap: BootstrapDto = {
  settings: {
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo repository",
    outcome: "Commit",
    updatedAt: "2026-09-02T12:00:00Z"
  },
  agents: [],
  flows: [],
  stats: {
    totalFlows: 0,
    activeFlows: 0,
    completedFlows: 0,
    learnedRefinements: 0,
    totalAgentMinutes: 0
  },
  copilotCliAvailable: true,
  copilotCli: {
    ready: true,
    command: "copilot",
    resolvedPath: "copilot",
    version: "1.0.0",
    detail: "Ready",
    checkedAt: "2026-09-02T12:00:00Z"
  },
  workflow: {
    ready: true,
    sourcePath: "E:\\projects\\demo\\WORKFLOW.md",
    loadedAt: "2026-09-02T12:00:00Z",
    lastError: null,
    maxConcurrentAgents: 1,
    maxAttempts: 1,
    workspaceRoot: "E:\\projects\\demo\\.workspaces"
  },
  factoryEnabled: true,
  factoryDisabledReason: ""
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("IntakePage", () => {
  it("shows the customer message while the account manager is thinking", async () => {
    let rejectIntake!: (reason: Error) => void;
    const intakeRequest = new Promise<IntakeResponse>((_resolve, reject) => {
      rejectIntake = reject;
    });
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    const continueIntake = vi.spyOn(api, "continueIntake").mockReturnValue(intakeRequest);

    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={["/intake"]}>
            <Routes>
              <Route path="/intake" element={<IntakePage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    const request = "Build a clickable redesign.";
    const composer = screen.getByRole("textbox", { name: "Customer request" });
    fireEvent.change(composer, { target: { value: request } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect(screen.getByText(request)).toBeInTheDocument();
    expect(composer).toHaveValue("");
    expect(screen.getByRole("button", { name: "Thinking..." })).toBeDisabled();
    await waitFor(() =>
      expect(continueIntake).toHaveBeenCalledWith({ flowId: null, message: request })
    );

    await act(async () => {
      rejectIntake(new Error("Account Manager unavailable."));
    });

    await waitFor(() => expect(screen.getByRole("button", { name: "Send" })).toBeEnabled());
    expect(composer).toHaveValue(request);
  });
});
