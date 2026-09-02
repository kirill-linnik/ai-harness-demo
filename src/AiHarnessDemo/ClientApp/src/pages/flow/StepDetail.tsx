import type { FlowStepDto, HandoffGateRecordDto } from "../../api/types";
import { formatDuration, statusLabel } from "../../lib/format";

export function StepDetail({ step, gate }: { step: FlowStepDto; gate: HandoffGateRecordDto | undefined }) {
  return (
    <>
      <div className="detail-kicker">{step.label || step.agentRole}</div>
      <h3>{step.agentName}</h3>
      <div className="detail-model">
        <span className="model-chip">{step.model || "Model pending"}</span>
        <span>{step.durationMilliseconds ? formatDuration(step.durationMilliseconds) : "Not completed"}</span>
        {step.status === "Running" && <span>{statusLabel(step.phase)}</span>}
        {step.executionAttempts > 1 && <span>{step.executionAttempts} runtime attempts</span>}
      </div>
      <p className="muted">{step.modelReason || step.inputSummary || "Waiting for Team Lead selection."}</p>
      {gate && (
        <div
          className="callout"
          style={{ marginTop: 14, borderColor: "rgba(155,135,245,.24)", background: "rgba(155,135,245,.055)" }}
        >
          <span>
            <strong>Handoff gate: {statusLabel(gate.decision)}</strong>
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
      <div className="detail-output">
        {step.outputSummary || (step.status === "Running" ? "Agent is reasoning, acting, and observing..." : "This handoff has not started.")}
      </div>
    </>
  );
}
