import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api/client";
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
    currentFileValid: true,
    hasEffectiveDefinition: true,
    sourcePath: "E:\\projects\\demo\\WORKFLOW.md",
    effectiveLoadedAt: "2026-09-02T12:00:00Z",
    effectiveRevision: "workflow-test",
    currentFileError: null,
    loadedAt: "2026-09-02T12:00:00Z",
    lastError: null,
    maxConcurrentAgents: 1,
    maxAttempts: 1,
    workspaceRoot: "E:\\projects\\demo\\.workspaces"
  },
  agentCatalog: {
    ready: true,
    hasEffectiveCatalog: true,
    effectiveRevision: "catalog-test",
    loadedAt: "2026-09-02T12:00:00Z",
    lastError: null,
    validDefinitionCount: 0,
    invalidDefinitionCount: 0
  },
  admission: {
    ready: true,
    failures: [],
    checkedAt: "2026-09-02T12:00:00Z"
  },
  factoryEnabled: true,
  factoryDisabledReason: "",
  githubCliAvailable: true,
  githubCliAuthenticated: true
};

const flowId = "11111111-1111-1111-1111-111111111111";
const timestamp = "2026-09-02T12:00:00Z";

function confirmationFlow(status: FlowDetailDto["status"] = "Intake"): FlowDetailDto {
  return {
    id: flowId,
    title: "Refresh the public site",
    originalRequest: "Give the public site a fresh design.",
    consolidatedRequest: "Outcome: Refresh the public site with an interactive design.",
    kind: "Delivery",
    parentFlowRunId: null,
    parentIteration: null,
    linkKind: null,
    linkedFlows: [],
    agentCatalogRevision: "catalog-test",
    outcomeOwnerPlanStepKey: null,
    publicationPlanStepKey: null,
    currentBlockerCode: null,
    currentBlockerSummary: null,
    currentBlockerDataJson: null,
    customerBlockerMessage: null,
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
    reviewedPreviewUrl: null,
    outcomeResult: null,
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
    review: {
      gateId: null,
      available: false,
      resolved: false,
      approved: null,
      decision: null,
      publicationStatus: "AwaitingApproval"
    },
    publicationStatus: "AwaitingApproval"
  };
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("IntakePage", () => {
  it("sends the selected photo with the customer message and keeps its receipt visible", async () => {
    const initialFlow = confirmationFlow();
    const photo = new File(["speaker-photo"], "Photo_Speaker.jpg", {
      type: "image/jpeg"
    });
    const persisted = {
      ...initialFlow,
      messages: [
        ...initialFlow.messages,
        {
          id: "88888888-8888-8888-8888-888888888888",
          role: "Customer" as const,
          content: "Here is the requested photo.",
          isQuestion: false,
          createdAt: timestamp,
          attachments: [{
            id: "99999999-9999-9999-9999-999999999999",
            fileName: photo.name,
            contentType: photo.type,
            length: photo.size
          }]
        }
      ]
    } satisfies FlowDetailDto;
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(initialFlow);
    const continueIntake = vi.spyOn(api, "continueIntake").mockResolvedValue({
      flow: persisted,
      reply: "I have the photo.",
      readyToStart: false,
      shouldSpeak: false
    });
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), initialFlow);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[`/intake/${flowId}`]}>
            <Routes>
              <Route path="/intake/:id" element={<IntakePage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    fireEvent.change(screen.getByLabelText("Attach files for the Account Manager"), {
      target: { files: [photo] }
    });
    fireEvent.change(screen.getByRole("textbox", { name: "Customer request" }), {
      target: { value: "Here is the requested photo." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() => expect(continueIntake).toHaveBeenCalledWith({
      flowId,
      message: "Here is the requested photo.",
      files: [photo]
    }));
    await waitFor(() =>
      expect(screen.getByRole("list", { name: "Uploaded files" })).toHaveTextContent(
        "Photo_Speaker.jpg"
      )
    );
    expect(screen.queryByRole("list", { name: "Files ready to send" })).not.toBeInTheDocument();
  });

  it("rejects oversized files before submitting a customer message", () => {
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    const continueIntake = vi.spyOn(api, "continueIntake");
    const queryClient = new QueryClient();
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
    const oversized = new File(["x"], "huge.jpg");
    Object.defineProperty(oversized, "size", { value: 8 * 1024 * 1024 + 1 });
    fireEvent.change(screen.getByLabelText("Attach files for the Account Manager"), {
      target: { files: [oversized] }
    });

    expect(screen.queryByRole("list", { name: "Files ready to send" })).not.toBeInTheDocument();
    expect(continueIntake).not.toHaveBeenCalled();
  });

  it("shows whether the microphone is off or recording", () => {
    class MockSpeechRecognition extends EventTarget {
      lang = "";
      continuous = false;
      interimResults = false;
      onstart: (() => void) | null = null;
      onresult = null;
      onerror = null;
      onend: (() => void) | null = null;

      start(): void { this.onstart?.(); }
      stop(): void { this.onend?.(); }
    }
    vi.stubGlobal("SpeechRecognition", MockSpeechRecognition);
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false } }
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

    const microphone = screen.getByRole("button", { name: "Start voice recording" });
    expect(microphone).toHaveTextContent("Mic off");
    expect(microphone).toHaveAttribute("aria-pressed", "false");
    expect(microphone).not.toHaveClass("recording");

    fireEvent.click(microphone);
    expect(microphone).toHaveTextContent("Recording");
    expect(microphone).toHaveAttribute("aria-pressed", "true");
    expect(microphone).toHaveClass("recording");
    expect(screen.getByRole("button", { name: "Stop voice recording" })).toBe(microphone);

    fireEvent.click(microphone);
    expect(microphone).toHaveTextContent("Mic off");
    expect(microphone).toHaveAttribute("aria-pressed", "false");
    expect(microphone).not.toHaveClass("recording");
  });

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

  it("does not duplicate a pending customer message after polling observes it", async () => {
    let resolveIntake!: (response: IntakeResponse) => void;
    const intakeRequest = new Promise<IntakeResponse>(resolve => {
      resolveIntake = resolve;
    });
    const initialFlow = confirmationFlow();
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(initialFlow);
    vi.spyOn(api, "continueIntake").mockReturnValue(intakeRequest);
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), initialFlow);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[`/intake/${flowId}`]}>
            <Routes>
              <Route path="/intake/:id" element={<IntakePage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    const confirmation = "correct";
    fireEvent.change(screen.getByRole("textbox", { name: "Customer request" }), {
      target: { value: confirmation }
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(screen.getAllByText(confirmation)).toHaveLength(1);

    const persistedFlow = {
      ...initialFlow,
      messages: [
        ...initialFlow.messages,
        {
          id: "77777777-7777-7777-7777-777777777777",
          role: "Customer" as const,
          content: confirmation,
          isQuestion: false,
          createdAt: timestamp
        }
      ]
    } satisfies FlowDetailDto;
    act(() => {
      queryClient.setQueryData(queryKeys.flow(flowId), persistedFlow);
    });

    expect(screen.getAllByText(confirmation)).toHaveLength(1);
    await act(async () => {
      resolveIntake({
        flow: persistedFlow,
        reply: "Thanks.",
        readyToStart: false,
        shouldSpeak: false
      });
    });
  });

  it("navigates to a persisted failed intake and retries with the same flow id", async () => {
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(confirmationFlow());
    const continueIntake = vi.spyOn(api, "continueIntake")
      .mockRejectedValueOnce(
        new ApiError(
          "The flow was saved; retry it.",
          409,
          {
            status: 409,
            detail: "The flow was saved; retry it.",
            flowId,
            flowStatus: "Intake",
            retryMessage: "Open the saved flow and retry."
          }
        )
      )
      .mockResolvedValueOnce({
        flow: confirmationFlow(),
        reply: "Please confirm the saved brief.",
        readyToStart: false,
        shouldSpeak: false
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
          <MemoryRouter initialEntries={["/intake"]}>
            <Routes>
              <Route path="/intake" element={<IntakePage />} />
              <Route path="/intake/:id" element={<IntakePage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    const composer = screen.getByRole("textbox", {
      name: "Customer request"
    });
    fireEvent.change(composer, {
      target: { value: "Assess checkout resilience." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() =>
      expect(api.flow).toHaveBeenCalledWith(flowId)
    );
    await waitFor(() =>
      expect(screen.getByText(/Flow 11111111/)).toBeInTheDocument()
    );
    fireEvent.change(
      screen.getByRole("textbox", {
        name: "Customer request"
      }),
      { target: { value: "Retry the saved intake." } }
    );
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() =>
      expect(continueIntake).toHaveBeenLastCalledWith({
        flowId,
        message: "Retry the saved intake."
      })
    );
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

  it("renders the persisted Account Manager classification without sending a client-selected kind", async () => {
    const advisory = {
      ...confirmationFlow(),
      kind: "Advisory" as const
    };
    vi.spyOn(api, "bootstrap").mockResolvedValue(bootstrap);
    vi.spyOn(api, "flow").mockResolvedValue(advisory);
    const continueIntake = vi.spyOn(api, "continueIntake").mockResolvedValue({
      flow: advisory,
      reply: "Please confirm.",
      readyToStart: false,
      shouldSpeak: false
    });
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false, staleTime: Infinity },
        mutations: { retry: false }
      }
    });
    queryClient.setQueryData(queryKeys.bootstrap, bootstrap);
    queryClient.setQueryData(queryKeys.flow(flowId), advisory);

    render(
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <MemoryRouter initialEntries={[`/intake/${flowId}`]}>
            <Routes>
              <Route path="/intake/:id" element={<IntakePage />} />
            </Routes>
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );

    expect(screen.getByRole("heading", { name: "Proposed as Advisory" })).toBeInTheDocument();
    expect(screen.getByText(/without changing or publishing source code/i)).toBeInTheDocument();
    expect(
      screen.getByRole("button", { name: "Start voice recording" })
    ).toHaveAttribute("aria-pressed", "false");
    fireEvent.change(screen.getByLabelText("Customer request"), {
      target: { value: "Please include rollout risks." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    await waitFor(() =>
      expect(continueIntake).toHaveBeenCalledWith({
        flowId,
        message: "Please include rollout risks."
      })
    );
  });

  it("shows the exact admission reason when new intake is disabled", () => {
    const locked = {
      ...bootstrap,
      factoryEnabled: false,
      factoryDisabledReason:
        "Agent catalog is not ready: team-lead.agent.md is missing.",
      admission: {
        ready: false,
        failures: ["Agent catalog is not ready: team-lead.agent.md is missing."],
        checkedAt: timestamp
      }
    } satisfies BootstrapDto;
    vi.spyOn(api, "bootstrap").mockResolvedValue(locked);
    const queryClient = new QueryClient();
    queryClient.setQueryData(queryKeys.bootstrap, locked);

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

    expect(
      screen.getByText("Agent catalog is not ready: team-lead.agent.md is missing.")
    ).toBeInTheDocument();
  });
});
