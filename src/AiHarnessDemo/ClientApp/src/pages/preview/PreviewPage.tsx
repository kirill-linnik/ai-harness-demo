import { type MouseEvent, useState } from "react";
import { useParams } from "react-router-dom";
import { usePreviewQuery } from "../../api/queries";
import { api } from "../../api/endpoints";
import type { DemoRuntimeStatus } from "../../api/types";
import { FatalScreen } from "../../components/FatalScreen";
import { BootScreen } from "../../components/BootScreen";
import { BackIcon, CheckIcon, ExternalIcon, RefreshIcon } from "../../lib/icons";
import { formatDuration } from "../../lib/format";
import { useToast } from "../../lib/toast";

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
  const toast = useToast();
  const previewQuery = usePreviewQuery(id);
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null);
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
          {preview.review.resolved && preview.review.approved === true && (
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
            <span className={`status-pill ${preview.review.resolved ? "approved" : "waitingforfeedback"}`}>
              {preview.review.resolved
                ? preview.review.decision ?? "Reviewed"
                : "Customer review pending"}
            </span>
            <span className="model-chip">
              {advisory ? "Read-only recommendation" : preview.outcomeLabel || "Delivery result"}
            </span>
            <span className="model-chip">{contributors.length} completed handoffs</span>
          </div>
        </section>
        {preview.outcomeResult && (
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
        {preview.deliveryReadiness && (
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
                : "This Delivery result contains no browser artifact. Review the normalized result above."}
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
                <div className={selectedArtifact.demoFailureDetail ? "pushback-callout" : undefined}>
                  <strong>
                    {selectedArtifact.demoFailureDetail
                      ? "Live demo unavailable"
                      : "Interactive offline preview · Live server not configured"}
                  </strong>
                  {selectedArtifact.demoFailureDetail ? (
                    <>
                      <p role="alert">{selectedArtifact.demoFailureDetail}</p>
                      {preview.review.available && !preview.review.resolved ? (
                        <a className="button" href={`#/factory/${preview.flowId}`}>
                          <RefreshIcon /> Rebuild and verify preview
                        </a>
                      ) : (
                        <span className="muted">
                          Rebuild is unavailable because there is no current safe review or refinement action.
                        </span>
                      )}
                    </>
                  ) : (
                    <p className="muted">This preview works without a live application server.</p>
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
        {preview.status === "WaitingForFeedback" && (
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
      </main>
    </div>
  );
}
