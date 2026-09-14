import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import type { DeliveryReadinessDto, FlowDetailDto } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { ReadinessPanel } from "./ReadinessPanel";
import { ReviewCard } from "./ReviewCard";

const flowId = "44444444-4444-4444-8444-444444444444";
const gateId = "55555555-5555-4555-8555-555555555555";
const waiverGateId = "66666666-6666-4666-8666-666666666666";
const candidateId = "77777777-7777-4777-8777-777777777777";
const timestamp = "2026-09-08T09:00:00Z";

function readiness(
  overrides: Partial<DeliveryReadinessDto> = {}
): DeliveryReadinessDto {
  return {
    state: "ReadyToApprove",
    revision: 1,
    contractHash: `sha256:${"a".repeat(64)}`,
    reviewedCandidateId: candidateId,
    candidateFingerprintPrefix: "sha256:aaaaaaaaaa",
    criteria: [
      {
        criterionId: "AC-001",
        requirement: "The checkout retry survives a duplicate submit.",
        outcome: "Verified",
        rationale: "The scripted duplicate submit produced one charge.",
        remediation: null,
        evidenceIds: ["EV-001"],
        responsibleRoles: ["software-engineer"],
        customerVisible: true
      }
    ],
    risks: [],
    requiredWaiverRiskIds: [],
    grantedWaiverRiskIds: [],
    diagnostics: [],
    allowedActions: ["Accept", "RequestRefinement"],
    reviewGateId: gateId,
    waiverGateId: null,
    publicationAssurance:
      "Publication authorization is bound to this readiness assessment",
    label: "Ready to approve",
    ...overrides
  };
}

function flow(overrides: Partial<FlowDetailDto> = {}): FlowDetailDto {
  return {
    id: flowId,
    title: "Harden checkout retries",
    originalRequest: "Harden checkout retries.",
    consolidatedRequest: "{}",
    kind: "Delivery",
    parentFlowRunId: null,
    parentIteration: null,
    linkKind: null,
    linkedFlows: [],
    agentCatalogRevision: "catalog-test",
    outcomeOwnerPlanStepKey: "prepare",
    publicationPlanStepKey: "publish",
    currentBlockerCode: null,
    currentBlockerSummary: null,
    currentBlockerDataJson: null,
    customerBlockerMessage: null,
    status: "WaitingForFeedback",
    iteration: 1,
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo",
    outcome: "PullRequest",
    modelSelectionStrategy: "MaximumQuality",
    workspacePath: "E:\\worktrees\\flow",
    branchName: "ai-harness\\checkout",
    outcomeUrl: "",
    outcomeLabel: "Customer review ready",
    reviewedPreviewUrl: null,
    outcomeResult: {
      goal: "Harden checkout retries.",
      summary: "Duplicate submits now resolve to one charge.",
      implementationDetails: ["Persist an idempotency key before payment."],
      artifacts: []
    },
    failureReason: "",
    createdAt: timestamp,
    updatedAt: timestamp,
    completedAt: null,
    steps: [],
    messages: [],
    events: [],
    gateRecords: [],
    review: {
      gateId,
      available: true,
      resolved: false,
      approved: null,
      decision: null,
      publicationStatus: "AwaitingApproval"
    },
    publicationStatus: "AwaitingApproval",
    deliveryReadiness: readiness(),
    ...overrides
  };
}

function renderPanel(component: React.ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } }
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MemoryRouter>{component}</MemoryRouter>
      </ToastProvider>
    </QueryClientProvider>
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("ReadinessPanel", () => {
  it("renders verified criteria and the ready-to-approve label", () => {
    renderPanel(<ReadinessPanel flow={flow()} />);

    expect(screen.getAllByText("Ready to approve").length).toBeGreaterThan(0);
    expect(screen.getByText("AC-001 · Verified")).toBeInTheDocument();
    expect(
      screen.getByText("The checkout retry survives a duplicate submit.")
    ).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /Acknowledge and waive risks/ })
    ).not.toBeInTheDocument();
  });

  it("shows failed criteria and remediation without any accept or publication language", () => {
    const notReady = flow({
      deliveryReadiness: readiness({
        state: "NeedsRefinement",
        label: "Needs refinement",
        allowedActions: ["RequestRefinement"],
        reviewGateId: null,
        publicationAssurance: "Publication is not authorized",
        criteria: [
          {
            criterionId: "AC-001",
            requirement: "The checkout retry survives a duplicate submit.",
            outcome: "Failed",
            rationale: "The duplicate submit produced two charges.",
            remediation: "Persist the idempotency key before the payment call.",
            evidenceIds: ["EV-002"],
            responsibleRoles: ["software-engineer"],
            customerVisible: true
          }
        ],
        diagnostics: ["planned criterion 'AC-002' has no QA result"]
      })
    });

    renderPanel(<ReadinessPanel flow={notReady} />);

    expect(screen.getByText("AC-001 · Failed")).toBeInTheDocument();
    expect(
      screen.getByText("Remediation: Persist the idempotency key before the payment call.")
    ).toBeInTheDocument();
    expect(
      screen.getByText("planned criterion 'AC-002' has no QA result")
    ).toBeInTheDocument();
    expect(screen.getByText("Publication is not authorized")).toBeInTheDocument();
  });

  it("offers a waiver only for waiver-required risks and sends the immutable binding", async () => {
    const waive = vi.spyOn(api, "grantReadinessWaiver").mockResolvedValue({
      flowId,
      gateId: waiverGateId,
      reviewedCandidateId: candidateId,
      readinessRevision: 2,
      readinessContractHash: `sha256:${"b".repeat(64)}`,
      waivedRiskIds: ["RR-001"],
      message: "The waived risks were recorded."
    });
    const needsWaiver = flow({
      deliveryReadiness: readiness({
        state: "NeedsCustomerWaiver",
        label: "Needs customer waiver",
        allowedActions: ["GrantWaiver", "RequestRefinement"],
        reviewGateId: null,
        waiverGateId,
        requiredWaiverRiskIds: ["RR-001"],
        risks: [
          {
            riskId: "RR-001",
            classification: "WaiverRequired",
            severity: "High",
            statement: "Retries are unverified for the retired gateway.",
            impact: "A duplicate charge is possible on that gateway only.",
            evidenceIds: ["EV-RISK"],
            criterionIds: [],
            sourceRole: "quality-engineer",
            sourceStepId: gateId,
            preMortemFindingId: "PM-001",
            waived: false
          }
        ]
      })
    });

    renderPanel(<ReadinessPanel flow={needsWaiver} />);

    expect(
      screen.getByText("RR-001 · WaiverRequired · High")
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Acknowledge and waive RR-001/), {
      target: { value: "I accept the disclosed gateway risk." }
    });
    fireEvent.click(
      screen.getByRole("button", { name: /Acknowledge and waive risks/ })
    );

    await waitFor(() => expect(waive).toHaveBeenCalledTimes(1));
    expect(waive).toHaveBeenCalledWith(flowId, {
      gateId: waiverGateId,
      reviewedCandidateId: candidateId,
      readinessRevision: 1,
      readinessContractHash: `sha256:${"a".repeat(64)}`,
      riskIds: ["RR-001"],
      acknowledgement: "I accept the disclosed gateway risk."
    });
  });

  it("offers typed resolution actions when readiness needs refinement", async () => {
    const resolve = vi.spyOn(api, "resolveReadiness").mockResolvedValue({
      flowId,
      action: "RequestRefinement",
      resolvedFrom: "NeedsRefinement",
      status: "Reworking",
      iteration: 2,
      message: "A new iteration was queued."
    });
    const notReady = flow({
      deliveryReadiness: readiness({
        state: "NeedsRefinement",
        label: "Needs refinement",
        allowedActions: ["RequestRefinement"],
        reviewGateId: null,
        publicationAssurance: "Publication is not authorized"
      })
    });

    renderPanel(<ReadinessPanel flow={notReady} />);
    fireEvent.change(screen.getByLabelText(/Requested changes \(one per line\)/), {
      target: { value: "Make the archive error state distinguishable." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Request refinement" }));

    await waitFor(() => expect(resolve).toHaveBeenCalledTimes(1));
    expect(resolve).toHaveBeenCalledWith(flowId, {
      reviewedCandidateId: candidateId,
      readinessRevision: 1,
      readinessContractHash: `sha256:${"a".repeat(64)}`,
      action: "RequestRefinement",
      refinement: {
        goal: null,
        requestedChanges: ["Make the archive error state distinguishable."]
      }
    });
    expect(
      screen.queryByRole("button", { name: /^Accept$/ })
    ).not.toBeInTheDocument();
  });

  it("offers Continue, Replan, and Abandon when readiness is blocked", async () => {
    const resolve = vi.spyOn(api, "resolveReadiness").mockResolvedValue({
      flowId,
      action: "Continue",
      resolvedFrom: "Blocked",
      status: "Queued",
      iteration: 1,
      message: "The blocked iteration was re-queued."
    });
    const blocked = flow({
      status: "Blocked",
      deliveryReadiness: readiness({
        state: "Blocked",
        label: "Blocked",
        allowedActions: ["Continue", "Replan", "Abandon"],
        reviewGateId: null,
        publicationAssurance: "Publication is not authorized"
      })
    });

    renderPanel(<ReadinessPanel flow={blocked} />);
    expect(screen.getByRole("button", { name: "Replan" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Abandon" })).toBeInTheDocument();
    expect(
      screen.queryByRole("button", { name: /Acknowledge and waive risks/ })
    ).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    await waitFor(() => expect(resolve).toHaveBeenCalledTimes(1));
    expect(resolve).toHaveBeenCalledWith(flowId, {
      reviewedCandidateId: candidateId,
      readinessRevision: 1,
      readinessContractHash: `sha256:${"a".repeat(64)}`,
      action: "Continue",
      refinement: null
    });
  });

});

describe("ReviewCard readiness gating", () => {
  it("suppresses Accept whenever readiness is not ReadyToApprove", () => {
    const notReady = flow({
      deliveryReadiness: readiness({
        state: "NeedsCustomerWaiver",
        label: "Needs customer waiver",
        allowedActions: ["GrantWaiver", "RequestRefinement"],
        reviewGateId: null,
        waiverGateId
      })
    });

    renderPanel(<ReviewCard flow={notReady} />);

    expect(
      screen.queryByRole("button", { name: /Accept/ })
    ).not.toBeInTheDocument();
    expect(
      screen.getByText(/Acceptance is unavailable because host-derived Delivery readiness is/)
    ).toBeInTheDocument();
  });

  it("submits the immutable readiness binding with an accepted review", async () => {
    const review = vi.spyOn(api, "reviewFlow").mockResolvedValue({
      flowId,
      gateId,
      intent: "Accept",
      decision: "Accepted",
      status: "Queued",
      iteration: 1,
      publicationStepId: null,
      linkedFlowId: null,
      publicationStatus: "Queued",
      message: "Customer acceptance was recorded."
    });

    renderPanel(<ReviewCard flow={flow()} />);
    fireEvent.click(screen.getByRole("button", { name: /Accept/ }));

    await waitFor(() => expect(review).toHaveBeenCalledTimes(1));
    expect(review).toHaveBeenCalledWith(flowId, {
      gateId,
      intent: "Accept",
      refinement: null,
      reviewedCandidateId: candidateId,
      readinessRevision: 1,
      readinessContractHash: `sha256:${"a".repeat(64)}`
    });
  });
});
