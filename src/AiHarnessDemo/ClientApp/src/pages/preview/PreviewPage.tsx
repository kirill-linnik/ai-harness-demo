import { useParams } from "react-router-dom";
import { usePreviewQuery } from "../../api/queries";
import { FatalScreen } from "../../components/FatalScreen";
import { BootScreen } from "../../components/BootScreen";
import { BackIcon, CheckIcon } from "../../lib/icons";
import { formatDuration } from "../../lib/format";

export function PreviewPage() {
  const { id } = useParams<{ id: string }>();
  const previewQuery = usePreviewQuery(id);

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
            <span className="status-pill approved">Quality gate passed</span>
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
      </main>
    </div>
  );
}
