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
  const runtime = step.runtimeActivity;
  const outputPlaceholder =
    step.status === "Running"
      ? "Awaiting an observed response; see execution activity."
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
      <section className="callout" aria-label="Execution activity" style={{ marginTop: 14 }}>
        <strong>Execution activity</strong>
        {runtime ? (
          <>
            {runtime.softWarning && (
              <p role="status">Soft elapsed-time limit reached. {step.status === "Running" && !runtime.terminationReason
                ? "Execution continues within its absolute budget."
                : "This warning did not terminate execution."}</p>
            )}
            <div className="runtime-line">
              <small>Last structured activity</small>
              <span>{runtime.lastStructuredEvent ?? "Unavailable"}{runtime.lastStructuredActivityAt
                ? ` · ${new Date(runtime.lastStructuredActivityAt).toLocaleString()}` : ""}</span>
            </div>
            <div className="runtime-line">
              <small>Active tool</small>
              <span>{runtime.activeTool ?? (runtime.completedToolCount === null ? "Unavailable" : "None observed active")}
                {runtime.activeToolElapsedMilliseconds !== null
                  ? ` · ${runtime.activeToolElapsedMilliseconds === 0 ? "0s" : formatDuration(runtime.activeToolElapsedMilliseconds)}` : ""}</span>
            </div>
            <div className="runtime-line"><small>Completed tools this invocation</small><span>{runtime.completedToolCount ?? "Unavailable"}</span></div>
            <div className="runtime-line"><small>Assignment elapsed (including downtime)</small><span>{runtime.totalElapsedMilliseconds === 0 ? "0s" : formatDuration(runtime.totalElapsedMilliseconds)}</span></div>
            <div className="runtime-line"><small>Remaining absolute budget</small><span>{runtime.remainingBudgetMilliseconds === 0 ? "0s" : formatDuration(runtime.remainingBudgetMilliseconds)}</span></div>
            <div className="runtime-line"><small>Assignment deadline</small><span>{new Date(runtime.budgetDeadlineAt).toLocaleString()}</span></div>
            <div className="runtime-line">
              <small>Raw stdout/stderr activity (not progress)</small>
              <span>{runtime.lastRawOutputAt ? new Date(runtime.lastRawOutputAt).toLocaleString() : "Unavailable"}</span>
            </div>
            {runtime.terminationReason && <p>Termination reason: {statusLabel(runtime.terminationReason)}</p>}
          </>
        ) : <p className="muted">Runtime activity is unavailable for this attempt.</p>}
      </section>
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
      {step.executionPolicyJson && (
        <details className="operator-details">
          <summary>Persisted execution policy</summary>
          <pre>{step.executionPolicyJson}</pre>
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
          <strong>{step.status === "Pushback" ? "Pushback:" : "Execution failure:"}</strong>{" "}
          {step.pushbackReason}
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
