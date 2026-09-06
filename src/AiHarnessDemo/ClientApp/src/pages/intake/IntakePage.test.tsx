import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import { queryKeys } from "../../api/queries";
import type { BootstrapDto, FlowDetailDto, IntakeResponse } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { IntakePage } from "./IntakePage";

const bootstrap: BootstrapDto = {
  settings: {
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo repository",
    outcome: "Commit",
    maxHandoffRetries: 2,
    modelSelectionStrategy: "MaximumQuality",
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
  modelCatalog: {
    ready: true,
    catalogVersion: "acp-test",
    candidateCount: 4,
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
    workspaceRoot: "E:\\projects\\demo\\.workspaces",
    outcomeVerificationEnabled: true,
    outcomeVerificationMaxRounds: 3
  },
  factoryEnabled: true,
  factoryDisabledReason: ""
};

const flowId = "11111111-1111-1111-1111-111111111111";
const timestamp = "2026-09-02T12:00:00Z";

function confirmationFlow(status: FlowDetailDto["status"] = "Intake"): FlowDetailDto {
  return {
    id: flowId,
    title: "Refresh the public site",
    originalRequest: "Give the public site a fresh design.",
    consolidatedRequest: "Outcome: Refresh the public site with an interactive design.",
    status,
    iteration: 1,
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo repository",
    outcome: "Commit",
    modelSelectionStrategy: "MaximumQuality",
    workspacePath: "E:\\projects\\demo\\.workspaces\\flow",
    branchName: "ai-harness\\refresh-site",
    outcomeUrl: "",
    outcomeLabel: "",
    failureReason: "",
    createdAt: timestamp,
    updatedAt: timestamp,
    completedAt: null,
    steps: [],
    messages: [
      {
        id: "22222222-2222-2222-2222-222222222222",
        role: "Customer",
        content: "Give the public site a fresh design.",
        isQuestion: false,
        createdAt: timestamp
      },
      {
        id: "33333333-3333-3333-3333-333333333333",
        role: "AccountManager",
        content:
          "Do I understand correctly that you want a fresh interactive design for the public site? If yes, I'll ask the team to implement it.",
        isQuestion: true,
        createdAt: timestamp
      }
    ],
    events: [
      {
        id: "44444444-4444-4444-4444-444444444444",
        flowStepId: null,
        type: "intake.confirmation_requested",
        message: "Account Manager presented its understanding.",
        createdAt: timestamp
      }
    ],
    gateRecords: [],
    outcomeVerification: {
      status: "NotStarted",
      legacyUnverified: false,
      currentRound: 0,
      maxRounds: 3,
      planHashPrefix: "",
      candidateFingerprintPrefix: "",
      candidateFingerprint: "",
      releaseGateId: null,
      criteria: [],
      evidence: [],
      latestResults: [],
      failedCriterionIds: [],
      pendingOwnerRoles: [],
      stale: false,
      previewRequired: false,
      releaseReady: false,
      verifiedAt: null,
      humanResolutionGate: null
    }
  };
}

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

  it("sends a confirmed brief directly to Team Lead without a second button", async () => {
    const pendingFlow = confirmationFlow();
    const queuedFlow = {
      ...pendingFlow,
      status: "Queued" as const,
      messages: [
        ...pendingFlow.messages,
        {
          id: "55555555-5555-5555-5555-555555555555",
          role: "Customer" as const,
          content: "Yes.",
          isQuestion: false,
          createdAt: timestamp
        },
        {
          id: "66666666-6666-6666-6666-666666666666",
          role: "AccountManager" as const,
          content: "Thanks - I'll ask the team to implement it now.",
          isQuestion: false,
          createdAt: timestamp
        }
      ],
      events: [
        {
          id: "77777777-7777-7777-7777-777777777777",
          flowStepId: null,
          type: "intake.confirmed",
          message: "Customer explicitly confirmed the brief.",
          createdAt: timestamp
        },
        ...pendingFlow.events
      ]
    } satisfies FlowDetailDto;
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(pendingFlow);
    const continueIntake = vi.spyOn(api, "continueIntake").mockResolvedValue({
      flow: queuedFlow,
      reply: "Thanks - I'll ask the team to implement it now.",
      readyToStart: true,
      shouldSpeak: false
    });
    const startFlow = vi.spyOn(api, "startFlow").mockResolvedValue(queuedFlow);

    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), pendingFlow);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[`/intake/${flowId}`]}>
            <Routes>
              <Route path="/intake/:id" element={<IntakePage />} />
              <Route path="/factory/:id" element={<div>Team Lead started</div>} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(screen.getByText("Confirm brief")).toBeInTheDocument();
    const composer = screen.getByPlaceholderText("Reply yes, or tell me what to change...");
    fireEvent.change(composer, { target: { value: "Yes." } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(screen.getByText("Team Lead started")).toBeInTheDocument());
    expect(continueIntake).toHaveBeenCalledWith({ flowId, message: "Yes." });
    expect(startFlow).not.toHaveBeenCalled();
  });
});
