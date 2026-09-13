import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import { queryKeys } from "../../api/queries";
import type { BootstrapDto, FlowDetailDto, FlowStepDto } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { FlowPage } from "./FlowPage";

const flowId = "b3bbe5ab-95ce-446d-9606-c22151285c33";
const timestamp = "2026-09-02T20:13:47Z";

beforeEach(() => {
  const toastRegion = document.createElement("div");
  toastRegion.id = "toast-region";
  document.body.append(toastRegion);
});

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
    workspaceRoot: "E:\\projects\\demo\\data\\worktrees",
    outcomeVerificationEnabled: true,
    outcomeVerificationMaxRounds: 3
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
    planStepKey: "",
    duties: [],
    stage: "BeforeReview",
    isOutcomeOwner: false,
    permissionProfile: "WorkspaceWrite",
    effectivePermissionJson: "",
    workflowRevision: "",
    dependencyStepIds: [],
    dependencyPlanStepKeys: [],
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
    kind: "Delivery",
    contractVersion: "legacy-v1",
    parentFlowRunId: null,
    parentIteration: null,
    linkKind: null,
    linkedFlows: [],
    agentCatalogRevision: "",
    outcomeOwnerPlanStepKey: null,
    publicationPlanStepKey: null,
    currentBlockerCode: null,
    currentBlockerSummary: null,
    currentBlockerDataJson: null,
    customerBlockerMessage: null,
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
    reviewedPreviewUrl: null,
    outcomeResult: null,
    failureReason: "Agent process produced no output for 300 seconds.",
    createdAt: timestamp,
    updatedAt: timestamp,
    completedAt: timestamp,
    steps: [step()],
    messages: [],
    events: [],
    gateRecords: [],
    review: {
      gateId: null,
      available: false,
      resolved: false,
      approved: null,
      decision: null,
      publicationStatus: "NotApplicable"
    },
    publicationStatus: "NotApplicable",
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

function studioAdvisoryFlow(): FlowDetailDto {
  const parentId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
  const childId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
  const inspect = step({
    id: "11111111-1111-4111-8111-111111111111",
    sequence: 20,
    agentId: "research-specialist",
    agentName: "Research Specialist",
    agentRole: "research-specialist",
    label: "Inspect the current system",
    planStepKey: "inspect-current-system",
    duties: ["Analyze"],
    permissionProfile: "ReadOnlySource",
    status: "Completed",
    phase: "Succeeded",
    model: "gpt-5.4",
    taskProfile: {
      version: "task-profile-v1",
      role: "research-specialist",
      planStepKey: "inspect-current-system",
      agentId: "research-specialist",
      complexity: 6,
      reasoningDepth: 7,
      contextDemand: 7,
      toolIntensity: 2,
      taskTypeTags: ["Architecture"],
      risk: "High",
      riskReason: "Repository evidence is required.",
      confidence: 0.8,
      rationales: ["Inspect the current boundaries."]
    }
  });
  const owner = step({
    id: "22222222-2222-4222-8222-222222222222",
    sequence: 30,
    agentId: "research-specialist",
    agentName: "Research Specialist",
    agentRole: "research-specialist",
    label: "Prepare the recommendation",
    planStepKey: "prepare-recommendation",
    duties: ["Analyze", "PrepareOutcome"],
    isOutcomeOwner: true,
    permissionProfile: "ReadOnlySource",
    dependencyStepIds: [inspect.id],
    dependencyPlanStepKeys: [inspect.planStepKey],
    status: "Completed",
    phase: "Succeeded"
  });
  return {
    ...failedFlow(),
    title: "Assess checkout resilience",
    originalRequest: "Assess checkout resilience.",
    consolidatedRequest: "{}",
    kind: "Advisory",
    contractVersion: "studio-v2",
    parentFlowRunId: parentId,
    parentIteration: 3,
    linkKind: "QualificationScopeRevision",
    linkedFlows: [
      {
        id: childId,
        title: "Implement checkout resilience",
        kind: "Delivery",
        status: "Intake",
        iteration: 1,
        parentIteration: 2,
        linkKind: "AdvisoryPromotion",
        outcomeLabel: "",
        review: {
          gateId: null,
          available: false,
          resolved: false,
          approved: null,
          decision: null,
          publicationStatus: "AwaitingApproval"
        },
        createdAt: timestamp
      }
    ],
    agentCatalogRevision: "sha256:catalog",
    outcomeOwnerPlanStepKey: owner.planStepKey,
    publicationPlanStepKey: null,
    status: "WaitingForFeedback",
    outcome: "None",
    outcomeUrl: `#/preview/${flowId}`,
    outcomeLabel: "Advisory result ready",
    outcomeResult: {
      goal: "Harden checkout retries.",
      summary: "Use durable idempotency.",
      implementationDetails: ["Persist a request key before payment."],
      artifacts: [
        {
          id: "artifact-1",
          path: "checkout.md",
          mediaType: "text/markdown",
          byteLength: 42,
          url: `/api/flows/${flowId}/artifacts/artifact-1/checkout.md`,
          downloadUrl: `/api/flows/${flowId}/artifacts/artifact-1/checkout.md?download=true`
        }
      ]
    },
    failureReason: "",
    completedAt: null,
    steps: [inspect, owner],
    review: {
      gateId: "33333333-3333-4333-8333-333333333333",
      available: true,
      resolved: false,
      approved: null,
      decision: null,
      publicationStatus: "NotApplicable"
    },
    publicationStatus: "NotApplicable"
  };
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  document.getElementById("toast-region")?.remove();
});

describe("FlowPage manual restart", () => {
  it("offers guarded recovery for an execution stalled after suspension", async () => {
    const running: FlowDetailDto = {
      ...failedFlow(),
      status: "Running",
      failureReason: "",
      completedAt: null,
      steps: [
        step({
          status: "Running",
          phase: "StreamingTurn",
          completedAt: null
        })
      ]
    };
    const recovered: FlowDetailDto = {
      ...running,
      status: "Queued",
      steps: [
        step({
          status: "Pending",
          phase: "CanceledByReconciliation",
          completedAt: null
        })
      ]
    };
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(recovered);
    const recoverFlow = vi.spyOn(api, "recoverFlow").mockResolvedValue(recovered);
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), running);

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

    fireEvent.click(
      await screen.findByRole("button", {
        name: "Recover stalled execution"
      })
    );
    expect(
      screen.getByRole("dialog", { name: "Recover this flow?" })
    ).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Stop and recover" }));

    await waitFor(() => expect(recoverFlow).toHaveBeenCalledWith(flowId));
    await waitFor(() =>
      expect(
        screen.queryByRole("dialog", { name: "Recover this flow?" })
      ).not.toBeInTheDocument()
    );
  });

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
    vi.spyOn(api, "flow").mockResolvedValue(restarted);
    const restartFlow = vi.spyOn(api, "restartFlow").mockResolvedValue(restarted);
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
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
      expect(
        screen.getAllByText("Manual restart of Software Engineer").length
      ).toBeGreaterThan(0)
    );
    expect(screen.queryByText("Flow stopped")).not.toBeInTheDocument();
  });

  it("reports committed recovery when the restart response is ambiguous", async () => {
    const failed = failedFlow();
    const retry = step({
      id: "22222222-2222-2222-2222-222222222222",
      sequence: 50,
      label: "Manual restart of Software Engineer",
      status: "Running",
      phase: "StreamingTurn",
      attempt: 2,
      startedAt: timestamp,
      completedAt: null
    });
    const restarted: FlowDetailDto = {
      ...failed,
      status: "Running",
      failureReason: "",
      completedAt: null,
      steps: [...failed.steps, retry],
      events: [
        {
          id: "44444444-4444-4444-8444-444444444444",
          flowStepId: failed.steps[0].id,
          type: "flow.manual-restart",
          message: "Manual restart requested.",
          dataJson: null,
          createdAt: timestamp
        }
      ]
    };
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(restarted);
    const restartFlow = vi
      .spyOn(api, "restartFlow")
      .mockRejectedValue(new Error("Only a failed flow can be restarted."));
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
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
    expect(
      await screen.findByText(
        "Failed task recovery was committed despite the interrupted response; the current flow state was refreshed."
      )
    ).toBeInTheDocument();
    expect(screen.queryByText("Only a failed flow can be restarted.")).not.toBeInTheDocument();
    expect(screen.getAllByText("Manual restart of Software Engineer").length).toBeGreaterThan(0);
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

  it("renders repeated dynamic agents as a persisted sequential plan with lineage and review", async () => {
    const flow = studioAdvisoryFlow();
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(flow);
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);

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

    expect(
      await screen.findByRole("heading", { name: "Sequential execution plan" })
    ).toBeInTheDocument();
    expect(api.flow).toHaveBeenCalledWith(flowId);
    expect(screen.getAllByRole("button", { name: /Research Specialist/ })).toHaveLength(2);
    expect(screen.getAllByText("Step prepare-recommendation").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Prepare Outcome").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Outcome owner").length).toBeGreaterThan(0);
    expect(screen.getByText(/Depends on:/)).toHaveTextContent("inspect-current-system");
    expect(screen.getByText(/Profile: High risk/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Parent/ })).toHaveAttribute(
      "href",
      `/factory/${flow.parentFlowRunId}`
    );
    expect(screen.getByRole("link", { name: /Implement checkout resilience/ })).toHaveAttribute(
      "href",
      "/intake/bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
    );
    expect(screen.getByRole("button", { name: "Promote to Delivery" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "View" })).toHaveAttribute(
      "href",
      `/api/flows/${flowId}/artifacts/artifact-1/checkout.md`
    );
    expect(screen.queryByText("Outcome verification")).not.toBeInTheDocument();
    expect(screen.queryByText(/pull request outcome/i)).not.toBeInTheDocument();
  });

  it("shows Delivery publication progress only after persisted acceptance", () => {
    const beforeAcceptance: FlowDetailDto = {
      ...studioAdvisoryFlow(),
      kind: "Delivery",
      outcome: "PullRequest",
      linkedFlows: [],
      parentFlowRunId: null,
      parentIteration: null,
      linkKind: null,
      outcomeUrl: "",
      reviewedPreviewUrl: `#/preview/${flowId}`,
      review: {
        gateId: "33333333-3333-4333-8333-333333333333",
        available: true,
        resolved: false,
        approved: null,
        decision: null,
        publicationStatus: "AwaitingApproval"
      },
      publicationStatus: "AwaitingApproval"
    };
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(beforeAcceptance);
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), beforeAcceptance);
    const rendered = render(
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

    expect(screen.queryByText(/Publication running/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Promote to Delivery" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Accept" })).toBeInTheDocument();
    expect(
      screen.getByRole("link", { name: "Open reviewed preview" })
    ).toHaveAttribute("href", `#/preview/${flowId}`);
    expect(
      screen.queryByRole("link", { name: "Published outcome" })
    ).not.toBeInTheDocument();

    const afterAcceptance: FlowDetailDto = {
      ...beforeAcceptance,
      status: "Running",
      review: {
        ...beforeAcceptance.review,
        resolved: true,
        approved: true,
        decision: "Accepted",
        publicationStatus: "Running"
      },
      publicationStatus: "Running",
      reviewedPreviewUrl: null,
      steps: [
        ...beforeAcceptance.steps,
        step({
          id: "55555555-5555-4555-8555-555555555555",
          sequence: 40,
          agentId: "publisher",
          agentName: "Publisher",
          agentRole: "publisher",
          planStepKey: "publish-approved-result",
          duties: ["Publish"],
          stage: "AfterApproval",
          permissionProfile: "Publish",
          status: "Running",
          phase: "StreamingTurn"
        })
      ]
    };
    queryClient.setQueryData(queryKeys.flow(flowId), afterAcceptance);
    rendered.rerender(
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

    expect(screen.getByText(/Publication running/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Accept" })).not.toBeInTheDocument();
    expect(
      screen.queryByRole("link", { name: "Open reviewed preview" })
    ).not.toBeInTheDocument();

    const published: FlowDetailDto = {
      ...afterAcceptance,
      status: "Approved",
      outcomeUrl: "https://github.com/example/repository/pull/42",
      outcomeLabel: "Pull request #42",
      reviewedPreviewUrl: null,
      review: {
        ...afterAcceptance.review,
        publicationStatus: "Published"
      },
      publicationStatus: "Published",
      steps: afterAcceptance.steps.map(item =>
        item.planStepKey === "publish-approved-result"
          ? { ...item, status: "Completed", phase: "Succeeded" }
          : item
      )
    };
    queryClient.setQueryData(queryKeys.flow(flowId), published);
    rendered.rerender(
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

    expect(
      screen.queryByRole("link", { name: "Open reviewed preview" })
    ).not.toBeInTheDocument();
    expect(
      screen.getAllByRole("link", { name: "Published outcome" })
    ).not.toHaveLength(0);
  });
});
