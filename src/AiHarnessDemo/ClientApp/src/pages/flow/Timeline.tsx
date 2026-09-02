import type { FlowEventDto } from "../../api/types";
import { timeAgo } from "../../lib/format";

function eventClass(type: string): string {
  if (type.includes("pushback")) return "pushback";
  if (type.includes("failed")) return "failed";
  if (type.includes("completed") || type.includes("approved")) return "completed";
  return "";
}

export function Timeline({ events }: { events: FlowEventDto[] }) {
  const visible = events.slice(0, 18);
  if (!visible.length) return <p className="muted">Waiting for the first event.</p>;

  return (
    <div className="timeline">
      {visible.map(event => (
        <div className={`timeline-item ${eventClass(event.type)}`} key={event.id}>
          <span className="timeline-dot"></span>
          <div className="timeline-copy">
            <strong>{event.message}</strong>
            <span>
              {timeAgo(event.createdAt)} · {event.type}
            </span>
          </div>
        </div>
      ))}
    </div>
  );
}
