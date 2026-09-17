import { useEffect, useMemo, useRef } from "react";
import type { FlowEventDto, FlowStepDto } from "../../api/types";
import { statusLabel, timeAgo } from "../../lib/format";

type TimelineStep = Pick<FlowStepDto, "id" | "agentName" | "attempt">;

type TimelineEntry =
  | { kind: "event"; event: FlowEventDto }
  | {
      kind: "runtime";
      id: string;
      flowStepId: string | null;
      events: FlowEventDto[];
    };

const runtimePhaseEvent = /^agent\.(?:PreparingWorkspace|BuildingPrompt|LaunchingAgentProcess|InitializingSession|StreamingTurn|Finishing|Retrying|Succeeded|Failed|TimedOut|Stalled|CanceledByReconciliation)$/;

const eventLabels: Readonly<Record<string, string>> = {
  "flow.failed": "Flow stopped",
  "flow.started": "Flow execution started",
  "flow.recovery-queued": "Interrupted flow queued",
  "flow.manual-recovery-queued": "Operator recovery queued",
  "flow.contract-correction-queued": "Completed work recovered for response correction",
  "agent.contract-correction-scheduled": "Response correction queued",
  "agent.context-snapshot": "Bounded prompt and complete inputs saved",
  "step.started": "Agent step started",
  "step.skipped": "Step skipped",
  "step.interrupted": "Agent step interrupted",
  "step.resume-queued": "Interrupted session queued to resume",
  "agent.session-created": "Copilot session created",
  "agent.session-resumed": "Copilot session resumed",
  "agent.session-discovered": "Copilot session recovered",
  "agent.orphan-stopped": "Orphaned Copilot process stopped",
  "gate.decision": "Handoff decision",
  "handoff.pushback": "Upstream revision requested",
  "handoff.retry-limit-exhausted": "Handoff retry limit reached",
  "learning.pushback-recorded": "Learning recorded"
};

const clockFormatter = new Intl.DateTimeFormat(undefined, {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit"
});

function eventClass(type: string): string {
  if (type.startsWith("learning.")) return "learning";
  if (
    type === "flow.failed" ||
    type.endsWith(".failed") ||
    type.includes("retry-limit-exhausted") ||
    type.includes("recovery-failed")
  ) {
    return "failed";
  }
  if (
    type === "handoff.pushback" ||
    type.includes("blocked") ||
    type.includes("denied")
  ) {
    return "warning";
  }
  if (
    type.includes("completed") ||
    type.includes("approved") ||
    type.includes("published") ||
    type.includes("recovered")
  ) {
    return "completed";
  }
  return "";
}

function fallbackEventLabel(type: string): string {
  const [domain, ...action] = type.split(".");
  const domainLabel = statusLabel(domain);
  const actionLabel = statusLabel(action.join(" "));
  return [domainLabel, actionLabel]
    .filter(Boolean)
    .map(value => value.charAt(0).toUpperCase() + value.slice(1))
    .join(" · ");
}

function eventLabel(type: string): string {
  return eventLabels[type] ?? fallbackEventLabel(type);
}

function formatClock(value: string): string {
  return clockFormatter.format(new Date(value));
}

function summarize(message: string, maximum = 180): string {
  if (message.length <= maximum) return message;
  const clipped = message.slice(0, maximum);
  const boundary = clipped.lastIndexOf(" ");
  return `${clipped.slice(0, boundary > maximum / 2 ? boundary : maximum).trimEnd()}…`;
}

function chronologicalEntries(events: FlowEventDto[]): TimelineEntry[] {
  const ordered = [...events]
    .reverse()
    .sort(
      (left, right) =>
        new Date(left.createdAt).getTime() - new Date(right.createdAt).getTime()
    );
  const entries: TimelineEntry[] = [];

  for (const event of ordered) {
    if (!runtimePhaseEvent.test(event.type)) {
      entries.push({ kind: "event", event });
      continue;
    }

    const previous = entries.at(-1);
    if (
      previous?.kind === "runtime" &&
      previous.flowStepId === event.flowStepId
    ) {
      previous.events.push(event);
      continue;
    }
    entries.push({
      kind: "runtime",
      id: `runtime:${event.id}`,
      flowStepId: event.flowStepId,
      events: [event]
    });
  }

  return entries;
}

function EventMessage({ message }: { message: string }) {
  if (message.length <= 220) {
    return <p className="timeline-message">{message}</p>;
  }
  return (
    <details className="timeline-event-detail">
      <summary>{summarize(message)}</summary>
      <p>{message}</p>
    </details>
  );
}

function EventMeta({
  event,
  step
}: {
  event: FlowEventDto;
  step: TimelineStep | undefined;
}) {
  const date = new Date(event.createdAt);
  return (
    <span className="timeline-meta">
      <time dateTime={event.createdAt} title={date.toLocaleString()}>
        {formatClock(event.createdAt)}
      </time>
      <span>{timeAgo(event.createdAt)}</span>
      {step && <span>{step.agentName} · attempt {step.attempt}</span>}
    </span>
  );
}

export function Timeline({
  events,
  steps
}: {
  events: FlowEventDto[];
  steps: TimelineStep[];
}) {
  const timelineRef = useRef<HTMLOListElement>(null);
  const stepById = useMemo(
    () => new Map(steps.map(step => [step.id, step])),
    [steps]
  );
  const entries = useMemo(() => chronologicalEntries(events), [events]);

  useEffect(() => {
    const timeline = timelineRef.current;
    if (timeline) {
      timeline.scrollTop = timeline.scrollHeight;
    }
  }, [events[0]?.id]);

  if (!entries.length) {
    return <p className="muted">Waiting for the first event.</p>;
  }

  return (
    <>
      <div className="timeline-order">Oldest to newest · latest event kept in view</div>
      <ol
        className="timeline"
        ref={timelineRef}
        aria-label="Execution ledger, oldest to newest"
      >
        {entries.map(entry => {
          if (entry.kind === "runtime") {
            const step = entry.flowStepId
              ? stepById.get(entry.flowStepId)
              : undefined;
            const first = entry.events[0]!;
            const last = entry.events.at(-1)!;
            return (
              <li className="timeline-item runtime" key={entry.id}>
                <span className="timeline-dot"></span>
                <details className="timeline-runtime">
                  <summary>
                    <strong>
                      {step ? `${step.agentName} runtime activity` : "Agent runtime activity"}
                    </strong>
                    <span className="timeline-meta">
                      <span>
                        {formatClock(first.createdAt)}
                        {first.id !== last.id ? `–${formatClock(last.createdAt)}` : ""}
                      </span>
                      {step && <span>attempt {step.attempt}</span>}
                      <span>{entry.events.length} runtime update{entry.events.length === 1 ? "" : "s"}</span>
                    </span>
                  </summary>
                  <div className="timeline-runtime-events">
                    {entry.events.map(event => (
                      <div key={event.id}>
                        <time dateTime={event.createdAt}>{formatClock(event.createdAt)}</time>
                        <span>{event.message}</span>
                      </div>
                    ))}
                  </div>
                </details>
              </li>
            );
          }

          const { event } = entry;
          const step = event.flowStepId
            ? stepById.get(event.flowStepId)
            : undefined;
          return (
            <li
              className={`timeline-item ${eventClass(event.type)}`}
              key={event.id}
            >
              <span className="timeline-dot"></span>
              <div className="timeline-copy">
                <strong title={event.type}>{eventLabel(event.type)}</strong>
                <EventMessage message={event.message} />
                <EventMeta event={event} step={step} />
              </div>
            </li>
          );
        })}
      </ol>
    </>
  );
}
