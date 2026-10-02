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
    currentFileValid: true,
    hasEffectiveDefinition: true,
    sourcePath: "E:\\projects\\demo\\WORKFLOW.md",
    effectiveLoadedAt: timestamp,
    effectiveRevision: "workflow-test",
    currentFileError: null,
    loadedAt: timestamp,
    lastError: null,
    maxConcurrentAgents: 1,
    maxAttempts: 1,
    workspaceRoot: "E:\\projects\\demo\\data\\worktrees"
  },
  agentCatalog: {
    ready: true,
    hasEffectiveCatalog: true,
    effectiveRevision: "catalog-test",
    loadedAt: timestamp,
    lastError: null,
    validDefinitionCount: 0,
    invalidDefinitionCount: 0
  },
  admission: {
    ready: true,
    failures: [],
    checkedAt: timestamp
  },
  factoryEnabled: true,
  factoryDisabledReason: "",
  githubCliAvailable: true,
  githubCliAuthenticated: true
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("SettingsPage model strategy", () => {
  it("states that the complete editable knowledge is supplied to agents", () => {
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

    expect(
      screen.getByText(
        "An evidence-grounded, editable synthesis supplied to every agent before task-specific work."
      )
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Copilot reconciles documentation with manifests, source, and tests. This text is passed in full; edit it to correct assumptions or add domain context."
      )
    ).toBeInTheDocument();
  });

  it("offers only persisted Delivery outcomes", () => {
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

    expect(screen.getByRole("button", { name: "Commit" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Pull request" })).toBeInTheDocument();
    expect(screen.queryByText("None")).not.toBeInTheDocument();
  });

  it("shows a pull-request prerequisite warning when the Studio process cannot find gh", () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, {
      ...bootstrap,
      githubCliAvailable: false,
      githubCliAuthenticated: false
    });

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(
      screen.getByText(/Pull-request Delivery cannot be confirmed or accepted/)
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Commit" }));
    expect(
      screen.queryByText(/Pull-request Delivery cannot be confirmed or accepted/)
    ).not.toBeInTheDocument();
  });

  it("distinguishes an installed CLI from missing Studio-process authentication", () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, {
      ...bootstrap,
      githubCliAuthenticated: false
    });

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(
      screen.getByText(/this Studio process has no GitHub token/)
    ).toBeInTheDocument();
    expect(
      screen.getByText(/before confirming or accepting pull-request Delivery/)
    ).toBeInTheDocument();
  });

  it("does not claim authentication is missing when an older Studio process has not reported it", () => {
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, {
      ...bootstrap,
      githubCliAuthenticated: undefined
    });

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(
      screen.getByText(/Authentication status is unavailable from this Studio process/)
    ).toBeInTheDocument();
    expect(
      screen.queryByText(/this Studio process has no GitHub token/)
    ).not.toBeInTheDocument();
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

  it("shows catalog readiness, exact diagnostics, and disables required core toggles", async () => {
    const reload = vi.spyOn(api, "reloadAgentCatalog").mockResolvedValue({
      status: {
        ready: false,
        hasEffectiveCatalog: true,
        effectiveRevision: "ABC",
        loadedAt: timestamp,
        lastError: "Required agent definition is malformed.",
        validDefinitionCount: 1,
        invalidDefinitionCount: 1
      },
      agents: []
    });
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, {
      ...bootstrap,
      agents: [
        {
          id: "account-manager",
          name: "Account Manager",
          description: "Intake",
          role: "account-manager",
          accent: "amber",
          enabled: true,
          sortOrder: 10,
          definitionStatus: "Valid",
          validationError: "",
          required: true,
          switchable: false,
          definitionHash: "A",
          loadedAt: timestamp
        },
        {
          id: "team-lead",
          name: "Team Lead",
          description: "Plans the work",
          role: "team-lead",
          accent: "blue",
          enabled: true,
          sortOrder: 20,
          definitionStatus: "Valid",
          validationError: "",
          required: true,
          switchable: false,
          definitionHash: "B",
          loadedAt: timestamp
        },
        {
          id: "pre-mortem-sceptic",
          name: "Pre-mortem Sceptic",
          description: "Challenges planned work",
          role: "pre-mortem-sceptic",
          accent: "red",
          enabled: false,
          sortOrder: 30,
          definitionStatus: "Valid",
          validationError: "",
          required: true,
          switchable: true,
          definitionHash: "C",
          loadedAt: timestamp
        },
        {
          id: "optional",
          name: "Optional",
          description: "",
          role: "optional",
          accent: "violet",
          enabled: true,
          sortOrder: 500,
          definitionStatus: "Invalid",
          validationError: "description is required",
          required: false,
          switchable: true,
          definitionHash: "",
          loadedAt: timestamp
        }
      ],
      agentCatalog: {
        ready: false,
        hasEffectiveCatalog: true,
        effectiveRevision: "ABC",
        loadedAt: timestamp,
        lastError: "Required agent definition is malformed.",
        validDefinitionCount: 3,
        invalidDefinitionCount: 1
      },
      admission: {
        ready: false,
        failures: ["Agent catalog is not ready."],
        checkedAt: timestamp
      }
    } satisfies BootstrapDto);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter>
            <SettingsPage />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(screen.getAllByText("Required")).toHaveLength(2);
    expect(screen.getByTitle("Account Manager is required").querySelector("input")).toBeDisabled();
    expect(screen.getByTitle("Team Lead is required").querySelector("input")).toBeDisabled();
    expect(screen.getByText("Required definition · Optional execution")).toBeInTheDocument();
    expect(screen.getByLabelText("Enable Pre-mortem Sceptic")).toBeEnabled();
    expect(
      screen.getByLabelText("Optional has an invalid definition and cannot be enabled")
    ).toBeDisabled();
    expect(
      screen.getByLabelText("Optional has an invalid definition and cannot be enabled")
    ).not.toBeChecked();
    expect(screen.getByRole("heading", { name: "Symphony workflow contract" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Available agents" })).toBeInTheDocument();
    expect(screen.getByText("Effective last-known-good")).toBeInTheDocument();
    expect(screen.getByText("Effective catalog revision")).toBeInTheDocument();
    expect(screen.getByText("Concurrent flows")).toBeInTheDocument();
    expect(screen.queryByText("Concurrent agents")).not.toBeInTheDocument();
    expect(screen.getByText("description is required")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Reload catalog" }));
    await waitFor(() => expect(reload).toHaveBeenCalledOnce());
  });
});
