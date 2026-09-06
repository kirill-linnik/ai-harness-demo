import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import { queryKeys } from "../../api/queries";
import type { BootstrapDto } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { SettingsPage } from "./SettingsPage";

const timestamp = "2026-09-03T06:00:00Z";
const bootstrap: BootstrapDto = {
  settings: {
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo",
    outcome: "PullRequest",
    maxHandoffRetries: 2,
    modelSelectionStrategy: "MaximumQuality",
    updatedAt: timestamp
  },
  agents: [],
  flows: [],
  stats: { totalFlows: 0, activeFlows: 0, completedFlows: 0, learnedRefinements: 0, totalAgentMinutes: 0 },
  copilotCliAvailable: true,
  copilotCli: {
    ready: true,
    command: "copilot",
    resolvedPath: "copilot.exe",
    version: "1.0.0",
    detail: "Ready",
    checkedAt: timestamp
  },
  modelCatalog: {
    ready: true,
    catalogVersion: "acp-test",
    candidateCount: 6,
    detail: "ACP discovered 6 candidates.",
    checkedAt: timestamp
  },
  workflow: {
    ready: true,
    sourcePath: "E:\\projects\\demo\\WORKFLOW.md",
    loadedAt: timestamp,
    lastError: null,
    maxConcurrentAgents: 1,
    maxAttempts: 1,
    workspaceRoot: "E:\\projects\\demo\\data\\worktrees",
    outcomeVerificationEnabled: true,
    outcomeVerificationMaxRounds: 3
  },
  factoryEnabled: true,
  factoryDisabledReason: ""
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("SettingsPage model strategy", () => {
  it("shows the repository-owned outcome verification round policy read-only", () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(screen.getByText("3 QA rounds")).toBeInTheDocument();
    expect(
      screen.getByText((_, element) =>
        element?.tagName === "SMALL" &&
        Boolean(element.textContent?.includes("Outcome verification is repository policy"))
      )
    ).toHaveTextContent("WORKFLOW.md");
  });

  it("hydrates a persisted strategy after an asynchronous bootstrap load", async () => {
    vi.spyOn(api, "bootstrap").mockResolvedValue({
      ...bootstrap,
      settings: {
        ...bootstrap.settings,
        modelSelectionStrategy: "LowestCost"
      }
    });
    const queryClient = new QueryClient();

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(await screen.findByLabelText("Adaptive model strategy")).toHaveValue("LowestCost");
  });

  it("saves the accessible adaptive strategy selector", async () => {
    const save = vi.spyOn(api, "saveSettings").mockImplementation(async request => ({
      ...bootstrap.settings,
      ...request,
      updatedAt: timestamp
    }));
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    fireEvent.change(screen.getByLabelText("Adaptive model strategy"), {
      target: { value: "LowestCost" }
    });
    fireEvent.click(screen.getByRole("button", { name: "Save settings" }));

    await waitFor(() =>
      expect(save).toHaveBeenCalledWith(expect.objectContaining({ modelSelectionStrategy: "LowestCost" }))
    );
  });
});
