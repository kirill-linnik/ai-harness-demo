import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import type { FlowStepDto, HandoffGateRecordDto } from "../../api/types";
import { formatDuration, statusLabel } from "../../lib/format";

function HandoffContent({
  title,
  content,
  emptyMessage
}: {
  title: string;
  content: string;
  emptyMessage: string;
}) {
  return (
    <section className="detail-artifact" aria-label={title}>
      <div className="detail-artifact-header">{title}</div>
      <div className="detail-markdown">
        {content ? (
          <ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml>
            {content}
          </ReactMarkdown>
        ) : (
          <p className="detail-placeholder">{emptyMessage}</p>
        )}
      </div>
    </section>
  );
}

export function StepDetail({ step, gate }: { step: FlowStepDto; gate: HandoffGateRecordDto | undefined }) {
  const outputPlaceholder =
    step.status === "Running"
      ? "Agent is reasoning, acting, and observing..."
      : "This handoff has not started.";

  return (
    <>
      <div className="detail-kicker">{step.label || step.agentRole}</div>
      <h3>{step.agentName}</h3>
      <div className="detail-model" aria-label="Persisted plan assignment">
        <span className="mono">{step.agentId}</span>
        {step.planStepKey && <span className="model-chip">Step {step.planStepKey}</span>}
        <span>{statusLabel(step.stage)}</span>
        <span>{statusLabel(step.permissionProfile)}</span>
        {step.isOutcomeOwner && <span className="model-chip">Outcome owner</span>}
      </div>
      {step.duties.length > 0 && (
        <div className="detail-model" aria-label="Plan duties">
          {step.duties.map(duty => (
            <span className="model-chip" key={duty}>{statusLabel(duty)}</span>
          ))}
        </div>
      )}
      {(step.dependencyPlanStepKeys.length > 0 || step.dependencyStepIds.length > 0) && (
        <p className="muted">
          Depends on{" "}
          <span className="mono">
            {(step.dependencyPlanStepKeys.length > 0
              ? step.dependencyPlanStepKeys
              : step.dependencyStepIds.map(id => id.slice(0, 8))).join(", ")}
          </span>
        </p>
      )}
      <div className="detail-model">
        <span className="model-chip">
          {step.model ? `${step.model}${step.modelEffort ? ` · ${step.modelEffort}` : ""}` : "Model pending"}
        </span>
        <span>{step.durationMilliseconds ? formatDuration(step.durationMilliseconds) : "Not completed"}</span>
        {step.status === "Running" && <span>{statusLabel(step.phase)}</span>}
        {step.executionAttempts > 1 && <span>{step.executionAttempts} runtime attempts</span>}
        {step.copilotSessionId && (
          <span title={`Copilot session ${step.copilotSessionId}`}>
            session {step.copilotSessionId.slice(0, 8)}
          </span>
        )}
      </div>
      <p className="muted">{step.modelReason || step.inputSummary || "Waiting for Team Lead selection."}</p>
      {step.routing && (
        <section className="callout" aria-label="Routing decision" style={{ marginTop: 14 }}>
          <strong>
            {statusLabel(step.routing.strategy)} · {(step.routing.predictedQuality * 100).toFixed(1)}% conservative quality
          </strong>
          <div className="runtime-line">
            <small>Expected accepted time</small>
            <span>{formatDuration(step.routing.predictedAcceptedTimeSeconds * 1000)}</span>
          </div>
          <div className="runtime-line">
            <small>Expected premium requests</small>
            <span>
              {step.routing.predictedPremiumRequests.toFixed(2)}
              {step.routing.premiumUseEstimated ? " estimated" : ""}
            </span>
          </div>
          <div className="runtime-line">
            <small>Confidence</small>
            <span>{(step.routing.confidence * 100).toFixed(0)}%</span>
          </div>
          <div className="runtime-line">
            <small>Routing</small>
            <span>
              Adaptive
              {step.routing.rerouteCount > 0 ? ` · reroute ${step.routing.rerouteCount}` : ""}
            </span>
          </div>
          {step.routing.exploration && <span className="status-pill">Deterministic exploration</span>}
          {step.routing.alternatives.length > 0 && (
            <div style={{ marginTop: 10 }}>
              <strong>Top rejected alternatives</strong>
              {step.routing.alternatives.map(alternative => (
                <p className="muted" key={`${alternative.model}:${alternative.effort}`}>
                  <strong>{alternative.model} · {alternative.effort}</strong>
                  {" — "}
                  {(alternative.predictedQuality * 100).toFixed(1)}% quality ·{" "}
                  {formatDuration(alternative.predictedAcceptedTimeSeconds * 1000)} accepted time ·{" "}
                  {alternative.predictedPremiumRequests.toFixed(2)} premium requests ·{" "}
                  {(alternative.confidence * 100).toFixed(0)}% confidence. {alternative.reason}
                </p>
              ))}
            </div>
          )}
        </section>
      )}
      {step.taskProfile && (
        <section className="callout" aria-label="Task profile" style={{ marginTop: 14 }}>
          <strong>
            {step.taskProfile.role} · {step.taskProfile.risk} risk
          </strong>
          <p className="muted">{step.taskProfile.riskReason}</p>
          <div className="detail-model">
            <span>C{step.taskProfile.complexity}</span>
            <span>R{step.taskProfile.reasoningDepth}</span>
            <span>Context {step.taskProfile.contextDemand}</span>
            <span>Tools {step.taskProfile.toolIntensity}</span>
            <span>Profile confidence {(step.taskProfile.confidence * 100).toFixed(0)}%</span>
            {step.taskProfile.taskTypeTags.map(tag => (
              <span className="model-chip" key={tag}>{tag}</span>
            ))}
          </div>
          {step.taskProfile.rationales.map((rationale, index) => (
            <p className="muted" key={`${index}:${rationale}`}>{rationale}</p>
          ))}
        </section>
      )}
      {step.effectivePermissionJson && (
        <details className="operator-details">
          <summary>Effective permission policy</summary>
          <pre>{step.effectivePermissionJson}</pre>
        </details>
      )}
      {gate && (
        <div
          className="callout"
          style={{ marginTop: 14, borderColor: "rgba(155,135,245,.24)", background: "rgba(155,135,245,.055)" }}
        >
          <span>
            <strong>
              {gate.actionType === "RequestRevision" ? "Revision request" : "Handoff gate"}: {statusLabel(gate.decision)}
            </strong>
            <br />
            {gate.reason} · {gate.trustLevelAtDecision} trust
          </span>
        </div>
      )}
      {step.pushbackReason && (
        <div className="pushback-callout">
          <strong>Pushback:</strong> {step.pushbackReason}
        </div>
      )}
      {step.toolCalls.length > 0 && (
        <div className="quick-prompts" style={{ marginTop: 14 }}>
          {step.toolCalls.map(tool => (
            <span className="model-chip" title={tool.argumentsSummary} key={tool.id}>
              {tool.succeeded ? "✓" : "!"} {tool.toolName}
            </span>
          ))}
        </div>
      )}
      <div className="detail-artifacts">
        <HandoffContent
          title="Prompt sent to Copilot CLI"
          content={step.executionPrompt}
          emptyMessage="The exact prompt was not captured for this earlier handoff."
        />
        <HandoffContent
          title="Current output"
          content={step.outputSummary}
          emptyMessage={outputPlaceholder}
        />
      </div>
    </>
  );
}
