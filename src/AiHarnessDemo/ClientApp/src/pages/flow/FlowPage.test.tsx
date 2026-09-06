import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import { queryKeys } from "../../api/queries";
import type { BootstrapDto, FlowDetailDto, FlowStepDto } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { FlowPage } from "./FlowPage";

const flowId = "b3bbe5ab-95ce-446d-9606-c22151285c33";
const timestamp = "2026-09-02T20:13:47Z";

const bootstrap: BootstrapDto = {
  settings: {
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo repository",
    outcome: "PullRequest",
    maxHandoffRetries: 2,
    modelSelectionStrategy: "MaximumQuality",
    updatedAt: timestamp
  },
  agents: [],
  flows: [],
  stats: {
    totalFlows: 1,
    activeFlows: 0,
    completedFlows: 0,
    learnedRefinements: 0,
    totalAgentMinutes: 1
  },
  copilotCliAvailable: true,
  copilotCli: {
    ready: true,
    command: "copilot",
    resolvedPath: "copilot",
    version: "1.0.82",
    detail: "Ready",
    checkedAt: timestamp
  },
  modelCatalog: {
    ready: true,
    catalogVersion: "acp-test",
    candidateCount: 4,
    detail: "Ready",
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

function step(overrides: Partial<FlowStepDto> = {}): FlowStepDto {
  return {
    id: "11111111-1111-1111-1111-111111111111",
    iteration: 1,
    sequence: 40,
    agentId: "software-engineer",
    agentName: "Software Engineer",
    agentRole: "software-engineer",
    label: "Execute Software Engineer contract",
    model: "gpt-5.4",
    modelEffort: "high",
    modelReason: "Selected for implementation.",
    taskProfile: null,
    routing: null,
    status: "Failed",
    phase: "Stalled",
    attempt: 1,
    executionAttempts: 1,
    inputSummary: "Implement the approved change.",
    executionPrompt: "Implement the approved change.",
    copilotSessionId: "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
    outputSummary: "",
    pushbackReason: "",
    startedAt: timestamp,
    completedAt: timestamp,
    durationMilliseconds: 300_000,
    toolCalls: [],
    assignedCriterionIds: [],
    outcomeQaRound: null,
    ...overrides
  };
}

function failedFlow(): FlowDetailDto {
  return {
    id: flowId,
    title: "Refresh the public site",
    originalRequest: "Refresh the public site.",
    consolidatedRequest: "Refresh the public site.",
    status: "Failed",
    iteration: 1,
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo repository",
    outcome: "PullRequest",
    modelSelectionStrategy: "MaximumQuality",
    workspacePath: "E:\\projects\\demo\\data\\worktrees\\flow",
    branchName: "ai-harness\\refresh-site",
    outcomeUrl: "",
    outcomeLabel: "",
    failureReason: "Agent process produced no output for 300 seconds.",
    createdAt: timestamp,
    updatedAt: timestamp,
    completedAt: timestamp,
    steps: [step()],
    messages: [],
    events: [],
    gateRecords: [],
    outcomeVerification: {
      status: "LegacyUnverified",
      legacyUnverified: true,
      currentRound: 0,
      maxRounds: 0,
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

describe("FlowPage manual restart", () => {
  it("restarts a failed task and selects the scheduled retry", async () => {
    const failed = failedFlow();
    const restarted: FlowDetailDto = {
      ...failed,
      status: "Queued",
      failureReason: "",
      completedAt: null,
      steps: [
        ...failed.steps,
        step({
          id: "22222222-2222-2222-2222-222222222222",
          sequence: 50,
          label: "Manual restart of Software Engineer",
          status: "Pending",
          phase: "CanceledByReconciliation",
          attempt: 2,
          startedAt: null,
          completedAt: null
        })
      ]
    };
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(failed);
    const restartFlow = vi.spyOn(api, "restartFlow").mockResolvedValue(restarted);
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), failed);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[`/factory/${flowId}`]}>
            <Routes>
              <Route path="/factory/:id" element={<FlowPage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    fireEvent.click(await screen.findByRole("button", { name: "Recover failed task" }));

    await waitFor(() => expect(restartFlow).toHaveBeenCalledWith(flowId));
    await waitFor(() =>
      expect(screen.getByText("Manual restart of Software Engineer")).toBeInTheDocument()
    );
    expect(screen.queryByText("Flow stopped")).not.toBeInTheDocument();
  });

  it("requires a reason before resolving an exhausted outcome cycle", async () => {
    const flow: FlowDetailDto = {
      ...failedFlow(),
      status: "WaitingForFeedback",
      failureReason: "",
      outcomeVerification: {
        status: "AwaitingHumanResolution",
        legacyUnverified: false,
        currentRound: 3,
        maxRounds: 3,
        planHashPrefix: "sha256:aaaaaaaaaaaa",
        candidateFingerprintPrefix: "sha256:bbbbbbbbbbbb",
        candidateFingerprint:
          "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        releaseGateId: "33333333-3333-4333-8333-333333333333",
        criteria: [],
        evidence: [],
        latestResults: [],
        failedCriterionIds: ["AC-001"],
        pendingOwnerRoles: [],
        stale: false,
        previewRequired: false,
        releaseReady: false,
        verifiedAt: null,
        humanResolutionGate: {
          gateId: "44444444-4444-4444-8444-444444444444",
          decision: "AwaitingHumanApproval",
          summary: "QA budget exhausted.",
          createdAt: timestamp
        }
      }
    };
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(flow);
    const resolve = vi.spyOn(api, "resolveOutcome").mockResolvedValue({
      ...flow,
      status: "Queued"
    });
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), flow);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[`/factory/${flowId}`]}>
            <Routes>
              <Route path="/factory/:id" element={<FlowPage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    fireEvent.click(await screen.findByRole("button", { name: "Grant one QA round" }));
    expect(resolve).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText("Operator reason"), {
      target: { value: "Dependency restored." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Grant one QA round" }));
    await waitFor(() =>
      expect(resolve).toHaveBeenCalledWith(flowId, {
        gateId: "44444444-4444-4444-8444-444444444444",
        action: "Continue",
        reason: "Dependency restored."
      })
    );
    expect(screen.queryByText("Approve this result or request changes")).not.toBeInTheDocument();
  });
});
