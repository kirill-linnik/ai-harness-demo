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
      <div className="detail-model">
        <span className="model-chip">{step.model || "Model pending"}</span>
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
