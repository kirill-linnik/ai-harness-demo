import type { FlowStepDto } from "../../api/types";
import { statusLabel } from "../../lib/format";

function PlanStepCard({
  step,
  position,
  selected,
  onSelect
}: {
  step: FlowStepDto;
  position: number;
  selected: boolean;
  onSelect: () => void;
}) {
  const stateClass = step.status.toLowerCase();
  const dependencies =
    step.dependencyPlanStepKeys.length > 0
      ? step.dependencyPlanStepKeys
      : step.dependencyStepIds.map(id => id.slice(0, 8));
  return (
    <button
      className={`plan-step-card ${stateClass} ${selected ? "selected" : ""}`}
      onClick={onSelect}
      aria-pressed={selected}
    >
      <span className="plan-step-order" aria-hidden="true">{position}</span>
      <span className="plan-step-main">
        <span className="plan-step-heading">
          <strong>{step.agentName}</strong>
          <span className={`status-pill ${stateClass}`}>
            {step.status === "Running"
              ? statusLabel(step.phase)
              : step.status === "Pushback"
                ? "Pushed back"
                : statusLabel(step.status)}
          </span>
        </span>
        <span className="plan-step-identity">
          <span className="mono">{step.agentId}</span>
          {step.planStepKey && <span className="mono">Step {step.planStepKey}</span>}
          <span>{statusLabel(step.stage)}</span>
          {step.isOutcomeOwner && <span className="model-chip">Outcome owner</span>}
        </span>
        <span className="plan-step-label">{step.label}</span>
        <span className="plan-step-facts">
          <span>Permission: {statusLabel(step.permissionProfile)}</span>
          <span>
            Model: {step.model
              ? `${step.model}${step.modelEffort ? ` · ${step.modelEffort}` : ""}`
              : "pending"}
          </span>
          {step.routing && (
            <span>
              Routing: {statusLabel(step.routing.strategy)}
              {step.routing.exploration ? " · exploration" : ""}
            </span>
          )}
          {step.taskProfile && (
            <span>
              Profile: {step.taskProfile.risk} risk · C{step.taskProfile.complexity} ·
              R{step.taskProfile.reasoningDepth}
            </span>
          )}
        </span>
        {step.duties.length > 0 && (
          <span className="plan-step-tags" aria-label="Plan duties">
            {step.duties.map(duty => (
              <span className="model-chip" key={duty}>{statusLabel(duty)}</span>
            ))}
          </span>
        )}
        {dependencies.length > 0 && (
          <span className="plan-step-dependencies">
            Depends on: <span className="mono">{dependencies.join(", ")}</span>
          </span>
        )}
        {step.assignedCriterionIds.length > 0 && (
          <span className="plan-step-dependencies">
            Criteria: <span className="mono">{step.assignedCriterionIds.join(", ")}</span>
          </span>
        )}
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
      <ol className="plan-step-list" aria-label={`Iteration ${iteration} sequential plan`}>
        {[...steps]
          .sort((left, right) => left.sequence - right.sequence)
          .map((step, index) => (
            <li key={step.id}>
              <PlanStepCard
                step={step}
                position={index + 1}
                selected={selectedStepId === step.id}
                onSelect={() => onSelectStep(step.id)}
              />
            </li>
          ))}
      </ol>
    </div>
  );
}
