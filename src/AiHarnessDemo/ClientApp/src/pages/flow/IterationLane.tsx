import type { FlowStepDto } from "../../api/types";
import { initials, statusLabel } from "../../lib/format";

function PipelineNode({
  step,
  selected,
  onSelect
}: {
  step: FlowStepDto;
  selected: boolean;
  onSelect: () => void;
}) {
  const stateClass = step.status.toLowerCase();
  return (
    <button className={`pipeline-node ${stateClass} ${selected ? "selected" : ""}`} onClick={onSelect}>
      <span className="node-avatar">{initials(step.agentName)}</span>
      <span className="node-copy">
        <strong>{step.agentName}</strong>
        <span>{step.model || "Model pending"}</span>
      </span>
      <span className="node-state">
        <span>
          {step.status === "Running"
            ? statusLabel(step.phase)
            : step.status === "Pushback"
              ? "Pushed back"
              : step.status}
        </span>
        <em></em>
      </span>
    </button>
  );
}

export function IterationLane({
  iteration,
  steps,
  selectedStepId,
  onSelectStep
}: {
  iteration: string;
  steps: FlowStepDto[];
  selectedStepId: string | null;
  onSelectStep: (stepId: string) => void;
}) {
  const completed = steps.filter(step => step.status === "Completed").length;
  return (
    <div className="iteration-lane">
      <div className="lane-label">
        <span>Iteration {iteration}</span>
        <span>{completed} completed</span>
      </div>
      <div className="pipeline">
        {steps.map((step, index) => {
          const next = steps[index + 1];
          const pushback =
            step.status === "Pushback" || (next ? next.label.toLowerCase().includes("pushback") : false);
          return (
            <div className="pipeline-stage" key={step.id}>
              <PipelineNode
                step={step}
                selected={selectedStepId === step.id}
                onSelect={() => onSelectStep(step.id)}
              />
              {index < steps.length - 1 && (
                <span className={`pipeline-connector ${pushback ? "pushback" : ""}`}></span>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
