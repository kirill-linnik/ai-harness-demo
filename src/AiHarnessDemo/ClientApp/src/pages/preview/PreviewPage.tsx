import { type MouseEvent, useState } from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { useNavigate, useParams } from "react-router-dom";
import { useDecideFlowMutation, usePreviewQuery } from "../../api/queries";
import { api } from "../../api/endpoints";
import type { DemoRuntimeStatus } from "../../api/types";
import { FatalScreen } from "../../components/FatalScreen";
import { BootScreen } from "../../components/BootScreen";
import { BackIcon, CheckIcon, ExternalIcon, RefreshIcon } from "../../lib/icons";
import { formatDuration } from "../../lib/format";
import { useToast } from "../../lib/toast";
import { ApiError } from "../../api/client";
import { AbandonFlowButton } from "../flow/AbandonFlowButton";
import { OutcomeVerificationPanel } from "../flow/OutcomeVerificationPanel";

function openPreviewInNewTab(event: MouseEvent<HTMLAnchorElement>, url: string) {
  if (
    event.defaultPrevented ||
    event.button !== 0 ||
    event.metaKey ||
    event.ctrlKey ||
    event.shiftKey ||
    event.altKey
  ) {
    return;
  }

  event.preventDefault();
  const openedWindow = window.open(url, "_blank", "noopener,noreferrer");
  if (openedWindow) {
    openedWindow.opener = null;
  }
}

export function PreviewPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const toast = useToast();
  const previewQuery = usePreviewQuery(id);
  const decideFlow = useDecideFlowMutation();
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null);
  const [feedback, setFeedback] = useState("");
  const [demoPending, setDemoPending] = useState<string | null>(null);
  const [demoError, setDemoError] = useState<string | null>(null);
  const [demoOverrides, setDemoOverrides] = useState<Record<string, DemoRuntimeStatus>>({});

  if (previewQuery.isLoading) return <BootScreen />;

  if (previewQuery.isError) {
    const error = previewQuery.error;
    const message = error instanceof Error ? error.message : String(error);
    return (
      <FatalScreen
        title="The customer preview could not load"
        message={message}
        onRetry={() => void previewQuery.refetch()}
      />
    );
  }

  const preview = previewQuery.data;
  if (!preview) return null;
  const reviewedOutcome = preview.outcomeVerification;
  const legacyPreview = preview.contractVersion === "legacy-v1";
  const advisory = preview.kind === "Advisory";

  const contributors = preview.deliveredBy.filter(step => step.status === "Completed");
  const selectedArtifactBase =
    preview.artifacts.find(artifact => artifact.id === selectedArtifactId) ??
    preview.artifacts[0];
  const selectedDemo = selectedArtifactBase
    ? demoOverrides[selectedArtifactBase.id] ?? {
        capability: selectedArtifactBase.demoCapability,
        instanceId: selectedArtifactBase.demoInstanceId,
        state: selectedArtifactBase.demoState,
        stableUrl: selectedArtifactBase.demoUrl,
        failureDetail: selectedArtifactBase.demoFailureDetail,
        candidateFingerprint: selectedArtifactBase.demoCandidateFingerprint,
        manifestHash: selectedArtifactBase.demoManifestHash
      }
    : null;
  const selectedArtifact = selectedArtifactBase
    ? {
        ...selectedArtifactBase,
        demoCapability: selectedDemo!.capability,
        demoInstanceId: selectedDemo!.instanceId,
        demoState: selectedDemo!.state,
        demoUrl: selectedDemo!.stableUrl,
        demoFailureDetail: selectedDemo!.failureDetail,
        demoCandidateFingerprint: selectedDemo!.candidateFingerprint,
        demoManifestHash: selectedDemo!.manifestHash
      }
    : undefined;
  const deciding = decideFlow.isPending;

  async function mutateDemo(action: "start" | "restart" | "stop") {
    if (
      !id ||
      !selectedArtifact ||
      !selectedArtifact.demoCandidateFingerprint ||
      !selectedArtifact.demoManifestHash
    ) {
      setDemoError("The sealed demo binding is unavailable. Refresh or rebuild the reviewed preview.");
      return;
    }
    setDemoPending(action);
    setDemoError(null);
    try {
      const body = {
        candidateFingerprint: selectedArtifact.demoCandidateFingerprint,
        manifestHash: selectedArtifact.demoManifestHash
      };
      const result =
        action === "start"
          ? await api.startDemo(id, selectedArtifact.id, body)
          : action === "restart"
            ? await api.restartDemo(id, selectedArtifact.id, body)
            : await api.stopDemo(id, selectedArtifact.id, body);
      setDemoOverrides(current => ({ ...current, [selectedArtifact.id]: result }));
      toast(
        action === "stop"
          ? "Live demo stopped."
          : action === "restart"
            ? "Live demo restarted."
            : "Live demo is running.",
        "success"
      );
      await previewQuery.refetch();
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error);
      setDemoError(message);
      toast(message, "error");
    } finally {
      setDemoPending(null);
    }
  }

  async function decide(approve: boolean) {
    if (!id) return;
    const customerFeedback = feedback.trim();
    if (!approve && !customerFeedback) {
      toast("Describe what should change before requesting another iteration.", "error");
      return;
    }

    try {
      const decisionBody = {
        approve,
        gateId: reviewedOutcome.releaseGateId ?? "",
        candidateFingerprint: reviewedOutcome.candidateFingerprint,
        feedback: approve ? "" : customerFeedback
      };
      const result = await decideFlow.mutateAsync({
        flowId: id,
        body: decisionBody
      });
      if (result.outcome === "RefreshQueued") {
        toast(
          "The candidate changed during approval. Refresh and re-verification were queued; approval was not recorded.",
          "error"
        );
        navigate(`/factory/${id}`);
        return;
      }
      toast(result.message, "success");
      navigate(`/factory/${id}`);
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        await previewQuery.refetch();
        toast(
          "This reviewed gate or candidate is stale. The preview was refreshed; review the current candidate before deciding.",
          "error"
        );
        return;
      }
      toast(error instanceof Error ? error.message : String(error), "error");
    }
  }

  return (
    <div className="preview-shell">
      <header className="preview-topbar">
        <div className="preview-brand">
          <div className="brand-mark">
            <span></span>
            <span></span>
            <span></span>
          </div>
          Customer acceptance build
        </div>
        <a className="button small" href={`#/factory/${preview.flowId}`}>
          <BackIcon /> Execution details
        </a>
      </header>
      <main className="preview-main">
        <section className="preview-hero">
          {(legacyPreview
            ? preview.outcomeVerification.releaseReady
            : preview.review.resolved && preview.review.approved === true) && (
            <div className="preview-check">
              <CheckIcon />
            </div>
          )}
          <div className="eyebrow">{preview.kind} · iteration {preview.iteration}</div>
          <h1>{preview.title}</h1>
          <p>
            {advisory
              ? `The read-only analysis of ${preview.repositoryName} is ready. No source changes or publication candidate were created.`
              : `The AI factory prepared this customer-checkable Delivery outcome for ${preview.repositoryName}.`}
          </p>
          <div className="preview-meta">
            {legacyPreview ? (
              <span
                className={`status-pill ${
                  preview.outcomeVerification.releaseReady
                    ? "approved"
                    : preview.outcomeVerification.legacyUnverified
                      ? "pending"
                      : "failed"
                }`}
              >
                {preview.outcomeVerification.releaseReady
                  ? "Outcome verified"
                  : preview.outcomeVerification.legacyUnverified
                    ? "Legacy unverified"
                    : preview.outcomeVerification.status}
              </span>
            ) : (
              <span className={`status-pill ${preview.review.resolved ? "approved" : "waitingforfeedback"}`}>
                {preview.review.resolved
                  ? preview.review.decision ?? "Reviewed"
                  : "Customer review pending"}
              </span>
            )}
            <span className="model-chip">
              {advisory ? "Read-only recommendation" : preview.outcomeLabel || "Delivery result"}
            </span>
            <span className="model-chip">{contributors.length} completed handoffs</span>
          </div>
        </section>
        {legacyPreview && <OutcomeVerificationPanel outcome={preview.outcomeVerification} />}
        {legacyPreview && (
          <section className="callout preview-responsibilities" aria-label="Decision responsibilities">
            <strong>Independent QA verification</strong> proves the candidate against the criterion matrix.{" "}
            <strong>Product Manager feedback</strong> interprets customer comments.{" "}
            <strong>Customer release approval</strong> is a separate final decision and never overrides failed QA.
          </section>
        )}
        {!legacyPreview && preview.outcomeResult && (
          <section className="preview-outcome-summary" aria-labelledby="normalized-outcome-heading">
            <div className="eyebrow">Normalized result</div>
            <h2 id="normalized-outcome-heading">{preview.outcomeResult.goal}</h2>
            <p>{preview.outcomeResult.summary}</p>
            <h3>Implementation details</h3>
            <ul>
              {preview.outcomeResult.implementationDetails.map((detail, index) => (
                <li key={`${index}:${detail}`}>{detail}</li>
              ))}
            </ul>
          </section>
        )}
        {!legacyPreview && preview.deliveryReadiness && (
          <section className="preview-outcome-summary" aria-labelledby="preview-readiness-heading">
            <div className="eyebrow">Delivery readiness</div>
            <h2 id="preview-readiness-heading">{preview.deliveryReadiness.label}</h2>
            <p>{preview.deliveryReadiness.publicationAssurance}</p>
            <h3>Acceptance criteria</h3>
            <ul>
              {preview.deliveryReadiness.criteria.map(criterion => (
                <li key={criterion.criterionId}>
                  <strong>{criterion.criterionId} · {criterion.outcome}</strong>{" "}
                  {criterion.requirement}
                  {criterion.evidenceIds.length > 0 && (
                    <small> Evidence: {criterion.evidenceIds.join(", ")}</small>
                  )}
                </li>
              ))}
            </ul>
            {preview.deliveryReadiness.risks.length > 0 && (
              <>
                <h3>Residual risks</h3>
                <ul>
                  {preview.deliveryReadiness.risks.map(risk => (
                    <li key={risk.riskId}>
                      <strong>{risk.riskId} · {risk.classification}</strong> {risk.statement}
                      {risk.evidenceIds.length > 0 && (
                        <small> Evidence: {risk.evidenceIds.join(", ")}</small>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
          </section>
        )}
        {legacyPreview && preview.historicalDeliveryEvidence && (
          <section className="preview-outcome-summary" aria-labelledby="historical-evidence-heading">
            <div className="eyebrow">Non-authoritative</div>
            <h2 id="historical-evidence-heading">Historical delivery evidence</h2>
            <p>
              This bounded projection preserves completed historical handoffs for customer context.
              It does not prove readiness or authorize publication.
            </p>
            {preview.historicalDeliveryEvidence.items.map(item => (
              <article key={`${item.kind}:${item.agentName}`} className="detail-markdown">
                <h3>{item.kind} · {item.agentName}</h3>
                <ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml>
                  {item.markdown}
                </ReactMarkdown>
              </article>
            ))}
          </section>
        )}
        <section className="delivery-strip">
          {contributors.map(step => (
            <div className="delivery-person" key={step.id}>
              <div className="delivery-dot"></div>
              <strong>{step.agentName}</strong>
              <span>
                {step.label} · {formatDuration(step.durationMilliseconds)}
              </span>
            </div>
          ))}
        </section>
        <section className="preview-deliverable" aria-labelledby="preview-result-heading">
          <div className="preview-deliverable-head">
            <div>
              <div className="eyebrow">Customer-checkable outcome</div>
              <h2 id="preview-result-heading">
                {advisory
                  ? "Advisory artifacts"
                  : "Reviewed preview"}
              </h2>
            </div>
            {selectedArtifact?.interactive && (
              <a
                className="button small"
                href={selectedArtifact.openUrl}
                target="_blank"
                rel="noopener noreferrer"
                onClick={event =>
                  openPreviewInNewTab(event, selectedArtifact.openUrl)
                }
              >
                <ExternalIcon /> Open in new tab
              </a>
            )}
          </div>
          {!advisory && preview.artifacts.length > 1 && (
            <div className="segmented preview-artifact-tabs" aria-label="Preview variant">
              {preview.artifacts.map(artifact => (
                <button
                  className={`segment ${artifact.id === selectedArtifact?.id ? "active" : ""}`}
                  key={artifact.id}
                  onClick={() => setSelectedArtifactId(artifact.id)}
                >
                  {artifact.label}
                </button>
              ))}
            </div>
          )}
          {advisory && preview.artifacts.length > 0 ? (
            <div className="artifact-list">
              {preview.artifacts.map(artifact => (
                <div className="artifact-row" key={artifact.id}>
                  <span>
                    <strong>{artifact.label}</strong>
                    <small>
                      {artifact.mediaType} · {(artifact.byteLength ?? 0).toLocaleString()} bytes
                    </small>
                  </span>
                  <span className="flow-heading-actions">
                    <a
                      className="button small"
                      href={artifact.openUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                    >
                      <ExternalIcon /> View safely
                    </a>
                    {artifact.downloadUrl && (
                      <a className="button small" href={artifact.downloadUrl}>
                        Download
                      </a>
                    )}
                  </span>
                </div>
              ))}
            </div>
          ) : selectedArtifact?.interactive ? (
            <iframe
              className="preview-frame"
              src={selectedArtifact.url}
              title={`${selectedArtifact.label} interactive customer preview`}
              sandbox="allow-scripts"
              referrerPolicy="no-referrer"
            />
          ) : (
            <div className="pushback-callout">
              {advisory
                ? "This Advisory result contains no downloadable artifacts."
                : !legacyPreview
                  ? "This Delivery result contains no browser artifact. Review the normalized result above."
                  : preview.outcomeVerification.previewRequired
                    ? "The verified outcome requires a browser artifact, but none is available. Release approval is disabled."
                    : "This outcome does not require a browser artifact; review the verified criterion matrix above."}
            </div>
          )}
          {!advisory && selectedArtifact && (
            <section className="live-demo-controls" aria-label={`${selectedArtifact.label} live demo`}>
              <div>
                <div className="eyebrow">Non-authoritative live demo</div>
                <p className="muted">
                  Live demo health and interactions never affect Delivery readiness, approval, or publication.
                </p>
              </div>
              {selectedArtifact.demoCapability === "OfflineOnly" ? (
                <div className="pushback-callout">
                  <strong>Offline preview only</strong>
                  <p>
                    No valid sealed customer-demo-v1 manifest is available for this reviewed candidate.
                  </p>
                  {((legacyPreview && reviewedOutcome.releaseGateId) ||
                    (!legacyPreview && preview.review.available && !preview.review.resolved)) ? (
                    <a className="button" href={`#/factory/${preview.flowId}`}>
                      <RefreshIcon /> Rebuild and verify preview
                    </a>
                  ) : (
                    <span className="muted">
                      Rebuild is unavailable because there is no current safe review or refinement action.
                    </span>
                  )}
                  {selectedArtifact.demoFailureDetail && (
                    <p role="alert">{selectedArtifact.demoFailureDetail}</p>
                  )}
                </div>
              ) : selectedArtifact.demoState === "Running" && selectedArtifact.demoUrl ? (
                <div className="feedback-actions">
                  <a
                    className="button primary"
                    href={selectedArtifact.demoUrl}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    <ExternalIcon /> Open live demo
                  </a>
                  <button
                    className="button"
                    disabled={demoPending !== null}
                    onClick={() => void mutateDemo("restart")}
                  >
                    <RefreshIcon /> {demoPending === "restart" ? "Restarting..." : "Restart demo"}
                  </button>
                  <button
                    className="button danger"
                    disabled={demoPending !== null}
                    onClick={() => void mutateDemo("stop")}
                  >
                    {demoPending === "stop" ? "Stopping..." : "Stop demo"}
                  </button>
                </div>
              ) : (
                <div className="feedback-actions">
                  <button
                    className="button primary"
                    disabled={demoPending !== null || selectedArtifact.demoState === "Starting"}
                    onClick={() => void mutateDemo("start")}
                  >
                    {demoPending === "start" || selectedArtifact.demoState === "Starting"
                      ? "Starting demo..."
                      : selectedArtifact.demoState === "Failed" ||
                          selectedArtifact.demoState === "Unhealthy"
                        ? "Retry start"
                        : "Start demo"}
                  </button>
                  {selectedArtifact.demoFailureDetail && (
                    <span role="alert">{selectedArtifact.demoFailureDetail}</span>
                  )}
                </div>
              )}
              {demoError && <p role="alert">{demoError}</p>}
            </section>
          )}
        </section>
        {!legacyPreview && preview.status === "WaitingForFeedback" && (
          <section className="preview-decision" aria-labelledby="studio-review-heading">
            <div>
              <div className="eyebrow">Customer decision</div>
              <h2 id="studio-review-heading">Continue in the flow review</h2>
              <p className="muted">
                {advisory
                  ? "Accept, request a refinement, or promote this Advisory from the persisted review card."
                  : "Accept or request a refinement from the persisted review card."}
              </p>
            </div>
            <a className="button primary" href={`#/factory/${preview.flowId}`}>
              <BackIcon /> Review result
            </a>
          </section>
        )}
        {legacyPreview && preview.status === "WaitingForFeedback" &&
          !preview.outcomeVerification.legacyUnverified &&
          (preview.outcomeVerification.releaseReady &&
              (!preview.outcomeVerification.previewRequired ||
                preview.artifacts.length > 0)) &&
          preview.outcomeVerification.releaseGateId && (
          <section className="preview-decision" aria-labelledby="customer-decision-heading">
            <div>
              <div className="eyebrow">Final customer gate</div>
              <h2 id="customer-decision-heading">Approve this result or request changes</h2>
              <p className="muted">
                Approval publishes the prepared pull request and closes the flow after publication succeeds. A change
                request keeps this result and starts a new iteration with your feedback.
              </p>
            </div>
            <label className="field" htmlFor="preview-feedback">
              <span>What should change?</span>
              <textarea
                id="preview-feedback"
                rows={3}
                value={feedback}
                placeholder="Required only when requesting changes."
                onChange={event => setFeedback(event.target.value)}
              />
            </label>
            <div className="feedback-actions">
              <AbandonFlowButton flowId={preview.flowId} />
              <button
                className="button danger"
                disabled={deciding}
                onClick={() => void decide(false)}
              >
                <RefreshIcon /> {deciding ? "Working..." : "Request changes"}
              </button>
              <button
                className="button success"
                disabled={deciding}
                onClick={() => void decide(true)}
              >
                <CheckIcon /> {deciding ? "Working..." : "Approve and publish"}
              </button>
            </div>
          </section>
        )}
        {legacyPreview &&
          preview.status === "WaitingForFeedback" &&
          preview.outcomeVerification.legacyUnverified &&
          preview.outcomeVerification.releaseGateId && (
            <section className="preview-decision" aria-labelledby="legacy-refinement-heading">
              <div>
                <div className="eyebrow">Historical result</div>
                <h2 id="legacy-refinement-heading">Approval is unavailable</h2>
                <p className="muted">
                  This legacy result has no authoritative QA PASS. Request a rebuild and verification;
                  historical prose cannot authorize publication.
                </p>
              </div>
              <label className="field" htmlFor="preview-feedback">
                <span>What should change?</span>
                <textarea
                  id="preview-feedback"
                  rows={3}
                  value={feedback}
                  placeholder="Describe the rebuild or correction required."
                  onChange={event => setFeedback(event.target.value)}
                />
              </label>
              <div className="feedback-actions">
                <AbandonFlowButton flowId={preview.flowId} />
                <button
                  className="button danger"
                  disabled={deciding}
                  onClick={() => void decide(false)}
                >
                  <RefreshIcon /> {deciding ? "Working..." : "Request changes"}
                </button>
              </div>
            </section>
          )}
        {legacyPreview && preview.outcomeVerification.status === "AwaitingHumanResolution" && (
          <section className="preview-decision" aria-labelledby="verification-resolution-heading">
            <div>
              <h2 id="verification-resolution-heading">Release approval is unavailable</h2>
              <p className="muted">
                QA did not produce a current all-criteria PASS. Return to execution details to continue one round,
                replan the requirements, or abandon the flow.
              </p>
            </div>
            <a className="button" href={`#/factory/${preview.flowId}`}>
              <BackIcon /> Resolve verification
            </a>
          </section>
        )}
      </main>
    </div>
  );
}
