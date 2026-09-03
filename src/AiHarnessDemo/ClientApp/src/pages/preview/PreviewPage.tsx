import { useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useDecideFlowMutation, usePreviewQuery, useSendFeedbackMutation } from "../../api/queries";
import { FatalScreen } from "../../components/FatalScreen";
import { BootScreen } from "../../components/BootScreen";
import { BackIcon, CheckIcon, ExternalIcon, RefreshIcon } from "../../lib/icons";
import { formatDuration } from "../../lib/format";
import { useToast } from "../../lib/toast";

export function PreviewPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const toast = useToast();
  const previewQuery = usePreviewQuery(id);
  const sendFeedback = useSendFeedbackMutation();
  const decideFlow = useDecideFlowMutation();
  const [selectedArtifactId, setSelectedArtifactId] = useState<string | null>(null);
  const [feedback, setFeedback] = useState("");

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

  const contributors = preview.deliveredBy.filter(step => step.status === "Completed");
  const selectedArtifact =
    preview.artifacts.find(artifact => artifact.id === selectedArtifactId) ??
    preview.artifacts[0];
  const deciding = sendFeedback.isPending || decideFlow.isPending;

  async function decide(approve: boolean) {
    if (!id) return;
    const customerFeedback = feedback.trim();
    if (!approve && !customerFeedback) {
      toast("Describe what should change before requesting another iteration.", "error");
      return;
    }

    try {
      if (!approve) {
        await sendFeedback.mutateAsync({
          flowId: id,
          body: { message: customerFeedback }
        });
      }
      await decideFlow.mutateAsync({ flowId: id, approve });
      toast(
        approve
          ? "Approval recorded. Publishing the pull request now."
          : "Feedback retained. A revised iteration was queued.",
        "success"
      );
      navigate(`/factory/${id}`);
    } catch (error) {
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
          <div className="preview-check">
            <CheckIcon />
          </div>
          <div className="eyebrow">Iteration {preview.iteration} is ready</div>
          <h1>{preview.title}</h1>
          <p>
            The AI factory completed its planned roles, resolved handoff gates, and prepared this customer-checkable
            outcome for {preview.repositoryName}.
          </p>
          <div className="preview-meta">
            <span className="status-pill approved">Release gate ready</span>
            <span className="model-chip">{preview.outcomeLabel}</span>
            <span className="model-chip">{contributors.length} verified handoffs</span>
          </div>
        </section>
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
        <section className="preview-deliverable" aria-labelledby="interactive-result-heading">
          <div className="preview-deliverable-head">
            <div>
              <div className="eyebrow">Customer-checkable outcome</div>
              <h2 id="interactive-result-heading">Interactive result</h2>
            </div>
            {selectedArtifact && (
              <a
                className="button small"
                href={selectedArtifact.url}
                target="_blank"
                rel="noreferrer"
              >
                <ExternalIcon /> Open in new tab
              </a>
            )}
          </div>
          {preview.artifacts.length > 1 && (
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
          {selectedArtifact ? (
            <iframe
              className="preview-frame"
              src={selectedArtifact.url}
              title={`${selectedArtifact.label} interactive customer preview`}
              sandbox="allow-forms allow-popups allow-same-origin allow-scripts"
            />
          ) : (
            <div className="pushback-callout">
              The release did not provide a browser artifact. Return to execution details and recover the release task.
            </div>
          )}
        </section>
        {preview.status === "WaitingForFeedback" && (
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
      </main>
    </div>
  );
}
