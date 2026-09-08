import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { api } from "../../api/endpoints";
import type { FlowDetailDto } from "../../api/types";
import { ToastProvider } from "../../lib/toast";
import { BlockedFlowCard } from "./BlockedFlowCard";
import { ReviewCard } from "./ReviewCard";

const flowId = "11111111-1111-4111-8111-111111111111";
const gateId = "22222222-2222-4222-8222-222222222222";
const childId = "33333333-3333-4333-8333-333333333333";
const timestamp = "2026-09-06T12:00:00Z";

function flow(overrides: Partial<FlowDetailDto> = {}): FlowDetailDto {
  return {
    id: flowId,
    title: "Assess checkout resilience",
    originalRequest: "Assess checkout resilience.",
    consolidatedRequest: "{}",
    kind: "Advisory",
    contractVersion: "studio-v2",
    parentFlowRunId: null,
    parentIteration: null,
    linkKind: null,
    linkedFlows: [],
    agentCatalogRevision: "catalog-test",
    outcomeOwnerPlanStepKey: "prepare-recommendation",
    publicationPlanStepKey: null,
    currentBlockerCode: null,
    currentBlockerSummary: null,
    currentBlockerDataJson: null,
    customerBlockerMessage: null,
    status: "WaitingForFeedback",
    iteration: 1,
    repositoryPath: "E:\\projects\\demo",
    repositoryKnowledge: "Demo",
    outcome: "None",
    modelSelectionStrategy: "MaximumQuality",
    workspacePath: "E:\\worktrees\\flow",
    branchName: "",
    outcomeUrl: `#/preview/${flowId}`,
    outcomeLabel: "Advisory result ready",
    reviewedPreviewUrl: null,
    outcomeResult: {
      goal: "Harden checkout retries.",
      summary: "Use durable idempotency.",
      implementationDetails: ["Persist a request key before payment."],
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
    },
    ...overrides
  };
}

function renderAction(component: React.ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false }
    }
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <MemoryRouter initialEntries={["/review"]}>
          <Routes>
            <Route path="/review" element={component} />
            <Route path="/factory/:id" element={<div>Linked Delivery opened</div>} />
            <Route path="/intake/:id" element={<div>Linked intake opened</div>} />
          </Routes>
        </MemoryRouter>
      </ToastProvider>
    </QueryClientProvider>
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("ReviewCard", () => {
  it("offers Advisory actions and submits bounded refinement fields", async () => {
    const review = vi.spyOn(api, "reviewFlow").mockResolvedValue({
      flowId,
      gateId,
      intent: "RequestRefinement",
      decision: "RefinementRequested",
      status: "Queued",
      iteration: 2,
      publicationStepId: null,
      linkedFlowId: null,
      publicationStatus: "NotApplicable",
      message: "Refinement queued."
    });
    renderAction(<ReviewCard flow={flow()} />);

    expect(screen.getByRole("button", { name: "Accept" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Request refinement" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Promote to Delivery" })).toBeInTheDocument();
    expect(screen.getByText("Harden checkout retries.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Request refinement" }));
    fireEvent.change(screen.getByLabelText("Refined goal (optional)"), {
      target: { value: "Focus on payment retries." }
    });
    fireEvent.change(screen.getByLabelText("Requested changes (one per line)"), {
      target: { value: "Exclude catalog retries.\nAdd rollout guidance." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Submit refinement" }));

    await waitFor(() =>
      expect(review).toHaveBeenCalledWith(flowId, {
        gateId,
        intent: "RequestRefinement",
        refinement: {
          goal: "Focus on payment retries.",
          requestedChanges: ["Exclude catalog retries.", "Add rollout guidance."]
        }
      })
    );
  });

  it("does not offer promotion for Delivery review", () => {
    renderAction(
      <ReviewCard
        flow={flow({
          kind: "Delivery",
          outcome: "PullRequest",
          review: {
            gateId,
            available: true,
            resolved: false,
            approved: null,
            decision: null,
            publicationStatus: "AwaitingApproval"
          },
          publicationStatus: "AwaitingApproval"
        })}
      />
    );

    expect(screen.getByRole("button", { name: "Accept" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Request refinement" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Promote to Delivery" })).not.toBeInTheDocument();
    expect(screen.queryByText(/Publication queued/i)).not.toBeInTheDocument();
  });

  it("shows only the server-projected reviewed preview for a waiting Delivery", () => {
    const reviewedPreviewUrl = `#/preview/${flowId}`;
    const rendered = renderAction(
      <ReviewCard
        flow={flow({
          kind: "Delivery",
          outcome: "PullRequest",
          reviewedPreviewUrl,
          review: {
            gateId,
            available: true,
            resolved: false,
            approved: null,
            decision: null,
            publicationStatus: "AwaitingApproval"
          },
          publicationStatus: "AwaitingApproval"
        })}
      />
    );

    expect(
      screen.getByRole("link", { name: "Open reviewed preview" })
    ).toHaveAttribute("href", reviewedPreviewUrl);

    rendered.rerender(
      <QueryClientProvider client={new QueryClient()}>
        <ToastProvider>
          <MemoryRouter>
            <ReviewCard
              flow={flow({
                kind: "Delivery",
                outcome: "PullRequest",
                reviewedPreviewUrl: null
              })}
            />
          </MemoryRouter>
        </ToastProvider>
      </QueryClientProvider>
    );
    expect(
      screen.queryByRole("link", { name: "Open reviewed preview" })
    ).not.toBeInTheDocument();
  });

  it("navigates to the idempotently returned promotion child", async () => {
    const review = vi.spyOn(api, "reviewFlow").mockResolvedValue({
      flowId,
      gateId,
      intent: "PromoteToDelivery",
      decision: "PromotedToDelivery",
      status: "Approved",
      iteration: 1,
      publicationStepId: null,
      linkedFlowId: childId,
      publicationStatus: "NotApplicable",
      message: "The existing linked Delivery flow was returned."
    });
    renderAction(<ReviewCard flow={flow()} />);

    fireEvent.click(screen.getByRole("button", { name: "Promote to Delivery" }));

    await waitFor(() => expect(screen.getByText("Linked intake opened")).toBeInTheDocument());
    expect(review).toHaveBeenCalledWith(flowId, {
      gateId,
      intent: "PromoteToDelivery",
      refinement: null
    });
  });

  it("offers promotion after an Advisory was accepted without a child", async () => {
    const review = vi.spyOn(api, "reviewFlow").mockResolvedValue({
      flowId,
      gateId,
      intent: "PromoteToDelivery",
      decision: "PromotedToDelivery",
      status: "Approved",
      iteration: 1,
      publicationStepId: null,
      linkedFlowId: childId,
      publicationStatus: "NotApplicable",
      message: "The accepted Advisory was promoted."
    });
    renderAction(
      <ReviewCard
        flow={flow({
          status: "Approved",
          review: {
            gateId,
            available: true,
            resolved: true,
            approved: true,
            decision: "Accepted",
            publicationStatus: "NotApplicable"
          }
        })}
      />
    );

    fireEvent.click(
      screen.getByRole("button", { name: "Promote to Delivery" })
    );

    await waitFor(() =>
      expect(screen.getByText("Linked intake opened")).toBeInTheDocument()
    );
    expect(review).toHaveBeenCalledWith(flowId, {
      gateId,
      intent: "PromoteToDelivery",
      refinement: null
    });
  });

  it("does not offer accepted-Advisory promotion when the child already exists", () => {
    renderAction(
      <ReviewCard
        flow={flow({
          status: "Approved",
          linkedFlows: [
            {
              id: childId,
              title: "Implement checkout resilience",
              kind: "Delivery",
              status: "Intake",
              iteration: 1,
              parentIteration: 1,
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
          review: {
            gateId,
            available: true,
            resolved: true,
            approved: true,
            decision: "Accepted",
            publicationStatus: "NotApplicable"
          }
        })}
      />
    );

    expect(
      screen.queryByRole("button", { name: "Promote to Delivery" })
    ).not.toBeInTheDocument();
  });
});

describe("BlockedFlowCard", () => {
  const blocked = flow({
    status: "Blocked",
    outcomeResult: null,
    outcomeUrl: "",
    outcomeLabel: "Qualification needed",
    currentBlockerCode: "missing-qualification",
    currentBlockerSummary: "No enabled agent can assess regional tax rules.",
    currentBlockerDataJson: "{\"missing\":[\"Tax analysis\"]}",
    customerBlockerMessage:
      "The current team does not include the specialist needed to assess this safely.",
    review: {
      gateId: null,
      available: false,
      resolved: false,
      approved: null,
      decision: null,
      publicationStatus: "NotApplicable"
    }
  });

  it("keeps customer-safe text prominent and operator diagnostics in a disclosure", async () => {
    vi.spyOn(api, "resolveQualification").mockResolvedValue({
      parentFlowId: flowId,
      parentIteration: 1,
      successorFlowId: childId,
      linkKind: "QualificationRosterRepair",
      parentStatus: "Blocked",
      successorStatus: "Intake",
      existingSuccessor: true,
      accountManagerReply: "The repaired roster is ready."
    });
    renderAction(<BlockedFlowCard flow={blocked} />);

    expect(
      screen.getByText("The current team does not include the specialist needed to assess this safely.")
    ).toBeInTheDocument();
    const disclosure = screen.getByText("Operator diagnostics").closest("details");
    expect(disclosure).not.toHaveAttribute("open");
    fireEvent.click(screen.getByText("Operator diagnostics"));
    expect(disclosure).toHaveAttribute("open");
    expect(screen.getByText("missing-qualification")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Retry with repaired roster" }));
    await waitFor(() => expect(screen.getByText("Linked intake opened")).toBeInTheDocument());
    expect(api.resolveQualification).toHaveBeenCalledWith(flowId, {
      action: "RosterRepair"
    });
  });

  it("submits a revised goal and linear scope to a linked successor", async () => {
    const resolve = vi.spyOn(api, "resolveQualification").mockResolvedValue({
      parentFlowId: flowId,
      parentIteration: 1,
      successorFlowId: childId,
      linkKind: "QualificationScopeRevision",
      parentStatus: "Blocked",
      successorStatus: "Queued",
      existingSuccessor: false,
      accountManagerReply: "The revised scope is queued."
    });
    renderAction(<BlockedFlowCard flow={blocked} />);

    fireEvent.click(screen.getByRole("button", { name: "Revise scope" }));
    fireEvent.change(screen.getByLabelText("Revised goal"), {
      target: { value: "Assess checkout only." }
    });
    fireEvent.change(screen.getByLabelText("Revised scope (one item per line)"), {
      target: { value: "Exclude tax advice.\nDocument retry risks." }
    });
    fireEvent.click(screen.getByRole("button", { name: "Create revised-scope flow" }));

    await waitFor(() => expect(screen.getByText("Linked Delivery opened")).toBeInTheDocument());
    expect(resolve).toHaveBeenCalledWith(flowId, {
      action: "ScopeRevision",
      scopeRevision: {
        goal: "Assess checkout only.",
        scope: ["Exclude tax advice.", "Document retry risks."]
      }
    });
  });
});
