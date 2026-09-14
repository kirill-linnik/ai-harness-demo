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
  kind: "Delivery",
  iteration: 1,
  status: "WaitingForFeedback",
  outcomeLabel: "Pull request candidate",
  outcomeResult: null,
  artifacts: [
    {
      id: "eu",
      label: "devclub.eu",
      url: `/api/previews/${flowId}/artifacts/eu/index.html`,
      openUrl: `/api/previews/${flowId}/artifacts/eu/view`,
      mediaType: "text/html",
      byteLength: null,
      downloadUrl: null,
      interactive: true,
      demoCapability: "OfflineOnly",
      demoInstanceId: null,
      demoState: "Stopped",
      demoUrl: null,
      demoFailureDetail: null,
      demoCandidateFingerprint: null,
      demoManifestHash: null
    },
    {
      id: "ee",
      label: "devclub.ee",
      url: `/api/previews/${flowId}/artifacts/ee/index.html`,
      openUrl: `/api/previews/${flowId}/artifacts/ee/view`,
      mediaType: "text/html",
      byteLength: null,
      downloadUrl: null,
      interactive: true,
      demoCapability: "OfflineOnly",
      demoInstanceId: null,
      demoState: "Stopped",
      demoUrl: null,
      demoFailureDetail: null,
      demoCandidateFingerprint: null,
      demoManifestHash: null
    }
  ],
  deliveredBy: [],
  review: {
    gateId: null,
    available: false,
    resolved: false,
    approved: null,
    decision: null,
    publicationStatus: "NotApplicable"
  },
  publicationStatus: "NotApplicable",
  deliveryReadiness: null,
  generatedAt: "2026-09-03T12:00:00Z"
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function renderPage(value: PreviewDto = preview) {
  const queryClient = new QueryClient();
  const previewRequest = vi.spyOn(api, "preview").mockResolvedValue(value);
  const toastRegion = document.createElement("div");
  toastRegion.id = "toast-region";
  document.body.append(toastRegion);
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
  return { previewRequest, queryClient };
}

describe("PreviewPage", () => {
  it("embeds the delivered artifact and routes customer review to the flow", async () => {
    renderPage();

    expect(
      await screen.findByTitle("devclub.eu interactive customer preview")
    ).toHaveAttribute("src", `/api/previews/${flowId}/artifacts/eu/index.html`);
    expect(screen.getByTitle("devclub.eu interactive customer preview")).toHaveAttribute(
      "sandbox",
      "allow-scripts"
    );
    expect(screen.getByRole("link", { name: "Review result" })).toHaveAttribute(
      "href",
      `#/factory/${flowId}`
    );
  });

  it("keeps untrusted preview messages away from decision controls and opens an isolated view", async () => {
    const openedWindow = { opener: window } as unknown as Window;
    const open = vi.spyOn(window, "open").mockReturnValue(openedWindow);
    renderPage();

    const frame = await screen.findByTitle("devclub.eu interactive customer preview");
    const sandbox = frame.getAttribute("sandbox")?.split(/\s+/) ?? [];
    const openLink = screen.getByRole("link", { name: "Open in new tab" });
    window.dispatchEvent(
      new MessageEvent("message", {
        data: {
          action: "approve",
          flowId,
          gateId: "33333333-3333-4333-8333-333333333333"
        },
        origin: "null"
      })
    );

    expect(sandbox).toContain("allow-scripts");
    expect(sandbox).not.toContain("allow-same-origin");
    expect(sandbox).not.toContain("allow-forms");
    expect(sandbox).not.toContain("allow-popups");
    expect(frame).toHaveAttribute("referrerpolicy", "no-referrer");
    expect(openLink).toHaveAttribute(
      "href",
      `/api/previews/${flowId}/artifacts/eu/view`
    );
    expect(openLink).toHaveAttribute("rel", "noopener noreferrer");
    fireEvent.click(openLink);
    expect(open).toHaveBeenCalledWith(
      `/api/previews/${flowId}/artifacts/eu/view`,
      "_blank",
      "noopener,noreferrer"
    );
    expect(openedWindow.opener).toBeNull();
  });

  it("shows the current Delivery fallback when no browser artifact is available", async () => {
    renderPage({
      ...preview,
      artifacts: []
    });

    expect(
      await screen.findByText(
        "This Delivery result contains no browser artifact. Review the normalized result above."
      )
    ).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Review result" })).toHaveAttribute(
      "href",
      `#/factory/${flowId}`
    );
  });

  it("lists Advisory text artifacts safely without pull-request or Delivery controls", async () => {
    const advisoryArtifactUrl =
      `/api/flows/${flowId}/artifacts/abc123/recommendation.md`;
    renderPage({
      ...preview,
      kind: "Advisory",
      outcomeLabel: "Advisory result ready",
      outcomeResult: {
        goal: "Assess checkout resilience.",
        summary: "The current retry boundary needs idempotency.",
        implementationDetails: ["Persist an idempotency key before payment."],
        artifacts: [
          {
            id: "abc123",
            path: "recommendation.md",
            mediaType: "text/markdown",
            byteLength: 128,
            url: advisoryArtifactUrl,
            downloadUrl: `${advisoryArtifactUrl}?download=true`
          }
        ]
      },
      artifacts: [
        {
          id: "abc123",
          label: "recommendation.md",
          url: advisoryArtifactUrl,
          openUrl: advisoryArtifactUrl,
          mediaType: "text/markdown",
          byteLength: 128,
          downloadUrl: `${advisoryArtifactUrl}?download=true`,
          interactive: false,
          demoCapability: "OfflineOnly",
          demoInstanceId: null,
          demoState: "Stopped",
          demoUrl: null,
          demoFailureDetail: null,
          demoCandidateFingerprint: null,
          demoManifestHash: null
        }
      ],
      review: {
        gateId: "99999999-9999-4999-8999-999999999999",
        available: true,
        resolved: false,
        approved: null,
        decision: null,
        publicationStatus: "NotApplicable"
      },
      publicationStatus: "NotApplicable"
    });

    expect(await screen.findByRole("heading", { name: "Advisory artifacts" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "View safely" })).toHaveAttribute(
      "href",
      advisoryArtifactUrl
    );
    expect(screen.getByRole("link", { name: "Download" })).toHaveAttribute(
      "href",
      `${advisoryArtifactUrl}?download=true`
    );
    expect(screen.queryByTitle(/interactive customer preview/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve and publish" })).not.toBeInTheDocument();
    expect(screen.queryByText(/pull request/i)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Review result" })).toHaveAttribute(
      "href",
      `#/factory/${flowId}`
    );
  });

  it("shows normalized studio details and typed readiness evidence and risks", async () => {
    renderPage({
      ...preview,
      outcomeResult: {
        goal: "Deliver a self-contained reviewed build.",
        summary: "The candidate is sealed and testable.",
        implementationDetails: ["Bundled representative content."],
        artifacts: []
      },
      deliveryReadiness: {
        state: "ReadyToApprove",
        revision: 1,
        contractHash: "sha256:ready",
        reviewedCandidateId: "11111111-1111-4111-8111-111111111111",
        candidateFingerprintPrefix: "sha256:candidate",
        criteria: [
          {
            criterionId: "AC-001",
            requirement: "Reviewed preview renders offline.",
            outcome: "Verified",
            rationale: "Observed by QA.",
            remediation: null,
            evidenceIds: ["EV-S001-001"],
            responsibleRoles: ["quality-engineer"],
            customerVisible: true
          }
        ],
        risks: [
          {
            riskId: "RISK-001",
            classification: "NonBlockingDisclosure",
            severity: "Low",
            statement: "Live demo is illustrative.",
            impact: "No readiness impact.",
            evidenceIds: ["EV-S001-001"],
            criterionIds: ["AC-001"],
            sourceRole: "quality-engineer",
            sourceStepId: "22222222-2222-4222-8222-222222222222",
            preMortemFindingId: null,
            waived: false
          }
        ],
        requiredWaiverRiskIds: [],
        grantedWaiverRiskIds: [],
        diagnostics: [],
        allowedActions: ["Accept"],
        reviewGateId: "33333333-3333-4333-8333-333333333333",
        waiverGateId: null,
        publicationAssurance: "Bound to this reviewed candidate.",
        label: "Ready to approve"
      }
    });

    expect(
      await screen.findByRole("heading", { name: "Deliver a self-contained reviewed build." })
    ).toBeInTheDocument();
    expect(screen.getByText(/AC-001 · Verified/)).toBeInTheDocument();
    expect(screen.getAllByText(/EV-S001-001/).length).toBeGreaterThan(0);
    expect(screen.getByText(/RISK-001 · NonBlockingDisclosure/)).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Reviewed preview" })).toBeInTheDocument();
  });

  it("labels artifacts offline-only and routes rebuild through the current review", async () => {
    renderPage({
      ...preview,
      review: {
        gateId: "99999999-9999-4999-8999-999999999999",
        available: true,
        resolved: false,
        approved: null,
        decision: null,
        publicationStatus: "AwaitingApproval"
      }
    });

    expect(await screen.findByText("Offline preview only")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Rebuild and verify preview" })).toHaveAttribute(
      "href",
      `#/factory/${flowId}`
    );
    expect(screen.getByText(/never affect Delivery readiness/i)).toBeInTheDocument();
  });

  it("starts a sealed demo and opens its stable harness URL", async () => {
    const candidateFingerprint = "sha256:candidate";
    const manifestHash = "sha256:manifest";
    const start = vi.spyOn(api, "startDemo").mockResolvedValue({
      capability: "Available",
      instanceId: "11111111-1111-4111-8111-111111111111",
      state: "Running",
      stableUrl: "/api/demos/11111111-1111-4111-8111-111111111111/",
      failureDetail: null,
      candidateFingerprint,
      manifestHash
    });
    renderPage({
      ...preview,
      artifacts: [
        {
          ...preview.artifacts[0],
          demoCapability: "Available",
          demoCandidateFingerprint: candidateFingerprint,
          demoManifestHash: manifestHash
        }
      ]
    });

    fireEvent.click(await screen.findByRole("button", { name: "Start demo" }));
    await waitFor(() =>
      expect(start).toHaveBeenCalledWith(flowId, "eu", {
        candidateFingerprint,
        manifestHash
      })
    );
    expect(await screen.findByRole("link", { name: "Open live demo" })).toHaveAttribute(
      "href",
      "/api/demos/11111111-1111-4111-8111-111111111111/"
    );
  });

  it("restarts and stops a running demo with controls disabled during mutations", async () => {
    const candidateFingerprint = "sha256:candidate";
    const manifestHash = "sha256:manifest";
    const running = {
      ...preview.artifacts[0],
      demoCapability: "Available" as const,
      demoInstanceId: "11111111-1111-4111-8111-111111111111",
      demoState: "Running" as const,
      demoUrl: "/api/demos/11111111-1111-4111-8111-111111111111/",
      demoCandidateFingerprint: candidateFingerprint,
      demoManifestHash: manifestHash
    };
    const restart = vi.spyOn(api, "restartDemo").mockResolvedValue({
      capability: "Available",
      instanceId: running.demoInstanceId,
      state: "Running",
      stableUrl: running.demoUrl,
      failureDetail: null,
      candidateFingerprint,
      manifestHash
    });
    const stop = vi.spyOn(api, "stopDemo").mockResolvedValue({
      capability: "Available",
      instanceId: running.demoInstanceId,
      state: "Stopped",
      stableUrl: running.demoUrl,
      failureDetail: null,
      candidateFingerprint,
      manifestHash
    });
    renderPage({ ...preview, artifacts: [running] });

    fireEvent.click(await screen.findByRole("button", { name: "Restart demo" }));
    await waitFor(() => expect(restart).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole("button", { name: "Stop demo" }));
    await waitFor(() => expect(stop).toHaveBeenCalledTimes(1));
    expect(await screen.findByRole("button", { name: "Start demo" })).toBeInTheDocument();
  });

  it("offers Retry start after failure and surfaces mutation errors", async () => {
    vi.spyOn(api, "startDemo").mockRejectedValue(new Error("Health check timed out."));
    renderPage({
      ...preview,
      artifacts: [
        {
          ...preview.artifacts[0],
          demoCapability: "Available",
          demoState: "Failed",
          demoFailureDetail: "Previous startup failed.",
          demoCandidateFingerprint: "sha256:candidate",
          demoManifestHash: "sha256:manifest"
        }
      ]
    });

    fireEvent.click(await screen.findByRole("button", { name: "Retry start" }));
    expect((await screen.findAllByText("Health check timed out.")).length).toBeGreaterThan(0);
    expect(screen.getByText("Previous startup failed.")).toBeInTheDocument();
  });

  it("disables start while the durable demo state is Starting", async () => {
    renderPage({
      ...preview,
      artifacts: [
        {
          ...preview.artifacts[0],
          demoCapability: "Available",
          demoState: "Starting",
          demoCandidateFingerprint: "sha256:candidate",
          demoManifestHash: "sha256:manifest"
        }
      ]
    });

    expect(await screen.findByRole("button", { name: "Starting demo..." })).toBeDisabled();
  });
});
